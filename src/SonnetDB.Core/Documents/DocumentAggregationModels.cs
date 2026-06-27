namespace SonnetDB.Documents;

/// <summary>
/// 文档聚合管线定义。
/// </summary>
/// <param name="Stages">按顺序执行的聚合阶段。</param>
public sealed record DocumentAggregationPipeline(IReadOnlyList<DocumentAggregationStage> Stages);

/// <summary>
/// 文档聚合阶段基类。
/// </summary>
public abstract record DocumentAggregationStage;

/// <summary>
/// `$match` 阶段，复用文档查询过滤表达式。
/// </summary>
/// <param name="Filter">过滤表达式。</param>
public sealed record DocumentMatchStage(DocumentFilter Filter) : DocumentAggregationStage;

/// <summary>
/// `$project` 阶段，按指定字段输出 JSON 对象。
/// </summary>
/// <param name="Projection">投影定义。</param>
public sealed record DocumentProjectStage(DocumentProjection Projection) : DocumentAggregationStage;

/// <summary>
/// `$group` 阶段，按 JSON path 或 `_id` 分组并计算聚合值。
/// </summary>
/// <param name="Keys">分组键列表；为空时表示全局分组。</param>
/// <param name="Accumulators">聚合函数列表。</param>
public sealed record DocumentGroupStage(
    IReadOnlyList<DocumentAggregationGroupKey> Keys,
    IReadOnlyList<DocumentAggregationAccumulator> Accumulators) : DocumentAggregationStage;

/// <summary>
/// 文档聚合分组键。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Field">输入字段引用。</param>
public sealed record DocumentAggregationGroupKey(string Name, DocumentFieldRef Field);

/// <summary>
/// 文档聚合函数定义。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Operator">聚合函数。</param>
/// <param name="Field">输入字段引用；`count` 可为空。</param>
public sealed record DocumentAggregationAccumulator(
    string Name,
    DocumentAggregationAccumulatorOperator Operator,
    DocumentFieldRef? Field = null);

/// <summary>
/// 文档聚合函数类型。
/// </summary>
public enum DocumentAggregationAccumulatorOperator
{
    /// <summary>计数。</summary>
    Count,
    /// <summary>求和。</summary>
    Sum,
    /// <summary>平均值。</summary>
    Average,
    /// <summary>最小值。</summary>
    Min,
    /// <summary>最大值。</summary>
    Max,
    /// <summary>第一条输入值。</summary>
    First,
    /// <summary>最后一条输入值。</summary>
    Last,
    /// <summary>组内去重值数组。</summary>
    Distinct,
}

/// <summary>
/// `$sort` 阶段。
/// </summary>
/// <param name="Sort">排序字段列表。</param>
public sealed record DocumentSortStage(IReadOnlyList<DocumentSort> Sort) : DocumentAggregationStage;

/// <summary>
/// `$limit` 阶段。
/// </summary>
/// <param name="Limit">最多输出的文档数。</param>
public sealed record DocumentLimitStage(int Limit) : DocumentAggregationStage;

/// <summary>
/// `$skip` 阶段。
/// </summary>
/// <param name="Skip">跳过的文档数。</param>
public sealed record DocumentSkipStage(int Skip) : DocumentAggregationStage;

/// <summary>
/// `$unwind` 阶段，将数组字段展开为多条文档。
/// </summary>
/// <param name="Field">要展开的字段。</param>
/// <param name="Name">可选输出别名；为空时替换原字段。</param>
/// <param name="PreserveNullAndEmptyArrays">数组为空或字段缺失时是否保留原文档。</param>
public sealed record DocumentUnwindStage(
    DocumentFieldRef Field,
    string? Name = null,
    bool PreserveNullAndEmptyArrays = false) : DocumentAggregationStage;

/// <summary>
/// `$count` 阶段。
/// </summary>
/// <param name="Name">输出计数字段名。</param>
public sealed record DocumentCountStage(string Name = "count") : DocumentAggregationStage;

/// <summary>
/// `$distinct` 等价阶段，输出指定字段的去重值。
/// </summary>
/// <param name="Field">去重字段。</param>
/// <param name="Name">输出字段名。</param>
/// <param name="Limit">最多输出的去重值数量。</param>
public sealed record DocumentDistinctStage(
    DocumentFieldRef Field,
    string Name = "value",
    int? Limit = null) : DocumentAggregationStage;

/// <summary>
/// 文档聚合执行结果。
/// </summary>
/// <param name="Documents">聚合输出的紧凑 JSON 文档。</param>
public sealed record DocumentAggregationResult(IReadOnlyList<string> Documents);
