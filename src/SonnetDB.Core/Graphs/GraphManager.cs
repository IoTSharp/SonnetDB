using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using SonnetDB.Tables;

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
    private readonly TableManager? _tables;
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
            tableManager: null,
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
        : this(
            rootDirectory,
            kvOptions,
            nameAvailabilityGuard,
            dependencyGuard,
            tableManager: null,
            synchronizationRoot)
    {
    }

    /// <summary>使用关系表管理器初始化支持 SQL/PGQ 映射的图管理器。</summary>
    internal GraphManager(
        string rootDirectory,
        KvOptions kvOptions,
        Action<string, string>? nameAvailabilityGuard,
        Action<string, string>? dependencyGuard,
        TableManager? tableManager,
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
        _tables = tableManager;
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
            PropertyGraphs = new PropertyGraphCatalog(PropertyGraphCatalogCodec.Load(PropertyGraphCatalogPath));
            foreach (PropertyGraphDefinition definition in PropertyGraphs.Snapshot())
            {
                if (Catalog.TryGet(definition.Name) is not null)
                {
                    throw new InvalidDataException(
                        $"graph 与 property graph 不能共享名称 '{definition.Name}'。");
                }
                _nameAvailabilityGuard?.Invoke(definition.Name, "property graph");
                if (_tables is not null)
                    ValidatePropertyGraphDefinition(definition, _tables);
            }
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

    /// <summary>SQL/PGQ 关系映射图目录。</summary>
    public PropertyGraphCatalog PropertyGraphs { get; }

    /// <summary>图根目录。</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>图目录文件完整路径。</summary>
    public string CatalogPath => Path.Combine(_rootDirectory, GraphCatalogCodec.FileName);

    /// <summary>SQL/PGQ 关系映射图目录文件完整路径。</summary>
    public string PropertyGraphCatalogPath => Path.Combine(_rootDirectory, PropertyGraphCatalogCodec.FileName);

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
                if (PropertyGraphs.TryGet(definition.Name) is not null)
                    throw new InvalidOperationException($"property graph '{definition.Name}' 已存在。");
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
    /// 创建只读 SQL/PGQ 关系映射图。此操作仅持久化 schema mapping，不复制关系行。
    /// </summary>
    /// <param name="definition">待创建的映射定义。</param>
    /// <returns>已发布的映射定义。</returns>
    public PropertyGraphDefinition CreatePropertyGraph(PropertyGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfUnavailable();
                TableManager tables = _tables
                    ?? throw new NotSupportedException("独立 GraphManager 未绑定关系表管理器，不能创建 property graph。");
                _nameAvailabilityGuard?.Invoke(definition.Name, "property graph");
                if (Catalog.TryGet(definition.Name) is not null)
                    throw new InvalidOperationException($"graph '{definition.Name}' 已存在。");
                if (PropertyGraphs.TryGet(definition.Name) is not null)
                    throw new InvalidOperationException($"property graph '{definition.Name}' 已存在。");
                ValidatePropertyGraphDefinition(definition, tables);

                PropertyGraphCatalogState previousState = PropertyGraphs.CaptureState();
                PropertyGraphCatalogState candidateState = new(
                    checked(previousState.Revision + 1),
                    previousState.Definitions.Concat([definition]).ToArray());
                try
                {
                    PropertyGraphCatalogCodec.Save(PropertyGraphCatalogPath, candidateState);
                }
                catch (Exception exception)
                {
                    throw MarkCatalogFault($"创建 property graph '{definition.Name}'", exception);
                }

                try
                {
                    PropertyGraphs.Add(definition);
                    return definition;
                }
                catch (Exception publicationException)
                {
                    var failures = new List<Exception> { publicationException };
                    try
                    {
                        PropertyGraphs.Restore(previousState);
                        PropertyGraphCatalogCodec.Save(PropertyGraphCatalogPath, previousState);
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                        throw MarkCatalogFault(
                            $"回滚 property graph '{definition.Name}' 创建",
                            CombineFailures(failures));
                    }
                    throw;
                }
            }
    }

    /// <summary>删除 SQL/PGQ 关系映射图，不删除任何关系表或关系行。</summary>
    /// <param name="name">映射图名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool DropPropertyGraph(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_schemaSync)
            lock (_sync)
            {
                ThrowIfUnavailable();
                if (PropertyGraphs.TryGet(name) is null)
                    return false;
                _dependencyGuard?.Invoke(name, "DROP PROPERTY GRAPH");

                PropertyGraphCatalogState previousState = PropertyGraphs.CaptureState();
                PropertyGraphCatalogState candidateState = new(
                    checked(previousState.Revision + 1),
                    previousState.Definitions
                        .Where(item => !string.Equals(item.Name, name, StringComparison.Ordinal))
                        .ToArray());
                try
                {
                    PropertyGraphCatalogCodec.Save(PropertyGraphCatalogPath, candidateState);
                }
                catch (Exception exception)
                {
                    throw MarkCatalogFault($"删除 property graph '{name}'", exception);
                }

                try
                {
                    return PropertyGraphs.Remove(name);
                }
                catch (Exception publicationException)
                {
                    var failures = new List<Exception> { publicationException };
                    try
                    {
                        PropertyGraphs.Restore(previousState);
                        PropertyGraphCatalogCodec.Save(PropertyGraphCatalogPath, previousState);
                    }
                    catch (Exception rollbackException)
                    {
                        failures.Add(rollbackException);
                        throw MarkCatalogFault(
                            $"回滚 property graph '{name}' 删除",
                            CombineFailures(failures));
                    }
                    throw;
                }
            }
    }

    /// <summary>打开关系映射图访问器；访问器直接读取当前关系表主数据。</summary>
    /// <param name="name">映射图名称。</param>
    /// <returns>无副本的关系图访问器。</returns>
    public RelationalGraphAccessor OpenPropertyGraph(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            ThrowIfUnavailable();
            TableManager tables = _tables
                ?? throw new NotSupportedException("独立 GraphManager 未绑定关系表管理器，不能打开 property graph。");
            PropertyGraphDefinition definition = PropertyGraphs.TryGet(name)
                ?? throw new InvalidOperationException($"property graph '{name}' 不存在。");
            return new RelationalGraphAccessor(tables, definition);
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

    private static void ValidatePropertyGraphDefinition(
        PropertyGraphDefinition definition,
        TableManager tables)
    {
        foreach (PropertyGraphVertexTable vertex in definition.VertexTables)
        {
            TableSchema schema = tables.Catalog.TryGet(vertex.TableName)
                ?? throw new InvalidOperationException(
                    $"property graph '{definition.Name}' 的 vertex table '{vertex.TableName}' 不存在。");
            ValidateUniqueKey(schema, vertex.KeyColumns, "vertex");
            ValidateColumns(schema, vertex.PropertyColumns, "vertex property");
        }

        foreach (PropertyGraphEdgeTable edge in definition.EdgeTables)
        {
            TableSchema edgeSchema = tables.Catalog.TryGet(edge.TableName)
                ?? throw new InvalidOperationException(
                    $"property graph '{definition.Name}' 的 edge table '{edge.TableName}' 不存在。");
            ValidateUniqueKey(edgeSchema, edge.KeyColumns, "edge");
            ValidateColumns(edgeSchema, edge.PropertyColumns, "edge property");

            PropertyGraphVertexTable source = definition.TryGetVertexTable(edge.SourceTable)
                ?? throw new InvalidOperationException(
                    $"edge table '{edge.TableName}' 的 source table '{edge.SourceTable}' 未声明为 vertex table。");
            PropertyGraphVertexTable destination = definition.TryGetVertexTable(edge.DestinationTable)
                ?? throw new InvalidOperationException(
                    $"edge table '{edge.TableName}' 的 destination table '{edge.DestinationTable}' 未声明为 vertex table。");
            ValidateEndpoint(
                tables,
                edgeSchema,
                edge.TableName,
                "source",
                edge.SourceColumns,
                source,
                edge.SourceReferenceColumns);
            ValidateEndpoint(
                tables,
                edgeSchema,
                edge.TableName,
                "destination",
                edge.DestinationColumns,
                destination,
                edge.DestinationReferenceColumns);
        }
    }

    private static void ValidateUniqueKey(
        TableSchema schema,
        IReadOnlyList<string> keyColumns,
        string mappingKind)
    {
        ValidateColumns(schema, keyColumns, mappingKind + " key");
        if (schema.PrimaryKey.SequenceEqual(keyColumns, StringComparer.Ordinal))
            return;
        if (schema.Indexes.Any(index =>
                index.IsUnique && index.Columns.SequenceEqual(keyColumns, StringComparer.Ordinal)))
            return;
        throw new InvalidOperationException(
            $"{mappingKind} table '{schema.Name}' 的 KEY ({string.Join(", ", keyColumns)}) "
            + "必须匹配 PRIMARY KEY 或完整 UNIQUE INDEX。");
    }

    private static void ValidateColumns(
        TableSchema schema,
        IReadOnlyList<string> columns,
        string description)
    {
        foreach (string column in columns)
            if (schema.TryGetColumn(column) is null)
                throw new InvalidOperationException(
                    $"{description} 引用了 table '{schema.Name}' 中不存在的列 '{column}'。");
    }

    private static void ValidateEndpoint(
        TableManager tables,
        TableSchema edgeSchema,
        string edgeTable,
        string endpoint,
        IReadOnlyList<string> endpointColumns,
        PropertyGraphVertexTable vertex,
        IReadOnlyList<string> referenceColumns)
    {
        if (!referenceColumns.SequenceEqual(vertex.KeyColumns, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"edge table '{edgeTable}' 的 {endpoint} REFERENCES 必须匹配 vertex table "
                + $"'{vertex.TableName}' 的 KEY ({string.Join(", ", vertex.KeyColumns)})。");
        }

        ValidateColumns(edgeSchema, endpointColumns, endpoint + " key");
        TableSchema vertexSchema = tables.Catalog.TryGet(vertex.TableName)
            ?? throw new InvalidOperationException($"vertex table '{vertex.TableName}' 不存在。");
        for (int index = 0; index < endpointColumns.Count; index++)
        {
            TableColumn edgeColumn = edgeSchema.TryGetColumn(endpointColumns[index])!;
            TableColumn vertexColumn = vertexSchema.TryGetColumn(referenceColumns[index])!;
            if (edgeColumn.DataType != vertexColumn.DataType)
            {
                throw new InvalidOperationException(
                    $"edge table '{edgeTable}' 的 {endpoint} 列 '{edgeColumn.Name}' 与 "
                    + $"vertex key '{vertex.TableName}.{vertexColumn.Name}' 类型不一致。");
            }
        }
    }

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
