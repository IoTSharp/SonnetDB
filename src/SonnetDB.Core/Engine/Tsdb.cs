using System.Diagnostics;
using SonnetDB.Catalog;
using SonnetDB.Diagnostics;
using SonnetDB.Documents;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Generations;
using SonnetDB.Graphs;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Modbus;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Routines;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using SonnetDB.Tables;
using SonnetDB.Views;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// SonnetDB 嵌入式时序数据库门面。负责：
/// <list type="bullet">
///   <item><description>启动时加载 catalog、回放 WAL 重建 MemTable；</description></item>
///   <item><description>写入路径：Append → WAL → MemTable，必要时触发 Flush；</description></item>
///   <item><description>关闭时：Flush MemTable + 持久化 catalog。</description></item>
/// </list>
/// 同一数据库根目录只能由一个实例打开（进程内根目录所有权与 WAL active segment 文件句柄共同保护）。
/// </summary>
public sealed class Tsdb : IDisposable
{
    private const int MaxRootDirectoryLinkResolutions = 64;

    private static readonly object RootDirectoryOwnersSync = new();
    private static readonly HashSet<string> RootDirectoryOwners = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly TsdbOptions _options;
    private readonly FlushCoordinator _flushCoordinator;
    private readonly WalGroupCommitCoordinator _walGroupCommit;
    private readonly object _writeSync = new();
    private readonly object _shutdownSync = new();
    private readonly object _measurementBatchSync = new();
    // 高位为关闭位，低位为已在失败前获得 admission 的批次计数。失败回调只能原子封门，
    // 不能等待 _writeSync：某些管理路径会持写锁等待 flush 泵完成。
    private long _batchWriteAdmissionState;
    // 高位为关闭位，低位为已在关闭前取得 admission 的 schema mutation 数量。
    // 关闭流程必须等待这些 mutation 完成，才能把 _disposed 置为 true；否则一个已
    // 取得 schema 锁、但暂时停在测试 hook 的 DDL 可能被错误地拒绝。
    private long _schemaMutationAdmissionState;
    private readonly MeasurementBatchLedger _measurementBatchLedger;
    // 锁序固定为 _schemaSync（外）→ _writeSync / 各模型 manager（内）。
    // 隐式 measurement 建立、跨模型 DDL 与备份均持有 schema 锁，保证名称检查和发布不可交错。
    private readonly object _schemaSync = new();
    // 维护操作串行锁：序列化 Compaction / Retention / DropMeasurement 的段读-规划-执行-替换，
    // 防止"compaction 把 retention 刚删的过期数据重新物化"以及后台 worker 无租约读段导致的
    // use-after-dispose（#191）。组合锁序固定为 _schemaSync → _maintenanceSync → _writeSync；
    // 不涉及 schema 的维护路径仍按 _maintenanceSync → _writeSync，直接写路径按 _schemaSync → _writeSync，
    // flush 泵不获取这些锁。
    private readonly object _maintenanceSync = new();
    private MemTable _activeMemTable;
    private readonly HashSet<ulong> _seriesWithWalRecord;
    private readonly KvKeyspaceManager _keyspaces;
    private readonly TableManager _tables;
    private readonly ModbusManager _modbus;
    private readonly DocumentCollectionManager _documents;
    private readonly DatabaseGenerationManager _generations;
    private readonly ViewManager _views;
    private readonly MaterializedViewManager _materializedViews;
    private readonly RoutineManager _routines;
    private readonly GraphManager _graphs;
    private readonly SqlGlobalMemoryBudget _sqlMemoryBudget;
    private readonly SqlParallelCoordinator _sqlParallelCoordinator;
    private readonly SqlRuntimeFeedbackStore _sqlRuntimeFeedback;

    private WalSegmentSet? _walSet;
    private long _nextSegmentId;
    private bool _catalogDirty;
    private bool _measurementSchemaDirty;
    private long _measurementSchemaPersistCount;
    private bool _disposed;
    private int _writeLifecycleClosing;
    private BackgroundFlushWorker? _flushWorker;
    private FlushPump? _flushPump;
    private CompactionWorker? _compactionWorker;
    private RetentionWorker? _retentionWorker;
    private KvExpirerWorker? _kvExpirerWorker;
    private long _checkpointLsn;
    private long _tombstoneDeletesSinceCheckpoint;
    private long _lastTombstoneCheckpointUtcTicks;
    private Exception? _lastError;
    // 任一密封表在段发布前失败后，后续 checkpoint 不能越过它，否则 WAL replay 会跳过
    // 尚未确认落段的数据。保持所有 sealing 表可查询，并拒绝新的 flush 直到重启恢复。
    private Exception? _flushRecoveryFault;
    private long _meterRegistration;
    private RootDirectoryOwnership? _rootDirectoryOwnership;

    /// <summary>仅供并发测试确认跨 catalog DDL 即将尝试获取 schema 锁。</summary>
    internal Action? BeforeSchemaMutationLockTestHook { get; set; }

    /// <summary>仅供并发测试确认跨 catalog DDL 已取得 schema 锁。</summary>
    internal Action? SchemaMutationLockAcquiredTestHook { get; set; }

    /// <summary>仅供恢复并发测试在 measurement 批次取得 admission 前暂停。</summary>
    internal Action? BeforeMeasurementBatchAdmissionTestHook { get; set; }

    /// <summary>仅供恢复并发测试在 measurement 批次取得 admission 后暂停。</summary>
    internal Action? AfterMeasurementBatchAdmissionTestHook { get; set; }

    /// <summary>仅供关闭恢复测试在后台 worker 停止路径注入故障。</summary>
    internal Action? BeforeBackgroundWorkerShutdownTestHook { get; set; }

    /// <summary>
    /// <see cref="WriteMany(ReadOnlySpan{Point})"/> 单次持锁处理的最大点数。超大批量按此粒度分块，
    /// 使硬上限背压能在批内周期性触发、块间释放写锁，避免单批无界撑大 MemTable/WAL（C4）。
    /// </summary>
    private const int WriteManyChunkSize = 8192;
    private const long BatchWriteAdmissionClosed = long.MinValue;
    private const long SchemaMutationAdmissionClosed = long.MinValue;

    /// <summary>数据库根目录路径。</summary>
    public string RootDirectory => _options.RootDirectory;

    /// <summary>当前数据库的 SQL 阻塞算子内存配置。</summary>
    internal SqlMemoryOptions SqlMemoryOptions => _options.SqlMemory;

    internal RetentionPolicy TimeSeriesRetentionPolicy => _options.Retention;

    /// <summary>当前数据库实例内所有 SQL 查询共享的内存预算。</summary>
    internal SqlGlobalMemoryBudget SqlMemoryBudget => _sqlMemoryBudget;

    /// <summary>当前数据库 SQL 并行 worker 协调器。</summary>
    internal SqlParallelCoordinator SqlParallelCoordinator => _sqlParallelCoordinator;

    /// <summary>当前数据库内存中的 SQL 运行时估算反馈。</summary>
    internal SqlRuntimeFeedbackStore SqlRuntimeFeedback => _sqlRuntimeFeedback;

    /// <summary>当前序列目录。</summary>
    public SeriesCatalog Catalog { get; }

    /// <summary>当前 Measurement schema 集合（线程安全）。</summary>
    public MeasurementCatalog Measurements { get; }

    /// <summary>
    /// 当前活跃内存层（MemTable）。Flush 时会被原子替换为新的空实例，因此本属性每次读取
    /// 返回"当前"活跃表；查询侧不直接用它，而是通过 <see cref="Segments"/> 的统一快照
    /// 同时拿到 active + sealing MemTable 与段集合，保证读一致（修 #190）。
    /// </summary>
    public MemTable MemTable => _activeMemTable;

    /// <summary>段集合与索引快照管理器。</summary>
    public SegmentManager Segments { get; }

    /// <summary>查询执行器：合并 MemTable 与多个 Segment 的候选 Block，提供原始点查询与聚合查询。</summary>
    public QueryEngine Query { get; }

    /// <summary>
    /// 用户自定义函数（UDF）注册表；可通过其 <c>RegisterScalar</c> /
    /// <c>RegisterAggregate</c> / <c>RegisterWindow</c> / <c>RegisterTableValuedFunction</c>
    /// 扩展 SQL 函数。当 <see cref="TsdbOptions.AllowUserFunctions"/> 为 <c>false</c> 时，
    /// 该实例存在但任何 Register 操作都会抛出。
    /// </summary>
    public UserFunctionRegistry Functions { get; }

    /// <summary>
    /// 内置 KV Keyspace 管理器，用于打开轻量键值命名空间。
    /// </summary>
    public KvKeyspaceManager Keyspaces => _keyspaces;

    /// <summary>
    /// 关系表管理器，提供 SQL 关系表 MVP 的 schema catalog 与 KV-backed rowstore。
    /// </summary>
    public TableManager Tables => _tables;

    /// <summary>
    /// Modbus source、endpoint 与关系表映射管理器。
    /// </summary>
    public ModbusManager Modbus => _modbus;

    /// <summary>
    /// JSON 文档集合管理器，提供 document collection schema catalog 与 KV-backed 主数据。
    /// </summary>
    public DocumentCollectionManager Documents => _documents;

    /// <summary>
    /// 跨 KV、Document 与 FullText 资源的原子 generation 发布、查询租约和 retired 清理管理器。
    /// </summary>
    public DatabaseGenerationManager Generations => _generations;

    /// <summary>
    /// 逻辑视图管理器，提供持久化定义目录与依赖查询。
    /// </summary>
    public ViewManager Views => _views;

    /// <summary>物化视图定义、刷新状态和物理代际管理器。</summary>
    public MaterializedViewManager MaterializedViews => _materializedViews;

    /// <summary>SQL 过程、关系表触发器及其治理诊断管理器。</summary>
    public RoutineManager Routines => _routines;

    /// <summary>
    /// 原生属性图目录和存储生命周期管理器。Phase 0 只开放 create/open/drop 与元数据，不开放查询能力。
    /// </summary>
    public GraphManager Graphs => _graphs;

    /// <summary>进程内墓碑集合，支持查询过滤与 Compaction 消化。</summary>
    public TombstoneTable Tombstones { get; private set; } = new TombstoneTable();

    /// <summary>
    /// 后台 Retention 工作线程；仅当 <see cref="TsdbOptions.Retention"/> 启用时非 null。
    /// </summary>
    public RetentionWorker? Retention { get; private set; }

    /// <summary>
    /// 最近一次被引擎内部捕获但未向调用方抛出的异常；当前主要用于 <see cref="Dispose"/> final flush 诊断。
    /// </summary>
    public Exception? LastError => Volatile.Read(ref _lastError);

