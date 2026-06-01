using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Storage.Segments;
using SonnetDB.Tables;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// SonnetDB 嵌入式时序数据库门面。负责：
/// <list type="bullet">
///   <item><description>启动时加载 catalog、回放 WAL 重建 MemTable；</description></item>
///   <item><description>写入路径：Append → WAL → MemTable，必要时触发 Flush；</description></item>
///   <item><description>关闭时：Flush MemTable + 持久化 catalog。</description></item>
/// </list>
/// 单实例只能由一个进程打开（WalSegmentSet 的 active segment 文件句柄提供锁保护）。
/// </summary>
public sealed class Tsdb : IDisposable
{
    private readonly TsdbOptions _options;
    private readonly FlushCoordinator _flushCoordinator;
    private readonly WalGroupCommitCoordinator _walGroupCommit;
    private readonly object _writeSync = new();
    private readonly HashSet<ulong> _seriesWithWalRecord;
    private readonly KvKeyspaceManager _keyspaces;
    private readonly TableManager _tables;
    private readonly DocumentCollectionManager _documents;

    private WalSegmentSet? _walSet;
    private long _nextSegmentId;
    private bool _catalogDirty;
    private bool _measurementSchemaDirty;
    private long _measurementSchemaPersistCount;
    private bool _disposed;
    private BackgroundFlushWorker? _flushWorker;
    private CompactionWorker? _compactionWorker;
    private RetentionWorker? _retentionWorker;
    private long _checkpointLsn;
    private long _tombstoneDeletesSinceCheckpoint;
    private long _lastTombstoneCheckpointUtcTicks;
    private Exception? _lastError;

    /// <summary>数据库根目录路径。</summary>
    public string RootDirectory => _options.RootDirectory;

    /// <summary>当前序列目录。</summary>
    public SeriesCatalog Catalog { get; }

    /// <summary>当前 Measurement schema 集合（线程安全）。</summary>
    public MeasurementCatalog Measurements { get; }

    /// <summary>当前内存层（MemTable）。</summary>
    public MemTable MemTable { get; }

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
    /// JSON 文档集合管理器，提供 document collection schema catalog 与 KV-backed 主数据。
    /// </summary>
    public DocumentCollectionManager Documents => _documents;

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

    /// <summary>后台 Flush 策略（供 BackgroundFlushWorker 访问）。</summary>
    internal MemTableFlushPolicy BackgroundFlushPolicy => _options.FlushPolicy;

