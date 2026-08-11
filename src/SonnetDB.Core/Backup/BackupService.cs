using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Kv;
using SonnetDB.Modbus;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;

namespace SonnetDB.Backup;

/// <summary>
/// SonnetDB 多模型备份、校验与离线恢复服务。
/// </summary>
public sealed class BackupService
{
    private const int MaximumGraphVerificationErrors = 100;

    private sealed record BackupCheckpointInfo(IReadOnlySet<string> CheckpointedKeyspaces);

    private static readonly string[] _transientSuffixes =
    [
        ".tmp",
        ".temp",
    ];

    /// <summary>仅供并发备份测试在指定文件复制后建立确定性同步点。</summary>
    internal Action<string>? AfterFileCopiedTestHook { get; set; }

    /// <summary>
    /// 创建当前数据库的一致目录备份。
    /// </summary>
    public BackupManifest Create(Tsdb tsdb, BackupCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationDirectory);

        return tsdb.CreateBackup(options, CreateAfterCheckpoint);
    }

    /// <summary>
    /// 读取备份 manifest。
    /// </summary>
    public BackupManifest ReadManifest(string backupDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupDirectory);
        string manifestPath = ManifestPath(backupDirectory);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("备份 manifest 不存在。", manifestPath);

        using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize(stream, BackupJsonContext.Default.BackupManifest)
            ?? throw new InvalidDataException("备份 manifest 内容无效。");
    }

    /// <summary>
    /// 校验备份 manifest 记录的全部文件大小和 SHA-256。
    /// </summary>
    public BackupVerificationResult Verify(string backupDirectory)
    {
        var errors = new List<string>();
        BackupManifest manifest;
        try
        {
            manifest = ReadManifest(backupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new BackupVerificationResult(false, 0, [ex.Message]);
        }

        ValidateManifestContract(manifest, errors);

        int checkedFiles = 0;
        if (manifest.Files is null)
            return new BackupVerificationResult(false, checkedFiles, errors.AsReadOnly());
        foreach (BackupFileEntry entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue;
            string path;
            try
            {
                path = ResolveManifestPath(backupDirectory, entry.Path);
            }
            catch (InvalidDataException ex)
            {
                errors.Add(ex.Message);
                continue;
            }

            if (!File.Exists(path))
            {
                if (entry.Required)
                    errors.Add($"Missing required file: {entry.Path}");
                continue;
            }

            checkedFiles++;
            try
            {
                var info = new FileInfo(path);
                if (info.Length != entry.SizeBytes)
                    errors.Add($"Size mismatch: {entry.Path} expected {entry.SizeBytes}, actual {info.Length}");

                string actualHash = ComputeSha256(path);
                if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"SHA-256 mismatch: {entry.Path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                errors.Add($"Failed to read backup file '{entry.Path}': {exception.Message}");
            }
        }

        if (errors.Count == 0)
            VerifyGraphState(backupDirectory, manifest, errors, isolateSource: true);

        return new BackupVerificationResult(errors.Count == 0, checkedFiles, errors.AsReadOnly());
    }

    /// <summary>
    /// 校验备份和恢复目标目录策略，但不复制任何文件。
    /// </summary>
    public BackupRestoreDryRunResult RestoreDryRun(BackupRestoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BackupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetDirectory);

        var errors = new List<string>();
        var verification = options.VerifyBeforeRestore
            ? Verify(options.BackupDirectory)
            : new BackupVerificationResult(true, 0, Array.Empty<string>());
        if (options.VerifyBeforeRestore && !verification.IsValid)
            errors.AddRange(verification.Errors);

        BackupManifest? manifest = null;
        try
        {
            manifest = ReadManifest(options.BackupDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            errors.Add(ex.Message);
        }

        if (manifest is not null && !options.VerifyBeforeRestore)
            ValidateManifestForRestore(manifest, options.BackupDirectory, errors);

        var target = EvaluateTargetDirectory(options.TargetDirectory, options.Overwrite);
        if (!target.IsAllowed)
            errors.Add($"恢复目标目录 '{Path.GetFullPath(options.TargetDirectory)}' 已存在且不允许覆盖。");

        return new BackupRestoreDryRunResult(
            errors.Count == 0,
            verification,
            manifest?.Files?.Count ?? 0,
            SumFileSizes(manifest?.Files),
            manifest?.Indexes?.Count ?? 0,
            target.Exists,
            target.Empty,
            errors.AsReadOnly());
    }

    /// <summary>
    /// 将备份离线恢复到新的数据库目录。
    /// </summary>
    public BackupManifest Restore(BackupRestoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BackupDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TargetDirectory);

        var dryRun = RestoreDryRun(options);
        if (!dryRun.IsValid)
            throw new InvalidDataException("恢复预检失败：" + string.Join("; ", dryRun.Errors));

        if (options.VerifyBeforeRestore)
        {
            if (!dryRun.Verification.IsValid)
                throw new InvalidDataException("备份校验失败：" + string.Join("; ", dryRun.Verification.Errors));
        }

        var manifest = ReadManifest(options.BackupDirectory);
        var manifestErrors = new List<string>();
        ValidateManifestForRestore(manifest, options.BackupDirectory, manifestErrors);
        if (manifestErrors.Count != 0)
        {
            throw new InvalidDataException(
                "恢复 manifest 合同校验失败：" + string.Join("; ", manifestErrors));
        }
        IReadOnlyList<BackupFileEntry> files = manifest.Files
            ?? throw new InvalidDataException("Backup manifest is missing its file list.");
        string targetDirectory = NormalizeFullDirectoryPath(options.TargetDirectory);
        string stagingDirectory = CreateRestoreStagingDirectory(targetDirectory);
        try
        {
            foreach (BackupFileEntry entry in files)
            {
                string source = ResolveManifestPath(options.BackupDirectory, entry.Path);
                if (!entry.Required && !File.Exists(source))
                    continue;
                string target = ResolveManifestPath(stagingDirectory, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyFile(source, target);
            }

            var graphErrors = new List<string>();
            VerifyGraphState(stagingDirectory, manifest, graphErrors, isolateSource: false);
            if (graphErrors.Count != 0)
            {
                throw new InvalidDataException(
                    "恢复后的 Graph reopen/invariant 校验失败：" + string.Join("; ", graphErrors));
            }

            DeleteRestoreLifecycleLocks(stagingDirectory);
            FlushRestoreStagingDirectories(stagingDirectory);
            PublishRestoreDirectory(stagingDirectory, targetDirectory, options.Overwrite);
            return manifest;
        }
        catch (Exception restoreException)
        {
            try
            {
                if (Directory.Exists(stagingDirectory))
                    Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception cleanupException) when (
                cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    "恢复失败，且 staging 目录清理失败。",
                    new AggregateException(restoreException, cleanupException));
            }
            throw;
        }
    }

    /// <summary>
    /// 从恢复后的主数据同步补建派生索引。
    /// </summary>
    public BackupIndexRebuildResult RebuildIndexes(Tsdb tsdb)
    {
        ArgumentNullException.ThrowIfNull(tsdb);

        var entries = new List<BackupIndexRebuildEntry>();
        foreach (var schema in tsdb.Tables.Catalog.Snapshot())
        {
            foreach (var index in schema.Indexes)
            {
                try
                {
                    _ = tsdb.Tables.RebuildIndex(schema.Name, index.Name);
                    entries.Add(new BackupIndexRebuildEntry(
                        "table",
                        schema.Name,
                        index.Name,
                        TableIndexKind(index),
                        "rebuilt",
                        "table index rebuilt from rowstore."));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    entries.Add(FailedIndex("table", schema.Name, index.Name, TableIndexKind(index), ex.Message));
                }
            }
        }

        foreach (var schema in tsdb.Documents.Catalog.Snapshot())
        {
            foreach (var index in schema.Indexes)
            {
                try
                {
                    _ = tsdb.Documents.RebuildIndex(schema.Name, index.Name);
                    entries.Add(new BackupIndexRebuildEntry(
                        "document",
                        schema.Name,
                        index.Name,
                        DocumentIndexKind(index),
                        "rebuilt",
                        "document index rebuilt from collection data."));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    entries.Add(FailedIndex("document", schema.Name, index.Name, DocumentIndexKind(index), ex.Message));
                }
            }

            foreach (var index in schema.FullTextIndexes)
            {
                try
                {
                    int documentCount = tsdb.Documents.RebuildFullTextIndex(schema.Name, index.Name);
                    entries.Add(new BackupIndexRebuildEntry(
                        "document",
                        schema.Name,
                        index.Name,
                        "fulltext",
                        "rebuilt",
                        "document fulltext index rebuilt/touched from collection data.",
                        documentCount));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    entries.Add(FailedIndex("document", schema.Name, index.Name, "fulltext", ex.Message));
                }
            }
        }

        foreach (var graph in tsdb.Graphs.Catalog.Snapshot())
        {
            try
            {
                GraphIndexRebuildResult rebuild = tsdb.Graphs.Open(graph.Name).RebuildIndexes();
                entries.Add(new BackupIndexRebuildEntry(
                    "graph",
                    graph.Name,
                    "__derived__",
                    "adjacency/property/unique",
                    "rebuilt",
                    rebuild.UniqueDeclarationsWereSupplied
                        ? "graph derived indexes rebuilt from element records and supplied unique declarations."
                        : "graph derived indexes rebuilt; unique declarations were limited to discoverable existing keys.",
                    rebuild.RepairedEntries));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                entries.Add(FailedIndex("graph", graph.Name, "__derived__", "adjacency/property/unique", ex.Message));
            }
        }

        foreach (var schema in tsdb.Measurements.Snapshot())
        {
            foreach (var column in schema.Columns)
            {
                if (column.DataType != FieldType.Vector || column.VectorIndex is null)
                    continue;

                entries.Add(new BackupIndexRebuildEntry(
                    "measurement",
                    schema.Name,
                    column.Name,
                    "vector:" + column.VectorIndex.Kind,
                    "planned",
                    "measurement vector index is maintained by Segment flush / compaction / restore lifecycle."));
            }
        }

        return new BackupIndexRebuildResult(
            entries.Count,
            entries.Count(static entry => string.Equals(entry.Status, "rebuilt", StringComparison.Ordinal)),
            entries.Count(static entry => string.Equals(entry.Status, "planned", StringComparison.Ordinal)),
            entries.Count(static entry => string.Equals(entry.Status, "failed", StringComparison.Ordinal)),
            entries.AsReadOnly());
    }

    internal BackupManifest CreateAfterCheckpoint(
        Tsdb tsdb,
        BackupCreateOptions options,
        IReadOnlyList<string> checkpointedKeyspaces)
    {
        string destination = Path.GetFullPath(options.DestinationDirectory);
        EnsureDirectoryIsOutsideSource(tsdb.RootDirectory, destination);
        PrepareBackupDirectory(destination, options.Overwrite);

        var graphStorageIds = tsdb.Graphs.Catalog.Snapshot()
            .Select(static graph => graph.StorageId)
            .ToHashSet();
        var includePredicate = CreateIncludePredicate(
            options.IncludeFullTextIndexes,
            graphStorageIds);
        var copied = CopyDatabaseFiles(tsdb.RootDirectory, destination, includePredicate);
        var entries = new List<BackupFileEntry>(copied.Count);
        foreach (string relativePath in copied.Order(StringComparer.Ordinal))
        {
            string path = Path.Combine(destination, relativePath);
            var info = new FileInfo(path);
            entries.Add(new BackupFileEntry(
                NormalizeRelativePath(relativePath),
                info.Length,
                ComputeSha256(path),
                Classify(relativePath),
                Required: IsRequired(relativePath)));
        }

        var manifest = BuildManifest(
            tsdb,
            options,
            entries.AsReadOnly(),
            new BackupCheckpointInfo(new HashSet<string>(checkpointedKeyspaces, StringComparer.Ordinal)));

        WriteManifest(destination, manifest);
        return manifest;
    }

    private static BackupManifest BuildManifest(
        Tsdb tsdb,
        BackupCreateOptions options,
        IReadOnlyList<BackupFileEntry> entries,
        BackupCheckpointInfo checkpointInfo)
    {
        return new BackupManifest(
            BackupManifest.CurrentFormatVersion,
            BackupManifest.CurrentDatabaseFormat,
            DateTimeOffset.UtcNow,
            Path.GetFullPath(tsdb.RootDirectory),
            new BackupConsistency(
                tsdb.CheckpointLsn,
                tsdb.NextSegmentId,
                tsdb.ListSegments().Count,
                entries.Sum(static e => e.SizeBytes)),
            BuildModelSummary(tsdb, options.IncludeFullTextIndexes, checkpointInfo),
            entries,
            BuildIndexEntries(tsdb, options.IncludeFullTextIndexes));
    }

    private static Predicate<string> CreateIncludePredicate(
        bool includeFullTextIndexes,
        IReadOnlySet<Guid> graphStorageIds)
    {
        ArgumentNullException.ThrowIfNull(graphStorageIds);
        return relativePath =>
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (normalized == BackupManifest.FileName)
                return false;

            string fileName = Path.GetFileName(normalized);
            // 生命周期锁只描述正在运行的实例，不能进入可恢复的数据清单。
            if (string.Equals(fileName, KvKeyspace.LifecycleLockFileName, StringComparison.OrdinalIgnoreCase))
                return false;

            for (int i = 0; i < _transientSuffixes.Length; i++)
            {
                if (fileName.EndsWith(_transientSuffixes[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (GetGraphFileKind(normalized) == BackupFileKind.GraphData
                && (!TryGetGraphStorageId(normalized, out Guid storageId)
                    || !graphStorageIds.Contains(storageId)))
            {
                return false;
            }

            if (includeFullTextIndexes)
                return true;

            return !normalized.StartsWith(
                TsdbPaths.DocumentsDirName + "/fulltext/",
                StringComparison.OrdinalIgnoreCase);
        };
    }

    /// <summary>快照源文件清单并按稳定顺序复制，避免枚举期间新文件混入本次备份。</summary>
    private IReadOnlyList<string> CopyDatabaseFiles(
        string sourceRoot,
        string destinationRoot,
        Predicate<string> include)
    {
        var copied = new List<string>();
        string[] sourceFiles = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string source in sourceFiles)
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            if (!include(relative))
                continue;

            string target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFile(source, target);
            copied.Add(relative);
            AfterFileCopiedTestHook?.Invoke(NormalizeRelativePath(relative));
        }

        return copied.AsReadOnly();
    }

    private static BackupModelSummary BuildModelSummary(
        Tsdb tsdb,
        bool includeFullTextIndexes,
        BackupCheckpointInfo checkpointInfo)
    {
        var measurements = tsdb.Measurements.Snapshot()
            .Select(static schema =>
            {
                var vectorIndexes = schema.Columns
                    .Where(static column => column.DataType == FieldType.Vector && column.VectorIndex is not null)
                    .Select(column => new BackupVectorIndexEntry(
                        schema.Name,
                        column.Name,
                        column.VectorIndex!.Kind.ToString(),
                        Rebuildable: true))
                    .ToArray();

                return new BackupMeasurementEntry(
                    schema.Name,
                    schema.Columns.Count(static c => c.Role == MeasurementColumnRole.Tag),
                    schema.Columns.Count(static c => c.Role == MeasurementColumnRole.Field),
                    vectorIndexes);
            })
            .ToArray();

        var tables = tsdb.Tables.Catalog.Snapshot()
            .Select(static schema => new BackupTableEntry(
                schema.Name,
                schema.PrimaryKey.ToArray(),
                schema.Columns.Count,
                schema.Indexes.Select(static index => new BackupSecondaryIndexEntry(
                    index.Name,
                    index.Columns.ToArray(),
                    index.IsUnique,
                    Rebuildable: true,
                    JsonPath: index.JsonPath)).ToArray()))
            .ToArray();

        var openedKeyspaces = tsdb.Keyspaces.List()
            .Select(name => new BackupKeyspaceEntry(name, checkpointInfo.CheckpointedKeyspaces.Contains(name)))
            .ToArray();

        var documents = tsdb.Documents.Catalog.Snapshot()
            .Select(schema => new BackupDocumentCollectionEntry(
                schema.Name,
                schema.Indexes.Count,
                schema.FullTextIndexes.Select(index => new BackupFullTextIndexEntry(
                    index.Name,
                    index.Fields.ToArray(),
                    index.Tokenizer,
                    Included: includeFullTextIndexes,
                    Rebuildable: true)).ToArray()))
            .ToArray();

        GraphCatalogState graphCatalogState = tsdb.Graphs.Catalog.CaptureState();
        var graphs = graphCatalogState.Definitions
            .OrderBy(static graph => graph.Name, StringComparer.Ordinal)
            .Select(static graph => new BackupGraphEntry(
                graph.Name,
                graph.StorageId,
                graph.RecordFormatVersion,
                CheckpointedDuringBackup: true,
                GraphIndexes()))
            .ToArray();

        return new BackupModelSummary(measurements, tables, openedKeyspaces, documents)
        {
            GraphCatalog = new BackupGraphCatalogEntry(graphCatalogState.Revision, graphs),
        };
    }

    private static IReadOnlyList<BackupIndexEntry> BuildIndexEntries(Tsdb tsdb, bool includeFullTextIndexes)
    {
        var indexes = new List<BackupIndexEntry>();

        foreach (var schema in tsdb.Tables.Catalog.Snapshot())
        {
            foreach (var index in schema.Indexes)
            {
                indexes.Add(new BackupIndexEntry(
                    "table",
                    schema.Name,
                    index.Name,
                    TableIndexKind(index),
                    Included: true,
                    Rebuildable: true,
                    RelativePath: null));
            }
        }

        foreach (var schema in tsdb.Documents.Catalog.Snapshot())
        {
            foreach (var index in schema.Indexes)
            {
                indexes.Add(new BackupIndexEntry(
                    "document",
                    schema.Name,
                    index.Name,
                    DocumentIndexKind(index),
                    Included: true,
                    Rebuildable: true,
                    RelativePath: null));
            }

            foreach (var index in schema.FullTextIndexes)
            {
                indexes.Add(new BackupIndexEntry(
                    "document",
                    schema.Name,
                    index.Name,
                    "fulltext",
                    Included: includeFullTextIndexes,
                    Rebuildable: true,
                    RelativePath: "documents/fulltext/" + EncodeName(schema.Name) + "/" + EncodeName(index.Name)));
            }
        }

        foreach (var schema in tsdb.Measurements.Snapshot())
        {
            foreach (var column in schema.Columns)
            {
                if (column.DataType != FieldType.Vector || column.VectorIndex is null)
                    continue;

                indexes.Add(new BackupIndexEntry(
                    "measurement",
                    schema.Name,
                    column.Name,
                    "vector:" + column.VectorIndex.Kind.ToString(),
                    Included: true,
                    Rebuildable: true,
                    RelativePath: null));
            }
        }

        foreach (GraphDefinition graph in tsdb.Graphs.Catalog.Snapshot())
        {
            string relativePath = TsdbPaths.GraphsDirName
                + "/" + TsdbPaths.GraphStoresDirName
                + "/" + graph.StorageId.ToString("N");
            foreach (BackupGraphIndexEntry index in GraphIndexes())
            {
                indexes.Add(new BackupIndexEntry(
                    "graph",
                    graph.Name,
                    index.Kind,
                    index.Kind,
                    index.Included,
                    index.Rebuildable,
                    relativePath));
            }
        }

        return indexes.AsReadOnly();
    }

    private static IReadOnlyList<BackupGraphIndexEntry> GraphIndexes()
        =>
        [
            new("outgoing-adjacency", Included: true, Rebuildable: false),
            new("incoming-adjacency", Included: true, Rebuildable: false),
            new("vertex-label", Included: true, Rebuildable: false),
            new("edge-label", Included: true, Rebuildable: false),
            new("vertex-property", Included: true, Rebuildable: false),
            new("edge-property", Included: true, Rebuildable: false),
            new("unique-property", Included: true, Rebuildable: false),
        ];

    private static BackupFileKind Classify(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string fileName = Path.GetFileName(normalized);

        if (string.Equals(
                normalized,
                TsdbPaths.GraphsDirName + "/" + TsdbPaths.GraphCatalogFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return BackupFileKind.GraphCatalog;
        }
        if (normalized.StartsWith(
                TsdbPaths.GraphsDirName + "/" + TsdbPaths.GraphStoresDirName + "/",
                StringComparison.OrdinalIgnoreCase))
        {
            return BackupFileKind.GraphData;
        }

        if (string.Equals(fileName, TsdbPaths.CatalogFileName, StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Catalog;
        if (string.Equals(fileName, TsdbPaths.MeasurementSchemaFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, TableSchemaCodec.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, DocumentCollectionSchemaCodec.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, ModbusCatalogCodec.FileName, StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Schema;
        if (string.Equals(fileName, TsdbPaths.TombstoneManifestFileName, StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Tombstone;
        if (normalized.StartsWith(TsdbPaths.WalDirName + "/", StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Wal;
        if (normalized.StartsWith(TsdbPaths.SegmentsDirName + "/", StringComparison.OrdinalIgnoreCase))
        {
            if (fileName.EndsWith(TsdbPaths.VectorIndexFileExtension, StringComparison.OrdinalIgnoreCase))
                return BackupFileKind.VectorIndex;
            if (fileName.EndsWith(TsdbPaths.AggregateIndexFileExtension, StringComparison.OrdinalIgnoreCase))
                return BackupFileKind.AggregateIndex;
            return BackupFileKind.Segment;
        }
        if (normalized.StartsWith(TsdbPaths.KvDirName + "/", StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Kv;
        if (normalized.StartsWith(TsdbPaths.TablesDirName + "/", StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Table;
        if (normalized.StartsWith(TsdbPaths.DocumentsDirName + "/fulltext/", StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.FullTextIndex;
        if (normalized.StartsWith(TsdbPaths.DocumentsDirName + "/", StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Document;

        return BackupFileKind.Other;
    }

    private static bool IsRequired(string relativePath)
    {
        var kind = Classify(relativePath);
        return kind is not BackupFileKind.FullTextIndex
            and not BackupFileKind.VectorIndex
            and not BackupFileKind.AggregateIndex;
    }

    private static void PrepareBackupDirectory(string destination, bool overwrite)
    {
        if (Directory.Exists(destination))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(destination).Any();
            if (!overwrite || !empty)
                throw new IOException($"备份目录 '{destination}' 已存在且不允许覆盖。");
            return;
        }

        Directory.CreateDirectory(destination);
    }

    private static void EnsureDirectoryIsOutsideSource(string sourceRoot, string destination)
    {
        string source = NormalizeFullDirectoryPath(sourceRoot);
        string target = NormalizeFullDirectoryPath(destination);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("备份目标目录不能位于数据库目录内部。");
        }
    }

    private static string NormalizeFullDirectoryPath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void CopyFile(string source, string target)
    {
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(
            target,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static string CreateRestoreStagingDirectory(string target)
    {
        string? parent = Path.GetDirectoryName(target);
        string name = Path.GetFileName(target);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            throw new IOException($"恢复目标目录 '{target}' 无法创建同卷 staging 目录。");

        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, name + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(staging);
        return staging;
    }

    private static void PublishRestoreDirectory(string staging, string target, bool overwrite)
    {
        if (Directory.Exists(target))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(target).Any();
            if (!overwrite || !empty)
                throw new IOException($"恢复目标目录 '{target}' 已存在且不允许覆盖。");
            Directory.Delete(target, recursive: false);
        }

        Directory.Move(staging, target);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(Path.GetDirectoryName(target)!);
    }

    private static void FlushRestoreStagingDirectories(string staging)
    {
        foreach (string directory in Directory
                     .EnumerateDirectories(staging, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
        }
        SonnetDB.Wal.DirectoryFsync.FlushRequired(staging);
    }

    private static void DeleteRestoreLifecycleLocks(string staging)
    {
        foreach (string path in Directory.EnumerateFiles(
                     staging,
                     KvKeyspace.LifecycleLockFileName,
                     SearchOption.AllDirectories))
        {
            File.Delete(path);
        }
    }

    private static RestoreTargetEvaluation EvaluateTargetDirectory(string target, bool overwrite)
    {
        bool exists = Directory.Exists(target);
        bool empty = !exists || !Directory.EnumerateFileSystemEntries(target).Any();
        bool allowed = !exists || (overwrite && empty);
        return new RestoreTargetEvaluation(exists, empty, allowed);
    }

    private static void WriteManifest(string destination, BackupManifest manifest)
    {
        string path = ManifestPath(destination);
        string tmpPath = path + ".tmp";
        using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, BackupJsonContext.Default.BackupManifest);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    private static string ManifestPath(string backupDirectory)
        => Path.Combine(backupDirectory, BackupManifest.FileName);

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateManifestForRestore(
        BackupManifest manifest,
        string backupDirectory,
        List<string> errors)
    {
        ValidateManifestContract(manifest, errors);

        if (manifest.Files is null)
            return;
        foreach (BackupFileEntry entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue;
            try
            {
                _ = ResolveManifestPath(backupDirectory, entry.Path);
            }
            catch (InvalidDataException ex)
            {
                errors.Add(ex.Message);
            }
        }
    }

    private static void ValidateManifestContract(BackupManifest manifest, List<string> errors)
    {
        if (!IsSupportedFormatVersion(manifest.FormatVersion))
            errors.Add($"Unsupported manifest format version {manifest.FormatVersion}.");

        if (manifest.Models is null)
        {
            errors.Add("Backup manifest is missing models.");
            return;
        }
        if (manifest.Consistency is null)
            errors.Add("Backup manifest is missing consistency metadata.");
        else if (manifest.Consistency.CheckpointLsn < 0
            || manifest.Consistency.NextSegmentId < 0
            || manifest.Consistency.SegmentCount < 0
            || manifest.Consistency.TotalBytes < 0)
        {
            errors.Add("Backup manifest consistency metadata contains a negative counter.");
        }
        if (!string.Equals(
                manifest.DatabaseFormat,
                BackupManifest.CurrentDatabaseFormat,
                StringComparison.Ordinal))
        {
            errors.Add($"Unsupported database format '{manifest.DatabaseFormat}'.");
        }
        if (manifest.Models.Measurements is null
            || manifest.Models.Tables is null
            || manifest.Models.Keyspaces is null
            || manifest.Models.DocumentCollections is null)
        {
            errors.Add("Backup manifest models contains a null model collection.");
        }
        if (manifest.Files is null)
            errors.Add("Backup manifest is missing its file list.");
        else
        {
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BackupFileEntry entry in manifest.Files)
            {
                if (entry is null)
                    errors.Add("Backup manifest file list contains a null entry.");
                else
                {
                    if (string.IsNullOrWhiteSpace(entry.Path))
                        errors.Add("Backup manifest file entry has an empty path.");
                    else
                    {
                        try
                        {
                            string normalizedPath = ValidateManifestRelativePath(entry.Path);
                            if (!filePaths.Add(normalizedPath))
                                errors.Add($"Backup manifest contains duplicate file path '{entry.Path}'.");
                            if (string.Equals(
                                    Path.GetFileName(normalizedPath),
                                    KvKeyspace.LifecycleLockFileName,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                errors.Add(
                                    $"Backup manifest cannot contain lifecycle lock file '{entry.Path}'.");
                            }
                        }
                        catch (InvalidDataException exception)
                        {
                            errors.Add(exception.Message);
                        }
                    }
                    if (entry.SizeBytes < 0)
                        errors.Add($"Backup manifest file '{entry.Path}' has a negative size.");
                    if (!IsSha256(entry.Sha256))
                        errors.Add($"Backup manifest file '{entry.Path}' has an invalid SHA-256 digest.");
                    if (!Enum.IsDefined(entry.Kind))
                        errors.Add($"Backup manifest file '{entry.Path}' has an unknown file kind.");
                }
            }
            if (FileSizesOverflow(manifest.Files))
                errors.Add("Backup manifest file sizes exceed the supported Int64 total.");
        }
        if (manifest.Indexes is null)
            errors.Add("Backup manifest is missing its index list.");
        else
        {
            foreach (BackupIndexEntry index in manifest.Indexes)
            {
                if (index is null)
                {
                    errors.Add("Backup manifest index list contains a null entry.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(index.Model)
                    || string.IsNullOrWhiteSpace(index.Owner)
                    || string.IsNullOrWhiteSpace(index.Name)
                    || string.IsNullOrWhiteSpace(index.Kind))
                {
                    errors.Add("Backup manifest index entry contains an empty model, owner, name, or kind.");
                }
            }
        }

        BackupGraphCatalogEntry? graphCatalog = manifest.Models.GraphCatalog;
        if (manifest.FormatVersion >= 2 && graphCatalog is null)
            errors.Add("Backup manifest v2 is missing models.graphCatalog.");
        if (graphCatalog is not null)
        {
            if (graphCatalog.Graphs is null)
                errors.Add("Backup manifest Graph catalog is missing its graph list.");
            if (graphCatalog.Revision < 0)
                errors.Add("Backup manifest Graph catalog revision cannot be negative.");
        }

        if (manifest.FormatVersion < 2)
        {
            bool containsGraphContent = graphCatalog is not null
                || manifest.Files?.Any(static entry => entry is not null
                    && !string.IsNullOrWhiteSpace(entry.Path)
                    && (entry.Kind is BackupFileKind.GraphCatalog or BackupFileKind.GraphData
                        || NormalizeRelativePath(entry.Path).StartsWith(
                            TsdbPaths.GraphsDirName + "/",
                            StringComparison.OrdinalIgnoreCase))) == true
                || manifest.Indexes?.Any(static index => index is not null
                    && string.Equals(index.Model, "graph", StringComparison.Ordinal)) == true;
            if (containsGraphContent)
                errors.Add("Graph backups require manifest format version 2 or later.");
            return;
        }

        if (graphCatalog?.Graphs is not null && manifest.Indexes is not null)
            ValidateGraphCatalogContract(graphCatalog, manifest.Indexes, errors);

        if (manifest.Files is null)
            return;
        foreach (BackupFileEntry entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue;
            string normalized = NormalizeRelativePath(entry.Path);
            BackupFileKind? expectedKind = GetGraphFileKind(normalized);
            bool declaredGraph = entry.Kind is BackupFileKind.GraphCatalog or BackupFileKind.GraphData;
            if (expectedKind is null && !declaredGraph)
                continue;
            if (expectedKind != entry.Kind)
            {
                errors.Add(
                    $"Graph backup file classification does not match its path: '{entry.Path}' "
                    + $"(declared {entry.Kind}, expected {expectedKind?.ToString() ?? "non-Graph"}).");
            }
        }

        if (graphCatalog?.Graphs is not null)
            ValidateGraphFileOwnership(graphCatalog, manifest.Files, errors);
    }

    private static void ValidateGraphFileOwnership(
        BackupGraphCatalogEntry graphCatalog,
        IReadOnlyList<BackupFileEntry> files,
        List<string> errors)
    {
        var storageIds = graphCatalog.Graphs
            .Where(static graph => graph is not null && graph.StorageId != Guid.Empty)
            .Select(static graph => graph.StorageId)
            .ToHashSet();
        var markerStorageIds = new HashSet<Guid>();
        int graphCatalogFiles = 0;
        foreach (BackupFileEntry entry in files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path))
                continue;
            string normalized = NormalizeRelativePath(entry.Path);
            BackupFileKind? kind = GetGraphFileKind(normalized);
            if (kind == BackupFileKind.GraphCatalog)
            {
                graphCatalogFiles++;
                if (!entry.Required)
                    AddGraphVerificationError(errors, "The Graph catalog backup file must be required.");
                continue;
            }
            if (kind != BackupFileKind.GraphData)
                continue;

            if (!TryGetGraphStorageId(normalized, out Guid storageId))
            {
                AddGraphVerificationError(
                    errors,
                    $"Graph data path does not contain a valid storage ID: '{entry.Path}'.");
                continue;
            }
            if (!storageIds.Contains(storageId))
            {
                AddGraphVerificationError(
                    errors,
                    $"Graph data path '{entry.Path}' is not owned by a graph in models.graphCatalog.");
            }
            if (!entry.Required)
            {
                AddGraphVerificationError(
                    errors,
                    $"Graph data backup file '{entry.Path}' must be required.");
            }

            string markerPath = TsdbPaths.GraphsDirName
                + "/" + TsdbPaths.GraphStoresDirName
                + "/" + storageId.ToString("N")
                + "/" + GraphStore.MarkerFileName;
            if (string.Equals(normalized, markerPath, StringComparison.OrdinalIgnoreCase))
                markerStorageIds.Add(storageId);
        }

        if (graphCatalogFiles > 1)
            AddGraphVerificationError(errors, "Backup manifest contains duplicate Graph catalog files.");
        if ((graphCatalog.Revision > 0 || storageIds.Count > 0) && graphCatalogFiles != 1)
            AddGraphVerificationError(errors, "Backup manifest is missing its required Graph catalog file.");
        foreach (Guid storageId in storageIds)
        {
            if (!markerStorageIds.Contains(storageId))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest is missing Graph store marker for storage ID '{storageId:N}'.");
            }
        }
    }

    private static bool TryGetGraphStorageId(string normalizedPath, out Guid storageId)
    {
        string prefix = TsdbPaths.GraphsDirName + "/" + TsdbPaths.GraphStoresDirName + "/";
        ReadOnlySpan<char> remainder = normalizedPath.AsSpan(prefix.Length);
        int separator = remainder.IndexOf('/');
        ReadOnlySpan<char> storageSegment = separator < 0 ? remainder : remainder[..separator];
        return Guid.TryParseExact(storageSegment, "N", out storageId);
    }

    private static bool IsSha256(string? value)
    {
        if (value is not { Length: 64 })
            return false;
        try
        {
            return Convert.FromHexString(value).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static long SumFileSizes(IReadOnlyList<BackupFileEntry>? files)
    {
        if (files is null)
            return 0;

        long total = 0;
        foreach (BackupFileEntry entry in files)
        {
            if (entry is null || entry.SizeBytes <= 0)
                continue;
            if (entry.SizeBytes > long.MaxValue - total)
                return long.MaxValue;
            total += entry.SizeBytes;
        }
        return total;
    }

    private static bool FileSizesOverflow(IReadOnlyList<BackupFileEntry> files)
    {
        long total = 0;
        foreach (BackupFileEntry entry in files)
        {
            if (entry is null || entry.SizeBytes <= 0)
                continue;
            if (entry.SizeBytes > long.MaxValue - total)
                return true;
            total += entry.SizeBytes;
        }
        return false;
    }

    private static void ValidateGraphCatalogContract(
        BackupGraphCatalogEntry graphCatalog,
        IReadOnlyList<BackupIndexEntry> topLevelIndexes,
        List<string> errors)
    {
        IReadOnlyList<BackupGraphIndexEntry> requiredIndexes = GraphIndexes();
        var requiredKinds = requiredIndexes
            .Select(static index => index.Kind)
            .ToHashSet(StringComparer.Ordinal);
        var graphNames = new HashSet<string>(StringComparer.Ordinal);
        var storageIds = new HashSet<Guid>();
        var validGraphs = new List<BackupGraphEntry>(graphCatalog.Graphs.Count);
        foreach (BackupGraphEntry graph in graphCatalog.Graphs)
        {
            if (graph is null)
            {
                AddGraphVerificationError(errors, "Backup manifest Graph catalog contains a null graph entry.");
                continue;
            }

            bool validIdentity = true;
            if (string.IsNullOrWhiteSpace(graph.Name))
            {
                AddGraphVerificationError(errors, "Backup manifest Graph name cannot be null or whitespace.");
                validIdentity = false;
            }
            else
            {
                try
                {
                    GraphDefinition.ValidateName(graph.Name);
                }
                catch (ArgumentException exception)
                {
                    AddGraphVerificationError(errors, "Backup manifest Graph name is invalid: " + exception.Message);
                    validIdentity = false;
                }
            }
            if (graph.StorageId == Guid.Empty)
            {
                AddGraphVerificationError(errors, $"Backup manifest Graph '{graph.Name}' has an empty storage ID.");
                validIdentity = false;
            }
            if (graph.RecordFormatVersion != GraphDefinition.CurrentRecordFormatVersion)
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' has unsupported record format version {graph.RecordFormatVersion}.");
                validIdentity = false;
            }
            if (!graph.CheckpointedDuringBackup)
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' was not checkpointed during backup.");
            }
            if (graph.Name is not null && !graphNames.Add(graph.Name))
            {
                AddGraphVerificationError(errors, $"Backup manifest contains duplicate Graph name '{graph.Name}'.");
                validIdentity = false;
            }
            if (graph.StorageId != Guid.Empty && !storageIds.Add(graph.StorageId))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest contains duplicate Graph storage ID '{graph.StorageId:N}'.");
                validIdentity = false;
            }

            ValidatePerGraphIndexes(graph, requiredKinds, errors);
            if (validIdentity)
                validGraphs.Add(graph);
        }

        ValidateTopLevelGraphIndexes(validGraphs, requiredKinds, topLevelIndexes, errors);
    }

    private static void ValidatePerGraphIndexes(
        BackupGraphEntry graph,
        IReadOnlySet<string> requiredKinds,
        List<string> errors)
    {
        if (graph.Indexes is null)
        {
            AddGraphVerificationError(errors, $"Backup manifest Graph '{graph.Name}' is missing its index summary.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (BackupGraphIndexEntry index in graph.Indexes)
        {
            if (index is null)
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' contains a null index entry.");
                continue;
            }
            if (!requiredKinds.Contains(index.Kind))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' contains unknown index kind '{index.Kind}'.");
                continue;
            }
            if (!seen.Add(index.Kind))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' contains duplicate index kind '{index.Kind}'.");
            }
            if (!index.Included || index.Rebuildable)
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' index '{index.Kind}' must be included and non-rebuildable.");
            }
        }

        foreach (string requiredKind in requiredKinds)
        {
            if (!seen.Contains(requiredKind))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest Graph '{graph.Name}' is missing index kind '{requiredKind}'.");
            }
        }
    }

    private static void ValidateTopLevelGraphIndexes(
        IReadOnlyList<BackupGraphEntry> graphs,
        IReadOnlySet<string> requiredKinds,
        IReadOnlyList<BackupIndexEntry> indexes,
        List<string> errors)
    {
        var graphsByName = graphs.ToDictionary(static graph => graph.Name, StringComparer.Ordinal);
        var seen = new HashSet<(string Owner, string Kind)>();
        foreach (BackupIndexEntry index in indexes)
        {
            if (index is null)
            {
                AddGraphVerificationError(errors, "Backup manifest contains a null top-level index entry.");
                continue;
            }
            if (!string.Equals(index.Model, "graph", StringComparison.Ordinal))
                continue;
            if (string.IsNullOrWhiteSpace(index.Owner)
                || string.IsNullOrWhiteSpace(index.Name)
                || string.IsNullOrWhiteSpace(index.Kind))
            {
                AddGraphVerificationError(errors, "Backup manifest contains a malformed top-level Graph index entry.");
                continue;
            }
            if (!graphsByName.TryGetValue(index.Owner, out BackupGraphEntry? graph)
                || !requiredKinds.Contains(index.Kind)
                || !string.Equals(index.Name, index.Kind, StringComparison.Ordinal))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest contains an unexpected top-level Graph index '{index.Owner}/{index.Name}'.");
                continue;
            }

            if (!seen.Add((index.Owner, index.Kind)))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest contains duplicate top-level Graph index '{index.Owner}/{index.Kind}'.");
            }
            string expectedPath = TsdbPaths.GraphsDirName
                + "/" + TsdbPaths.GraphStoresDirName
                + "/" + graph.StorageId.ToString("N");
            if (!index.Included
                || index.Rebuildable
                || !string.Equals(
                    NormalizeRelativePath(index.RelativePath ?? string.Empty),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddGraphVerificationError(
                    errors,
                    $"Backup manifest top-level Graph index '{index.Owner}/{index.Kind}' has an invalid lifecycle or path.");
            }
        }

        foreach (BackupGraphEntry graph in graphs)
        {
            foreach (string kind in requiredKinds)
            {
                if (!seen.Contains((graph.Name, kind)))
                {
                    AddGraphVerificationError(
                        errors,
                        $"Backup manifest is missing top-level Graph index '{graph.Name}/{kind}'.");
                }
            }
        }
    }

    private static string ResolveManifestPath(string rootDirectory, string relativePath)
    {
        try
        {
            string normalized = ValidateManifestRelativePath(relativePath);

            string root = NormalizeFullDirectoryPath(rootDirectory);
            string path = Path.GetFullPath(Path.Combine(root, normalized));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"备份 manifest 路径越界：{relativePath}");
            }

            return path;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("备份 manifest 包含无效路径。", exception);
        }
    }

    private static string ValidateManifestRelativePath(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string[] segments = normalized.Split('/');
        if (Path.IsPathRooted(normalized)
            || segments.Any(static part => part == ".."))
        {
            throw new InvalidDataException($"备份 manifest 包含不安全路径：{relativePath}");
        }
        if (!string.Equals(relativePath, normalized, StringComparison.Ordinal)
            || segments.Any(static part => part.Length == 0
                || part == "."
                || part.EndsWith('.')
                || part.EndsWith(' ')))
        {
            throw new InvalidDataException($"备份 manifest 包含非规范路径：{relativePath}");
        }
        return normalized;
    }

    private static bool IsSupportedFormatVersion(int formatVersion)
        => formatVersion >= BackupManifest.MinimumSupportedFormatVersion
            && formatVersion <= BackupManifest.CurrentFormatVersion;

    private static void VerifyGraphState(
        string databaseRoot,
        BackupManifest manifest,
        List<string> errors,
        bool isolateSource)
    {
        BackupGraphCatalogEntry? expectedCatalog = manifest.Models.GraphCatalog;
        if (expectedCatalog is null)
        {
            if (manifest.FormatVersion >= 2)
                AddGraphVerificationError(errors, "Backup manifest v2 is missing models.graphCatalog.");
            return;
        }
        if (expectedCatalog.Graphs is null)
        {
            AddGraphVerificationError(errors, "Backup manifest Graph catalog is missing its graph list.");
            return;
        }
        if (expectedCatalog.Revision < 0)
        {
            AddGraphVerificationError(errors, "Backup manifest Graph catalog revision cannot be negative.");
            return;
        }

        string graphRoot = TsdbPaths.GraphsDir(databaseRoot);
        if (expectedCatalog.Revision == 0
            && expectedCatalog.Graphs.Count == 0
            && !File.Exists(TsdbPaths.GraphCatalogPath(databaseRoot)))
        {
            return;
        }

        string? temporaryGraphRoot = null;
        try
        {
            if (isolateSource)
            {
                temporaryGraphRoot = Path.Combine(
                    Path.GetTempPath(),
                    "sonnetdb-graph-backup-verify-" + Guid.NewGuid().ToString("N"));
                CopyGraphFiles(databaseRoot, temporaryGraphRoot, manifest);
                graphRoot = temporaryGraphRoot;
            }

            using var manager = new GraphManager(graphRoot, KvOptions.Default);
            GraphCatalogState actualCatalog = manager.Catalog.CaptureState();
            if (actualCatalog.Revision != expectedCatalog.Revision)
            {
                AddGraphVerificationError(
                    errors,
                    $"Graph catalog revision mismatch: expected {expectedCatalog.Revision}, actual {actualCatalog.Revision}.");
            }

            var expectedByName = expectedCatalog.Graphs.ToDictionary(
                static graph => graph.Name,
                StringComparer.Ordinal);
            var actualByName = actualCatalog.Definitions.ToDictionary(
                static graph => graph.Name,
                StringComparer.Ordinal);
            foreach (BackupGraphEntry expected in expectedCatalog.Graphs)
            {
                if (!expected.CheckpointedDuringBackup)
                {
                    AddGraphVerificationError(
                        errors,
                        $"Graph '{expected.Name}' was not checkpointed during backup.");
                }
                if (!actualByName.TryGetValue(expected.Name, out GraphDefinition? actual))
                {
                    AddGraphVerificationError(errors, $"Graph '{expected.Name}' is missing from restored catalog.");
                    continue;
                }
                if (actual.StorageId != expected.StorageId
                    || actual.RecordFormatVersion != expected.RecordFormatVersion)
                {
                    AddGraphVerificationError(
                        errors,
                        $"Graph '{expected.Name}' catalog identity or record version does not match the manifest.");
                    continue;
                }

                GraphStore store = manager.Open(expected.Name);
                GraphInvariantReport report = GraphInvariantChecker.Check(store);
                if (report.IsValid)
                    continue;

                AddGraphVerificationError(
                    errors,
                    $"Graph '{expected.Name}' invariant check failed: complete={report.IsComplete}, issues={report.TotalIssueCount}.");
                foreach (GraphInvariantIssue issue in report.Issues)
                {
                    AddGraphVerificationError(
                        errors,
                        $"Graph '{expected.Name}' {issue.Kind}: {issue.Message}");
                }
            }

            foreach (GraphDefinition actual in actualCatalog.Definitions)
            {
                if (!expectedByName.ContainsKey(actual.Name))
                {
                    AddGraphVerificationError(
                        errors,
                        $"Graph '{actual.Name}' exists in the restored catalog but not in the manifest summary.");
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or ArgumentException)
        {
            AddGraphVerificationError(errors, "Graph reopen failed: " + exception.Message);
        }
        finally
        {
            if (temporaryGraphRoot is not null)
                DeleteVerificationDirectory(temporaryGraphRoot, errors);
        }
    }

    private static void AddGraphVerificationError(List<string> errors, string error)
    {
        if (errors.Count < MaximumGraphVerificationErrors)
            errors.Add(error);
    }

    private static void CopyGraphFiles(
        string databaseRoot,
        string destinationGraphRoot,
        BackupManifest manifest)
    {
        Directory.CreateDirectory(destinationGraphRoot);
        string graphPrefix = TsdbPaths.GraphsDirName + "/";
        foreach (BackupFileEntry entry in manifest.Files)
        {
            string normalized = NormalizeRelativePath(entry.Path);
            BackupFileKind? expectedKind = GetGraphFileKind(normalized);
            bool declaredGraph = entry.Kind is BackupFileKind.GraphCatalog or BackupFileKind.GraphData;
            if (expectedKind is null && !declaredGraph)
                continue;
            if (expectedKind != entry.Kind)
            {
                throw new InvalidDataException(
                    $"Graph backup file classification does not match its path: '{entry.Path}'.");
            }

            string sourcePath = ResolveManifestPath(databaseRoot, entry.Path);
            string relativeGraphPath = normalized[graphPrefix.Length..];
            string destinationPath = Path.Combine(destinationGraphRoot, relativeGraphPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static BackupFileKind? GetGraphFileKind(string normalizedPath)
    {
        if (string.Equals(
                normalizedPath,
                TsdbPaths.GraphsDirName + "/" + TsdbPaths.GraphCatalogFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return BackupFileKind.GraphCatalog;
        }
        if (normalizedPath.StartsWith(
                TsdbPaths.GraphsDirName + "/" + TsdbPaths.GraphStoresDirName + "/",
                StringComparison.OrdinalIgnoreCase))
        {
            return BackupFileKind.GraphData;
        }
        return null;
    }

    private static void DeleteVerificationDirectory(string directory, List<string> errors)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddGraphVerificationError(
                errors,
                $"Temporary Graph verification directory cleanup failed: {exception.Message}");
        }
    }

    private static string TableIndexKind(SonnetDB.Tables.TableIndex index)
        => string.IsNullOrWhiteSpace(index.JsonPath)
            ? index.IsUnique ? "unique_secondary" : "secondary"
            : "json_path";

    private static string DocumentIndexKind(DocumentPathIndex index)
    {
        if (index.IsTtl)
            return "ttl";
        if (index.IsUnique)
            return "unique_document";
        if (index.PartialFilter is not null)
            return "partial_document";
        if (index.IsSparse)
            return "sparse_document";
        return index.Paths.Count > 1 ? "compound_document" : "document";
    }

    private static BackupIndexRebuildEntry FailedIndex(string model, string owner, string name, string kind, string message)
        => new(model, owner, name, kind, "failed", message);

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string EncodeName(string name)
        => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)).ToLowerInvariant();

    private readonly record struct RestoreTargetEvaluation(bool Exists, bool Empty, bool IsAllowed);
}

internal static class BackupTsdbExtensions
{
    public static BackupManifest CreateBackup(
        this Tsdb tsdb,
        BackupCreateOptions options,
        Func<Tsdb, BackupCreateOptions, IReadOnlyList<string>, BackupManifest> afterCheckpoint)
    {
        return tsdb.CreateConsistentBackup(options, afterCheckpoint);
    }
}
