using SonnetDB.Catalog;
using SonnetDB.Diagnostics;
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
using System.Diagnostics;

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
    // 维护操作串行锁：序列化 Compaction / Retention / DropMeasurement 的段读-规划-执行-替换，
    // 防止"compaction 把 retention 刚删的过期数据重新物化"以及后台 worker 无租约读段导致的
    // use-after-dispose（#191）。锁序约定：_maintenanceSync（外）→ _writeSync（内）。
    // 任何同时需要两把锁的路径都必须先取 _maintenanceSync；写路径只取 _writeSync，flush 泵两把都不取。
    private readonly object _maintenanceSync = new();
    private MemTable _activeMemTable;
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
    private FlushPump? _flushPump;
    private CompactionWorker? _compactionWorker;
    private RetentionWorker? _retentionWorker;
    private KvExpirerWorker? _kvExpirerWorker;
    private long _checkpointLsn;
    private long _tombstoneDeletesSinceCheckpoint;
    private long _lastTombstoneCheckpointUtcTicks;
    private Exception? _lastError;
    private long _meterRegistration;

    /// <summary>
    /// <see cref="WriteMany(ReadOnlySpan{Point})"/> 单次持锁处理的最大点数。超大批量按此粒度分块，
    /// 使硬上限背压能在批内周期性触发、块间释放写锁，避免单批无界撑大 MemTable/WAL（C4）。
    /// </summary>
    private const int WriteManyChunkSize = 8192;

    /// <summary>数据库根目录路径。</summary>
    public string RootDirectory => _options.RootDirectory;

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
    internal SegmentWriterOptions CompactionWriterOptions => _options.SegmentWriterOptions;

    /// <summary>flush 泵当前排队中（含正在处理）的请求数；供 <see cref="SonnetDbMeter"/> 观测。</summary>
    internal long FlushPumpPendingCount => _flushPump?.PendingCount ?? 0;

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
        bool catalogDirty)
    {
        _options = options;
        Catalog = catalog;
        Measurements = measurements;
        _activeMemTable = memTable;
        _walSet = walSet;
        _nextSegmentId = nextSegmentId;
        _seriesWithWalRecord = seriesWithWalRecord;
        _catalogDirty = catalogDirty;
        Segments = segmentManager;
        _flushCoordinator = new FlushCoordinator(options);
        _flushPump = new FlushPump(this);
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

        // 扫描已存在的 Segment 与替换清单，计算 NextSegmentId。
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

        if (options.Kv.ExpirerEnabled)
        {
            tsdb._kvExpirerWorker = new KvExpirerWorker(tsdb, options.Kv);
            tsdb._kvExpirerWorker.Start();
        }

        tsdb._meterRegistration = SonnetDbMeter.RegisterEngine(tsdb);

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

        long startTimestamp = SonnetDbMeter.WriteDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

        WalGroupCommitTicket walSync = default;
        FlushPump.FlushRequest? hardCapFlush;
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            totalWritten += WriteManyChunk(points.Slice(offset, chunkLength));
            offset += chunkLength;
        }

        return totalWritten;
    }

    /// <summary>
    /// 单块批量写入：与整批写入语义一致，但只处理 <paramref name="chunk"/> 一段，
    /// 便于 <see cref="WriteMany(ReadOnlySpan{Point})"/> 在块间释放锁并施加硬上限背压。
    /// </summary>
    private int WriteManyChunk(ReadOnlySpan<Point> chunk)
    {
        long startTimestamp = SonnetDbMeter.WriteDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

        int written = 0;
        WalGroupCommitTicket walSync = default;
        FlushPump.FlushRequest? hardCapFlush = null;
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

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

                hardCapFlush = FlushForHardCapIfNeededLocked();
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
    /// 删除指定 measurement 的 schema、series catalog 与对应时序数据。
    /// </summary>
    /// <param name="name">measurement 名称。</param>
    /// <returns>找到并删除返回 <c>true</c>；不存在返回 <c>false</c>。</returns>
    public bool DropMeasurement(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // 先取维护锁（外），再取写锁（内）：与 Compaction / Retention 互斥，杜绝它们并发变更段集合
        // 导致的 use-after-dispose / 数据复活；锁序 _maintenanceSync → _writeSync 全局一致。
        lock (_maintenanceSync)
        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

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

        lock (_writeSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            SealAndWaitLocked();
            _walSet?.Sync();
            TombstoneManifestCodec.Save(TsdbPaths.TombstoneManifestPath(RootDirectory), Tombstones.All);
            PersistMeasurementSchemasLocked();
            CatalogFileCodec.Save(Catalog, TsdbPaths.CatalogPath(RootDirectory));
            _catalogDirty = false;

            Tables.CheckpointAll();
            Documents.CompactAll();
            var checkpointedKeyspaces = Keyspaces.CheckpointOpened();

            return afterCheckpoint(this, options, checkpointedKeyspaces);
        }
    }

    /// <summary>
    /// 关闭数据库：先关闭后台 Flush 线程，再 Flush 剩余 MemTable、保存 catalog、关闭 WAL。
    /// </summary>
    public void Dispose()
    {
        SonnetDbMeter.UnregisterEngine(_meterRegistration);

        _kvExpirerWorker?.Dispose();
        _kvExpirerWorker = null;

        // 先关闭 Retention 后台线程（在锁外，防止与内部操作死锁）
        _retentionWorker?.Dispose();
        _retentionWorker = null;

        // 再关闭 Compaction 后台线程（在锁外，防止与内部操作死锁）
        _compactionWorker?.Dispose();
        _compactionWorker = null;

        // 再关闭后台 Flush 线程（在锁外，防止与 InternalFlushFromBackground 死锁）
        _flushWorker?.Dispose();
        _flushWorker = null;

        // 排空 flush 泵：等待所有已入队的密封表落盘完成（此时 _walSet 仍有效）。
        // 必须在关闭 WAL 之前，否则在飞 flush 会因 WAL 被释放而失败/丢数据。
        _flushPump?.Dispose();
        _flushPump = null;

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
    /// Phase 2 密封（调用方必须持有 _writeSync 锁）：把当前活跃 MemTable 换成新空实例、
    /// 追加 WAL checkpoint 前的 catalog 持久化，并把密封表 + sealLsn + segId 打包入 flush 泵队列。
    /// 本方法只做 O(1) 的 swap/入队与少量 catalog I/O，<b>不</b>执行编码落盘或 WAL 回收（由泵在锁外做）。
    /// </summary>
    /// <returns>已入队的 flush 请求；活跃表为空或引擎已关闭时返回 null。</returns>
    private FlushPump.FlushRequest? SealAndEnqueueLocked()
    {
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
    /// 由 flush 泵线程调用（锁外）：把已密封的 MemTable 编码落盘 + WAL checkpoint/roll/recycle，
    /// 然后原子发布新段并从 sealing 列表移除该表。全程不获取 <c>_writeSync</c>。
    /// </summary>
    internal void ExecutePumpFlush(FlushPump.FlushRequest request)
    {
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

        // 原子发布：接入新段 + 从 sealing 移除该表（SegmentManager 一次 Volatile.Write）。
        Segments.PublishSegmentAndReleaseSealed(result.Path, request.SealedTable);
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
    internal void OnPumpFlushFailed(FlushPump.FlushRequest request, Exception ex)
    {
        try
        {
            Segments.ReleaseSealed(request.SealedTable);
        }
        catch
        {
            // 忽略：引擎可能正在关闭。
        }

        Volatile.Write(ref _lastError, ex);
        ReportDiagnostic(
            "FlushPump.Flush",
            TsdbDiagnosticSeverity.Error,
            "后台 flush 泵执行失败；密封表已从 sealing 移除，数据仍可由 WAL replay 恢复。",
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
            if (_disposed)
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

    /// <summary>仅供测试注入 KV 过期清理故障；生产恒为 null。</summary>
    internal Action? _kvExpirerFaultHook;

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
    private FlushPump.FlushRequest? FlushForHardCapIfNeededLocked()
    {
        long hardCapBytes = _options.FlushPolicy.ResolveHardCapBytes();
        if (hardCapBytes <= 0)
            return null;

        if (MemTable.EstimatedBytes < hardCapBytes)
            return null;

        return SealAndEnqueueLocked();
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

    private static bool IsCheckpointSegmentPresent(string root, WalCheckpointState state)
    {
        if (!TsdbPaths.TryGetSegmentPath(root, state.SegmentId, out string segmentPath))
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
        // 模拟崩溃：停掉 flush 泵线程（不排空——崩溃语义下在飞 flush 视为丢失，靠 WAL replay 恢复），
        // 再直接关闭 WAL，不保存 catalog、不 flush。
        _flushPump?.Dispose();
        _flushPump = null;

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