    /// <summary>
    /// 返回 KV generation 后台回收的进度、速率、最近节流原因与错误类型。
    /// </summary>
    public KvMaintenanceStatus GetKvMaintenanceStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        KvCleanupRoundResult current = GetCleanupStatusFromBackground();
        return _kvExpirerWorker?.GetStatus(current)
            ?? new KvMaintenanceStatus(
                0,
                0,
                current.PendingFiles,
                current.PendingBytes,
                0,
                0,
                null,
                null);
    }

    /// <summary>
    /// 引擎内部捕获到可诊断异常时触发的事件。事件处理器抛出的异常会被忽略，以保持调用方语义稳定。
    /// </summary>
    public event EventHandler<TsdbDiagnosticEvent>? DiagnosticEvent;

    /// <summary>下一个将分配的 SegmentId（线程安全读取）。</summary>
    public long NextSegmentId
    {
        get
        {
            lock (_writeSync)
                return _nextSegmentId;
        }
    }

    /// <summary>最近一次 WAL Checkpoint 的 LSN（启动时从 durable checkpoint 文件与 WAL replay 合并获得；仅诊断/测试用）。</summary>
    public long CheckpointLsn
    {
        get
        {
            lock (_writeSync)
                return _checkpointLsn;
        }
    }

    /// <summary>
    /// 获取仅含运行时计数与 WAL 文件元数据的线程安全诊断快照。
    /// </summary>
    /// <remarks>
    /// 快照不会枚举或返回用户数据点、字段值或 WAL 记录内容。待压缩任务数是在维护锁内，
    /// 基于稳定 Segment 读租约按当前 Compaction 策略即时规划得到。
    /// </remarks>
    /// <returns>当前引擎运行时诊断快照。</returns>
    public TsdbRuntimeDiagnosticSnapshot GetRuntimeDiagnosticSnapshot()
    {
        lock (_maintenanceSync)
        {
            long memTableEstimatedBytes;
            long memTablePointCount;
            long pendingFlushTasks;
            long checkpointLsn;
            IReadOnlyList<WalFileDiagnosticSnapshot> walFiles;

            lock (_writeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                memTableEstimatedBytes = _activeMemTable.EstimatedBytes;
                memTablePointCount = _activeMemTable.PointCount;
                pendingFlushTasks = _flushPump?.PendingCount ?? 0;
                checkpointLsn = _checkpointLsn;

                var activeWalPath = _walSet?.ActiveSegmentPath;
                walFiles = _walSet?.Segments
                    .Select(segment => new WalFileDiagnosticSnapshot(
                        Path.GetFileName(segment.Path),
                        segment.FileLength,
                        segment.StartLsn,
                        segment.HasLastLsn ? segment.LastLsn : null,
                        string.Equals(segment.Path, activeWalPath, StringComparison.OrdinalIgnoreCase)))
                    .ToArray()
                    ?? [];
            }

            using var lease = Segments.AcquireSnapshot();
            var readers = lease.Readers;
            var pendingCompactionTasks = _options.Compaction.Enabled
                ? CompactionPlanner.Plan(readers, _options.Compaction).Count
                : 0;

            return new TsdbRuntimeDiagnosticSnapshot(
                memTableEstimatedBytes,
                memTablePointCount,
                readers.Count,
                pendingFlushTasks,
                pendingCompactionTasks,
                checkpointLsn,
                walFiles);
        }
    }

    /// <summary>后台 Flush 策略（供 BackgroundFlushWorker 访问）。</summary>
    internal MemTableFlushPolicy BackgroundFlushPolicy => _options.FlushPolicy;
    internal SegmentWriterOptions CompactionWriterOptions => _options.SegmentWriterOptions;

    /// <summary>flush 泵当前排队中（含正在处理）的请求数；供 <see cref="SonnetDbMeter"/> 观测。</summary>
    internal long FlushPumpPendingCount => _flushPump?.PendingCount ?? 0;

    internal long KvCleanupPendingFiles => _kvExpirerWorker?.CleanupPendingFiles ?? 0;

    internal long KvCleanupPendingBytes => _kvExpirerWorker?.CleanupPendingBytes ?? 0;

    internal double KvCleanupBytesPerSecond => _kvExpirerWorker?.LastCleanupBytesPerSecond ?? 0;

    /// <summary>
    /// 获取一次统一读快照租约：一次调用原子拿到 {active + sealing MemTable + 段读取器} 一致视图。
    /// 供需要同时读 MemTable 与段的执行器（KNN / hybrid / TVF / explain）使用，
    /// 避免"先读 MemTable、再读段"的两次独立读跨越 flush 边界。用完必须 Dispose 释放租约。
    /// </summary>
    internal SegmentManagerSnapshotLease AcquireReadSnapshot() => Segments.AcquireSnapshot();

    /// <summary>
    /// 在维护串行锁（<c>_maintenanceSync</c>）内执行 <paramref name="action"/>。
    /// 序列化 Compaction / Retention / DropMeasurement，杜绝它们的段变更相互交错
    /// （如 compaction 重新物化 retention 刚删的过期数据）。锁序：_maintenanceSync 先于 _writeSync。
    /// </summary>
    internal void RunUnderMaintenanceLock(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_maintenanceSync)
        {
            action();
        }
    }

    /// <summary>
    /// 线程安全地分配下一个 SegmentId（单调递增）。
    /// </summary>
    /// <returns>新分配的 SegmentId。</returns>
    internal long AllocateSegmentId()
    {
        lock (_writeSync)
            return _nextSegmentId++;
    }

    internal long WalSyncCount
    {
        get
        {
            lock (_writeSync)
                return _walSet?.SyncCount ?? 0L;
        }
    }

    internal long MeasurementSchemaPersistCount
    {
        get
        {
            lock (_writeSync)
                return _measurementSchemaPersistCount;
        }
    }

    private Tsdb(
        TsdbOptions options,
        SeriesCatalog catalog,
        MeasurementCatalog measurements,
        MemTable memTable,
        WalSegmentSet walSet,
        long nextSegmentId,
        HashSet<ulong> seriesWithWalRecord,
        SegmentManager segmentManager,
        long checkpointLsn,
        bool catalogDirty,
        MeasurementBatchLedger measurementBatchLedger)
    {
        _options = options;
        _sqlMemoryBudget = new SqlGlobalMemoryBudget(options.SqlMemory.GlobalLimitBytes);
        _sqlParallelCoordinator = new SqlParallelCoordinator(options.SqlMemory.MaxParallelWorkers);
        _sqlRuntimeFeedback = new SqlRuntimeFeedbackStore();
        Catalog = catalog;
        Measurements = measurements;
        _activeMemTable = memTable;
        _walSet = walSet;
        _nextSegmentId = nextSegmentId;
        _seriesWithWalRecord = seriesWithWalRecord;
        _catalogDirty = catalogDirty;
        _measurementBatchLedger = measurementBatchLedger;
        Segments = segmentManager;
        _flushCoordinator = new FlushCoordinator(options);
        _walGroupCommit = new WalGroupCommitCoordinator(options.WalGroupCommit);
        Query = new QueryEngine(
            memTable,
            segmentManager,
            catalog,
            Tombstones,
            options.UseSimdNumericAggregates);
        Functions = new UserFunctionRegistry(options.AllowUserFunctions);
        _views = new ViewManager(
            TsdbPaths.ViewsDir(options.RootDirectory),
            EnsureViewDefinitionNameAvailable,
            _schemaSync);
        _materializedViews = new MaterializedViewManager(
            TsdbPaths.MaterializedViewsDir(options.RootDirectory),
            EnsureViewDefinitionNameAvailable,
            _schemaSync);
        _routines = new RoutineManager(TsdbPaths.RoutinesDir(options.RootDirectory));
        _keyspaces = new KvKeyspaceManager(TsdbPaths.KvDir(options.RootDirectory), options.Kv);
        _tables = new TableManager(
            TsdbPaths.TablesDir(options.RootDirectory),
            options.Kv,
            EnsureViewNameAvailable,
            EnsureNoTableDependents,
            _schemaSync);
        _modbus = new ModbusManager(
            TsdbPaths.ModbusDir(options.RootDirectory),
            _tables.Catalog,
            _schemaSync);
        _documents = new DocumentCollectionManager(
            TsdbPaths.DocumentsDir(options.RootDirectory),
            options.Kv,
            EnsureViewNameAvailable,
            EnsureNoViewDependents,
            _schemaSync);
        _graphs = new GraphManager(
            TsdbPaths.GraphsDir(options.RootDirectory),
            options.Kv,
            EnsureGraphNameAvailable,
            EnsureNoViewDependents,
            _tables,
            _schemaSync);
        _generations = new DatabaseGenerationManager(
            TsdbPaths.GenerationsDir(options.RootDirectory),
            options.Kv,
            _keyspaces,
            _documents,
            _schemaSync);
        Measurements.MutationGuard = EnsureManagedMeasurementCatalogMutation;
        _checkpointLsn = checkpointLsn;
        _lastTombstoneCheckpointUtcTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// 打开（不存在则创建）TSDB 根目录，自动加载 catalog 并回放 WAL。
    /// </summary>
    /// <param name="options">引擎选项；为 null 时使用 <see cref="TsdbOptions.Default"/>。</param>
    /// <returns>已初始化的 <see cref="Tsdb"/> 实例。</returns>
    /// <exception cref="IOException">
    /// 同一进程或另一个进程中的实例已打开同一数据库根目录，或前一实例仍在完成关闭时抛出。
    /// </exception>
    public static Tsdb Open(TsdbOptions? options = null)
    {
        options ??= TsdbOptions.Default;
        ArgumentNullException.ThrowIfNull(options.SqlMemory);
        options.SqlMemory.Validate();

        string root = options.RootDirectory;
        Directory.CreateDirectory(root);
        RootDirectoryOwnership? rootDirectoryOwnership = AcquireRootDirectoryOwnership(root);
        WalSegmentSet? walSet = null;
        SegmentManager? segmentManager = null;
        Tsdb? tsdb = null;
        try
        {
            Directory.CreateDirectory(TsdbPaths.WalDir(root));
            Directory.CreateDirectory(TsdbPaths.SegmentsDir(root));
            Directory.CreateDirectory(TsdbPaths.KvDir(root));
            Directory.CreateDirectory(TsdbPaths.GenerationsDir(root));
            Directory.CreateDirectory(TsdbPaths.TablesDir(root));
            Directory.CreateDirectory(TsdbPaths.DocumentsDir(root));
            Directory.CreateDirectory(TsdbPaths.ViewsDir(root));
            Directory.CreateDirectory(TsdbPaths.MaterializedViewsDir(root));
            Directory.CreateDirectory(TsdbPaths.RoutinesDir(root));
            Directory.CreateDirectory(TsdbPaths.ModbusDir(root));
            Directory.CreateDirectory(TsdbPaths.GraphsDir(root));

            // 加载 measurement schema 集合（文件不存在时返回空集合）
            var measurements = new MeasurementCatalog();
            foreach (var schema in MeasurementSchemaCodec.Load(TsdbPaths.MeasurementSchemaPath(root)))
                measurements.LoadOrReplace(schema);
            // 加载 catalog（文件不存在时返回空目录）
            var catalog = CatalogFileCodec.Load(TsdbPaths.CatalogPath(root));

            // 所有目录读取、WAL 打开与 spill 清理都必须在进程内根目录所有权之后进行。
            // 部分平台不会通过 FileShare 拒绝同进程重复打开。
            string walDir = TsdbPaths.WalDir(root);
            // 先打开 WAL 并持有 active segment 的独占写句柄，再执行任何 marker/临时段清理。
            // 这样另一个进程不能在当前实例进行发布时进入破坏性恢复路径；WalSegmentSet.Open
            // 同时负责 legacy active.SDBWAL 升级。
            walSet = WalSegmentSet.Open(walDir, options.WalRolling, options.WalBufferSize, initialStartLsn: 1);
            ReconcileInterruptedFlushPublications(
                root,
                walDir,
                options.SegmentWriterOptions.TempFileSuffix,
                options.SegmentReaderOptions);

            // Flush/Compaction 可能在写入最终 .SDBSEG 前崩溃，且尚未留下可识别的 marker；
            // 在计算 nextSegmentId 和打开 SegmentManager 前清理严格匹配的临时段文件，
            // 避免残留文件长期占用空间。清理范围仅限 segments/ 下的 canonical 文件名。
            SegmentManager.CleanupAllSegmentTemporaryFiles(
                root,
                options.SegmentWriterOptions.TempFileSuffix);

            // 只有在清理未提交 Flush 段后才扫描 Segment：否则孤立段会被计入 next id 或被
            // SegmentManager 加载，和随后的 WAL replay 形成用户可见重复点。
            // 即便 pending compaction 的新段尚未落盘，也不能复用其 SegmentId。
            long nextSegmentId = 1;
            foreach (var (segId, _) in TsdbPaths.EnumerateSegments(root))
            {
                if (segId + 1 > nextSegmentId)
                    nextSegmentId = segId + 1;
            }
            var segmentReplacementManifest = SegmentReplacementManifest.LoadForRoot(root);
            if (segmentReplacementManifest.MaxSegmentId + 1 > nextSegmentId)
                nextSegmentId = segmentReplacementManifest.MaxSegmentId + 1;

            // 先打开全部已发布 canonical 段。任何未被 replacement manifest 抑制的损坏段
            // 都必须在 WAL replay 前 fail closed；WAL 在成功 flush 后已经允许回收，不能
            // 把这类段当作可忽略的临时文件。
            segmentManager = SegmentManager.Open(
                root,
                options.SegmentReaderOptions,
                options.SegmentWriterOptions.TempFileSuffix);

            _ = SqlSpillWorkspace.CleanupStale(root);
            long durableCheckpointLsn = GetVerifiedDurableCheckpointLsn(
                root,
                walDir,
                options.SegmentReaderOptions,
                segmentReplacementManifest);

            // 兼容引入独立 checkpoint 文件之前创建的数据库：旧版本只把 checkpoint
            // 记录写入 WAL，因此不存在 checkpoint.SDBWCKP。只要恢复阶段没有 pending
            // publication marker（有 marker 时 ReconcileInterruptedFlushPublications 已经
            // 要求独立边界可验证），可以继续信任旧 WAL checkpoint；否则旧库会在升级后
            // 因“WAL checkpoint 超过已验证边界”无法打开。独立文件存在但损坏时仍保持
            // fail-closed，避免把新格式的校验失败误当作旧库。
            long highestVerifiedCheckpointLsn = durableCheckpointLsn;
            string checkpointPath = WalSegmentLayout.CheckpointPath(walDir);
            bool hasPublicationMarkers =
                WalSegmentLayout.EnumerateFlushPublicationPaths(walDir).Count != 0;
            if (durableCheckpointLsn == 0
                && !File.Exists(checkpointPath)
                && !hasPublicationMarkers)
            {
                highestVerifiedCheckpointLsn = long.MaxValue;
            }

            // 回放全部 WAL segment。新格式的 checkpoint 只能推进到已验证的独立边界；
            // 独立文件损坏或关联段不可读时，残留 checkpoint 不能单独作为丢弃旧写入的依据。
            // 对没有独立 sidecar、且没有 pending marker 的旧库，上方会保留旧版本仅依赖
            // WAL checkpoint 的行为，避免升级后无法打开已有数据库。
            var memTable = new MemTable();
            int catalogCountBeforeReplay = catalog.Count;
            string tombstoneManifestPath = TsdbPaths.TombstoneManifestPath(root);
            IReadOnlyList<Tombstone> manifestTombstones = TombstoneManifestCodec.LoadWithFallback(tombstoneManifestPath);
            long durableTombstoneCheckpointLsn = GetMaxTombstoneLsn(manifestTombstones);
            var result = walSet.ReplayWithCheckpoint(
                catalog,
                durableCheckpointLsn,
                durableTombstoneCheckpointLsn,
                highestVerifiedCheckpointLsn);
            memTable.ReplayFrom(result.WritePoints);
            long checkpointLsn = result.CheckpointLsn;
            bool catalogDirty = catalog.Count != catalogCountBeforeReplay;

            var seriesWithWalRecord = catalog.Snapshot().Select(e => e.Id).ToHashSet();

            tsdb = new Tsdb(
                options,
                catalog,
                measurements,
                memTable,
                walSet,
                nextSegmentId,
                seriesWithWalRecord,
                segmentManager,
                checkpointLsn,
                catalogDirty,
                MeasurementBatchLedger.Load(TsdbPaths.MeasurementBatchLedgerPath(root)));

            // 加载墓碑清单（文件不存在时返回空集合）
            tsdb.Tombstones.LoadFrom(manifestTombstones);

            // 追加 WAL replay 中 checkpoint 之后的 Delete 记录
            foreach (var del in result.DeleteRecords)
                tsdb.Tombstones.Add(new Tombstone(del.SeriesId, del.FieldName, del.FromTimestamp, del.ToTimestamp, del.Lsn));

            // 重写一遍 manifest（合并 manifest + WAL replay 的结果）
            TombstoneManifestCodec.Save(tombstoneManifestPath, tsdb.Tombstones.All);

            // 所有持久化目录校验完成后再启动 flush 泵，避免构造失败遗留永久后台线程。
            tsdb._flushPump = new FlushPump(tsdb);

            // 启动后台 Flush 线程
            if (options.BackgroundFlush.Enabled)
            {
                tsdb._flushWorker = new BackgroundFlushWorker(tsdb, options.BackgroundFlush);
                tsdb._flushWorker.Start();
            }

            // 启动后台 Compaction 线程
            if (options.Compaction.Enabled)
            {
                tsdb._compactionWorker = new CompactionWorker(tsdb, options.Compaction);
                tsdb._compactionWorker.Start();
            }

            // 启动后台 Retention 线程
            if (options.Retention.Enabled)
            {
                tsdb._retentionWorker = new RetentionWorker(tsdb, options.Retention);
                tsdb.Retention = tsdb._retentionWorker;
                tsdb._retentionWorker.Start();
            }

            if (options.Kv.ExpirerEnabled || options.Kv.CleanupEnabled)
            {
                tsdb._kvExpirerWorker = new KvExpirerWorker(tsdb, options.Kv);
                tsdb._kvExpirerWorker.Start();
            }

            tsdb._meterRegistration = SonnetDbMeter.RegisterEngine(tsdb);
            tsdb._rootDirectoryOwnership = Interlocked.Exchange(ref rootDirectoryOwnership, null);
            return tsdb;
        }
        catch
        {
            try
            {
                if (tsdb is not null)
                {
                    // 启动后段失败时先把 owner 转交给实例。若 Dispose 在进入 committed
                    // 关闭前也失败，owner 会保守保留，避免存活资源与新实例并存。
                    tsdb._rootDirectoryOwnership = Interlocked.Exchange(ref rootDirectoryOwnership, null);
                    DisposeAfterFailedOpen(tsdb);
                }
                else
                {
                    // 构造器尚未返回时，WAL 与 Segment 仍由 Open 的局部变量负责释放。
                    try
                    {
                        segmentManager?.Dispose();
                    }
                    finally
                    {
                        walSet?.Dispose();
                    }
                }
            }
            finally
            {
                ReleaseRootDirectoryOwnership(ref rootDirectoryOwnership);
            }

            throw;
        }
    }

    private static RootDirectoryOwnership AcquireRootDirectoryOwnership(string rootDirectory)
    {
        string normalizedRoot = NormalizeRootDirectoryForOwnership(rootDirectory);
        lock (RootDirectoryOwnersSync)
        {
            if (!RootDirectoryOwners.Add(normalizedRoot))
            {
                throw new IOException(
                    $"数据库根目录 '{normalizedRoot}' 已由当前进程中的另一个 Tsdb 实例打开。");
            }
        }

        FileStream? lifecycleLease = null;
        try
        {
            // FileShare.None 作为跨进程 lease，必须在加载 catalog、打开/升级 WAL 或清理
            // 任意恢复 artifact 前取得。进程内 HashSet 仍保留，因为某些平台不会用 share
            // mode 阻止同一进程重复打开同一文件。
            lifecycleLease = new FileStream(
                TsdbPaths.LifecycleLockPath(normalizedRoot),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            // FileShare.None 在全部支持的平台阻止常规打开；在支持区间锁的平台再加一层
            // byte-range lease，以覆盖 share-mode 仅为 advisory 的文件系统。macOS 不支持
            // FileStream.Lock，那里由 FileShare.None 负责跨进程排他。
            if (!OperatingSystem.IsMacOS())
                lifecycleLease.Lock(0, 1);
            return new RootDirectoryOwnership(normalizedRoot, lifecycleLease);
        }
        catch (IOException exception)
        {
            lifecycleLease?.Dispose();
            lock (RootDirectoryOwnersSync)
                _ = RootDirectoryOwners.Remove(normalizedRoot);

            throw new IOException(
                $"数据库根目录 '{normalizedRoot}' 已由另一个进程中的 Tsdb 实例打开，" +
                "或前一实例仍在完成关闭。",
                exception);
        }
        catch
        {
            lifecycleLease?.Dispose();
            lock (RootDirectoryOwnersSync)
                _ = RootDirectoryOwners.Remove(normalizedRoot);
            throw;
        }
    }

    private static string NormalizeRootDirectoryForOwnership(string rootDirectory)
    {
        string candidatePath = Path.GetFullPath(rootDirectory);
        for (int resolutionCount = 0;
             resolutionCount < MaxRootDirectoryLinkResolutions;
             resolutionCount++)
        {
            string pathRoot = Path.GetPathRoot(candidatePath)
                ?? throw new IOException($"无法确定数据库根目录 '{candidatePath}' 的路径根。");
            string relativePath = Path.GetRelativePath(pathRoot, candidatePath);
            string[] segments = relativePath.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            string currentPath = pathRoot;
            bool resolvedLink = false;

            for (int index = 0; index < segments.Length; index++)
            {
                currentPath = Path.Combine(currentPath, segments[index]);
                FileSystemInfo? target = new DirectoryInfo(currentPath).ResolveLinkTarget(returnFinalTarget: true);
                if (target is null)
                    continue;

                candidatePath = target.FullName;
                for (int suffixIndex = index + 1; suffixIndex < segments.Length; suffixIndex++)
                    candidatePath = Path.Combine(candidatePath, segments[suffixIndex]);
                candidatePath = Path.GetFullPath(candidatePath);
                resolvedLink = true;
                break;
            }

            if (!resolvedLink)
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentPath));
        }

        throw new IOException(
            $"数据库根目录 '{rootDirectory}' 的符号链接解析超过 {MaxRootDirectoryLinkResolutions} 次。");
    }

    private static void ReleaseRootDirectoryOwnership(
        ref RootDirectoryOwnership? rootDirectoryOwnership)
    {
        RootDirectoryOwnership? ownership = Interlocked.Exchange(ref rootDirectoryOwnership, null);
        if (ownership is null)
            return;

        ReleaseRootDirectoryOwnership(ownership);
    }

    private static void ReleaseRootDirectoryOwnershipAfter(
        ref RootDirectoryOwnership? rootDirectoryOwnership,
        Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);

        RootDirectoryOwnership? ownership = Interlocked.Exchange(ref rootDirectoryOwnership, null);
        if (ownership is null)
            return;

        if (completion.IsCompleted)
        {
            ReleaseRootDirectoryOwnership(ownership);
            return;
        }

        _ = completion.ContinueWith(
            static (_, state) => ReleaseRootDirectoryOwnership((RootDirectoryOwnership)state!),
            ownership,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void ReleaseRootDirectoryOwnership(RootDirectoryOwnership ownership)
    {
        try
        {
            ownership.Dispose();
        }
        finally
        {
            lock (RootDirectoryOwnersSync)
                _ = RootDirectoryOwners.Remove(ownership.NormalizedRoot);
        }
    }

    /// <summary>
    /// 同时保存进程内根目录所有权与跨进程文件句柄；必须在全部 WAL、Segment 和模型资源
    /// 已释放后才释放，避免新进程与上一实例的关闭清理交错。
    /// </summary>
    private sealed class RootDirectoryOwnership : IDisposable
    {
        private FileStream? _lifecycleLease;

        public RootDirectoryOwnership(string normalizedRoot, FileStream lifecycleLease)
        {
            ArgumentException.ThrowIfNullOrEmpty(normalizedRoot);
            ArgumentNullException.ThrowIfNull(lifecycleLease);
            NormalizedRoot = normalizedRoot;
            _lifecycleLease = lifecycleLease;
        }

        public string NormalizedRoot { get; }

        public void Dispose()
        {
            Interlocked.Exchange(ref _lifecycleLease, null)?.Dispose();
        }
    }

    /// <summary>数据库对象已构造但启动后续步骤失败时，尽力释放全部资源并保留原始打开异常。</summary>
    private static void DisposeAfterFailedOpen(Tsdb tsdb)
    {
        try
        {
            tsdb.Dispose();
        }
        catch
        {
            // 打开阶段的原始异常更有诊断价值；Dispose 已通过 finally 尽力释放底层资源。
        }
    }

    /// <summary>
    /// 写入一个 Point。自动写入 WAL，追加到 MemTable，必要时触发 Flush。
    /// </summary>
    /// <param name="point">要写入的数据点（已校验）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="point"/> 为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public void Write(Point point)
    {
        ArgumentNullException.ThrowIfNull(point);

        long startTimestamp = SonnetDbMeter.WriteDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

        WalGroupCommitTicket walSync = default;
        FlushPump.FlushRequest? hardCapFlush;
        lock (_schemaSync)
            lock (_writeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfWriteLifecycleClosing();
                ThrowIfFlushRecoveryFault();
                var normalized = EnsureMeasurementSchemaLocked(point, persistImmediately: true);
                WritePointLocked(normalized);
                hardCapFlush = FlushForHardCapIfNeededLocked();

                if (_options.SyncWalOnEveryWrite)
                    walSync = _walGroupCommit.Prepare(_walSet!);
                else
                    FlushWalToOsIfEnabledLocked();
            }

        walSync.Wait();

        // 锁外应用硬上限背压：sealing 积压过多时等待，避免无界内存/磁盘增长。
        ApplyFlushBackpressure(hardCapFlush);

        // 锁外向后台线程发送非阻塞信号
        _flushWorker?.Signal();

        SonnetDbMeter.WritePoints.Add(1);
        if (startTimestamp != 0)
            SonnetDbMeter.WriteDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
    }

    /// <summary>
    /// 批量写入多个 Point。
    /// </summary>
    /// <remarks>
    /// 若 <paramref name="points"/> 实为 <see cref="Point"/>[] / <see cref="List{Point}"/> /
    /// <see cref="ArraySegment{Point}"/>，将自动走 <see cref="WriteMany(ReadOnlySpan{Point})"/>
    /// 的批量快路径（单次 <c>_writeSync</c> 锁、批末仅 Signal 一次）；其它枚举走逐点回退。
    /// </remarks>
    /// <param name="points">要写入的数据点序列。</param>
    /// <returns>成功写入的点数量。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="points"/> 为 null 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public int WriteMany(IEnumerable<Point> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        // 快路径：尽量把可索引集合下沉到 ReadOnlySpan 重载，避免逐点 lock。
        switch (points)
        {
            case Point[] arr:
                return WriteMany((ReadOnlySpan<Point>)arr);
            case List<Point> list:
                return WriteMany((ReadOnlySpan<Point>)System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
            case ArraySegment<Point> seg when seg.Array is not null:
                return WriteMany(seg.AsSpan());
        }

        int count = 0;
        foreach (var point in points)
        {
            Write(point);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 以稳定批次标识写入 measurement。相同标识和相同 payload 的重放返回 0，
    /// 相同标识但 payload 不同则拒绝，账本在成功写入后持久化并随数据库重开恢复。
    /// </summary>
    /// <param name="points">批次中的点。</param>
    /// <param name="batchId">调用方生成的稳定批次标识（ASCII，1-256 字符）。</param>
    /// <returns>首次提交实际写入的点数；幂等重放返回 0。</returns>
    public int WriteMany(ReadOnlySpan<Point> points, string batchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        if (batchId.Length > 256 || batchId.Any(static c => c < 0x21 || c > 0x7e || c == '\t'))
            throw new ArgumentException("Batch ID must contain 1-256 printable ASCII characters.", nameof(batchId));
        if (points.IsEmpty)
            return 0;

        string fingerprint = MeasurementBatchLedger.Fingerprint(points);
        lock (_measurementBatchSync)
        {
            BeforeMeasurementBatchAdmissionTestHook?.Invoke();
            EnterBatchWriteAdmission();
            try
            {
                AfterMeasurementBatchAdmissionTestHook?.Invoke();
                if (_measurementBatchLedger.TryGet(batchId, out string? existing))
                {
                    if (!string.Equals(existing, fingerprint, StringComparison.Ordinal))
                        throw new InvalidOperationException($"Measurement batch '{batchId}' was already committed with a different payload.");
                    if (_measurementBatchLedger.IsCommitted(batchId))
                        return 0;

                    // A crash can leave a durable pending marker after WAL append. Reconcile
                    // against the current read view and only append fields that are absent.
                    var missing = new List<Point>(points.Length);
                    foreach (Point point in points)
                    {
                        if (point is null)
                            continue;
                        var absentFields = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                        var entry = Catalog.GetOrAdd(point);
                        foreach (var field in point.Fields)
                        {
                            bool present = Query.Execute(new PointQuery(
                                entry.Id,
                                field.Key,
                                new TimeRange(point.Timestamp, checked(point.Timestamp + 1)),
                                Limit: 8)).Any(dataPoint => dataPoint.Value == field.Value);
                            if (!present)
                                absentFields[field.Key] = field.Value;
                        }
                        if (absentFields.Count > 0)
                            missing.Add(Point.Create(point.Measurement, point.Timestamp, point.Tags, absentFields));
                    }

                    int repaired = missing.Count == 0 ? 0 : WriteManyFromBatchAdmission(missing.ToArray());
                    _measurementBatchLedger.Commit(batchId, fingerprint);
                    return repaired;
                }

                // Persist intent before WAL writes. Recovery can distinguish a committed
                // batch from an interrupted write and reconcile the latter without replaying
                // already durable fields. admission 保证 Flush 失败已先发生时不会走到此处。
                _measurementBatchLedger.Prepare(batchId, fingerprint);
                int written = WriteManyFromBatchAdmission(points);
                _measurementBatchLedger.Commit(batchId, fingerprint);
                return written;
            }
            finally
            {
                ExitBatchWriteAdmission();
            }
        }
    }

    /// <summary>以稳定批次标识写入 measurement 的集合重载。</summary>
    public int WriteMany(IEnumerable<Point> points, string batchId)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points is Point[] array)
            return WriteMany(array, batchId);
        var materialized = points.ToArray();
        return WriteMany(materialized, batchId);
    }

    /// <summary>
    /// 批量写入多个 Point（高吞吐快路径）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WriteMany(IEnumerable{Point})"/> 不同，本重载按 <see cref="WriteManyChunkSize"/>
    /// 分块处理：每块只获取一次 <c>_writeSync</c> 锁、块末 <see cref="BackgroundFlushWorker.Signal"/>
    /// 一次，显著降低逐点入锁 / 信号开销；块间释放写锁并在锁外施加硬上限背压，避免单批无界撑大
    /// MemTable/WAL。中小批量（≤ 一块）仍是单次入锁。WAL 记录格式与逐点写入完全一致，向后兼容旧库。
    /// </remarks>
    /// <param name="points">要写入的数据点连续切片。</param>
    /// <returns>成功写入的点数量（不含 null 跳过）。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public int WriteMany(ReadOnlySpan<Point> points)
        => WriteManyCore(points, hasBatchWriteAdmission: false);

    /// <summary>
    /// 在已于 flush 失败前取得 batch admission 的情况下写入。调用方持有 admission 时，
    /// 首个写入块以 admission 为线性化点完成，确保随后发生的 flush 故障不会让尚未产生
    /// WAL/MemTable 变更的批次遗留 pending ledger。首块之后的故障可留下 pending ledger，
    /// 此时账本已有对应的可恢复写入意图。
    /// </summary>
    private int WriteManyFromBatchAdmission(ReadOnlySpan<Point> points)
        => WriteManyCore(points, hasBatchWriteAdmission: true);

    private int WriteManyCore(ReadOnlySpan<Point> points, bool hasBatchWriteAdmission)
    {
        if (points.IsEmpty)
            return 0;

        // 分块处理超大批量：每块单独入锁、写入、检查硬上限并在锁外施加背压，块间释放 _writeSync。
        // 这样单批百万点不会在一次持锁内无界撑大 MemTable/WAL 致 OOM 且长时间阻塞其它写入者（C4）。
        int totalWritten = 0;
        int offset = 0;
        while (offset < points.Length)
        {
            int chunkLength = Math.Min(WriteManyChunkSize, points.Length - offset);
            // batch admission 只跨越第一个实际写入块。它的作用是让 Prepare 与首次 WAL/MemTable
            // 变更按同一线性化点发生；后续块恢复正常 fault 检查，避免失败后无界继续写入。
            bool isFirstAdmittedBatchChunk = hasBatchWriteAdmission && offset == 0;
            totalWritten += WriteManyChunk(points.Slice(offset, chunkLength), isFirstAdmittedBatchChunk);
            offset += chunkLength;
        }

        return totalWritten;
    }

    /// <summary>
    /// 单块批量写入：与整批写入语义一致，但只处理 <paramref name="chunk"/> 一段，
    /// 便于 <see cref="WriteMany(ReadOnlySpan{Point})"/> 在块间释放锁并施加硬上限背压。
    /// </summary>
    private int WriteManyChunk(ReadOnlySpan<Point> chunk, bool hasBatchWriteAdmission)
    {
        long startTimestamp = SonnetDbMeter.WriteDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

        int written = 0;
        WalGroupCommitTicket walSync = default;
        FlushPump.FlushRequest? hardCapFlush = null;
        lock (_schemaSync)
            lock (_writeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!hasBatchWriteAdmission)
                {
                    ThrowIfWriteLifecycleClosing();
                    ThrowIfFlushRecoveryFault();
                }

                var normalizedPoints = new Point?[chunk.Length];

                for (int i = 0; i < chunk.Length; i++)
                {
                    var point = chunk[i];
                    if (point is null)
                        continue;

                    normalizedPoints[i] = EnsureMeasurementSchemaLocked(point, persistImmediately: false);
                    written++;
                }

                if (written > 0)
                {
                    PersistMeasurementSchemasLocked();

                    for (int i = 0; i < normalizedPoints.Length; i++)
                    {
                        var normalized = normalizedPoints[i];
                        if (normalized is not null)
                            WritePointLocked(NormalizePointAgainstCurrentSchemaLocked(normalized));
                    }

                    hardCapFlush = FlushForHardCapIfNeededLocked(
                        permitFaultedBatchAdmission: hasBatchWriteAdmission);
                }

                if (_options.SyncWalOnEveryWrite && written > 0)
                    walSync = _walGroupCommit.Prepare(_walSet!);
                else if (written > 0)
                    FlushWalToOsIfEnabledLocked();
            }

        walSync.Wait();

        ApplyFlushBackpressure(hardCapFlush);

        if (written > 0)
        {
            _flushWorker?.Signal();
            SonnetDbMeter.WritePoints.Add(written);
        }

        if (startTimestamp != 0)
            SonnetDbMeter.WriteDuration.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

        return written;
    }

    /// <summary>
    /// 删除某 (seriesId, fieldName) 在 [fromTimestamp, toTimestamp] 时间窗内的所有点。
    /// 在 WAL 中追加 Delete 记录并<b>同步落盘</b>（不受 <see cref="TsdbOptions.SyncWalOnEveryWrite"/> 影响，#194），
    /// 将墓碑加入内存 <see cref="Tombstones"/> 集合；manifest 为恢复加速的可选快照，WAL 为权威恢复来源。
    /// 强制同步保证崩溃后删除不丢失、已落段的被删数据不会复活。
    /// </summary>
    /// <param name="seriesId">目标序列 ID（XxHash64 值）。</param>
    /// <param name="fieldName">目标字段名称（非空）。</param>
    /// <param name="fromTimestamp">删除时间窗起始时间戳（Unix 毫秒，闭区间）。</param>
    /// <param name="toTimestamp">删除时间窗结束时间戳（Unix 毫秒，闭区间）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> 为 null 时抛出。</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> 为空字符串时抛出。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fromTimestamp"/> &gt; <paramref name="toTimestamp"/> 时抛出。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public void Delete(ulong seriesId, string fieldName, long fromTimestamp, long toTimestamp)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        if (fieldName.Length == 0)
            throw new ArgumentException("fieldName 不能为空字符串。", nameof(fieldName));
        if (fromTimestamp > toTimestamp)
            throw new ArgumentOutOfRangeException(nameof(fromTimestamp),
                $"fromTimestamp ({fromTimestamp}) 不能大于 toTimestamp ({toTimestamp})。");

        WalGroupCommitTicket walSync = default;
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfWriteLifecycleClosing();
            ThrowIfFlushRecoveryFault();

            long lsn = _walSet!.AppendDelete(seriesId, fieldName, fromTimestamp, toTimestamp);
            var tomb = new Tombstone(seriesId, fieldName, fromTimestamp, toTimestamp, lsn);
            Tombstones.Add(tomb);
            _tombstoneDeletesSinceCheckpoint++;
            MaybeCheckpointTombstoneManifestLocked(DateTime.UtcNow.Ticks);

            // Delete 无条件同步 WAL（不受 SyncWalOnEveryWrite 影响）：删除必须持久化，否则崩溃后
            // buffered 的 Delete 记录丢失、周期 manifest 又未 checkpoint 时，已落段的被删数据会"复活"
            // （比丢写更危险）。删除频率远低于写入，强制 fsync 代价可接受；group-commit 会批处理并发删除。#194
            walSync = _walGroupCommit.Prepare(_walSet!);
        }

        walSync.Wait();

        // 锁外向后台线程发送非阻塞信号
        _flushWorker?.Signal();
    }

    /// <summary>
    /// 删除某 (measurement, tags, fieldName) 在 [fromTimestamp, toTimestamp] 时间窗内的所有点。
    /// 若序列不存在于 Catalog 中则直接返回 false，不做任何操作。
    /// </summary>
    /// <param name="measurement">Measurement 名称。</param>
    /// <param name="tags">Tag 键值对。</param>
    /// <param name="fieldName">目标字段名称（非空）。</param>
    /// <param name="fromTimestamp">删除时间窗起始时间戳（Unix 毫秒，闭区间）。</param>
    /// <param name="toTimestamp">删除时间窗结束时间戳（Unix 毫秒，闭区间）。</param>
    /// <returns>序列存在并成功标记墓碑时返回 <c>true</c>；序列不存在时返回 <c>false</c>。</returns>
    /// <exception cref="ArgumentNullException">任何参数为 null 时抛出。</exception>
    public bool Delete(string measurement, IReadOnlyDictionary<string, string> tags, string fieldName, long fromTimestamp, long toTimestamp)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(fieldName);

        var key = new SeriesKey(measurement, tags);
        var entry = Catalog.TryGet(key);
        if (entry == null)
            return false;

        Delete(entry.Id, fieldName, fromTimestamp, toTimestamp);
        return true;
    }

    /// <summary>
    /// 注册一个 measurement schema 并立即将整个 schema 文件原子持久化。
    /// </summary>
    /// <param name="schema">已通过 <see cref="MeasurementSchema.Create"/> 校验的 schema。</param>
    /// <returns>注册到 catalog 的同一 schema 实例。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">同名 measurement 已存在。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭。</exception>
    public MeasurementSchema CreateMeasurement(MeasurementSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_schemaSync)
            lock (_writeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfWriteLifecycleClosing();
                ThrowIfFlushRecoveryFault();
                EnsureViewNameAvailable(schema.Name, "measurement");
                Measurements.Add(schema);

                // 立即把全量 schema 集合原子写入磁盘，确保 CREATE 语义具备崩溃安全性
                MarkMeasurementSchemasDirty();
                PersistMeasurementSchemasLocked();
            }

        return schema;
    }

    /// <summary>
    /// 在 schema 锁内按名称读取或创建 measurement，保证幂等 DDL 的并发首次创建只有一个发布者。
    /// </summary>
    /// <param name="name">Measurement 名称。</param>
    /// <param name="schemaFactory">确认同名 measurement 不存在后调用的 schema 工厂。</param>
    /// <returns>已存在或本次新建的 schema。</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空，或工厂返回了不同名称的 schema。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="schemaFactory"/> 为 null。</exception>
    /// <exception cref="ObjectDisposedException">实例已关闭。</exception>
    internal MeasurementSchema GetOrCreateMeasurement(
        string name,
        Func<MeasurementSchema> schemaFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(schemaFactory);

        lock (_schemaSync)
            lock (_writeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfWriteLifecycleClosing();
                ThrowIfFlushRecoveryFault();
                var existing = Measurements.TryGet(name);
                if (existing is not null)
                {
                    return existing;
                }

                EnsureViewNameAvailable(name, "measurement");
                var schema = schemaFactory();
                if (!string.Equals(schema.Name, name, StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Measurement schema 工厂返回名称 '{schema.Name}'，预期为 '{name}'。",
                        nameof(schemaFactory));
                }

                Measurements.Add(schema);
                MarkMeasurementSchemasDirty();
                PersistMeasurementSchemasLocked();
                return schema;
            }
    }

    /// <summary>
    /// 删除指定 measurement 的 schema、series catalog 与对应时序数据。
    /// </summary>
    /// <param name="name">measurement 名称。</param>
    /// <returns>找到并删除返回 <c>true</c>；不存在返回 <c>false</c>。</returns>
    public bool DropMeasurement(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // schema 锁覆盖依赖检查与目录发布；随后按 maintenance → write 获取底层存储锁，
        // 与 Compaction / Retention 互斥，杜绝检查后并发建依赖和段集合数据复活。
        lock (_schemaSync)
            lock (_maintenanceSync)
                lock (_writeSync)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    ThrowIfWriteLifecycleClosing();
                    ThrowIfFlushRecoveryFault();
                    EnsureNoViewDependents(name, "DROP MEASUREMENT");

                    if (!Measurements.Contains(name))
                        return false;

                    SealAndWaitLocked();

                    var removedSeries = Catalog.RemoveMeasurement(name);
                    var removedSeriesIds = removedSeries.Select(static entry => entry.Id).ToHashSet();
                    foreach (ulong seriesId in removedSeriesIds)
                        _seriesWithWalRecord.Remove(seriesId);

                    MemTable.RemoveSeries(removedSeriesIds);
                    RemoveMeasurementSegmentsLocked(removedSeriesIds);

                    Measurements.Remove(name);
                    MarkMeasurementSchemasDirty();
                    PersistMeasurementSchemasLocked();

                    _catalogDirty = true;
                    PersistCatalogCheckpointLocked();

                    return true;
                }
    }

    /// <summary>
    /// 主动触发一次 Flush：把 MemTable 写出为 Segment，追加 WAL Checkpoint，Roll WAL，回收旧段，重置 MemTable。
    /// </summary>
    /// <returns>Segment 构建结果；MemTable 为空时返回 null。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public SegmentBuildResult? FlushNow()
    {
        // 密封在 _writeSync 内 O(1) 完成，编码落盘由 flush 泵在锁外执行；此处同步等待其完成，
        // 保持"FlushNow 返回时数据已落盘"的既有契约。
        return FlushNowAndWait()?.Result;
    }

    /// <summary>
    /// 异步触发一次后台 Flush：仅向 <see cref="BackgroundFlushWorker"/> 发信号后立即返回，
    /// 由后台线程实际执行 Flush。若未启用后台 Flush，则降级为同步 <see cref="FlushNow"/>。
    /// </summary>
    /// <remarks>用于批量入库端点的 <c>?flush=async</c> 档位：低延迟通知 + 不阻塞调用方。</remarks>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public void SignalFlush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfWriteLifecycleClosing();
        var worker = _flushWorker;
        if (worker is not null)
        {
            worker.Signal();
            return;
        }
        // 未启用后台 Flush：降级为同步执行，保证 flush=async 始终具有"已入盘"语义的最终一致。
        FlushNow();
    }

    /// <summary>
    /// 枚举当前已落盘的 Segment 文件，按 SegmentId 升序排列。
    /// </summary>
    /// <returns>已落盘段文件的 (SegmentId, FilePath) 只读列表。</returns>
    public IReadOnlyList<(long SegmentId, string Path)> ListSegments()
    {
        var list = new List<(long SegmentId, string Path)>(TsdbPaths.EnumerateSegments(RootDirectory));
        list.Sort(static (a, b) => a.SegmentId.CompareTo(b.SegmentId));
        return list.AsReadOnly();
    }

    /// <summary>
    /// 在写锁内创建一致备份：先 checkpoint 时序、表、文档和 KV，再由调用方复制文件并生成 manifest。
    /// </summary>
    internal SonnetDB.Backup.BackupManifest CreateConsistentBackup(
        SonnetDB.Backup.BackupCreateOptions options,
        Func<Tsdb, SonnetDB.Backup.BackupCreateOptions, IReadOnlyList<string>, SonnetDB.Backup.BackupManifest> afterCheckpoint)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(afterCheckpoint);

        // schema 锁覆盖 checkpoint、逐文件复制和 manifest 构建，避免备份观察到跨 catalog 的半次 DDL。
        lock (_schemaSync)
        {
            lock (_writeSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ThrowIfWriteLifecycleClosing();

                SealAndWaitLocked();
                _walSet?.Sync();
                TombstoneManifestCodec.Save(TsdbPaths.TombstoneManifestPath(RootDirectory), Tombstones.All);
                PersistMeasurementSchemasLocked();
                CatalogFileCodec.Save(Catalog, TsdbPaths.CatalogPath(RootDirectory));
                _catalogDirty = false;

                return Tables.ExecuteConsistentBackup(() =>
                {
                    Tables.CheckpointAll();
                    Documents.CompactAll();
                    return Generations.ExecuteConsistentBackup(() =>
                        Graphs.ExecuteConsistentBackup(() =>
                        {
                            var checkpointedKeyspaces = Keyspaces.CheckpointOpened();
                            return afterCheckpoint(this, options, checkpointedKeyspaces);
                        }));
                });
            }
        }
    }

    /// <summary>
    /// 在数据库级 schema 锁内执行一次跨 catalog 变更，保证关系表与扩展目录的发布不可交错。
    /// </summary>
    /// <typeparam name="TResult">操作返回值类型。</typeparam>
    /// <param name="action">需要串行执行的 schema 变更。</param>
    /// <returns>操作返回值。</returns>
    internal TResult ExecuteSchemaMutation<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnterSchemaMutationAdmission();
        try
        {
            BeforeSchemaMutationLockTestHook?.Invoke();
            lock (_schemaSync)
            {
                SchemaMutationLockAcquiredTestHook?.Invoke();
                // Admission is the linearization point for this mutation.  Once it has
                // succeeded, shutdown waits for it instead of rejecting it merely because
                // the lifecycle-closing bit was raised while it was waiting for the lock.
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
                return action();
            }
        }
        finally
        {
            ExitSchemaMutationAdmission();
        }
    }

    /// <summary>
    /// 关闭数据库：先关闭后台 Flush 线程，再 Flush 剩余 MemTable、保存 catalog、关闭 WAL。
    /// 已启动的流式查询可继续完成其已取得的段读取租约；在最后一个租约归还前，同一根目录
    /// 仍保持跨进程所有权，新的 <see cref="Tsdb"/> 实例不能打开该目录。
    /// </summary>
    public void Dispose()
    {
        lock (_shutdownSync)
        {
            if (_disposed)
            {
                ReleaseRootDirectoryOwnership(ref _rootDirectoryOwnership);
                return;
            }

            BeginWriteLifecycleShutdown();
            try
            {
                SonnetDbMeter.UnregisterEngine(_meterRegistration);
                StopBackgroundWorkers();
            }
            finally
            {
                DisposeCommittedState();
            }
        }
    }

    /// <summary>
    /// 按依赖顺序停止后台 worker。每个 finally 均保证即使前一个 worker 的关闭失败，
    /// 后续 worker 与 flush 泵仍会被停止，避免在已提交资源处置后继续访问它们。
    /// </summary>
    private void StopBackgroundWorkers()
    {
        try
        {
            BeforeBackgroundWorkerShutdownTestHook?.Invoke();
        }
        finally
        {
            try
            {
                _kvExpirerWorker?.Dispose();
            }
            finally
            {
                _kvExpirerWorker = null;
                try
                {
                    // Retention 与 Compaction 必须在 Flush 泵之前停止。
                    _retentionWorker?.Dispose();
                }
                finally
                {
                    _retentionWorker = null;
                    try
                    {
                        _compactionWorker?.Dispose();
                    }
                    finally
                    {
                        _compactionWorker = null;
                        try
                        {
                            _flushWorker?.Dispose();
                        }
                        finally
                        {
                            _flushWorker = null;
                            try
                            {
                                // 排空泵后才能关闭 WAL；否则在飞 flush 可能访问已释放资源。
                                _flushPump?.Dispose();
                            }
                            finally
                            {
                                _flushPump = null;
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>停止后台 worker 后，在 schema → write 锁序下处置数据库的已提交资源。</summary>
    private void DisposeCommittedState()
    {

        // 关闭提交段与 DDL、备份使用相同的 schema → write 锁序；在途 schema 变更先完成，
        // 关闭取得 schema 锁后再处置各 manager，防止关闭返回后继续发布 Modbus 绑定。
        lock (_schemaSync)
            lock (_writeSync)
            {
                WalSegmentSet? walSetToDispose = _walSet;
                _walSet = null;

                try
                {
                    if (walSetToDispose != null)
                    {
                        _walGroupCommit.FlushPending(walSetToDispose);

                        // 任何密封 Flush 失败后都不能再创建更高 checkpoint：那会让 WAL replay
                        // 跳过失败密封表的数据。关闭时保留 WAL，交给下次 Open 的 marker 恢复路径。
                        Exception? flushRecoveryFault = Volatile.Read(ref _flushRecoveryFault);
                        if (flushRecoveryFault is not null)
                        {
                            ReportDiagnostic(
                                "Dispose.FinalFlush",
                                TsdbDiagnosticSeverity.Warning,
                                "检测到未恢复的 Flush 发布失败；跳过 final flush 并保留 WAL 供下次启动恢复。",
                                flushRecoveryFault);
                        }
                        // 尝试 Flush 剩余数据（Flush 内部会保存 manifest）
                        else if (MemTable.PointCount > 0)
                        {
                            try
                            {
                                PersistMeasurementSchemasLocked();
                                PersistCatalogCheckpointLocked();
                                var flushing = _activeMemTable;
                                long lsnBeforeFlush = flushing.LastLsn;
                                var result = _flushCoordinator.Flush(
                                    flushing,
                                    walSetToDispose,
                                    _nextSegmentId++,
                                    Tombstones,
                                    Catalog,
                                    Measurements);
                                if (result != null)
                                {
                                    _checkpointLsn = lsnBeforeFlush;
                                    // 关闭路径不发布段索引（进程即将退出），换空表保持不变式一致。
                                    _activeMemTable = new MemTable();
                                }
                            }
                            catch (Exception ex)
                            {
                                // Flush 失败不应阻止 catalog 保存和 WAL 关闭
                                ReportDiagnostic(
                                    "Dispose.FinalFlush",
                                    TsdbDiagnosticSeverity.Error,
                                    "Dispose final flush 失败；异常已被捕获，WAL 将保留为恢复来源。",
                                    ex);
                            }
                        }
                        else
                        {
                            // MemTable 为空时，仍需持久化 manifest（可能有 Delete 操作但没有写入）
                            try
                            {
                                TombstoneManifestCodec.Save(TsdbPaths.TombstoneManifestPath(RootDirectory), Tombstones.All);
                            }
                            catch
                            {
                                // manifest 保存失败不阻止关闭（WAL 仍可作为恢复手段）

                                // 保存 measurement schema
                                try
                                {
                                    MeasurementSchemaCodec.Save(
                                        TsdbPaths.MeasurementSchemaPath(RootDirectory),
                                        Measurements.Snapshot());
                                }
                                catch
                                {
                                    // schema 保存失败不阻止关闭（已写入磁盘的版本仍可恢复）
                                }
                            }
                        }

                        // 保存 schema/catalog
                        PersistMeasurementSchemasLocked();
                        CatalogFileCodec.Save(Catalog, TsdbPaths.CatalogPath(RootDirectory));
                        _catalogDirty = false;
                    }
                }
                finally
                {
                    DisposeCommittedResources(walSetToDispose);
                }
            }
    }

    private void DisposeCommittedResources(WalSegmentSet? walSetToDispose)
    {
        Task segmentReaderLeaseDrain = Task.CompletedTask;
        try
        {
            try
            {
                walSetToDispose?.Dispose();
            }
            catch (Exception ex)
            {
                ReportDiagnostic(
                    "Dispose.WalClose",
                    TsdbDiagnosticSeverity.Warning,
                    "Dispose 关闭 WAL 时发生异常；资源释放会继续执行。",
                    ex);
            }

            try
            {
                _walGroupCommit.Dispose();
            }
            finally
            {
                try
                {
                    _modbus.Dispose();
                }
                finally
                {
                    try
                    {
                        _tables.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            _generations.Dispose();
                        }
                        finally
                        {
                            try
                            {
                                _documents.Dispose();
                            }
                            finally
                            {
                                try
                                {
                                    _graphs.Dispose();
                                }
                                finally
                                {
                                    try
                                    {
                                        _keyspaces.Dispose();
                                    }
                                    finally
                                    {
                                        try
                                        {
                                            segmentReaderLeaseDrain =
                                                Segments.DisposeAndGetSnapshotLeaseDrainTask();
                                        }
                                        finally
                                        {
                                            _sqlParallelCoordinator.Dispose();
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ReleaseRootDirectoryOwnershipAfter(
                ref _rootDirectoryOwnership,
                segmentReaderLeaseDrain);
        }
    }

    // ── 内部辅助 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 2 密封（调用方必须持有 _writeSync 锁）：把当前活跃 MemTable 换成新空实例、
    /// 追加 WAL checkpoint 前的 catalog 持久化，并把密封表 + sealLsn + segId 打包入 flush 泵队列。
    /// 本方法只做 O(1) 的 swap/入队与少量 catalog I/O，<b>不</b>执行编码落盘或 WAL 回收（由泵在锁外做）。
    /// </summary>
    /// <returns>已入队的 flush 请求；活跃表为空或引擎已关闭时返回 null。</returns>
    private FlushPump.FlushRequest? SealAndEnqueueLocked(bool permitFaultedBatchAdmission = false)
    {
        if (!permitFaultedBatchAdmission)
            ThrowIfFlushRecoveryFault();

        if (_walSet == null || _flushPump == null)
            return null;

        _walGroupCommit.FlushPending(_walSet);
        PersistMeasurementSchemasLocked();

        var sealing = _activeMemTable;
        if (sealing.PointCount == 0)
        {
            PersistCatalogCheckpointLocked();
            return null;
        }

        // WAL 回收前必须先持久化完整 catalog snapshot（泵稍后会回收旧 WAL segment）。
        // 这样旧 WAL segment 中的 CreateSeries 被回收后，segment 中的 SeriesId 仍能通过 catalog 文件解析。
        PersistCatalogCheckpointLocked();

        long sealLsn = sealing.LastLsn;
        long segId = _nextSegmentId++;

        // 在 _writeSync 内只做 Roll（不追加 checkpoint）：把被密封数据与 seal 之后的并发写入用 roll
        // 边界干净隔开——被密封数据完整落在 roll 之前的段（lastLsn = sealLsn），并发写入落到新 active 段
        // （startLsn = sealLsn+1）。checkpoint 记录必须等段编码成功后才由泵追加（崩溃安全：编码失败时
        // 不能存在"数据已落盘"的 checkpoint，否则 replay 会跳过尚未真正落盘的数据）。
        // Roll 必须在锁内、任何后续 append 之前完成，故放在这里。
        _walSet.Roll();

        // 原子密封：换新空表 + 旧表进 sealing 列表（SegmentManager 一次 Volatile.Write 发布）。
        var fresh = new MemTable();
        var sealed_ = Segments.SealActiveAndSwap(fresh);
        _activeMemTable = fresh;

        // 理论上 sealed_ 就是 sealing；防御式取真实密封表。
        var request = new FlushPump.FlushRequest(sealed_ ?? sealing, sealLsn, segId);
        _flushPump.Enqueue(request);
        return request;
    }

    /// <summary>
    /// 由 flush 泵线程调用（锁外）：把已密封的 MemTable 编码落盘，先原子发布新段并从
    /// sealing 列表移除该表，再提交 WAL checkpoint/回收旧段。全程不获取 <c>_writeSync</c>。
    /// </summary>
    internal void ExecutePumpFlush(FlushPump.FlushRequest request)
    {
        ThrowIfFlushRecoveryFault();

        // 捕获 WAL 引用；若已进入 Dispose 且 WAL 已释放，则跳过（Dispose 会先排空泵，通常不会走到）。
        var walSet = _walSet;
        if (walSet == null)
        {
            Segments.ReleaseSealed(request.SealedTable);
            return;
        }

        using var activity = SonnetDbActivitySource.StartOperation("sonnetdb.flush", "flush");
        activity?.SetTag("sonnetdb.segment.id", request.SegmentId);
        long startTimestamp = Stopwatch.GetTimestamp();
        long flushedPoints = request.SealedTable.PointCount;

        SegmentBuildResult? result;
        try
        {
            result = _flushCoordinator.FlushSealed(
                request.SealedTable,
                walSet,
                request.SealLsn,
                request.SegmentId,
                Tombstones,
                Catalog,
                Measurements);
        }
        catch (Exception ex)
        {
            SonnetDbActivitySource.RecordFailure(activity, ex);
            SonnetDbMeter.FlushDuration.Record(
                Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, SonnetDbMeter.OutcomeError);
            throw;
        }

        if (result == null)
        {
            // 空表（不应发生，密封前已判空）：仅从 sealing 移除。
            Segments.ReleaseSealed(request.SealedTable);
            return;
        }

        // 先原子发布：接入新段 + 从 sealing 移除该表（SegmentManager 一次 Volatile.Write）。
        // WAL checkpoint/recycle 必须在此之后执行；若读快照发布失败，WAL 仍完整保留，
        // 下次启动可删除/校验该段并回放，而不会因提前回收 WAL 丢失数据。
        Segments.PublishSegmentAndReleaseSealed(result.Path, request.SealedTable);

        // 只有段已由 SegmentManager 成功打开并纳入当前快照，才允许 WAL 越过 sealLsn。
        _flushCoordinator.FinalizeSealedFlush(walSet, request.SealLsn);
        request.Result = result;

        SonnetDbMeter.FlushDuration.Record(
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, SonnetDbMeter.OutcomeOk);
        SonnetDbMeter.FlushPoints.Add(flushedPoints);
        SonnetDbMeter.FlushBytes.Add(result.TotalBytes);

        // 更新 checkpoint LSN（无锁；FIFO 单泵保证 sealLsn 单调，CAS 提升即可）。
        // 关键：泵<b>绝不</b>获取 _writeSync，从而与"持 _writeSync 触发 flush"的路径无锁序环。
        long prev = Interlocked.Read(ref _checkpointLsn);
        while (request.SealLsn > prev)
        {
            long witnessed = Interlocked.CompareExchange(ref _checkpointLsn, request.SealLsn, prev);
            if (witnessed == prev)
                break;
            prev = witnessed;
        }
        if (Tombstones.Count > 0)
        {
            Interlocked.Exchange(ref _tombstoneDeletesSinceCheckpoint, 0);
            Interlocked.Exchange(ref _lastTombstoneCheckpointUtcTicks, DateTime.UtcNow.Ticks);
        }
    }

    /// <summary>
    /// 在已持有 <c>_writeSync</c> 的上下文里密封并<b>同步等待</b>该次 flush 完成。
    /// 因为 flush 泵绝不获取 <c>_writeSync</c>（checkpoint 更新走 Interlocked），故此处持锁等待
    /// 不会死锁。用于 DropMeasurement / backup 等要求"flush 完成后再继续"且已在写锁内的低频路径。
    /// </summary>
    private void SealAndWaitLocked()
    {
        var request = SealAndEnqueueLocked();
        request?.Completion.Task.GetAwaiter().GetResult();
        // 排空泵：等待所有在飞 flush（可能是本次 seal 之前由后台 worker 入队的）全部发布为段。
        // DropMeasurement / backup 依赖"所有 drop 相关数据已落为可见段"再扫描移除；仅等自身 seal
        // 在活跃表为空（seal 返回 null）时不足以覆盖先前入队的请求，故显式 Drain。泵不取 _writeSync，无死锁。
        _flushPump?.Drain();
    }

    private void ThrowIfFlushRecoveryFault()
    {
        Exception? fault = Volatile.Read(ref _flushRecoveryFault);
        if (fault is not null)
        {
            throw new InvalidOperationException(
                "此前的 Flush 发布失败，后续 Flush 和 checkpoint 已被停止；请关闭并重新打开数据库以从 WAL 恢复。",
                fault);
        }
    }

    private void ThrowIfWriteLifecycleClosing()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _writeLifecycleClosing) != 0, this);

    /// <summary>
    /// 先阻止新的批次 admission 并等待已获 admission 的批次建立其首个写入边界，
    /// 再在 schema → write 锁序下关闭写生命周期。等待阶段不持有任何写相关锁，
    /// 因此不会阻塞已获 admission 的批次或 flush 泵。
    /// </summary>
    private void BeginWriteLifecycleShutdown()
    {
        CloseBatchWriteAdmission();
        CloseSchemaMutationAdmission();
        Volatile.Write(ref _writeLifecycleClosing, 1);

        var spinner = new SpinWait();
        while ((Volatile.Read(ref _batchWriteAdmissionState) & ~BatchWriteAdmissionClosed) != 0)
            spinner.SpinOnce();

        spinner = new SpinWait();
        while ((Volatile.Read(ref _schemaMutationAdmissionState) & ~SchemaMutationAdmissionClosed) != 0)
            spinner.SpinOnce();

        lock (_schemaSync)
            lock (_writeSync)
                _disposed = true;
    }

    /// <summary>
    /// 获取一个 schema mutation admission。关闭开始后不再接受新的 mutation；已取得
    /// admission 的调用由关闭流程等待完成。
    /// </summary>
    private void EnterSchemaMutationAdmission()
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
            ThrowIfWriteLifecycleClosing();

            long observed = Volatile.Read(ref _schemaMutationAdmissionState);
            if ((observed & SchemaMutationAdmissionClosed) != 0)
            {
                ThrowIfWriteLifecycleClosing();
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
                throw new ObjectDisposedException(GetType().Name);
            }

            if (Interlocked.CompareExchange(
                    ref _schemaMutationAdmissionState,
                    observed + 1,
                    observed) != observed)
            {
                continue;
            }

            // Close may have won the race immediately after the increment.  In that case
            // withdraw the admission and retry so a mutation cannot start after shutdown
            // has sealed the gate.
            if ((Volatile.Read(ref _schemaMutationAdmissionState) & SchemaMutationAdmissionClosed) == 0
                && Volatile.Read(ref _writeLifecycleClosing) == 0
                && !Volatile.Read(ref _disposed))
            {
                return;
            }

            _ = Interlocked.Decrement(ref _schemaMutationAdmissionState);
            ThrowIfWriteLifecycleClosing();
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        }
    }

    private void ExitSchemaMutationAdmission()
        => _ = Interlocked.Decrement(ref _schemaMutationAdmissionState);

    private void CloseSchemaMutationAdmission()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _schemaMutationAdmissionState);
            if ((observed & SchemaMutationAdmissionClosed) != 0)
                return;

            if (Interlocked.CompareExchange(
                    ref _schemaMutationAdmissionState,
                    observed | SchemaMutationAdmissionClosed,
                    observed) == observed)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 获取 measurement 批次的 flush-failure admission。成功返回后，即使失败回调随后运行，
    /// 当前批次仍按此前线性化顺序完成；失败先封门的批次在写入 ledger 前被拒绝。
    /// </summary>
    private void EnterBatchWriteAdmission()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _batchWriteAdmissionState);
            if ((observed & BatchWriteAdmissionClosed) != 0)
            {
                ThrowIfFlushRecoveryFault();
                ThrowIfWriteLifecycleClosing();
                throw new InvalidOperationException("Flush failure admission has been closed.");
            }

            if (Volatile.Read(ref _flushRecoveryFault) is not null)
            {
                CloseBatchWriteAdmission();
                ThrowIfFlushRecoveryFault();
            }

            if (Interlocked.CompareExchange(
                    ref _batchWriteAdmissionState,
                    observed + 1,
                    observed) != observed)
            {
                continue;
            }

            if ((Volatile.Read(ref _batchWriteAdmissionState) & BatchWriteAdmissionClosed) == 0
                && Volatile.Read(ref _flushRecoveryFault) is null)
            {
                return;
            }

            _ = Interlocked.Decrement(ref _batchWriteAdmissionState);
            ThrowIfFlushRecoveryFault();
            ThrowIfWriteLifecycleClosing();
        }
    }

    private void ExitBatchWriteAdmission()
        => _ = Interlocked.Decrement(ref _batchWriteAdmissionState);

    private void CloseBatchWriteAdmission()
    {
        while (true)
        {
            long observed = Volatile.Read(ref _batchWriteAdmissionState);
            if ((observed & BatchWriteAdmissionClosed) != 0)
                return;

            if (Interlocked.CompareExchange(
                    ref _batchWriteAdmissionState,
                    observed | BatchWriteAdmissionClosed,
                    observed) == observed)
            {
                return;
            }
        }
    }

    internal void OnPumpFlushFailed(FlushPump.FlushRequest request, Exception ex)
    {
        _ = Interlocked.CompareExchange(ref _flushRecoveryFault, ex, null);
        CloseBatchWriteAdmission();
        Volatile.Write(ref _lastError, ex);
        ReportDiagnostic(
            "FlushPump.Flush",
            TsdbDiagnosticSeverity.Error,
            "后台 flush 泵执行失败；密封表保持在查询快照中，后续 flush 已停止以保留 WAL 恢复边界。",
            ex);
    }

    /// <summary>
    /// 触发一次 flush 并<b>同步等待</b>其落盘完成（用于 FlushNow / backup / schema 提升 / Dispose）。
    /// 在 <c>_writeSync</c> 内密封入队，<b>锁外</b>等待泵完成，避免持锁等待造成锁序问题。
    /// </summary>
    /// <returns>已完成的 flush 请求；活跃表为空时返回 null。</returns>
    private FlushPump.FlushRequest? FlushNowAndWait()
    {
        FlushPump.FlushRequest? request;
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfWriteLifecycleClosing();
            request = SealAndEnqueueLocked();
        }

        if (request == null)
            return null;

        request.Completion.Task.GetAwaiter().GetResult();
        return request;
    }

    /// <summary>
    /// 硬上限触发的 flush：同步等待其落盘完成（内存红线，写入者必须停下抽干）。
    /// 调用方<b>不应</b>持有 <c>_writeSync</c>（在锁外调用）。
    /// </summary>
    private static void ApplyFlushBackpressure(FlushPump.FlushRequest? hardCapFlush)
    {
        if (hardCapFlush == null)
            return;

        try
        {
            hardCapFlush.Completion.Task.GetAwaiter().GetResult();
        }
        catch
        {
            // flush 失败已在 OnPumpFlushFailed 上报；此处不再抛，写入路径继续。
        }
    }

    /// <summary>
    /// 由后台 Flush 线程调用的 Flush 入口：仅在 <c>_writeSync</c> 内 O(1) 密封入队后立即返回，
    /// 编码落盘由 flush 泵在锁外异步完成——后台线程不再被整个 flush 阻塞（Phase 2 核心）。
    /// </summary>
    internal void InternalFlushFromBackground()
    {
        lock (_writeSync)
        {
            if (_disposed || Volatile.Read(ref _writeLifecycleClosing) != 0)
                return;
            SealAndEnqueueLocked();
        }
    }

    internal void ReportBackgroundWorkerDiagnostic(
        string operation,
        TsdbDiagnosticSeverity severity,
        string message,
        Exception? exception)
        => ReportDiagnostic(operation, severity, message, exception);

    internal int CleanExpiredKeyspacesFromBackground(int limitPerKeyspace)
    {
        // 测试注入点：模拟后台过期清理抛出，验证 KvExpirerWorker 的诊断兜底（C11）。
        _kvExpirerFaultHook?.Invoke();
        return Keyspaces.CleanExpiredOpened(DateTimeOffset.UtcNow, limitPerKeyspace);
    }

    internal KvCleanupRoundResult CleanupKeyspacesFromBackground(int maxFilesPerKeyspace)
    {
        _kvCleanupFaultHook?.Invoke();
        return Keyspaces.CleanupPendingOpenedWithResult(maxFilesPerKeyspace)
            + Tables.CleanupPendingFilesWithResult(maxFilesPerKeyspace);
    }

    internal KvCleanupRoundResult GetCleanupStatusFromBackground()
        => Keyspaces.GetCleanupStatusOpened() + Tables.GetCleanupStatusOpened();

    internal KvCleanupThrottleReason GetInjectedCleanupThrottleReason()
        => _kvCleanupThrottleHook?.Invoke() ?? KvCleanupThrottleReason.None;

    /// <summary>仅供测试注入 KV 过期清理故障；生产恒为 null。</summary>
    internal Action? _kvExpirerFaultHook;

    /// <summary>仅供测试注入 generation cleanup 节流原因；生产恒为 null。</summary>
    internal Func<KvCleanupThrottleReason>? _kvCleanupThrottleHook;

    /// <summary>仅供测试注入 generation cleanup 故障；生产恒为 null。</summary>
    internal Action? _kvCleanupFaultHook;

    private void WritePointLocked(Point point)
    {
        var entry = Catalog.GetOrAdd(point);

        // 若是本进程首次写入该 series，向 WAL 追加 CreateSeries 记录
        if (_seriesWithWalRecord.Add(entry.Id))
        {
            _walSet!.AppendCreateSeries(entry.Id, entry.Measurement, entry.Tags);
            _catalogDirty = true;
        }

        // 每个字段写入 WAL 和 MemTable。热路径：绝大多数 Point 的 Fields 实为 Dictionary，
        // 直接用其 struct 枚举器遍历，避免 IReadOnlyDictionary foreach 装箱枚举器（C3）。
        if (point.Fields is Dictionary<string, FieldValue> concreteFields)
        {
            foreach (var (fieldName, value) in concreteFields)
            {
                long lsn = _walSet!.AppendWritePoint(entry.Id, point.Timestamp, fieldName, value);
                MemTable.Append(entry.Id, point.Timestamp, fieldName, value, lsn);
            }
        }
        else
        {
            foreach (var (fieldName, value) in point.Fields)
            {
                long lsn = _walSet!.AppendWritePoint(entry.Id, point.Timestamp, fieldName, value);
                MemTable.Append(entry.Id, point.Timestamp, fieldName, value, lsn);
            }
        }
    }

    /// <summary>
    /// 若启用（默认）且未走每写 fsync，则把 WAL 缓冲 flush 到 OS（不 fsync），使普通进程崩溃后
    /// 已确认写可恢复（#196）。调用方须持有 <c>_writeSync</c>；开销为一次用户态→内核态拷贝。
    /// </summary>
    private void FlushWalToOsIfEnabledLocked()
    {
        if (_options.FlushWalToOsOnWrite && _walSet is not null)
            _walSet.FlushToOs();
    }

    /// <summary>
    /// 硬上限背压：活跃 MemTable 超过硬上限（内存紧急阈值）时，在 <c>_writeSync</c> 内密封入队（O(1)），
    /// 返回该请求供调用方在锁外<b>同步等待</b>其落盘完成。返回 null 表示未触发。
    /// </summary>
    private FlushPump.FlushRequest? FlushForHardCapIfNeededLocked(
        bool permitFaultedBatchAdmission = false)
    {
        long hardCapBytes = _options.FlushPolicy.ResolveHardCapBytes();
        if (hardCapBytes <= 0)
            return null;

        if (MemTable.EstimatedBytes < hardCapBytes)
            return null;

        // 已在 flush 失败前获得 batch admission 的首块必须能够完成其账本提交。若失败
        // 已可见，保留 active table 由 WAL 恢复，不再制造一个必然失败的第二个 sealing 请求。
        if (permitFaultedBatchAdmission && Volatile.Read(ref _flushRecoveryFault) is not null)
            return null;

        return SealAndEnqueueLocked(permitFaultedBatchAdmission);
    }

    private void RemoveMeasurementSegmentsLocked(IReadOnlySet<ulong> removedSeriesIds)
    {
        if (removedSeriesIds.Count == 0 || Segments.SegmentCount == 0)
            return;

        // 持读租约：即便本方法已在 _maintenanceSync 内（与其它维护操作互斥），仍持租约保证
        // MeasurementDropCompactor.RewriteWithoutSeries 解码 block 期间 reader 不被回收（防御性，与
        // Compaction 路径一致）。
        using var lease = Segments.AcquireSnapshot();
        var sourceReaders = lease.Readers.ToArray();
        foreach (var reader in sourceReaders)
        {
            bool hasDroppedSeries = false;
            bool hasKeptSeries = false;
            foreach (var block in reader.Blocks)
            {
                if (removedSeriesIds.Contains(block.SeriesId))
                {
                    hasDroppedSeries = true;
                    continue;
                }

                hasKeptSeries = true;
            }

            if (!hasDroppedSeries)
                continue;

            long sourceSegmentId = reader.Header.SegmentId;
            if (!hasKeptSeries)
            {
                SegmentReplacementManifest.CommitDroppedSegments(RootDirectory, [sourceSegmentId]);
                Segments.DropSegments([sourceSegmentId]);
                foreach (string artifactPath in TsdbPaths.SegmentArtifactPaths(RootDirectory, sourceSegmentId))
                    TryDeleteSegmentArtifact(artifactPath);
                continue;
            }

            long replacementSegmentId = AllocateSegmentId();
            string replacementPath = TsdbPaths.SegmentPath(RootDirectory, replacementSegmentId);

            SegmentReplacementManifest.RecordPendingReplacement(
                RootDirectory,
                replacementSegmentId,
                [sourceSegmentId]);

            bool touched = MeasurementDropCompactor.RewriteWithoutSeries(
                this,
                reader,
                removedSeriesIds,
                replacementSegmentId,
                replacementPath,
                out var result);

            if (!touched)
                continue;

            if (result is null)
            {
                SegmentReplacementManifest.CommitDroppedSegments(RootDirectory, [sourceSegmentId]);
                Segments.DropSegments([sourceSegmentId]);
            }
            else
            {
                SegmentReplacementManifest.CommitReplacement(
                    RootDirectory,
                    replacementSegmentId,
                    [sourceSegmentId]);
                Segments.SwapSegments([sourceSegmentId], replacementPath);
            }

            foreach (string artifactPath in TsdbPaths.SegmentArtifactPaths(RootDirectory, sourceSegmentId))
                TryDeleteSegmentArtifact(artifactPath);
        }
    }

    private static void TryDeleteSegmentArtifact(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Windows 上 reader/杀毒软件可能短暂占用旧段；manifest 已提交，启动时会压制旧段。
        }
    }

    private Point EnsureMeasurementSchemaLocked(Point point, bool persistImmediately)
    {
        var schema = Measurements.TryGet(point.Measurement);
        if (schema is null)
        {
            EnsureViewNameAvailable(point.Measurement, "measurement");
            var created = CreateSchemaFromPoint(point);
            Measurements.Add(created);
            MarkMeasurementSchemasDirty();
            if (persistImmediately)
                PersistMeasurementSchemasLocked();
            return point;
        }

        // 稳态（无新列 / 无类型提升）不复制 schema.Columns：仅在真检测到变化时才 copy-on-write，
        // 消除每点 new List(schema.Columns) 后丢弃的锁内分配（C3）。
        List<MeasurementColumn>? columns = null;
        var changed = false;
        var promotedIntToFloat = false;
        Dictionary<string, FieldValue>? normalizedFields = null;

        foreach (var (tagName, _) in point.Tags)
        {
            var column = schema.TryGetColumn(tagName);
            if (column is null)
            {
                columns ??= new List<MeasurementColumn>(schema.Columns);
                columns.Add(new MeasurementColumn(tagName, MeasurementColumnRole.Tag, Storage.Format.FieldType.String));
                changed = true;
                continue;
            }

            if (column.Role != MeasurementColumnRole.Tag)
                throw new InvalidOperationException(
                    $"Measurement '{schema.Name}' 的列 '{tagName}' 已声明为 FIELD，不能作为 TAG 写入。");
        }

        foreach (var (fieldName, value) in point.Fields)
        {
            var column = schema.TryGetColumn(fieldName);
            if (column is null)
            {
                columns ??= new List<MeasurementColumn>(schema.Columns);
                columns.Add(CreateFieldColumn(fieldName, value));
                changed = true;
                continue;
            }

            if (column.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException(
                    $"Measurement '{schema.Name}' 的列 '{fieldName}' 已声明为 TAG，不能作为 FIELD 写入。");

            var normalized = NormalizeFieldValue(schema.Name, column, value, out var promoted);
            if (promoted)
            {
                columns ??= new List<MeasurementColumn>(schema.Columns);
                ReplaceColumn(columns, fieldName, column with { DataType = Storage.Format.FieldType.Float64 });
                changed = true;
                promotedIntToFloat = true;
            }

            if (!normalized.Equals(value))
            {
                normalizedFields ??= new Dictionary<string, FieldValue>(point.Fields, StringComparer.Ordinal);
                normalizedFields[fieldName] = normalized;
            }
        }

        if (changed)
        {
            // int→float 提升：先密封当前含旧 Int64 数据的活跃表（换成新空表），使随后写入的
            // Float64 落到新表，同一 key 不会在单个 MemTable 内混型（避免 MemTable.Append 抛异常）。
            // 密封是 O(1)，旧表的落盘由 flush 泵异步完成；查询侧 IsIntFloatCompatible 容忍 MemTable↔段
            // 之间的 int/float 跨源混型，故无需在此同步等待落盘。
            if (promotedIntToFloat && MemTable.PointCount > 0)
                SealAndEnqueueLocked();

            var updated = MeasurementSchema.Create(schema.Name, columns!, schema.CreatedAtUtcTicks);
            Measurements.LoadOrReplace(updated);
            MarkMeasurementSchemasDirty();
            if (persistImmediately)
                PersistMeasurementSchemasLocked();
        }

        if (normalizedFields is null)
            return point;

        return Point.Create(point.Measurement, point.Timestamp, point.Tags, normalizedFields);
    }

    private Point NormalizePointAgainstCurrentSchemaLocked(Point point)
    {
        var schema = Measurements.TryGet(point.Measurement)
            ?? throw new InvalidOperationException(
                $"Measurement '{point.Measurement}' 的 schema 尚未注册，无法写入数据。");

        Dictionary<string, FieldValue>? normalizedFields = null;
        foreach (var (fieldName, value) in point.Fields)
        {
            var column = schema.TryGetColumn(fieldName)
                ?? throw new InvalidOperationException(
                    $"Measurement '{schema.Name}' 的 FIELD 列 '{fieldName}' 尚未注册，无法写入数据。");

            if (column.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException(
                    $"Measurement '{schema.Name}' 的列 '{fieldName}' 已声明为 TAG，不能作为 FIELD 写入。");

            var normalized = NormalizeFieldValue(schema.Name, column, value, out var promoted);
            if (promoted)
                throw new InvalidOperationException(
                    $"Measurement '{schema.Name}' 的 FIELD 列 '{fieldName}' 在批量 schema 合并后仍需要类型提升。");

            if (!normalized.Equals(value))
            {
                normalizedFields ??= new Dictionary<string, FieldValue>(point.Fields, StringComparer.Ordinal);
                normalizedFields[fieldName] = normalized;
            }
        }

        return normalizedFields is null
            ? point
            : Point.Create(point.Measurement, point.Timestamp, point.Tags, normalizedFields);
    }

    private static MeasurementSchema CreateSchemaFromPoint(Point point)
    {
        var columns = new List<MeasurementColumn>(point.Tags.Count + point.Fields.Count);
        foreach (var (tagName, _) in point.Tags)
            columns.Add(new MeasurementColumn(tagName, MeasurementColumnRole.Tag, Storage.Format.FieldType.String));
        foreach (var (fieldName, value) in point.Fields)
            columns.Add(CreateFieldColumn(fieldName, value));
        return MeasurementSchema.Create(point.Measurement, columns);
    }

    private static MeasurementColumn CreateFieldColumn(string fieldName, FieldValue value)
        => value.Type == Storage.Format.FieldType.Vector
            ? new MeasurementColumn(fieldName, MeasurementColumnRole.Field, value.Type, value.VectorDimension)
            : new MeasurementColumn(fieldName, MeasurementColumnRole.Field, value.Type);

    private static FieldValue NormalizeFieldValue(
        string measurement,
        MeasurementColumn column,
        FieldValue value,
        out bool promotedIntToFloat)
    {
        promotedIntToFloat = false;
        if (column.DataType == Storage.Format.FieldType.Float64 && value.Type == Storage.Format.FieldType.Int64)
            return FieldValue.FromDouble(value.AsLong());

        if (column.DataType == Storage.Format.FieldType.Int64 && value.Type == Storage.Format.FieldType.Float64)
        {
            promotedIntToFloat = true;
            return value;
        }

        if (column.DataType == Storage.Format.FieldType.Vector && value.Type == Storage.Format.FieldType.Vector)
        {
            var expectedDim = column.VectorDimension
                ?? throw new InvalidOperationException(
                    $"Measurement '{measurement}' 的 VECTOR 列 '{column.Name}' 缺少维度声明。");
            if (value.VectorDimension != expectedDim)
                throw new InvalidOperationException(
                    $"Measurement '{measurement}' 的 VECTOR 列 '{column.Name}' 维度不匹配：声明 {expectedDim}，实际 {value.VectorDimension}。");
            return value;
        }

        if (column.DataType != value.Type)
            throw new InvalidOperationException(
                $"Measurement '{measurement}' 的 FIELD 列 '{column.Name}' 期望 {column.DataType}，实际写入 {value.Type}。");

        return value;
    }

    private static void ReplaceColumn(List<MeasurementColumn> columns, string name, MeasurementColumn replacement)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i].Name, name, StringComparison.Ordinal))
            {
                columns[i] = replacement;
                return;
            }
        }
    }

    /// <summary>
    /// 返回可作为 WAL replay 跳过边界的已验证 durable checkpoint。
    /// </summary>
    /// <remarks>
    /// checkpoint 文件的存在本身不能证明关联段仍在：它可能在目录损坏、手工删除或
    /// 不完整发布后留下。无法验证时返回 0，让调用方把 WAL 中更高的 checkpoint 当作
    /// 未验证记录处理并 fail closed；在 WAL 尚未写入 checkpoint 的早期发布窗口，完整
    /// WAL 仍可安全重放。
    /// </remarks>
    private static long GetVerifiedDurableCheckpointLsn(
        string root,
        string walDirectory,
        SegmentReaderOptions? readerOptions,
        SegmentReplacementManifest replacementManifest)
    {
        ArgumentNullException.ThrowIfNull(replacementManifest);
        WalCheckpointState? checkpoint = WalCheckpointFile.TryLoad(
            WalSegmentLayout.CheckpointPath(walDirectory));
        if (checkpoint is not { } state)
            return 0L;

        return IsCheckpointSegmentPresent(root, state, readerOptions, replacementManifest)
            ? state.CheckpointLsn
            : 0L;
    }

    private static bool IsCheckpointSegmentPresent(
        string root,
        WalCheckpointState state,
        SegmentReaderOptions? readerOptions,
        SegmentReplacementManifest? replacementManifest = null)
    {
        if (replacementManifest?.CoversCheckpointSegment(root, state.SegmentId, readerOptions) == true)
            return true;

        if (!TsdbPaths.TryGetSegmentPath(root, state.SegmentId, out string segmentPath))
            return false;

        try
        {
            // checkpoint 不只是长度提示：它会让 WAL replay 跳过此前记录，必须确认关联段
            // 可以被完整解析并且 header 中的 SegmentId 与 checkpoint 一致。
            using var reader = SegmentReader.Open(segmentPath, readerOptions);
            return reader.Header.SegmentId == state.SegmentId
                && reader.FileLength == state.SegmentLength;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 在任何 Segment 扫描前恢复上次中断的 Flush 发布。marker 未被有效 checkpoint 覆盖时，
    /// 该段从未提交，必须删除全部 artifact 并让 WAL replay 恢复；marker 损坏或删除失败一律
    /// 拒绝启动，避免把可能重复的数据静默加载到查询面。
    /// </summary>
    private static void ReconcileInterruptedFlushPublications(
        string root,
        string walDirectory,
        string segmentTemporarySuffix,
        SegmentReaderOptions? readerOptions)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(walDirectory);
        ArgumentNullException.ThrowIfNull(segmentTemporarySuffix);

        IReadOnlyList<(long SegmentId, string Path)> publicationPaths =
            WalSegmentLayout.EnumerateFlushPublicationPaths(walDirectory);
        if (publicationPaths.Count == 0)
            return;

        // 读取原始 checkpoint 是为了侦测“marker 尚未确认，但后来已有更高 checkpoint”的
        // 不可判定状态；完整性验证只在其与 marker 精确匹配时作为提交依据。
        WalCheckpointState? durableCheckpoint = WalCheckpointFile.TryLoad(
            WalSegmentLayout.CheckpointPath(walDirectory));
        SegmentReplacementManifest replacementManifest = SegmentReplacementManifest.LoadForRoot(root);
        long maxWalCheckpointLsn = GetMaximumWalCheckpointLsn(walDirectory);

        foreach (var (segmentIdFromPath, publicationPath) in publicationPaths)
        {
            FlushPublicationState publication = FlushPublicationFile.Load(publicationPath);
            if (publication.SegmentId != segmentIdFromPath)
            {
                throw new InvalidDataException(
                    $"Flush publication marker '{publicationPath}' names segment {segmentIdFromPath}, "
                    + $"but its payload names segment {publication.SegmentId}.");
            }

            if (publication.Status == FlushPublicationStatus.Committed)
            {
                // Committed 表示独立 checkpoint 已在标记提升前成功持久化；后续 checkpoint
                // 覆盖该单一文件也不影响此结论。清理失败不能把已提交数据降级为失败。
                FlushPublicationFile.TryClearCommitted(publicationPath);
                continue;
            }

            bool exactDurableCommit = durableCheckpoint is { } checkpoint
                && checkpoint.SegmentId == publication.SegmentId
                && checkpoint.CheckpointLsn == publication.CheckpointLsn
                && IsCheckpointSegmentPresent(root, checkpoint, readerOptions, replacementManifest);
            if (exactDurableCommit)
            {
                // 掉电可能落在 checkpoint 与 marker 状态提升之间。此时段和 checkpoint 已完整
                // 验证，可以补做状态提升；若提升失败，Open 必须失败而不是误删已提交数据。
                FlushPublicationFile.MarkCommittedRequired(publicationPath, publication);
                FlushPublicationFile.TryClearCommitted(publicationPath);
                continue;
            }

            bool durableCheckpointCoversPending = durableCheckpoint is { } laterDurable
                && laterDurable.CheckpointLsn >= publication.CheckpointLsn;
            bool walCheckpointCoversPending = maxWalCheckpointLsn >= publication.CheckpointLsn;
            if (durableCheckpointCoversPending || walCheckpointCoversPending)
            {
                throw new InvalidDataException(
                    $"Pending flush publication for segment {publication.SegmentId} at LSN "
                    + $"{publication.CheckpointLsn} is followed by a durable or WAL checkpoint. "
                    + "Database open is rejected because the earlier segment cannot be proven committed.");
            }

            // 尚无任何可跳过该 marker 的 checkpoint：只有先证明 WAL 从最近已验证 checkpoint
            // 连续覆盖到 marker 的最后一个 WritePoint，才可删除孤立段并重放。否则 marker
            // 对应的段可能是唯一副本，绝不能为了避免重复而静默删除它。
            EnsurePendingFlushHasCompleteWalCoverage(
                root,
                walDirectory,
                publication,
                durableCheckpoint,
                readerOptions,
                replacementManifest);
            DeleteUncommittedFlushSegmentRequired(
                root,
                publication.SegmentId,
                segmentTemporarySuffix);
            FlushPublicationFile.ClearUncommittedRequired(publicationPath);
        }
    }

    /// <summary>
    /// 确认未提交 Flush 的恢复日志完整覆盖 marker 的 checkpoint。合法的尾部 torn write 可以
    /// 位于 marker 之后；它不影响已经完整写入的 flush 数据，不能使启动误判为损坏。
    /// </summary>
    private static void EnsurePendingFlushHasCompleteWalCoverage(
        string root,
        string walDirectory,
        FlushPublicationState publication,
        WalCheckpointState? durableCheckpoint,
        SegmentReaderOptions? readerOptions,
        SegmentReplacementManifest replacementManifest)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(walDirectory);
        ArgumentNullException.ThrowIfNull(replacementManifest);

        if (publication.CheckpointLsn == 0)
        {
            throw CreateIncompletePendingFlushWalException(
                publication,
                "the marker has no positive replay boundary");
        }

        long durableCheckpointLsn = 0;
        if (durableCheckpoint is { } checkpoint)
        {
            // 使用前必须验证 checkpoint 所指段；否则把它当作 WAL 可缺失的下界会让损坏的
            // 旧段和缺失的历史日志一起被静默接受。
            if (!IsCheckpointSegmentPresent(root, checkpoint, readerOptions, replacementManifest))
            {
                throw new InvalidDataException(
                    $"Pending flush publication for segment {publication.SegmentId} at LSN "
                    + $"{publication.CheckpointLsn} cannot verify the preceding durable checkpoint "
                    + $"at LSN {checkpoint.CheckpointLsn}. Database open is rejected before removing "
                    + "the pending segment.");
            }

            durableCheckpointLsn = checkpoint.CheckpointLsn;
        }

        IReadOnlyList<WalSegmentInfo> segments = WalSegmentLayout.Enumerate(walDirectory);
        if (segments.Count == 0)
            throw CreateIncompletePendingFlushWalException(publication, "no WAL segment is present");

        // 每一条 durable checkpoint 之后的首个 WAL record 都会保留，直到更高的
        // checkpoint 回收它。因此不能接受任何 LSN 跳跃：否则无法区分“缺失的真实写入”
        // 和“看似可省略的 checkpoint record”。
        long expectedFirstReplayLsn = checked(durableCheckpointLsn + 1L);
        if (segments[0].StartLsn > expectedFirstReplayLsn)
        {
            throw CreateIncompletePendingFlushWalException(
                publication,
                $"the first retained WAL segment starts at LSN {segments[0].StartLsn}, expected no later than {expectedFirstReplayLsn}");
        }

        bool reachedReplayRange = false;
        long expectedLsn = expectedFirstReplayLsn;
        foreach (WalSegmentInfo segment in segments)
        {
            using var reader = WalReader.Open(segment.Path);
            if (reader.FirstLsn != segment.StartLsn)
            {
                throw CreateIncompletePendingFlushWalException(
                    publication,
                    $"WAL file '{segment.Path}' names start LSN {segment.StartLsn}, but its header declares {reader.FirstLsn}");
            }

            foreach (WalRecord record in reader.Replay())
            {
                if (!reachedReplayRange)
                {
                    if (record.Lsn <= durableCheckpointLsn)
                        continue;

                    reachedReplayRange = true;
                }

                if (record.Lsn != expectedLsn)
                {
                    throw CreateIncompletePendingFlushWalException(
                        publication,
                        $"WAL has an LSN gap at {record.Lsn}, expected {expectedLsn}");
                }

                if (record.Lsn == publication.CheckpointLsn)
                {
                    if (record is not WritePointRecord)
                    {
                        throw CreateIncompletePendingFlushWalException(
                            publication,
                            $"LSN {publication.CheckpointLsn} is not a WritePoint record");
                    }

                    return;
                }

                if (record.Lsn > publication.CheckpointLsn)
                {
                    throw CreateIncompletePendingFlushWalException(
                        publication,
                        $"WAL passed marker LSN {publication.CheckpointLsn} without its WritePoint record");
                }

                expectedLsn++;
            }
        }

        throw CreateIncompletePendingFlushWalException(
            publication,
            $"WAL ends before marker LSN {publication.CheckpointLsn}");
    }

    private static InvalidDataException CreateIncompletePendingFlushWalException(
        FlushPublicationState publication,
        string reason)
        => new(
            $"Pending flush publication for segment {publication.SegmentId} at LSN {publication.CheckpointLsn} "
            + $"does not have complete WAL coverage: {reason}. Database open is rejected before removing "
            + "the pending segment.");

    private static long GetMaximumWalCheckpointLsn(string walDirectory)
    {
        long maximum = 0;
        foreach (var segment in WalSegmentLayout.Enumerate(walDirectory))
        {
            using var reader = WalReader.Open(segment.Path);
            foreach (WalRecord record in reader.Replay())
            {
                if (record is CheckpointRecord checkpoint
                    && checkpoint.CheckpointLsn > maximum)
                {
                    maximum = checkpoint.CheckpointLsn;
                }
            }
        }

        return maximum;
    }

    private static void DeleteUncommittedFlushSegmentRequired(
        string root,
        long segmentId,
        string segmentTemporarySuffix)
    {
        ArgumentNullException.ThrowIfNull(segmentTemporarySuffix);

        var changedDirectories = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var artifactPaths = new HashSet<string>(
            TsdbPaths.SegmentArtifactPaths(root, segmentId),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (segmentTemporarySuffix.Length > 0)
        {
            foreach (string artifactPath in artifactPaths.ToArray())
                artifactPaths.Add(artifactPath + segmentTemporarySuffix);
        }

        foreach (string artifactPath in artifactPaths)
        {
            bool existed = File.Exists(artifactPath);
            try
            {
                if (existed)
                    File.Delete(artifactPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Unable to remove uncommitted flush segment artifact '{artifactPath}' for segment {segmentId}. "
                    + "Database open is rejected to prevent duplicate WAL replay.",
                    ex);
            }

            if (File.Exists(artifactPath))
            {
                throw new IOException(
                    $"Uncommitted flush segment artifact '{artifactPath}' for segment {segmentId} remains after deletion. "
                    + "Database open is rejected to prevent duplicate WAL replay.");
            }

            if (existed)
            {
                string? directory = Path.GetDirectoryName(artifactPath);
                if (string.IsNullOrEmpty(directory))
                {
                    throw new InvalidDataException(
                        $"Uncommitted flush segment artifact '{artifactPath}' has no parent directory.");
                }

                changedDirectories.Add(directory);
            }
        }

        foreach (string directory in changedDirectories)
            DirectoryFsync.FlushRequired(directory);
    }

    private static long GetMaxTombstoneLsn(IReadOnlyList<Tombstone> tombstones)
    {
        long max = 0;
        for (int i = 0; i < tombstones.Count; i++)
        {
            long lsn = tombstones[i].CreatedLsn;
            if (lsn > max)
                max = lsn;
        }

        return max;
    }

    private void MaybeCheckpointTombstoneManifestLocked(long nowUtcTicks)
    {
        var checkpoint = _options.TombstoneCheckpoint;
        if (!checkpoint.Enabled || _tombstoneDeletesSinceCheckpoint <= 0)
            return;

        bool countDue = checkpoint.MaxDeletesSinceCheckpoint > 0
            && _tombstoneDeletesSinceCheckpoint >= checkpoint.MaxDeletesSinceCheckpoint;
        bool timeDue = checkpoint.MaxInterval > TimeSpan.Zero
            && nowUtcTicks - _lastTombstoneCheckpointUtcTicks >= checkpoint.MaxInterval.Ticks;

        if (!countDue && !timeDue)
            return;

        try
        {
            TombstoneManifestCodec.Save(
                TsdbPaths.TombstoneManifestPath(RootDirectory),
                Tombstones.All);
            MarkTombstoneManifestCheckpointedLocked(nowUtcTicks);
        }
        catch (IOException)
        {
            // 周期性 checkpoint 是恢复加速路径；失败时仍保留 WAL 作为权威恢复来源。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上：不改变 Delete 已写 WAL + 内存 tombstone 的语义。
        }
    }

    private void MarkTombstoneManifestCheckpointedLocked(long nowUtcTicks)
    {
        _tombstoneDeletesSinceCheckpoint = 0;
        _lastTombstoneCheckpointUtcTicks = nowUtcTicks;
    }

    private void PersistMeasurementSchemasLocked()
    {
        if (!_measurementSchemaDirty)
            return;

        MeasurementSchemaCodec.Save(
            TsdbPaths.MeasurementSchemaPath(RootDirectory),
            Measurements.Snapshot());
        _measurementSchemaDirty = false;
        _measurementSchemaPersistCount++;
    }

    private void MarkMeasurementSchemasDirty()
        => _measurementSchemaDirty = true;

    private void ReportDiagnostic(
        string operation,
        TsdbDiagnosticSeverity severity,
        string message,
        Exception? exception)
    {
        if (exception is not null)
            Volatile.Write(ref _lastError, exception);

        var diagnostic = new TsdbDiagnosticEvent(operation, severity, message, exception);
        var handlers = DiagnosticEvent;
        if (handlers is null)
            return;

        foreach (EventHandler<TsdbDiagnosticEvent> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, diagnostic);
            }
            catch
            {
                // 诊断通道不能改变 Dispose / 后台任务的外部语义。
            }
        }
    }

    private void PersistCatalogCheckpointLocked()
    {
        if (!_catalogDirty)
            return;

        CatalogFileCodec.Save(Catalog, TsdbPaths.CatalogPath(RootDirectory));
        _catalogDirty = false;
    }

    private void EnsureViewNameAvailable(string objectName, string objectType)
    {
        if (_views.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 view 已存在。");
        }
        if (_materializedViews.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 materialized view 已存在。");
        }
        if (_graphs.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 graph 已存在。");
        }
        if (_graphs.PropertyGraphs.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 property graph 已存在。");
        }
    }

    /// <summary>拒绝与现有基础对象或视图同名的原生图定义。</summary>
    private void EnsureGraphNameAvailable(string objectName, string objectType)
    {
        if (_tables.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 table 已存在。");
        }
        if (Measurements.Contains(objectName))
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 measurement 已存在。");
        }
        if (_documents.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 document collection 已存在。");
        }
        if (_views.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 view 已存在。");
        }
        if (_materializedViews.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 materialized view 已存在。");
        }
    }

    /// <summary>拒绝与任一基础对象、其它视图类别或原生图同名的视图定义。</summary>
    private void EnsureViewDefinitionNameAvailable(string objectName, string objectType)
    {
        EnsureGraphNameAvailable(objectName, objectType);
        if (_graphs.Catalog.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 graph 已存在。");
        }
        if (_graphs.PropertyGraphs.TryGet(objectName) is not null)
        {
            throw new InvalidOperationException(
                $"无法创建 {objectType} '{objectName}'：同名 property graph 已存在。");
        }
    }

    private void EnsureNoViewDependents(string objectName, string operation)
    {
        SqlRoutineRuntime.EnsureNoDependents(this, objectName, operation);
        var viewDependents = _views.FindDependents(objectName);
        var materializedDependents = _materializedViews.FindDependents(objectName);
        if (viewDependents.Count == 0 && materializedDependents.Count == 0)
            return;

        string dependents = string.Join(
            "', '",
            viewDependents.Select(static view => view.Name)
                .Concat(materializedDependents.Select(static view => view.Name))
                .Order(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"无法执行 {operation}：view/materialized view "
            + $"'{dependents}' 依赖对象 '{objectName}'。");
    }

    /// <summary>阻止调用方绕过 Tsdb 的 schema 锁和持久化路径直接修改 measurement 目录。</summary>
    private void EnsureManagedMeasurementCatalogMutation(string measurementName, string operation)
    {
        if (!Monitor.IsEntered(_schemaSync))
        {
            throw new InvalidOperationException(
                $"不能直接对受管理的 MeasurementCatalog 执行 {operation} '{measurementName}'；请使用 Tsdb 的 measurement schema API。");
        }
    }

    /// <summary>拒绝会使 Modbus、例程或视图引用失效的关系表 schema 变更。</summary>
    private void EnsureNoTableDependents(string tableName, string operation)
    {
        _modbus.EnsureTableCanMutate(tableName, operation);
        IReadOnlyList<PropertyGraphDefinition> propertyGraphDependents =
            _graphs.PropertyGraphs.FindDependents(tableName);
        if (propertyGraphDependents.Count != 0)
        {
            string dependents = string.Join(
                "', '",
                propertyGraphDependents.Select(static definition => definition.Name));
            throw new InvalidOperationException(
                $"无法执行 {operation}：property graph '{dependents}' 依赖 table '{tableName}'。");
        }
        EnsureNoViewDependents(tableName, operation);
    }

    /// <summary>
    /// （仅测试用）模拟崩溃式关闭：不保存 catalog，也不 Flush 当前活跃 MemTable。
    /// 为安全释放进程内资源，已交给 flush 泵的密封请求会先完成；真实的在飞 I/O 崩溃由子进程终止测试覆盖。
    /// </summary>
    internal void CrashSimulationCloseWal()
    {
        lock (_shutdownSync)
        {
            if (_disposed)
            {
                ReleaseRootDirectoryOwnership(ref _rootDirectoryOwnership);
                return;
            }

            BeginWriteLifecycleShutdown();
            try
            {
                SonnetDbMeter.UnregisterEngine(_meterRegistration);
                StopBackgroundWorkers();
            }
            finally
            {
                CrashSimulationDisposeCommittedState();
            }
        }
    }

    /// <summary>在崩溃模拟路径中关闭已提交资源，不保存 catalog 也不执行最终 flush。</summary>
    private void CrashSimulationDisposeCommittedState()
    {
        lock (_schemaSync)
            lock (_writeSync)
            {
                WalSegmentSet? walSetToDispose = _walSet;
                _walSet = null;
                try
                {
                    if (walSetToDispose is not null)
                        _walGroupCommit.FlushPending(walSetToDispose);
                }
                finally
                {
                    DisposeCommittedResources(walSetToDispose);
                }
            }
    }
}
