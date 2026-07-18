using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using SonnetDB.Engine;

namespace SonnetDB.Diagnostics;

/// <summary>
/// SonnetDB.Core 的进程级 <see cref="Meter"/> 基线（M17 #89）。
/// <para>
/// 仅使用 BCL <c>System.Diagnostics.Metrics</c>，不依赖 OpenTelemetry SDK；无监听者时
/// 所有 <c>Add</c>/<c>Record</c> 近似零开销，热路径计时统一用 <see cref="Instrument.Enabled"/>
/// 先行短路，未监听时连 <c>Stopwatch.GetTimestamp()</c> 都不执行。
/// </para>
/// <para>
/// 计量维度约定：热路径 Counter/Histogram 不带 per-db tag（保持写入路径最小开销）；
/// per-db 可见性由 <see cref="RegisterEngine"/> 注册的 ObservableGauge 提供
/// （tag <c>sonnetdb.database</c> = 数据库根目录叶名）。时延单位统一毫秒（ms）。
/// </para>
/// </summary>
public static class SonnetDbMeter
{
    /// <summary>Meter 名称；服务端 OTel 引导用 <c>AddMeter(SonnetDbMeter.MeterName)</c> 订阅。</summary>
    public const string MeterName = "SonnetDB.Core";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    // ── 写入路径 ─────────────────────────────────────────────────────────────

    /// <summary>成功写入的数据点计数（<c>Tsdb.Write</c> / <c>WriteMany</c>）。</summary>
    internal static readonly Counter<long> WritePoints = Meter.CreateCounter<long>(
        "sonnetdb.write.points", unit: "{point}",
        description: "Total data points accepted by the write path.");

    /// <summary>单次写入调用端到端时延（含 WAL 落盘等待与硬上限背压；单条 Write 与批量 chunk 混合分布）。</summary>
    internal static readonly Histogram<double> WriteDuration = Meter.CreateHistogram<double>(
        "sonnetdb.write.duration", unit: "ms",
        description: "End-to-end write call latency including WAL durability and hard-cap backpressure waits.");

    // ── WAL ──────────────────────────────────────────────────────────────────

    /// <summary>WAL fsync 时延（所有 fsync 汇聚于 <c>WalSegmentSet.Sync</c>，此处单点计量）。</summary>
    internal static readonly Histogram<double> WalFsyncDuration = Meter.CreateHistogram<double>(
        "sonnetdb.wal.fsync.duration", unit: "ms",
        description: "Latency of WAL fsync (FlushFileBuffers / fdatasync).");

    // ── Flush ────────────────────────────────────────────────────────────────

    /// <summary>一次 flush（密封 MemTable → 段落盘 → checkpoint → WAL 回收）的总时延；tag <c>outcome</c>。</summary>
    internal static readonly Histogram<double> FlushDuration = Meter.CreateHistogram<double>(
        "sonnetdb.flush.duration", unit: "ms",
        description: "Latency of a sealed-MemTable flush (encode + persist + checkpoint + WAL recycle).");

    /// <summary>成功 flush 落盘的数据点计数。</summary>
    internal static readonly Counter<long> FlushPoints = Meter.CreateCounter<long>(
        "sonnetdb.flush.points", unit: "{point}",
        description: "Data points persisted to segments by flush.");

    /// <summary>成功 flush 写出的段文件字节数。</summary>
    internal static readonly Counter<long> FlushBytes = Meter.CreateCounter<long>(
        "sonnetdb.flush.bytes", unit: "By",
        description: "Segment bytes written by flush.");

    // ── Compaction ───────────────────────────────────────────────────────────

    /// <summary>单个 compaction plan 的执行时延（plan → 新段 → swap）；tag <c>outcome</c>。</summary>
    internal static readonly Histogram<double> CompactionDuration = Meter.CreateHistogram<double>(
        "sonnetdb.compaction.duration", unit: "ms",
        description: "Latency of executing one compaction plan (merge + swap).");

    // ── Segment 读取 ─────────────────────────────────────────────────────────

    /// <summary>物理读取（解码缓存未命中）的 block 数。</summary>
    internal static readonly Counter<long> SegmentBlockReads = Meter.CreateCounter<long>(
        "sonnetdb.segment.block.reads", unit: "{block}",
        description: "Segment blocks physically read (decode-cache misses included).");

    /// <summary>物理读取的 block payload 字节数。</summary>
    internal static readonly Counter<long> SegmentBlockReadBytes = Meter.CreateCounter<long>(
        "sonnetdb.segment.block.read.bytes", unit: "By",
        description: "Payload bytes physically read from segment blocks.");

    // ── 查询 ─────────────────────────────────────────────────────────────────

