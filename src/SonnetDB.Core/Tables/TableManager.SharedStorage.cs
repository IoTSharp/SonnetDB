using SonnetDB.Kv;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表的共享 LSM 存储（K6/K7，决策 D4）：一个数据库的所有关系表共用 <c>tables/lsm</c> 下的一个 LSM 实例，
/// 每张表是按 <c>StorageId</c> 前缀划分的逻辑 keyspace。
/// </summary>
/// <remarks>
/// <para>
/// 表目录（<c>tables/rowstore/...</c>）仍然存在，保存生命周期租约、维护标记等附属文件，以及指向共享实例编号的
/// v3 <c>generation.meta</c>。因此 RENAME、CTAS 暂存目录改名、DDL 事务的目录切换都随目录一起携带编号，不移动数据；
/// DROP 删除目录后删除该编号的全部数据（range tombstone）。
/// </para>
/// <para>
/// 只有新建的表在 <see cref="KvOptions.StorageEngine"/> 为 <see cref="KvStorageEngine.Lsm"/> 时进入共享实例；
/// 已有 legacy 数据的表在选择 LSM 且允许迁移时于打开过程中导入共享实例（<see cref="KvLegacyMigration"/>），否则按原布局打开。
/// 共享实例与表存储一起在 DDL 发布前关闭、之后按需重新打开；DDL 事务的撤销副本与工作区一并复制、切换 <c>tables/lsm</c>。
/// </para>
/// </remarks>
public sealed partial class TableManager
{
    /// <summary>共享 LSM 实例相对 tables 根目录的目录名。</summary>
    internal const string SharedStorageDirectoryName = "lsm";

    private readonly object _sharedStoreSync = new();
    private KvLsmStore? _sharedStore;
    private bool _recoveringTransactionJournal;

    /// <summary>共享 LSM 实例目录。</summary>
    internal string SharedStorageDirectory => Path.Combine(_rootDirectory, SharedStorageDirectoryName);

    /// <summary>
    /// 打开表的 KV 存储：附属目录带共享标记时绑定共享实例；新表且选择 LSM 时分配新编号；
    /// 其余情况（legacy 或独立 LSM 数据）沿用独立打开。
    /// </summary>
    /// <param name="keyspaceName">Keyspace 名称。</param>
    /// <param name="tableDirectory">表目录。</param>
    /// <param name="standaloneBudget">独立打开时使用的读取预算；为 null 时使用管理器预算。共享实例始终使用管理器预算。</param>
    private KvKeyspace OpenTableKeyspace(string keyspaceName, string tableDirectory, KvDiskReadBudget? standaloneBudget = null)
    {
        KvLegacyMigration.RecoverRollback(tableDirectory);
        // 先补完上一次中断在导入之后的迁移（导入之前中断的编号由孤立回收处理）。
        KvLegacyMigration.RecoverShared(tableDirectory);
        if (KvLsmLayoutMarkerFile.TryRead(tableDirectory, out KvLsmLayoutMarker marker)
            && marker.FormatVersion == KvLsmLayoutMarkerFile.RollbackVersion)
            throw new InvalidDataException("Table rollback barrier has no completed intent; rerun the offline rollback command before opening the table.");
        if (marker.Kind == KvLsmLayoutKind.Shared)
        {
            KvKeyspace shared = KvKeyspace.OpenShared(keyspaceName, tableDirectory, GetSharedStore(), marker.StorageId, _kvOptions, _diskReadBudget);
            KvLegacyMigration.CleanupRetained(tableDirectory, _kvOptions.LegacyMigrationRetention);
            return shared;
        }

        bool lsm = _kvOptions.StorageEngine == KvStorageEngine.Lsm && !_recoveringTransactionJournal;
        bool fresh = !KvLsmKeyspaceBackend.HasLegacyData(tableDirectory) && !KvLsmKeyspaceBackend.IsLsmLayout(tableDirectory);
        if (!fresh && lsm && _kvOptions.LegacyMigration == KvLegacyMigrationMode.Auto && KvLsmKeyspaceBackend.HasLegacyData(tableDirectory))
        {
            // 打开时迁移（决策 D2）：legacy 表数据导入共享实例；空间不足时继续以 legacy 打开。
            long? migrated = KvLegacyMigration.TryMigrateIntoShared(
                keyspaceName, tableDirectory, GetSharedStore(), _kvOptions, standaloneBudget ?? _diskReadBudget, out _);
            if (migrated is long migratedId)
                return KvKeyspace.OpenShared(keyspaceName, tableDirectory, GetSharedStore(), migratedId, _kvOptions, _diskReadBudget);
        }

        if (!fresh || !lsm)
        {
            // 表的 legacy/独立数据只按原布局打开，不迁移为独立 LSM（表的迁移目标是共享实例）。
            return KvKeyspace.Open(keyspaceName, tableDirectory, _kvOptions with { LegacyMigration = KvLegacyMigrationMode.Disabled },
                standaloneBudget ?? _diskReadBudget);
        }

        KvLsmStore store = GetSharedStore();
        long storageId = store.Create(keyspaceName);
        try
        {
            return KvKeyspace.OpenShared(keyspaceName, tableDirectory, store, storageId, _kvOptions, _diskReadBudget);
        }
        catch
        {
            try { store.Drop(storageId); }
            catch { /* 保留原始失败；孤立编号在下次打开时回收 */ }
            throw;
        }
    }

