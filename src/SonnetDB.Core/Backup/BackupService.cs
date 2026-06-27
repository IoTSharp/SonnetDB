using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;

namespace SonnetDB.Backup;

/// <summary>
/// SonnetDB 多模型备份、校验与离线恢复服务。
/// </summary>
public sealed class BackupService
{
    private sealed record BackupCheckpointInfo(IReadOnlySet<string> CheckpointedKeyspaces);

    private static readonly string[] _transientSuffixes =
    [
        ".tmp",
        ".temp",
    ];

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

        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
            errors.Add($"Unsupported manifest format version {manifest.FormatVersion}.");

        int checkedFiles = 0;
        foreach (var entry in manifest.Files)
        {
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
            var info = new FileInfo(path);
            if (info.Length != entry.SizeBytes)
                errors.Add($"Size mismatch: {entry.Path} expected {entry.SizeBytes}, actual {info.Length}");

            string actualHash = ComputeSha256(path);
            if (!string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                errors.Add($"SHA-256 mismatch: {entry.Path}");
        }

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
            manifest?.Files.Count ?? 0,
            manifest?.Files.Sum(static f => f.SizeBytes) ?? 0,
            manifest?.Indexes.Count ?? 0,
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
        PrepareTargetDirectory(options.TargetDirectory, options.Overwrite);
        foreach (var entry in manifest.Files)
        {
            string source = ResolveManifestPath(options.BackupDirectory, entry.Path);
            string target = ResolveManifestPath(options.TargetDirectory, entry.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }

        return manifest;
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

        var includePredicate = CreateIncludePredicate(options.IncludeFullTextIndexes);
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
            "SonnetDB/MM9",
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

    private static Predicate<string> CreateIncludePredicate(bool includeFullTextIndexes)
    {
        return relativePath =>
        {
            string normalized = NormalizeRelativePath(relativePath);
            if (normalized == BackupManifest.FileName)
                return false;

            string fileName = Path.GetFileName(normalized);
            for (int i = 0; i < _transientSuffixes.Length; i++)
            {
                if (fileName.EndsWith(_transientSuffixes[i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (includeFullTextIndexes)
                return true;

            return !normalized.StartsWith(
                TsdbPaths.DocumentsDirName + "/fulltext/",
                StringComparison.OrdinalIgnoreCase);
        };
    }

    private static IReadOnlyList<string> CopyDatabaseFiles(
        string sourceRoot,
        string destinationRoot,
        Predicate<string> include)
    {
        var copied = new List<string>();
        foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            if (!include(relative))
                continue;

            string target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFile(source, target);
            copied.Add(relative);
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

        return new BackupModelSummary(measurements, tables, openedKeyspaces, documents);
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

        return indexes.AsReadOnly();
    }

    private static BackupFileKind Classify(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string fileName = Path.GetFileName(normalized);

        if (string.Equals(fileName, TsdbPaths.CatalogFileName, StringComparison.OrdinalIgnoreCase))
            return BackupFileKind.Catalog;
        if (string.Equals(fileName, TsdbPaths.MeasurementSchemaFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, TableSchemaCodec.FileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, DocumentCollectionSchemaCodec.FileName, StringComparison.OrdinalIgnoreCase))
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

    private static void PrepareTargetDirectory(string target, bool overwrite)
    {
        if (Directory.Exists(target))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(target).Any();
            if (!overwrite || !empty)
                throw new IOException($"恢复目标目录 '{target}' 已存在且不允许覆盖。");
            return;
        }

        Directory.CreateDirectory(target);
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
        if (manifest.FormatVersion != BackupManifest.CurrentFormatVersion)
            errors.Add($"Unsupported manifest format version {manifest.FormatVersion}.");

        foreach (var entry in manifest.Files)
        {
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

    private static string ResolveManifestPath(string rootDirectory, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(static part => part == ".."))
            throw new InvalidDataException($"备份 manifest 包含不安全路径：{relativePath}");

        string root = NormalizeFullDirectoryPath(rootDirectory);
        string path = Path.GetFullPath(Path.Combine(root, normalized));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"备份 manifest 路径越界：{relativePath}");
        }

        return path;
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
