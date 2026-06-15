namespace SonnetDB.Parity.Adapters;

/// <summary>
/// Parity 能力契约：一个 <see cref="IDataPlane"/> 把一种后端（SonnetDB 自身或某个竞品）
/// 抽象成若干"支柱"操作集合，使同一份 <see cref="Scenarios.IScenario"/> 能在两边各跑一遍。
/// </summary>
/// <remarks>
/// PR #127 仅落地关系型支柱（<see cref="Relational"/>）。后续 PR（#128+）会按里程碑顺序
/// 逐步补齐 Ts / Kv / Objects / Mq / Fulltext / Vector / Analytics 等支柱属性，每次新增
/// 都向后兼容（仅追加成员）。
/// </remarks>
public interface IDataPlane : IAsyncDisposable
{
    /// <summary>当前后端实际支持的能力位集合，供 runner 判定场景是否应被 SKIP。</summary>
    Capability Capabilities { get; }

    /// <summary>关系型操作集合（PR #127）。</summary>
    IRelationalOps Relational { get; }
}

/// <summary>
/// 后端能力标志位。每个 <see cref="Scenarios.IScenario"/> 通过 <see cref="Scenarios.IScenario.Required"/>
/// 声明其依赖；runner 看到后端 <see cref="IDataPlane.Capabilities"/> 不包含所需位时，
/// 将该场景标记为 SKIPPED（记录 gap_reason），而不是判定 FAIL。
/// </summary>
/// <remarks>
/// 位定义与 <c>docs/parity-roadmap.md</c> 的契约保持一致：低位为八大支柱，
/// 高位（1L &lt;&lt; 16 起）为细粒度能力，便于场景精确声明依赖。
/// </remarks>
[Flags]
public enum Capability : long
{
    /// <summary>无任何能力。</summary>
    None = 0,

    /// <summary>关系型（表 / SQL / 事务）。</summary>
    Relational = 1L << 0,

    /// <summary>时序（measurement / 聚合 / 窗口）。</summary>
    TimeSeries = 1L << 1,

    /// <summary>KV / 缓存。</summary>
    Kv = 1L << 2,

    /// <summary>对象桶。</summary>
    Object = 1L << 3,

    /// <summary>消息队列 / 追加日志。</summary>
    Mq = 1L << 4,

    /// <summary>全文检索。</summary>
    Fulltext = 1L << 5,

    /// <summary>向量检索。</summary>
    Vector = 1L << 6,

    /// <summary>分析（大规模 GROUP BY / 窗口函数）。</summary>
    Analytics = 1L << 7,

    // ── 细粒度能力标志（每个场景按需声明依赖） ──────────────────────────────

    /// <summary>KV 原子自增 / 自减。</summary>
    KvIncr = 1L << 16,

    /// <summary>KV 比较并交换（乐观锁）。</summary>
    KvCas = 1L << 17,

    /// <summary>KV 区间 / 前缀扫描。</summary>
    KvRangeScan = 1L << 18,

    /// <summary>对象桶 multipart 上传。</summary>
    ObjectMultipart = 1L << 19,

    /// <summary>MQ 消费组。</summary>
    MqConsumerGroup = 1L << 20,

    /// <summary>MQ 按 offset 重放。</summary>
    MqReplayFromOffset = 1L << 21,

    /// <summary>SQL 子查询。</summary>
    SqlSubquery = 1L << 22,

    /// <summary>SQL 窗口函数。</summary>
    SqlWindowFunction = 1L << 23,

    /// <summary>SQL 外键约束。</summary>
    SqlForeignKey = 1L << 24,

    /// <summary>分位数算法准确度（t-digest 等）。</summary>
    AccuracyPercentile = 1L << 25,

    /// <summary>HNSW 带过滤的向量检索。</summary>
    HnswFiltered = 1L << 26,
}
