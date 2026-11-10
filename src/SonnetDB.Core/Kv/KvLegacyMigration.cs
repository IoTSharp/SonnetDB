using System.Globalization;
using SonnetDB.Kv.Lsm;

namespace SonnetDB.Kv;

/// <summary>
/// 打开时把 legacy keyspace 迁移到 LSM（决策 D2，K7）。
/// </summary>
/// <remarks>
/// <para>
/// 独立 keyspace：在 <c>.lsm-migrating</c> 中把 legacy 的全部可见条目按 key 顺序直接写成最底层 SST（保留每条记录的版本与 TTL、
/// LastSequence 与 generation），写好 manifest 后再切换：先写切换标记，把 legacy 文件移入 <c>legacy-&lt;sequence&gt;</c>，
/// 再把 LSM 文件移入根目录（CURRENT 最后），最后写 v3 布局标记。切换标记之前崩溃，下次打开删除暂存目录、legacy 不受影响；
/// 之后崩溃，下次打开补完剩余的移动。
/// </para>
/// <para>
/// 关系表：把 legacy 条目导入共享实例的新编号（新提交 sequence），同步 WAL 后写待处理日志，把 legacy 文件移入
/// <c>legacy-&lt;sequence&gt;</c>，写 v3 共享标记后删除日志。日志之前崩溃，导入的编号成为孤立数据并在下次打开时回收。
/// </para>
/// <para>
/// 只移动 legacy KV 自身的文件（generation、WAL、快照、段、清理清单），表目录与文档集合目录中的其他附属文件保持原位。
/// 可用空间不足（legacy 大小 × 1.2 + 64 MiB）时跳过迁移并继续以 legacy 打开。保留目录超过
/// <see cref="KvOptions.LegacyMigrationRetention"/> 后在打开时删除；保留期内且迁移后没有新写入时可以回滚。
/// </para>
/// </remarks>
internal static class KvLegacyMigration
{
    /// <summary>独立迁移的暂存目录名。</summary>
    internal const string StagingDirectoryName = ".lsm-migrating";

    /// <summary>保留旧文件的目录名前缀。</summary>
    internal const string RetainedPrefix = "legacy-";

    /// <summary>保留目录中的迁移信息文件。</summary>
    internal const string InfoFileName = "migration.info";

    /// <summary>独立迁移切换开始标记（位于暂存目录）。</summary>
    internal const string SwitchingFileName = "switching";

    /// <summary>表迁移待处理日志（位于表目录）。</summary>
    internal const string PendingFileName = "lsm-migration.pending";

    private const long SpaceMarginBytes = 64L * 1024 * 1024;
    private const int ImportBatchEntries = 1024;
    private const long ImportBatchBytes = 4L * 1024 * 1024;
    private const long TargetSstBytes = 64L * 1024 * 1024;

    /// <summary>迁移扫描：页字节预算放宽到单条大值也能读出（迁移必须复制全部记录）。</summary>
    private static readonly KvRangeScanOptions MigrationScan = new() { PageSize = 256, MaxPageBytes = 1024 * 1024 * 1024 };

    /// <summary>测试钩子：在指定阶段抛出异常即模拟在该点崩溃。</summary>
    internal static Action<string, string>? PhaseTestHook { get; set; }

    /// <summary>legacy KV 自身拥有的根目录条目（迁移时整体移动）。</summary>
    private static readonly string[] LegacyEntries =
    [
        KvGenerationFile.FileName,
        "wal",
        "snapshots",
        "segments",
        KvCleanupManifest.FileName,
    ];

    // ───────────────────────────── 独立 keyspace ─────────────────────────────

    /// <summary>补完或放弃上一次中断的独立迁移。必须在取得目录租约之前、选择引擎之前调用。</summary>
    internal static void RecoverStandalone(string rootDirectory)
    {
        string staging = Path.Combine(rootDirectory, StagingDirectoryName);
        if (!Directory.Exists(staging))
            return;
        string switching = Path.Combine(staging, SwitchingFileName);
        if (!File.Exists(switching))
        {
            // 切换尚未开始：legacy 文件仍在原位且完整，丢弃暂存结果，稍后重新迁移。
            Directory.Delete(staging, recursive: true);
            return;
        }
        MigrationInfo info = MigrationInfo.Read(switching);
        CompleteStandaloneSwitch(rootDirectory, staging, info);
    }

