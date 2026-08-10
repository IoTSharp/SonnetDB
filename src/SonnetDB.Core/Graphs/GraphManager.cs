using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;

namespace SonnetDB.Graphs;

/// <summary>
/// 管理同一数据库目录下的原生属性图目录和延迟打开的图存储。
/// </summary>
/// <remarks>
/// 图目录文件和每个 graph 的物理存储使用独立格式。创建时先持久化并校验物理 marker，
/// 再持久化候选目录并发布内存快照；删除时先提交目录移除，再清理物理目录，因而崩溃最多留下不可见孤儿存储。
/// 同一 graph 根目录在进程内和跨进程均只允许一个存活的管理器实例。
/// </remarks>
public sealed class GraphManager : IDisposable
{
    private static readonly object RootOwnersSync = new();
    private static readonly HashSet<string> RootOwners = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _commitGate = new();
    private readonly object _sync = new();
    private readonly object _schemaSync;
    private readonly GraphCatalogCodec _catalogCodec = new();
    private readonly string _rootDirectory;
    private readonly KvOptions _kvOptions;
    private readonly Action<string, string>? _nameAvailabilityGuard;
    private readonly Action<string, string>? _dependencyGuard;
    private readonly Dictionary<string, GraphStore> _stores = new(StringComparer.Ordinal);
    private readonly string _ownerKey;
    private FileStream? _lifecycleLease;
    private Exception? _catalogFault;
    private bool _disposed;

    /// <summary>仅供测试在候选目录文件落盘后、内存快照发布前建立同步点。</summary>
    internal Action? AfterCatalogPersistedBeforePublishTestHook { get; set; }

    /// <summary>仅供测试在 catalog 原子保存的指定阶段注入故障。</summary>
    internal Action<GraphCatalogSavePhase>? CatalogSavePhaseTestHook
    {
        get => _catalogCodec.SavePhaseTestHook;
        set => _catalogCodec.SavePhaseTestHook = value;
    }

    /// <summary>
    /// 初始化独立的图管理器。
    /// </summary>
    /// <param name="rootDirectory">graphs 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    public GraphManager(string rootDirectory, KvOptions? kvOptions = null)
        : this(
            rootDirectory,
            kvOptions ?? KvOptions.Default,
            nameAvailabilityGuard: null,
            dependencyGuard: null,
            synchronizationRoot: new object())
    {
    }

    /// <summary>
    /// 使用数据库级 schema 锁和跨模型守卫初始化图管理器。
    /// </summary>
    /// <param name="rootDirectory">graphs 根目录。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    /// <param name="nameAvailabilityGuard">跨模型名称占用检查。</param>
    /// <param name="dependencyGuard">删除前的跨目录依赖检查。</param>
    /// <param name="synchronizationRoot">数据库级 schema 同步根。</param>
    internal GraphManager(
        string rootDirectory,
        KvOptions kvOptions,
        Action<string, string>? nameAvailabilityGuard,
        Action<string, string>? dependencyGuard,
        object synchronizationRoot)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(kvOptions);
        ArgumentNullException.ThrowIfNull(synchronizationRoot);
        ValidateKvCapacity(kvOptions);