    /// <summary>Compaction 写入选项（供 CompactionWorker 访问）。</summary>
    internal SegmentWriterOptions CompactionWriterOptions => _options.SegmentWriterOptions;

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
        bool catalogDirty)
    {
        _options = options;
        Catalog = catalog;
        Measurements = measurements;
        MemTable = memTable;
        _walSet = walSet;
        _nextSegmentId = nextSegmentId;
        _seriesWithWalRecord = seriesWithWalRecord;
        _catalogDirty = catalogDirty;
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
        _keyspaces = new KvKeyspaceManager(TsdbPaths.KvDir(options.RootDirectory), options.Kv);
        _tables = new TableManager(TsdbPaths.TablesDir(options.RootDirectory), options.Kv);
        _documents = new DocumentCollectionManager(TsdbPaths.DocumentsDir(options.RootDirectory), options.Kv);
        _checkpointLsn = checkpointLsn;
        _lastTombstoneCheckpointUtcTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// 打开（不存在则创建）TSDB 根目录，自动加载 catalog 并回放 WAL。
    /// </summary>
    /// <param name="options">引擎选项；为 null 时使用 <see cref="TsdbOptions.Default"/>。</param>
    /// <returns>已初始化的 <see cref="Tsdb"/> 实例。</returns>
    public static Tsdb Open(TsdbOptions? options = null)
    {
        options ??= TsdbOptions.Default;

        string root = options.RootDirectory;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(TsdbPaths.WalDir(root));
        Directory.CreateDirectory(TsdbPaths.SegmentsDir(root));
        Directory.CreateDirectory(TsdbPaths.KvDir(root));
        Directory.CreateDirectory(TsdbPaths.TablesDir(root));
        Directory.CreateDirectory(TsdbPaths.DocumentsDir(root));

        // 加载 measurement schema 集合（文件不存在时返回空集合）
        var measurements = new MeasurementCatalog();
        foreach (var schema in MeasurementSchemaCodec.Load(TsdbPaths.MeasurementSchemaPath(root)))
            measurements.LoadOrReplace(schema);
        // 加载 catalog（文件不存在时返回空目录）
        var catalog = CatalogFileCodec.Load(TsdbPaths.CatalogPath(root));

        // 扫描已存在的 Segment，计算 NextSegmentId
        long nextSegmentId = 1;
        foreach (var (segId, _) in TsdbPaths.EnumerateSegments(root))
        {
            if (segId + 1 > nextSegmentId)
                nextSegmentId = segId + 1;
        }

        // 打开 WAL segment 集合（自动升级 legacy active.SDBWAL）
        string walDir = TsdbPaths.WalDir(root);
        var walSet = WalSegmentSet.Open(walDir, options.WalRolling, options.WalBufferSize, initialStartLsn: 1);
        long durableCheckpointLsn = WalCheckpointFile
            .TryLoad(
                WalSegmentLayout.CheckpointPath(walDir),
                state => IsCheckpointSegmentPresent(root, state))
            ?.CheckpointLsn ?? 0L;

        // 回放全部 WAL segment，使用 Checkpoint LSN 跳过已落盘记录
        var memTable = new MemTable();
        int catalogCountBeforeReplay = catalog.Count;
        string tombstoneManifestPath = TsdbPaths.TombstoneManifestPath(root);
        IReadOnlyList<Tombstone> manifestTombstones = TombstoneManifestCodec.Load(tombstoneManifestPath);
        long durableTombstoneCheckpointLsn = GetMaxTombstoneLsn(manifestTombstones);
        var result = walSet.ReplayWithCheckpoint(catalog, durableCheckpointLsn, durableTombstoneCheckpointLsn);
        memTable.ReplayFrom(result.WritePoints);
        long checkpointLsn = result.CheckpointLsn;
        bool catalogDirty = catalog.Count != catalogCountBeforeReplay;

        var seriesWithWalRecord = catalog.Snapshot().Select(e => e.Id).ToHashSet();

        var segmentManager = SegmentManager.Open(root, options.SegmentReaderOptions);

        var tsdb = new Tsdb(options, catalog, measurements, memTable, walSet, nextSegmentId, seriesWithWalRecord, segmentManager, checkpointLsn, catalogDirty);

        // 加载墓碑清单（文件不存在时返回空集合）
        tsdb.Tombstones.LoadFrom(manifestTombstones);

        // 追加 WAL replay 中 checkpoint 之后的 Delete 记录
        foreach (var del in result.DeleteRecords)
            tsdb.Tombstones.Add(new Tombstone(del.SeriesId, del.FieldName, del.FromTimestamp, del.ToTimestamp, del.Lsn));

        // 重写一遍 manifest（合并 manifest + WAL replay 的结果）
        TombstoneManifestCodec.Save(tombstoneManifestPath, tsdb.Tombstones.All);

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

        return tsdb;
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

        WalGroupCommitTicket walSync = default;
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var normalized = EnsureMeasurementSchemaLocked(point, persistImmediately: true);
            WritePointLocked(normalized);

            if (_options.SyncWalOnEveryWrite)
                walSync = _walGroupCommit.Prepare(_walSet!);
        }

        walSync.Wait();

        // 锁外向后台线程发送非阻塞信号
        _flushWorker?.Signal();
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
    /// 批量写入多个 Point（高吞吐快路径）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="WriteMany(IEnumerable{Point})"/> 不同，本重载在整批操作期间仅获取一次
    /// <c>_writeSync</c> 锁，并在批末统一调用一次 <see cref="BackgroundFlushWorker.Signal"/>，
    /// 显著降低 N 次入锁 / 信号开销。WAL 记录格式与逐点写入完全一致，向后兼容旧库。
    /// </remarks>
    /// <param name="points">要写入的数据点连续切片。</param>
    /// <returns>成功写入的点数量（不含 null 跳过）。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public int WriteMany(ReadOnlySpan<Point> points)
    {
        if (points.IsEmpty)
            return 0;

        int written = 0;
        WalGroupCommitTicket walSync = default;
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var normalizedPoints = new Point?[points.Length];

            for (int i = 0; i < points.Length; i++)
            {
                var point = points[i];
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
            }

            if (_options.SyncWalOnEveryWrite && written > 0)
                walSync = _walGroupCommit.Prepare(_walSet!);
        }

        walSync.Wait();

        if (written > 0)
            _flushWorker?.Signal();

        return written;
    }

    /// <summary>
    /// 删除某 (seriesId, fieldName) 在 [fromTimestamp, toTimestamp] 时间窗内的所有点。
    /// 在 WAL 中追加 Delete 记录，并将墓碑加入内存 <see cref="Tombstones"/> 集合。
    /// manifest 将在下次 FlushNow / Compaction / Dispose 时持久化；崩溃时 WAL replay 兜底。
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

            long lsn = _walSet!.AppendDelete(seriesId, fieldName, fromTimestamp, toTimestamp);
            var tomb = new Tombstone(seriesId, fieldName, fromTimestamp, toTimestamp, lsn);
            Tombstones.Add(tomb);
            _tombstoneDeletesSinceCheckpoint++;
            MaybeCheckpointTombstoneManifestLocked(DateTime.UtcNow.Ticks);

            if (_options.SyncWalOnEveryWrite)
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

        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Measurements.Add(schema);

            // 立即把全量 schema 集合原子写入磁盘，确保 CREATE 语义具备崩溃安全性
            MarkMeasurementSchemasDirty();
            PersistMeasurementSchemasLocked();
        }

        return schema;
    }

    /// <summary>
    /// 主动触发一次 Flush：把 MemTable 写出为 Segment，追加 WAL Checkpoint，Roll WAL，回收旧段，重置 MemTable。
    /// </summary>
    /// <returns>Segment 构建结果；MemTable 为空时返回 null。</returns>
    /// <exception cref="ObjectDisposedException">实例已关闭时抛出。</exception>
    public SegmentBuildResult? FlushNow()
    {
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return FlushNowLocked();
        }
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
    /// 关闭数据库：先关闭后台 Flush 线程，再 Flush 剩余 MemTable、保存 catalog、关闭 WAL。
    /// </summary>
    public void Dispose()
    {
        // 先关闭 Retention 后台线程（在锁外，防止与内部操作死锁）
        _retentionWorker?.Dispose();
        _retentionWorker = null;

        // 再关闭 Compaction 后台线程（在锁外，防止与内部操作死锁）
        _compactionWorker?.Dispose();
        _compactionWorker = null;

        // 再关闭后台 Flush 线程（在锁外，防止与 InternalFlushFromBackground 死锁）
        _flushWorker?.Dispose();
        _flushWorker = null;

        lock (_writeSync)
        {
            if (_disposed)
                return;
            _disposed = true;

            WalSegmentSet? walSetToDispose = _walSet;
            _walSet = null;

            try
            {
                if (walSetToDispose != null)
                {
                    _walGroupCommit.FlushPending(walSetToDispose);

                    // 尝试 Flush 剩余数据（Flush 内部会保存 manifest）
                    if (MemTable.PointCount > 0)
                    {
                        try
                        {
                            PersistMeasurementSchemasLocked();
                            PersistCatalogCheckpointLocked();
                            var result = _flushCoordinator.Flush(
                                MemTable,
                                walSetToDispose,
                                _nextSegmentId++,
                                Tombstones,
                                Catalog,
                                Measurements);
                            if (result != null)
                                _checkpointLsn = MemTable.LastLsn;
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
                        _tables.Dispose();
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
                                _keyspaces.Dispose();
                            }
                            finally
                            {
                                Segments.Dispose();
                            }
                        }
                    }
                }
            }
        }
    }

    // ── 内部辅助 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 执行 Flush（调用方必须持有 _writeSync 锁）。
    /// </summary>
    private SegmentBuildResult? FlushNowLocked()
    {
        if (_walSet == null)
            return null;

        _walGroupCommit.FlushPending(_walSet);
        PersistMeasurementSchemasLocked();

        if (MemTable.PointCount == 0)
        {
            PersistCatalogCheckpointLocked();
            return null;
        }

        // WAL 回收前必须先持久化完整 catalog snapshot。
        // 这样旧 WAL segment 中的 CreateSeries 被回收后，segment 中的 SeriesId 仍能通过 catalog 文件解析。
        PersistCatalogCheckpointLocked();

        long lsnBeforeFlush = MemTable.LastLsn;
        long segId = _nextSegmentId++;
        var result = _flushCoordinator.Flush(
            MemTable,
            _walSet,
            segId,
            Tombstones,
            Catalog,
            Measurements);

        // Flush 成功后旧 WAL segment 可能已回收；完整 catalog 已在回收前持久化。
        if (result != null)
        {
            if (Tombstones.Count > 0)
                MarkTombstoneManifestCheckpointedLocked(DateTime.UtcNow.Ticks);

            // 更新 CheckpointLsn（在锁内完成）
            if (lsnBeforeFlush != long.MinValue)
                _checkpointLsn = lsnBeforeFlush;

            // 仅在非关闭路径（非 Dispose 内部调用）时更新索引快照
            if (!_disposed)
                Segments.AddSegment(result.Path);
        }

        return result;
    }

    /// <summary>
    /// 由后台 Flush 线程调用的 Flush 入口。与同步 FlushNow 共享 _writeSync 锁，保证互斥。
    /// </summary>
    internal void InternalFlushFromBackground()
    {
        lock (_writeSync)
        {
            if (_disposed)
                return;
            FlushNowLocked();
        }
    }

    private void WritePointLocked(Point point)
    {
        var entry = Catalog.GetOrAdd(point);

        // 若是本进程首次写入该 series，向 WAL 追加 CreateSeries 记录
        if (_seriesWithWalRecord.Add(entry.Id))
        {
            _walSet!.AppendCreateSeries(entry.Id, entry.Measurement, entry.Tags);
            _catalogDirty = true;
        }

        // 每个字段写入 WAL 和 MemTable
        foreach (var (fieldName, value) in point.Fields)
        {
            long lsn = _walSet!.AppendWritePoint(entry.Id, point.Timestamp, fieldName, value);
            MemTable.Append(entry.Id, point.Timestamp, fieldName, value, lsn);
        }
    }

    private Point EnsureMeasurementSchemaLocked(Point point, bool persistImmediately)
    {
        var schema = Measurements.TryGet(point.Measurement);
        if (schema is null)
        {
            var created = CreateSchemaFromPoint(point);
            Measurements.Add(created);
            MarkMeasurementSchemasDirty();
            if (persistImmediately)
                PersistMeasurementSchemasLocked();
            return point;
        }

        var columns = new List<MeasurementColumn>(schema.Columns);
        var changed = false;
        var promotedIntToFloat = false;
        Dictionary<string, FieldValue>? normalizedFields = null;

        foreach (var (tagName, _) in point.Tags)
        {
            var column = schema.TryGetColumn(tagName);
            if (column is null)
            {
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
            if (promotedIntToFloat && MemTable.PointCount > 0)
                FlushNowLocked();

            var updated = MeasurementSchema.Create(schema.Name, columns, schema.CreatedAtUtcTicks);
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

    private static bool IsCheckpointSegmentPresent(string root, WalCheckpointState state)
    {
        string segmentPath = TsdbPaths.SegmentPath(root, state.SegmentId);
        if (!File.Exists(segmentPath))
            return false;

        try
        {
            return new FileInfo(segmentPath).Length == state.SegmentLength;
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

    /// <summary>
    /// （仅测试用）模拟进程崩溃：直接关闭 WAL，不保存 catalog，不 Flush MemTable。
    /// </summary>
    internal void CrashSimulationCloseWal()
    {
        lock (_writeSync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_walSet is not null)
                _walGroupCommit.FlushPending(_walSet);
            _walSet?.Dispose();
            _walGroupCommit.Dispose();
            _tables.Dispose();
            _documents.Dispose();
            _keyspaces.Dispose();
            _walSet = null;
        }
    }
}