    /// <summary>查询执行时延（流式枚举从开始到耗尽/提前中止）；tag <c>db.operation</c> ∈ points|aggregate。</summary>
    internal static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "sonnetdb.query.duration", unit: "ms",
        description: "Latency of query execution (streaming enumeration start to completion).");

    // ── KV generation / cleanup ─────────────────────────────────────────────

    /// <summary>Keyspace generation 切换次数。</summary>
    internal static readonly Counter<long> KvGenerationChanges = Meter.CreateCounter<long>(
        "sonnetdb.kv.generation.changes", unit: "{generation}",
        description: "KV keyspace generation changes committed by clear/truncate operations.");

    /// <summary>generation 切换一次隐藏的 key 数。</summary>
    internal static readonly Counter<long> KvClearedKeys = Meter.CreateCounter<long>(
        "sonnetdb.kv.clear.keys", unit: "{key}",
        description: "Keys made invisible by KV generation changes.");

    /// <summary>generation 切换端到端耗时。</summary>
    internal static readonly Histogram<double> KvClearDuration = Meter.CreateHistogram<double>(
        "sonnetdb.kv.clear.duration", unit: "ms",
        description: "Latency of a durable KV generation change including WAL and generation metadata fsync.");

    /// <summary>后台回收的旧 KV state 文件数。</summary>
    internal static readonly Counter<long> KvCleanupFiles = Meter.CreateCounter<long>(
        "sonnetdb.kv.cleanup.files", unit: "{file}",
        description: "Obsolete KV state files reclaimed by manifest-driven cleanup.");

    /// <summary>后台回收的旧 KV state 字节数。</summary>
    internal static readonly Counter<long> KvCleanupBytes = Meter.CreateCounter<long>(
        "sonnetdb.kv.cleanup.bytes", unit: "By",
        description: "Obsolete KV state bytes reclaimed by manifest-driven cleanup.");

    /// <summary>单轮 keyspace cleanup 耗时。</summary>
    internal static readonly Histogram<double> KvCleanupDuration = Meter.CreateHistogram<double>(
        "sonnetdb.kv.cleanup.duration", unit: "ms",
        description: "Latency of one manifest-driven KV cleanup round.");

    /// <summary>因资源压力暂停的 generation 回收轮数；tag <c>reason</c>。</summary>
    internal static readonly Counter<long> KvCleanupThrottled = Meter.CreateCounter<long>(
        "sonnetdb.kv.cleanup.throttled", unit: "{round}",
        description: "KV cleanup rounds paused by query, flush, CPU, or memory pressure.");

    /// <summary>generation 后台回收失败次数。</summary>
    internal static readonly Counter<long> KvCleanupFailures = Meter.CreateCounter<long>(
        "sonnetdb.kv.cleanup.failures", unit: "{failure}",
        description: "KV generation cleanup failures captured by the background worker.");

    /// <summary>KV 自动检查点后台失败次数。</summary>
    internal static readonly Counter<long> KvCheckpointFailures = Meter.CreateCounter<long>(
        "sonnetdb.kv.checkpoint.failures", unit: "{failure}",
        description: "KV automatic checkpoint failures scheduled for delayed retry.");

    /// <summary>KV 写入因检查点预算耗尽而等待的时长。</summary>
    internal static readonly Histogram<double> KvCheckpointBackpressureDuration = Meter.CreateHistogram<double>(
        "sonnetdb.kv.checkpoint.backpressure.duration", unit: "ms",
        description: "KV write latency spent waiting for checkpoint budget backpressure.");

    /// <summary>KV 检查点失败或背压超时导致的写入拒绝次数。</summary>
    internal static readonly Counter<long> KvCheckpointWriteRejections = Meter.CreateCounter<long>(
        "sonnetdb.kv.checkpoint.write.rejections", unit: "{write}",
        description: "KV writes rejected before WAL append after checkpoint failure or backpressure timeout.");

    // ── 复用 tag（避免热路径每次分配 KeyValuePair 的装箱/字符串） ─────────────

    internal static readonly KeyValuePair<string, object?> OutcomeOk = new("outcome", "ok");
    internal static readonly KeyValuePair<string, object?> OutcomeError = new("outcome", "error");
    internal static readonly KeyValuePair<string, object?> OperationPoints = new("db.operation", "points");
    internal static readonly KeyValuePair<string, object?> OperationAggregate = new("db.operation", "aggregate");
    internal static readonly KeyValuePair<string, object?> ThrottleActiveQueries = new("reason", "active_queries");
    internal static readonly KeyValuePair<string, object?> ThrottleFlushPending = new("reason", "flush_pending");
    internal static readonly KeyValuePair<string, object?> ThrottleCpuPressure = new("reason", "cpu_pressure");
    internal static readonly KeyValuePair<string, object?> ThrottleMemoryPressure = new("reason", "memory_pressure");

    // ── per-db ObservableGauge（引擎实例注册表） ──────────────────────────────

    private static long s_nextEngineId;
    private static readonly ConcurrentDictionary<long, EngineEntry> Engines = new();

    private sealed record EngineEntry(WeakReference<Tsdb> Engine, KeyValuePair<string, object?> Tag);

    static SonnetDbMeter()
    {
        Meter.CreateObservableGauge(
            "sonnetdb.memtable.bytes", ObserveMemTableBytes, unit: "By",
            description: "Estimated bytes held by the active MemTable, per database.");
        Meter.CreateObservableGauge(
            "sonnetdb.memtable.points", ObserveMemTablePoints, unit: "{point}",
            description: "Data points held by the active MemTable, per database.");
        Meter.CreateObservableGauge(
            "sonnetdb.segments.count", ObserveSegmentCount, unit: "{segment}",
            description: "Active segment count, per database.");
        Meter.CreateObservableGauge(
            "sonnetdb.flush.pending", ObserveFlushPending, unit: "{request}",
            description: "Sealed MemTables queued (or in-flight) in the flush pump, per database.");
        Meter.CreateObservableGauge(
            "sonnetdb.kv.cleanup.pending.files", ObserveKvCleanupPendingFiles, unit: "{file}",
            description: "Obsolete KV state files still pending cleanup, per database.");
        Meter.CreateObservableGauge(
            "sonnetdb.kv.cleanup.pending.bytes", ObserveKvCleanupPendingBytes, unit: "By",
            description: "Obsolete KV state bytes still pending cleanup, per database.");
        Meter.CreateObservableGauge(
            "sonnetdb.kv.cleanup.rate", ObserveKvCleanupRate, unit: "By/s",
            description: "Bytes reclaimed per second by the most recent KV cleanup round, per database.");
    }

    /// <summary>
    /// 注册一个引擎实例，使 per-db ObservableGauge 覆盖它；返回句柄供 <see cref="UnregisterEngine"/>。
    /// 弱引用持有：未显式 Dispose 的实例不会因注册表而泄漏，其条目在观测时自动剔除。
    /// </summary>
    internal static long RegisterEngine(Tsdb engine)
    {
        long id = Interlocked.Increment(ref s_nextEngineId);
        string name = DatabaseLabel(engine.RootDirectory);
        Engines[id] = new EngineEntry(
            new WeakReference<Tsdb>(engine),
            new KeyValuePair<string, object?>("sonnetdb.database", name));
        return id;
    }

    /// <summary>注销引擎实例（<c>Tsdb.Dispose</c> 调用）。</summary>
    internal static void UnregisterEngine(long handle) => Engines.TryRemove(handle, out _);

    private static string DatabaseLabel(string rootDirectory)
    {
        string trimmed = rootDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        string name = System.IO.Path.GetFileName(trimmed);
        return name.Length > 0 ? name : trimmed;
    }

    private static IEnumerable<Measurement<long>> ObserveMemTableBytes()
        => Observe(static e => e.MemTable.EstimatedBytes);

    private static IEnumerable<Measurement<long>> ObserveMemTablePoints()
        => Observe(static e => e.MemTable.PointCount);

    private static IEnumerable<Measurement<long>> ObserveSegmentCount()
        => Observe(static e => e.Segments.SegmentCount);

    private static IEnumerable<Measurement<long>> ObserveFlushPending()
        => Observe(static e => e.FlushPumpPendingCount);

    private static IEnumerable<Measurement<long>> ObserveKvCleanupPendingFiles()
        => Observe(static e => e.KvCleanupPendingFiles);

    private static IEnumerable<Measurement<long>> ObserveKvCleanupPendingBytes()
        => Observe(static e => e.KvCleanupPendingBytes);

    private static IEnumerable<Measurement<double>> ObserveKvCleanupRate()
        => ObserveDouble(static e => e.KvCleanupBytesPerSecond);

    private static List<Measurement<long>> Observe(Func<Tsdb, long> selector)
    {
        var result = new List<Measurement<long>>(Engines.Count);
        foreach (var (id, entry) in Engines)
        {
            if (!entry.Engine.TryGetTarget(out var engine))
            {
                // 引擎已被 GC（调用方未 Dispose）：剔除死条目。
                Engines.TryRemove(id, out _);
                continue;
            }

            try
            {
                result.Add(new Measurement<long>(selector(engine), entry.Tag));
            }
            catch
            {
                // 与并发 Dispose 竞争时观测失败可安全跳过（下一轮观测不再包含该实例）。
            }
        }

        return result;
    }

    private static List<Measurement<double>> ObserveDouble(Func<Tsdb, double> selector)
    {
        var result = new List<Measurement<double>>(Engines.Count);
        foreach (var (id, entry) in Engines)
        {
            if (!entry.Engine.TryGetTarget(out var engine))
            {
                Engines.TryRemove(id, out _);
                continue;
            }

            try
            {
                result.Add(new Measurement<double>(selector(engine), entry.Tag));
            }
            catch
            {
                // 与并发 Dispose 竞争时跳过本轮观测。
            }
        }

        return result;
    }
}