    /// <summary>读取表目录绑定的共享编号；不是共享表时返回 null。</summary>
    private static long? TryGetSharedStorageId(string tableDirectory)
        => Directory.Exists(tableDirectory)
            && KvLsmLayoutMarkerFile.TryRead(tableDirectory, out KvLsmLayoutMarker marker)
            && marker.Kind == KvLsmLayoutKind.Shared
                ? marker.StorageId
                : null;

    /// <summary>删除表目录之后释放其共享数据（目录先删：崩溃时只留下可回收的孤立编号，不会出现指向已删数据的目录）。</summary>
    private void ReleaseSharedStorage(long? storageId)
    {
        if (storageId is long id)
            GetSharedStore().Drop(id);
    }

    private KvLsmStore GetSharedStore()
    {
        lock (_sharedStoreSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sharedStore ??= KvLsmStore.Open(SharedStorageDirectory, _kvOptions, _diskReadBudget);
        }
    }

    /// <summary>关闭共享实例（所有表存储已关闭）；下一次打开表时重新打开。</summary>
    private void CloseSharedStore()
    {
        KvLsmStore? store;
        lock (_sharedStoreSync)
        {
            store = _sharedStore;
            _sharedStore = null;
        }
        store?.Dispose();
    }

    /// <summary>
    /// 打开前回收孤立编号：共享实例中登记、但没有任何表目录（含 CTAS 暂存目录）指向的编号，
    /// 来自 DROP/CTAS 失败或 DDL 事务切换后被替换的旧数据。
    /// </summary>
    private void CollectOrphanedSharedStorage()
    {
        if (!Directory.Exists(SharedStorageDirectory))
            return;
        var referenced = new HashSet<long>();
        string rowstore = Path.Combine(_rootDirectory, "rowstore");
        if (Directory.Exists(rowstore))
        {
            foreach (string pending in Directory.EnumerateFiles(rowstore, KvLegacyMigration.RollbackPendingFileName, SearchOption.AllDirectories).ToArray())
                KvLegacyMigration.RecoverRollback(Path.GetDirectoryName(pending)!);
            // 先补完导入后中断的迁移：它们的编号此时只记录在待处理日志中，还没有表目录标记指向。
            foreach (string pending in Directory.EnumerateFiles(rowstore, KvLegacyMigration.PendingFileName, SearchOption.AllDirectories).ToArray())
                KvLegacyMigration.RecoverShared(Path.GetDirectoryName(pending)!);
            foreach (string marker in Directory.EnumerateFiles(rowstore, KvGenerationFile.FileName, SearchOption.AllDirectories))
            {
                if (TryGetSharedStorageId(Path.GetDirectoryName(marker)!) is long id)
                    referenced.Add(id);
            }
        }

        KvLsmStore store = GetSharedStore();
        foreach ((long storageId, _) in store.List())
        {
            if (!referenced.Contains(storageId))
                store.Drop(storageId);
        }
        // 回收后关闭：表在首次访问时按需重新打开；若数据库后续打开失败，不会留下未关闭的共享实例。
        CloseSharedStore();
    }
}