        _rootDirectory = rootDirectory;
        _ownerKey = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        _kvOptions = kvOptions;
        _nameAvailabilityGuard = nameAvailabilityGuard;
        _dependencyGuard = dependencyGuard;
        _schemaSync = synchronizationRoot;
        AcquireRootOwner(_ownerKey);
        try
        {
            Directory.CreateDirectory(_rootDirectory);
            Directory.CreateDirectory(StoresDirectory);
            _lifecycleLease = AcquireLifecycleLease(_rootDirectory);

            Catalog = new GraphCatalog(GraphCatalogCodec.Load(CatalogPath));
            foreach (GraphDefinition definition in Catalog.Snapshot())
                _nameAvailabilityGuard?.Invoke(definition.Name, "graph");
            Catalog.MutationGuard = EnsureManagedCatalogMutation;
        }
        catch (Exception initializationFailure)
        {
            Exception? leaseFailure = null;
            try
            {
                _lifecycleLease?.Dispose();
            }
            catch (Exception exception)
            {
                leaseFailure = exception;
            }
            finally
            {
                _lifecycleLease = null;
                ReleaseRootOwner(_ownerKey);
            }

            if (leaseFailure is not null)
            {
                throw new AggregateException(
                    "Graph manager 初始化失败，且生命周期租约释放失败。",
                    initializationFailure,
                    leaseFailure);
            }
            throw;
        }
    }

    /// <summary>图定义目录。</summary>
    public GraphCatalog Catalog { get; }

    /// <summary>图根目录。</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>图目录文件完整路径。</summary>
    public string CatalogPath => Path.Combine(_rootDirectory, GraphCatalogCodec.FileName);

    /// <summary>所有图物理存储的父目录。</summary>
    public string StoresDirectory => Path.Combine(_rootDirectory, "stores");

    /// <summary>
    /// 创建图定义、物理 marker 和底层 KV 存储。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>已打开的新图存储。</returns>
    public GraphStore Create(string name)
    {
        GraphDefinition definition = GraphDefinition.Create(name);
        return Create(definition);
    }

    /// <summary>
    /// 使用指定的稳定定义创建图存储。
    /// </summary>
    /// <param name="definition">待创建的图定义。</param>
    /// <returns>已打开的新图存储。</returns>
    public GraphStore Create(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfUnavailable();
                _nameAvailabilityGuard?.Invoke(definition.Name, "graph");
                if (Catalog.TryGet(definition.Name) is not null)
                    throw new InvalidOperationException($"graph '{definition.Name}' 已存在。");
                if (Catalog.TryGet(definition.StorageId) is not null)
                {
                    throw new InvalidOperationException(
                        $"graph storage id '{definition.StorageId:N}' 已存在。");
                }

                string storeDirectory = StoreDirectory(definition.StorageId);
                EnsureCandidateStoreDirectory(storeDirectory);
                GraphCatalogState previousState = Catalog.CaptureState();
                GraphCatalogState candidateState = new(
                    checked(previousState.Revision + 1),
                    previousState.Definitions.Concat([definition]).ToArray());
                GraphStore openedStore;
                try
                {
                    // 物理身份先落盘并校验，再把候选目录写入 durable catalog。
                    openedStore = GraphStore.CreateNew(
                        definition,
                        storeDirectory,
                        _kvOptions,
                        _commitGate);
                }
                catch (Exception createException)
                {
                    var cleanupErrors = new List<Exception>();
                    TryDeleteOwnedStoreDirectory(storeDirectory, cleanupErrors);
                    if (cleanupErrors.Count != 0)
                    {
                        cleanupErrors.Insert(0, createException);
                        throw new InvalidOperationException(
                            $"graph '{definition.Name}' 创建失败，且物理存储回滚失败。",
                            new AggregateException(cleanupErrors));
                    }

                    throw;
                }

                try
                {
                    _catalogCodec.Persist(CatalogPath, candidateState);
                }
                catch (Exception catalogException)
                {
                    var failures = new List<Exception> { catalogException };
                    TryDisposeStore(openedStore, failures);
                    throw MarkCatalogFault(
                        $"创建 graph '{definition.Name}'",
                        CombineFailures(failures));
                }

                try
                {
                    AfterCatalogPersistedBeforePublishTestHook?.Invoke();
                    Catalog.Add(definition);
                    _stores.Add(definition.Name, openedStore);
                    return openedStore;
                }
                catch (Exception publicationException)
                {
                    var failures = new List<Exception> { publicationException };
                    _stores.Remove(definition.Name);
                    bool memoryRestored = false;
                    try
                    {
                        Catalog.Restore(previousState);
                        memoryRestored = true;
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                    }

                    bool durableRollbackCompleted = false;
                    try
                    {
                        _catalogCodec.Persist(CatalogPath, previousState);
                        durableRollbackCompleted = true;
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                    }

                    TryDisposeStore(openedStore, failures);
                    if (!durableRollbackCompleted)
                    {
                        throw MarkCatalogFault(
                            $"回滚 graph '{definition.Name}' 创建",
                            CombineFailures(failures));
                    }

                    // 只有旧 catalog 已完整通过目录 fsync，删除 candidate store 才不会制造断链定义。
                    TryDeleteOwnedStoreDirectory(storeDirectory, failures);
                    if (!memoryRestored)
                    {
                        throw MarkCatalogFault(
                            $"恢复 graph '{definition.Name}' 的内存目录",
                            CombineFailures(failures));
                    }

                    if (failures.Count != 1)
                    {
                        throw new InvalidOperationException(
                            $"graph '{definition.Name}' 创建失败，且目录或物理存储回滚失败。",
                            new AggregateException(failures));
                    }

                    throw;
                }
            }
    }

    /// <summary>
    /// 打开已有图；打开前严格校验物理 marker。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>已打开的图存储。</returns>
    public GraphStore Open(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            ThrowIfUnavailable();
            GraphDefinition definition = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"graph '{name}' 不存在。");
            return OpenStoreLocked(definition);
        }
    }

    /// <summary>
    /// 尝试打开已有图；图不存在时返回 null。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>已打开的图存储，或 null。</returns>
    public GraphStore? TryOpen(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            ThrowIfUnavailable();
            GraphDefinition? definition = Catalog.TryGet(name);
            return definition is null ? null : OpenStoreLocked(definition);
        }
    }

    /// <summary>
    /// 关闭并重新打开指定图，用于显式验证 marker 与 KV 恢复路径。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>重新打开的图存储。</returns>
    public GraphStore Reopen(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            ThrowIfUnavailable();
            if (_stores.Remove(name, out GraphStore? existing))
                existing.Dispose();

            GraphDefinition definition = Catalog.TryGet(name)
                ?? throw new InvalidOperationException($"graph '{name}' 不存在。");
            return OpenStoreLocked(definition);
        }
    }

    /// <summary>
    /// 删除图目录并清理其物理存储。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Drop(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfUnavailable();
                GraphDefinition? definition = Catalog.TryGet(name);
                if (definition is null)
                    return false;

                _dependencyGuard?.Invoke(name, "DROP GRAPH");
                string storeDirectory = StoreDirectory(definition.StorageId);
                GraphStore.ValidateExistingMarker(definition, storeDirectory);
                GraphCatalogState previousState = Catalog.CaptureState();
                GraphCatalogState candidateState = new(
                    checked(previousState.Revision + 1),
                    previousState.Definitions
                        .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                        .ToArray());

                // 先提交 catalog 移除；后续删除失败时只留下不可见 orphan store，不能产生可见断链定义。
                try
                {
                    _catalogCodec.Persist(CatalogPath, candidateState);
                }
                catch (Exception catalogException)
                {
                    throw MarkCatalogFault(
                        $"删除 graph '{name}'",
                        catalogException);
                }

                try
                {
                    Catalog.Remove(name);
                }
                catch (Exception publicationException)
                {
                    var failures = new List<Exception> { publicationException };
                    bool memoryRestored = false;
                    try
                    {
                        Catalog.Restore(previousState);
                        memoryRestored = true;
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                    }

                    bool durableRollbackCompleted = false;
                    try
                    {
                        _catalogCodec.Persist(CatalogPath, previousState);
                        durableRollbackCompleted = true;
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                    }

                    if (!durableRollbackCompleted || !memoryRestored)
                    {
                        throw MarkCatalogFault(
                            $"回滚 graph '{name}' 删除",
                            CombineFailures(failures));
                    }

                    throw;
                }

                if (_stores.Remove(name, out GraphStore? store))
                    store.Dispose();

                if (Directory.Exists(storeDirectory))
                    Directory.Delete(storeDirectory, recursive: true);
                SonnetDB.Wal.DirectoryFsync.FlushBestEffort(StoresDirectory);
                return true;
            }
    }

    /// <summary>
    /// 为目录中的所有图创建 KV 一致快照。
    /// </summary>
    /// <returns>成功 checkpoint 的图名称快照。</returns>
    public IReadOnlyList<string> CheckpointAll()
    {
        lock (_commitGate)
            lock (_sync)
            {
                ThrowIfUnavailable();
                return CheckpointAllLocked();
            }
    }

    /// <summary>
    /// 在阻止所有 graph 原子发布的同一门内完成 checkpoint，并保持门直到备份复制和 manifest
    /// 构建回调结束。锁序固定为 commit gate、manager、store、KV。
    /// </summary>
    internal TResult ExecuteConsistentBackup<TResult>(Func<TResult> afterCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(afterCheckpoint);
        lock (_commitGate)
            lock (_sync)
            {
                ThrowIfUnavailable();
                _ = CheckpointAllLocked();
                return afterCheckpoint();
            }
    }

    /// <summary>
    /// 关闭所有已打开图存储；目录定义保留在磁盘中供下一次打开恢复。
    /// </summary>
    public void Dispose()
    {
        var failures = new List<Exception>();
        bool releaseOwner = false;
        try
        {
            lock (_schemaSync)
                lock (_sync)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    releaseOwner = true;
                    foreach (GraphStore store in _stores.Values)
                    {
                        try
                        {
                            store.Dispose();
                        }
                        catch (Exception exception)
                        {
                            failures.Add(exception);
                        }
                    }
                    _stores.Clear();
                }
        }
        finally
        {
            if (releaseOwner)
            {
                try
                {
                    _lifecycleLease?.Dispose();
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                finally
                {
                    _lifecycleLease = null;
                    ReleaseRootOwner(_ownerKey);
                }
            }
        }

        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Graph manager 关闭时发生多个错误。", failures);
    }

    private GraphStore OpenStoreLocked(GraphDefinition definition)
    {
        if (_stores.TryGetValue(definition.Name, out GraphStore? existing))
        {
            if (!existing.IsDisposed)
                return existing;
            _stores.Remove(definition.Name);
        }

        GraphStore store = GraphStore.OpenExisting(
            definition,
            StoreDirectory(definition.StorageId),
            _kvOptions,
            _commitGate);
        _stores[definition.Name] = store;
        return store;
    }

    private IReadOnlyList<string> CheckpointAllLocked()
    {
        var names = Catalog.Snapshot().Select(static item => item.Name).ToArray();
        foreach (string name in names)
            OpenStoreLocked(Catalog.TryGet(name)!).CreateSnapshot();
        return names;
    }

    private string StoreDirectory(Guid storageId)
        => Path.Combine(StoresDirectory, storageId.ToString("N"));

    private static void EnsureCandidateStoreDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException(
                $"图物理存储目录已存在且非空：'{path}'。");
        }
    }

    private static void TryDeleteOwnedStoreDirectory(string path, List<Exception> errors)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static void TryDisposeStore(GraphStore store, List<Exception> errors)
    {
        try
        {
            store.Dispose();
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
    }

    private static Exception CombineFailures(List<Exception> failures)
        => failures.Count == 1 ? failures[0] : new AggregateException(failures);

    private static void ValidateKvCapacity(KvOptions kvOptions)
    {
        if (kvOptions.MaxKeyBytes < GraphKeyCodec.MaxEncodedKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kvOptions),
                kvOptions.MaxKeyBytes,
                $"Graph V1 要求 MaxKeyBytes 至少为 {GraphKeyCodec.MaxEncodedKeyBytes} 字节。");
        }

        if (kvOptions.MaxValueBytes < GraphRecordEnvelopeCodec.MaxEncodedRecordBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kvOptions),
                kvOptions.MaxValueBytes,
                $"Graph V1 要求 MaxValueBytes 至少为 {GraphRecordEnvelopeCodec.MaxEncodedRecordBytes} 字节。");
        }
    }

    private IOException MarkCatalogFault(string operation, Exception exception)
    {
        _catalogFault ??= exception;
        return new IOException(
            $"{operation}时 Graph catalog 的持久化结果未知；当前 GraphManager 已停用，请 Dispose 后重新打开。",
            exception);
    }

    private void EnsureManagedCatalogMutation(string graphName, string operation)
    {
        if (!Monitor.IsEntered(_schemaSync))
        {
            throw new InvalidOperationException(
                $"不能直接对受管理的 GraphCatalog 执行 {operation} '{graphName}'；请使用 GraphManager 的 schema API。");
        }
    }

    private static void AcquireRootOwner(string ownerKey)
    {
        lock (RootOwnersSync)
        {
            if (!RootOwners.Add(ownerKey))
            {
                throw new InvalidOperationException(
                    $"图根目录 '{ownerKey}' 已由另一个 GraphManager 实例打开。");
            }
        }
    }

    private static void ReleaseRootOwner(string ownerKey)
    {
        lock (RootOwnersSync)
            RootOwners.Remove(ownerKey);
    }

    private static FileStream AcquireLifecycleLease(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, KvKeyspace.LifecycleLockFileName);
        try
        {
            return new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"图根目录 '{rootDirectory}' 已由另一个进程中的 GraphManager 打开。",
                exception);
        }
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_catalogFault is not null)
        {
            throw new IOException(
                "Graph catalog 的持久化结果未知；当前 GraphManager 已停用，请 Dispose 后重新打开。",
                _catalogFault);
        }
    }
}
