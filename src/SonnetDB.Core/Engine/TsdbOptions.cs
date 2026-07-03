using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;

namespace SonnetDB.Engine;

/// <summary>
/// SonnetDB 引擎全局配置选项。
/// </summary>
public sealed record TsdbOptions
{
    /// <summary>数据库根目录路径（默认为当前工作目录 "."）。</summary>
    public string RootDirectory { get; init; } = ".";

    /// <summary>MemTable Flush 阈值策略（默认 16 MB / 100 万点 / 5 分钟）。</summary>
    public MemTableFlushPolicy FlushPolicy { get; init; } = MemTableFlushPolicy.Default;

    /// <summary>Segment 写入选项（默认 64 KB 缓冲、fsync 开启）。</summary>
    public SegmentWriterOptions SegmentWriterOptions { get; init; } = SegmentWriterOptions.Default;

    /// <summary>WAL 写缓冲区大小（字节），默认 64 KB。</summary>
    public int WalBufferSize { get; init; } = 64 * 1024;

    /// <summary>
    /// 是否在每次 Append 后自动 fsync WAL（持久性最强；默认 false，由 Flush 时刻保证）。
    /// <para>
    /// 语义分级（从弱到强）：
    /// <list type="bullet">
    ///   <item><description><see cref="SyncWalOnEveryWrite"/>=false 且 <see cref="FlushWalToOsOnWrite"/>=false：
    ///     WAL 记录仅进程内 BufferedStream，直到 segment flush / roll / dispose 才落盘——普通进程 crash 也会
    ///     丢失最近一个 flush 窗口内的已确认写（最高吞吐，最弱持久性）。</description></item>
    ///   <item><description><see cref="FlushWalToOsOnWrite"/>=true（默认）：每次写入后把 WAL 交给 OS
    ///     （flush BufferedStream，不 fsync）——普通进程 crash 不丢已确认写（数据已在 OS page cache），
    ///     仅掉电/内核崩溃可能丢；开销远低于 fsync（#196）。</description></item>
    ///   <item><description><see cref="SyncWalOnEveryWrite"/>=true：每批写入 fsync（group-commit 批处理）——
    ///     掉电也不丢已确认写（最强持久性，写延迟最高）。</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public bool SyncWalOnEveryWrite { get; init; } = false;

    /// <summary>
    /// 是否在每次写入后把 WAL 缓冲区 flush 到 OS（不 fsync）。默认 <c>true</c>：普通进程 crash 不丢
    /// 已确认写。开销为一次用户态→内核态拷贝，远低于 fsync；仅当 <see cref="SyncWalOnEveryWrite"/>=false
    /// 时生效（后者已包含更强的 fsync）。设为 <c>false</c> 可换取极限写吞吐，但牺牲进程崩溃的持久性（#196）。
    /// </summary>
    public bool FlushWalToOsOnWrite { get; init; } = true;

    /// <summary>WAL group-commit 配置，仅在 <see cref="SyncWalOnEveryWrite"/> 为 <c>true</c> 时生效。</summary>
    public WalGroupCommitOptions WalGroupCommit { get; init; } = WalGroupCommitOptions.Default;

    /// <summary>段读取选项（默认两项校验均启用）。</summary>
    public SegmentReaderOptions SegmentReaderOptions { get; init; } = SegmentReaderOptions.Default;

    /// <summary>
    /// 是否允许数值聚合在适合的落盘 block 范围上使用 <c>System.Numerics.Vector&lt;T&gt;</c> SIMD 快路径。
    /// 默认开启；不支持硬件加速、遇到 NaN 或不适合的编码时会自动回退到标量实现。
    /// </summary>
    public bool UseSimdNumericAggregates { get; init; } = true;

    /// <summary>后台 Flush 线程选项（默认启用，轮询间隔 1s，关闭超时 30s）。</summary>
    public BackgroundFlushOptions BackgroundFlush { get; init; } = BackgroundFlushOptions.Default;

    /// <summary>Compaction 策略选项（默认启用，Size-Tiered，MinTierSize=4）。</summary>
    public CompactionPolicy Compaction { get; init; } = CompactionPolicy.Default;

    /// <summary>WAL 滚动策略（默认启用，64MB / 百万条双阈值）。</summary>
    public WalRollingPolicy WalRolling { get; init; } = WalRollingPolicy.Default;

    /// <summary>Retention TTL 策略（默认禁用，保持向后兼容）。</summary>
    public RetentionPolicy Retention { get; init; } = RetentionPolicy.Default;

    /// <summary>Tombstone manifest 周期性 checkpoint 选项。</summary>
    public TombstoneCheckpointOptions TombstoneCheckpoint { get; init; } = TombstoneCheckpointOptions.Default;

    /// <summary>内置 KV Keyspace 存储选项。</summary>
    public KvOptions Kv { get; init; } = KvOptions.Default;

    /// <summary>
    /// 是否允许通过 <c>Tsdb.Functions</c> 注册用户自定义函数（UDF）。
    /// 默认 <c>true</c>（嵌入式场景启用）；SonnetDB 默认设为 <c>false</c> 以保证 AOT 兼容。
    /// </summary>
    public bool AllowUserFunctions { get; init; } = true;

    /// <summary>默认配置实例。</summary>
    public static TsdbOptions Default { get; } = new();
}