    /// <summary>
    /// 若目录含 legacy 数据且空间足够，迁移为独立 LSM 布局。返回 true 表示已迁移（随后按 LSM 打开）。
    /// </summary>
    internal static bool TryMigrateStandalone(string name, string rootDirectory, KvOptions options, out string? skippedReason)
    {
        skippedReason = null;
        if (!KvLsmKeyspaceBackend.HasLegacyData(rootDirectory))
            return false;
        if (!HasRoomFor(rootDirectory, out skippedReason))
            return false;

        string staging = Path.Combine(rootDirectory, StagingDirectoryName);
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        MigrationInfo info;
        KvOptions legacyOptions = options with { StorageEngine = KvStorageEngine.Legacy, LegacyMigration = KvLegacyMigrationMode.Disabled };
        using (KvKeyspace legacy = KvKeyspace.Open(name, rootDirectory, legacyOptions))
        {
            info = new MigrationInfo("standalone", legacy.LastSequence, legacy.Generation, StorageId: 0, DateTime.UtcNow.Ticks);
            BuildStandaloneLayout(staging, legacy, info, options);
        }
        PhaseTestHook?.Invoke("standalone-built", rootDirectory);

        // 切换点之前：写入并 fsync 切换标记；从这里开始，中断后只会向前补完。
        info.Write(Path.Combine(staging, SwitchingFileName));
        SonnetDB.Wal.DirectoryFsync.FlushRequired(staging);
        PhaseTestHook?.Invoke("standalone-switching", rootDirectory);
        CompleteStandaloneSwitch(rootDirectory, staging, info);
        return true;
    }

