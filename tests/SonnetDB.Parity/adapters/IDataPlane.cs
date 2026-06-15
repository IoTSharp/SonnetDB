namespace SonnetDB.Parity.Adapters;

/// <summary>
/// Parity 能力契约：一个 <see cref="IDataPlane"/> 把一种后端（SonnetDB 自身或某个竞品）
/// 抽象成若干"支柱"操作集合，使同一份 <see cref="Scenarios.IScenario"/> 能在两边各跑一遍。
/// </summary>
/// <remarks>
/// PR #127 落地关系型支柱（<see cref="Relational"/>）；PR #129 追加 TSDB 支柱
/// （<see cref="TimeSeries"/>）。后续 PR 会按里程碑顺序继续补齐 Kv / Objects / Mq /
/// Fulltext / Vector / Analytics 等支柱属性。
/// </remarks>
public interface IDataPlane : IAsyncDisposable
{
    /// <summary>后端稳定名称（如 <c>sonnetdb</c> / <c>postgres</c>），用于报告与差异表。</summary>
    string BackendName { get; }

    /// <summary>当前后端实际支持的能力位集合，供 runner 判定场景是否应被 SKIP。</summary>
    Capability Capabilities { get; }

    /// <summary>关系型操作集合（PR #127）。</summary>
    IRelationalOps Relational { get; }

    /// <summary>时序操作集合（PR #129）。不支持时序的后端返回空操作对象。</summary>
    ITimeSeriesOps TimeSeries { get; }

    /// <summary>KV 操作集合。不支持 KV 的后端返回空操作对象。</summary>
    IKvOps Kv { get; }

    /// <summary>对象桶操作集合。不支持对象桶的后端返回空操作对象。</summary>
    IObjectOps Objects { get; }

    /// <summary>向量检索操作集合。不支持向量的后端返回空操作对象。</summary>
    IVectorOps Vector { get; }

    /// <summary>消息队列操作集合。不支持 MQ 的后端返回空操作对象。</summary>
    IMqOps Mq { get; }

    /// <summary>全文检索操作集合。不支持全文检索的后端返回空操作对象。</summary>
    IFullTextOps FullText { get; }

    /// <summary>分析操作集合。不支持分析能力的后端返回空操作对象。</summary>
    IAnalyticalOps Analytics { get; }
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

    /// <summary>SQL GROUP BY / 聚合过滤能力。</summary>
    SqlGroupBy = 1L << 27,

    /// <summary>SQL information_schema 元数据视图。</summary>
    SqlInformationSchema = 1L << 28,

    /// <summary>UPDATE 返回受影响行数。</summary>
    SqlUpdateCount = 1L << 29,

    /// <summary>ALTER TABLE 演进能力。</summary>
    SqlAlterTable = 1L << 30,

    /// <summary>默认或显式 READ COMMITTED 可见性边界。</summary>
    SqlReadCommitted = 1L << 31,

    /// <summary>UPDATE ... RETURNING 结果返回能力。</summary>
    SqlUpdateReturning = 1L << 32,

    /// <summary>ON DELETE CASCADE 外键级联能力。</summary>
    SqlCascadeDelete = 1L << 33,

    /// <summary>长时间 TPC-C 类事务压测能力。</summary>
    RelationalTpccLite = 1L << 34,

    /// <summary>SQL HAVING 聚合过滤能力。</summary>
    SqlHaving = 1L << 35,

    /// <summary>相关子查询能力。</summary>
    SqlCorrelatedSubquery = 1L << 36,

    /// <summary>时序 remote_write 或等价批量写入能力。</summary>
    TimeSeriesRemoteWrite = 1L << 37,

    /// <summary>时序按时间窗口聚合能力。</summary>
    TimeSeriesGroupByTime = 1L << 38,

    /// <summary>时序 derivative / deriv 变化率能力。</summary>
    TimeSeriesDerivative = 1L << 39,

    /// <summary>时序 rate / irate 计数器变化率能力。</summary>
    TimeSeriesRateIrate = 1L << 40,

    /// <summary>Holt-Winters 或等价预测能力。</summary>
    TimeSeriesHoltWinters = 1L << 41,

    /// <summary>分位数查询或近似分位数聚合能力。</summary>
    TimeSeriesQuantile = 1L << 42,

    /// <summary>去重计数能力（精确或 HLL 近似）。</summary>
    TimeSeriesDistinctCount = 1L << 43,

    /// <summary>全文 CJK 分词或等价中文检索能力。</summary>
    FulltextCjk = 1L << 44,

    /// <summary>全文 facet/filter 查询能力。</summary>
    FulltextFacetFilter = 1L << 45,

    /// <summary>全文 typo-tolerant 查询能力。</summary>
    FulltextTypoTolerant = 1L << 46,

    /// <summary>分析按时间桶聚合能力。</summary>
    AnalyticsGroupByTime = 1L << 47,

    /// <summary>分析 Top-N 分组排序能力。</summary>
    AnalyticsTopN = 1L << 48,

    /// <summary>分析压缩率指标能力。</summary>
    AnalyticsCompressionRatio = 1L << 49,
}