    private static void BuildStandaloneLayout(string staging, KvKeyspace legacy, MigrationInfo info, KvOptions options)
    {
        Directory.CreateDirectory(staging);
        using KvVersionSet versions = KvVersionSet.Create(staging, info.Generation);
        var edit = new KvVersionEdit { LastSequence = info.Sequence };
        KvSstWriterOptions writerOptions = KvSstWriterOptions.Default with { Generation = info.Generation };
        KvSstWriter? writer = null;
        long fileNumber = 0;
        string? path = null;

        void FinishFile()
        {
            if (writer is null)
                return;
            KvSstProperties properties = writer.Finish();
            writer.Dispose();
            writer = null;
            edit.AddedFiles.Add(KvLsmFileMetadata.FromProperties(fileNumber, KvVersion.LevelCount - 1, new FileInfo(path!).Length, properties));
        }

        try
        {
            using KvReadSnapshot snapshot = legacy.AcquireReadSnapshot();
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(MigrationScan);
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
                if (page.Count == 0)
                    break;
                foreach (KvEntry entry in page)
                {
                    if (writer is null)
                    {
                        fileNumber = versions.NewFileNumber();
                        path = KvLsmPaths.SstPath(staging, fileNumber);
                        writer = new KvSstWriter(path, writerOptions);
                    }
                    long sequence = Math.Clamp(entry.Version, 1, Math.Max(1, info.Sequence));
                    writer.Add(
                        KvInternalKey.Encode(entry.Key.Span, sequence, KvEntryKind.Put),
                        entry.Value.Span,
                        entry.ExpiresAtUtc?.UtcTicks ?? 0);
                    if (writer.EstimatedFileSize >= TargetSstBytes)
                        FinishFile();
                }
                if (cursor.IsExhausted)
                    break;
            }
            FinishFile();
            versions.LogAndApply(edit);
        }
        finally
        {
            writer?.Dispose();
        }
        _ = options;
    }

    private static void CompleteStandaloneSwitch(string rootDirectory, string staging, MigrationInfo info)
    {
        string retained = Path.Combine(rootDirectory, RetainedName(info.Sequence));
        Directory.CreateDirectory(retained);
        MoveLegacyEntries(rootDirectory, retained);
        PhaseTestHook?.Invoke("standalone-legacy-moved", rootDirectory);

        // LSM 文件移入根目录：sst 与 MANIFEST 先行，CURRENT 最后（之后根目录即为 LSM 布局）。
        foreach (string entry in Directory.EnumerateFileSystemEntries(staging).ToArray())
        {
            string fileName = Path.GetFileName(entry);
            if (fileName is KvLsmPaths.CurrentFileName or SwitchingFileName)
                continue;
            MoveEntry(entry, Path.Combine(rootDirectory, fileName));
        }
        string stagedCurrent = Path.Combine(staging, KvLsmPaths.CurrentFileName);
        if (File.Exists(stagedCurrent))
            File.Move(stagedCurrent, KvLsmPaths.CurrentPath(rootDirectory), overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(rootDirectory);
        PhaseTestHook?.Invoke("standalone-current-moved", rootDirectory);

        KvLsmLayoutMarkerFile.Write(rootDirectory, new KvLsmLayoutMarker(KvLsmLayoutKind.Standalone, 0));
        info.Write(Path.Combine(retained, InfoFileName));
        SonnetDB.Wal.DirectoryFsync.FlushRequired(retained);
        Directory.Delete(staging, recursive: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(rootDirectory);
    }

    /// <summary>
    /// 回滚独立 keyspace 的迁移：只在保留目录仍在、且迁移后没有任何新写入（LastSequence 未变）时允许。
    /// LSM 文件移入 <c>lsm-rolledback-&lt;ticks&gt;</c>，legacy 文件移回根目录。调用方必须保证目录未被打开。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有可回滚的迁移，或迁移后已有新写入。</exception>
    internal static void RollbackStandalone(string rootDirectory)
    {
        string retained = FindRetained(rootDirectory)
            ?? throw new InvalidOperationException($"KV directory '{rootDirectory}' has no retained legacy migration to roll back.");
        MigrationInfo info = MigrationInfo.Read(Path.Combine(retained, InfoFileName));
        if (info.Kind != "standalone")
            throw new InvalidOperationException("Only standalone KV migrations can be rolled back here; shared tables use their table manager.");
        long sequence;
        using (KvLsmEngine engine = KvLsmEngine.Open(rootDirectory, new KvLsmEngineOptions { BackgroundFlush = false }))
            sequence = engine.LastSequence;
        if (sequence != info.Sequence)
        {
            throw new InvalidOperationException(
                $"KV directory '{rootDirectory}' has new writes after migration (sequence {sequence} > {info.Sequence}); export the data instead of rolling back.");
        }

        string rolledBack = Path.Combine(rootDirectory, "lsm-rolledback-" + DateTime.UtcNow.Ticks.ToString("D20", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(rolledBack);
        foreach (string entry in Directory.EnumerateFileSystemEntries(rootDirectory).ToArray())
        {
            string fileName = Path.GetFileName(entry);
            if (fileName == KvLsmPaths.CurrentFileName)
                continue;
            if (fileName is KvLsmPaths.SstDirectoryName or "wal" || fileName == KvGenerationFile.FileName
                || fileName.StartsWith(KvLsmPaths.ManifestPrefix, StringComparison.Ordinal))
            {
                MoveEntry(entry, Path.Combine(rolledBack, fileName));
            }
        }
        // CURRENT 最先移走：一旦它不在根目录，目录就不再被识别为 LSM 布局。
        File.Move(KvLsmPaths.CurrentPath(rootDirectory), Path.Combine(rolledBack, KvLsmPaths.CurrentFileName));
        foreach (string entry in Directory.EnumerateFileSystemEntries(retained).ToArray())
        {
            string fileName = Path.GetFileName(entry);
            if (fileName != InfoFileName)
                MoveEntry(entry, Path.Combine(rootDirectory, fileName));
        }
        Directory.Delete(retained, recursive: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(rootDirectory);
    }

    // ───────────────────────────── 关系表（共享实例） ─────────────────────────────

    /// <summary>补完上一次中断在导入之后的表迁移。表目录未被打开时调用。</summary>
    internal static void RecoverShared(string tableDirectory)
    {
        string pending = Path.Combine(tableDirectory, PendingFileName);
        if (!File.Exists(pending))
            return;
        MigrationInfo info = MigrationInfo.Read(pending);
        CompleteSharedSwitch(tableDirectory, info);
    }

    /// <summary>
    /// 把表目录中的 legacy 数据导入共享实例的新编号并切换表目录。返回新编号；空间不足时返回 null 并给出原因。
    /// </summary>
    internal static long? TryMigrateIntoShared(
        string name,
        string tableDirectory,
        KvLsmStore store,
        KvOptions options,
        KvDiskReadBudget readBudget,
        out string? skippedReason)
    {
        skippedReason = null;
        if (!KvLsmKeyspaceBackend.HasLegacyData(tableDirectory))
            return null;
        if (!HasRoomFor(tableDirectory, out skippedReason))
            return null;

        long storageId = store.Create(name);
        MigrationInfo info;
        KvOptions legacyOptions = options with { StorageEngine = KvStorageEngine.Legacy, LegacyMigration = KvLegacyMigrationMode.Disabled };
        using (KvKeyspace legacy = KvKeyspace.Open(name, tableDirectory, legacyOptions, readBudget))
        {
            long imported = 0;
            using KvReadSnapshot snapshot = legacy.AcquireReadSnapshot();
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(MigrationScan);
            var batch = new List<KvBatchMutation>(ImportBatchEntries);
            long batchBytes = 0;
            while (true)
            {
                IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
                foreach (KvEntry entry in page)
                {
                    batch.Add(new KvBatchMutation(entry.Key.ToArray(), entry.Value.ToArray(), entry.ExpiresAtUtc));
                    batchBytes += entry.Key.Length + entry.Value.Length;
                    if (batch.Count >= ImportBatchEntries || batchBytes >= ImportBatchBytes)
                    {
                        imported = store.Import(storageId, batch);
                        batch.Clear();
                        batchBytes = 0;
                    }
                }
                if (page.Count == 0 || cursor.IsExhausted)
                    break;
            }
            if (batch.Count > 0)
                imported = store.Import(storageId, batch);
            info = new MigrationInfo("shared", legacy.LastSequence, legacy.Generation, storageId, DateTime.UtcNow.Ticks, imported);
        }
        store.Engine.SyncWal();
        PhaseTestHook?.Invoke("shared-imported", tableDirectory);

        // 切换点之前：待处理日志落盘；从这里开始，中断后只会向前补完。
        info.Write(Path.Combine(tableDirectory, PendingFileName));
        SonnetDB.Wal.DirectoryFsync.FlushRequired(tableDirectory);
        PhaseTestHook?.Invoke("shared-pending", tableDirectory);
        CompleteSharedSwitch(tableDirectory, info);
        return storageId;
    }

    private static void CompleteSharedSwitch(string tableDirectory, MigrationInfo info)
    {
        string retained = Path.Combine(tableDirectory, RetainedName(info.Sequence));
        Directory.CreateDirectory(retained);
        MoveLegacyEntries(tableDirectory, retained);
        PhaseTestHook?.Invoke("shared-legacy-moved", tableDirectory);
        KvLsmLayoutMarkerFile.Write(tableDirectory, new KvLsmLayoutMarker(KvLsmLayoutKind.Shared, info.StorageId));
        info.Write(Path.Combine(retained, InfoFileName));
        SonnetDB.Wal.DirectoryFsync.FlushRequired(retained);
        File.Delete(Path.Combine(tableDirectory, PendingFileName));
        SonnetDB.Wal.DirectoryFsync.FlushRequired(tableDirectory);
    }

    /// <summary>
    /// 回滚表迁移：只在保留目录仍在、且导入后该逻辑 keyspace 没有新提交时允许。legacy 文件移回表目录，删除共享编号的数据。
    /// 调用方必须保证该表未被打开。
    /// </summary>
    /// <exception cref="InvalidOperationException">没有可回滚的迁移，或导入后已有新写入。</exception>
    internal static void RollbackShared(string tableDirectory, KvLsmStore store)
    {
        string retained = FindRetained(tableDirectory)
            ?? throw new InvalidOperationException($"Table directory '{tableDirectory}' has no retained legacy migration to roll back.");
        MigrationInfo info = MigrationInfo.Read(Path.Combine(retained, InfoFileName));
        if (info.Kind != "shared")
            throw new InvalidOperationException("The retained migration is not a shared-table migration.");
        if (!store.Contains(info.StorageId) || store.GetLastSequence(info.StorageId) != info.ImportedSequence)
        {
            throw new InvalidOperationException(
                $"Table directory '{tableDirectory}' has new writes after migration; export the data instead of rolling back.");
        }

        // 先恢复 legacy generation 文件（覆盖 v3 标记），目录随即重新被识别为 legacy；再移回其余文件并删除共享数据。
        foreach (string entry in Directory.EnumerateFileSystemEntries(retained).ToArray())
        {
            string fileName = Path.GetFileName(entry);
            if (fileName == KvGenerationFile.FileName)
                File.Move(entry, Path.Combine(tableDirectory, fileName), overwrite: true);
        }
        if (!File.Exists(Path.Combine(tableDirectory, KvGenerationFile.FileName))
            || KvLsmLayoutMarkerFile.TryRead(tableDirectory, out _))
        {
            File.Delete(Path.Combine(tableDirectory, KvGenerationFile.FileName));
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(retained).ToArray())
        {
            string fileName = Path.GetFileName(entry);
            if (fileName != InfoFileName)
                MoveEntry(entry, Path.Combine(tableDirectory, fileName));
        }
        Directory.Delete(retained, recursive: true);
        SonnetDB.Wal.DirectoryFsync.FlushRequired(tableDirectory);
        store.Drop(info.StorageId);
    }

    // ───────────────────────────── 公共 ─────────────────────────────

    /// <summary>删除超过保留期的 <c>legacy-*</c> 目录。</summary>
    internal static void CleanupRetained(string directory, TimeSpan retention)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (string retained in Directory.EnumerateDirectories(directory, RetainedPrefix + "*"))
        {
            string infoPath = Path.Combine(retained, InfoFileName);
            if (!File.Exists(infoPath))
                continue; // 没有完成信息的目录可能来自中断的切换，交给恢复流程
            MigrationInfo info = MigrationInfo.Read(infoPath);
            if (DateTime.UtcNow.Ticks - info.CommittedUtcTicks >= retention.Ticks)
                Directory.Delete(retained, recursive: true);
        }
    }

    private static string RetainedName(long sequence)
        => RetainedPrefix + sequence.ToString("D20", CultureInfo.InvariantCulture);

    private static string? FindRetained(string directory)
        => Directory.Exists(directory)
            ? Directory.EnumerateDirectories(directory, RetainedPrefix + "*")
                .Where(static path => File.Exists(Path.Combine(path, InfoFileName)))
                .OrderByDescending(static path => path, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;

    private static void MoveLegacyEntries(string directory, string retained)
    {
        foreach (string name in LegacyEntries)
        {
            string source = Path.Combine(directory, name);
            if (name == KvGenerationFile.FileName && File.Exists(source) && KvLsmLayoutMarkerFile.TryRead(directory, out _))
                continue; // 已经是新标记（补完流程重入）
            if (File.Exists(source) || Directory.Exists(source))
                MoveEntry(source, Path.Combine(retained, name));
        }
        SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination, overwrite: true);
        }
    }

    private static bool HasRoomFor(string directory, out string? reason)
    {
        reason = null;
        long legacyBytes = 0;
        foreach (string name in LegacyEntries)
        {
            string path = Path.Combine(directory, name);
            if (File.Exists(path))
                legacyBytes += new FileInfo(path).Length;
            else if (Directory.Exists(path))
                legacyBytes += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
        }

        long required = checked((long)(legacyBytes * 1.2) + SpaceMarginBytes);
        long available;
        try
        {
            available = new DriveInfo(Path.GetFullPath(directory)).AvailableFreeSpace;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true; // 无法判断时按有空间处理；写入失败仍由迁移流程本身报告
        }
        if (available >= required)
            return true;
        reason = $"KV legacy migration skipped for '{directory}': {available} bytes available, {required} required.";
        return false;
    }

    private sealed record MigrationInfo(
        string Kind,
        long Sequence,
        long Generation,
        long StorageId,
        long CommittedUtcTicks,
        long ImportedSequence = 0)
    {
        internal void Write(string path)
        {
            string content = string.Join('\n',
                "kind=" + Kind,
                "sequence=" + Sequence.ToString(CultureInfo.InvariantCulture),
                "generation=" + Generation.ToString(CultureInfo.InvariantCulture),
                "storageId=" + StorageId.ToString(CultureInfo.InvariantCulture),
                "committedUtcTicks=" + CommittedUtcTicks.ToString(CultureInfo.InvariantCulture),
                "importedSequence=" + ImportedSequence.ToString(CultureInfo.InvariantCulture)) + "\n";
            string temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }

        internal static MigrationInfo Read(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in File.ReadAllLines(path))
            {
                int separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0)
                    values[line[..separator]] = line[(separator + 1)..];
            }
            try
            {
                return new MigrationInfo(
                    values["kind"],
                    long.Parse(values["sequence"], CultureInfo.InvariantCulture),
                    long.Parse(values["generation"], CultureInfo.InvariantCulture),
                    long.Parse(values["storageId"], CultureInfo.InvariantCulture),
                    long.Parse(values["committedUtcTicks"], CultureInfo.InvariantCulture),
                    values.TryGetValue("importedSequence", out string? imported) ? long.Parse(imported, CultureInfo.InvariantCulture) : 0);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or FormatException or OverflowException)
            {
                throw new InvalidDataException($"KV legacy migration info '{path}' is invalid.", exception);
            }
        }
    }
}
