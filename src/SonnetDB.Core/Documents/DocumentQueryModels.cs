namespace SonnetDB.Documents;

/// <summary>
/// 文档查询计划，统一供 SQL SELECT 与 Document API 使用。
/// </summary>
/// <param name="Filter">可选过滤表达式；为 null 时匹配全部文档。</param>
/// <param name="Projection">可选投影；为 null 时返回完整文档。</param>
/// <param name="Sort">排序字段列表；为空时按文档 ID 稳定升序。</param>
/// <param name="Limit">最多返回的文档数；为 null 时不限制。</param>
/// <param name="Skip">跳过的文档数。</param>
public sealed record DocumentQuery(
    DocumentFilter? Filter = null,
    DocumentProjection? Projection = null,
    IReadOnlyList<DocumentSort>? Sort = null,
    int? Limit = null,
    int Skip = 0)
{
    /// <summary>排序字段列表；为空时按文档 ID 稳定升序。</summary>
    public IReadOnlyList<DocumentSort> Sort { get; } = Sort ?? Array.Empty<DocumentSort>();
}

/// <summary>
/// 文档过滤表达式抽象基类。
/// </summary>
public abstract record DocumentFilter;

/// <summary>
/// 逻辑与过滤表达式。
/// </summary>
/// <param name="Filters">所有子过滤表达式。</param>
public sealed record DocumentAndFilter(IReadOnlyList<DocumentFilter> Filters) : DocumentFilter;

/// <summary>
/// 逻辑或过滤表达式。
/// </summary>
/// <param name="Filters">所有子过滤表达式。</param>
public sealed record DocumentOrFilter(IReadOnlyList<DocumentFilter> Filters) : DocumentFilter;

/// <summary>
/// 逻辑非过滤表达式。
/// </summary>
/// <param name="Filter">要取反的子过滤表达式。</param>
public sealed record DocumentNotFilter(DocumentFilter Filter) : DocumentFilter;

/// <summary>
/// 字段比较过滤表达式。
/// </summary>
/// <param name="Field">文档字段引用。</param>
/// <param name="Operator">比较运算符。</param>
/// <param name="Value">比较值；对 <see cref="DocumentFilterOperator.In"/> / <see cref="DocumentFilterOperator.NotIn"/> 为值列表。</param>
public sealed record DocumentFieldFilter(
    DocumentFieldRef Field,
    DocumentFilterOperator Operator,
    object? Value = null) : DocumentFilter;

/// <summary>
/// 文档字段引用。
/// </summary>
/// <param name="Kind">字段类别。</param>
/// <param name="Path">JSON path；仅当 <paramref name="Kind"/> 为 <see cref="DocumentFieldKind.JsonPath"/> 时使用。</param>
public sealed record DocumentFieldRef(DocumentFieldKind Kind, string? Path = null)
{
    /// <summary>文档 ID 字段。</summary>
    public static DocumentFieldRef Id { get; } = new(DocumentFieldKind.Id);

    /// <summary>完整 JSON 文档字段。</summary>
    public static DocumentFieldRef Document { get; } = new(DocumentFieldKind.Document);

    /// <summary>
    /// 创建 JSON path 字段引用。
    /// </summary>
    /// <param name="path">JSON path 文本。</param>
    public static DocumentFieldRef JsonPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new DocumentFieldRef(DocumentFieldKind.JsonPath, SonnetDB.Documents.JsonPath.Parse(path).Text);
    }
}

/// <summary>
/// 文档字段类别。
/// </summary>
public enum DocumentFieldKind
{
    /// <summary>文档 ID。</summary>
    Id,
    /// <summary>完整 JSON 文档。</summary>
    Document,
    /// <summary>JSON path 字段。</summary>
    JsonPath,
}

/// <summary>
/// 文档过滤运算符。
/// </summary>
public enum DocumentFilterOperator
{
    /// <summary>等于。</summary>
    Equal,
    /// <summary>不等于。</summary>
    NotEqual,
    /// <summary>大于。</summary>
    GreaterThan,
    /// <summary>大于等于。</summary>
    GreaterThanOrEqual,
    /// <summary>小于。</summary>
    LessThan,
    /// <summary>小于等于。</summary>
    LessThanOrEqual,
    /// <summary>属于给定值列表。</summary>
    In,
    /// <summary>不属于给定值列表。</summary>
    NotIn,
    /// <summary>字段是否存在。</summary>
    Exists,
    /// <summary>数组、对象 JSON 文本或字符串是否包含给定值。</summary>
    Contains,
}

/// <summary>
/// 文档查询投影。
/// </summary>
/// <param name="Fields">投影字段；为空时返回完整文档。</param>
public sealed record DocumentProjection(IReadOnlyList<DocumentProjectionField> Fields);

/// <summary>
/// 文档投影字段。
/// </summary>
/// <param name="Name">输出字段名。</param>
/// <param name="Field">源字段引用。</param>
public sealed record DocumentProjectionField(string Name, DocumentFieldRef Field);

/// <summary>
/// 文档排序字段。
/// </summary>
/// <param name="Field">排序字段引用。</param>
/// <param name="Descending">是否降序。</param>
public sealed record DocumentSort(DocumentFieldRef Field, bool Descending = false);

/// <summary>
/// 文档查询规划结果，描述最终访问路径、候选计划与未实现能力缺口。
/// </summary>
/// <param name="AccessPath">最终选择的访问路径。</param>
/// <param name="IndexName">最终选择的索引名；全表扫描时为 null。</param>
/// <param name="EstimatedCandidateRows">访问路径产生的候选行估算。</param>
/// <param name="EstimatedOutputRows">应用剩余过滤条件后的输出行估算。</param>
/// <param name="FilterPushdown">是否把部分过滤条件下推到访问路径。</param>
/// <param name="FilterPushdownFields">已下推的字段或 JSON path 列表。</param>
/// <param name="ResidualFilterFields">仍需逐行计算的字段或 JSON path 列表。</param>
/// <param name="SortUsesIndex">排序是否可由所选访问路径的天然顺序满足。</param>
/// <param name="ProjectionCoveredByIndex">投影是否可完全由索引覆盖。</param>
/// <param name="Candidates">规划器评估过的候选访问路径。</param>
/// <param name="GapReason">未实现优化的原因；没有缺口时为 null。</param>
public sealed record DocumentQueryPlan(
    string AccessPath,
    string? IndexName,
    int EstimatedCandidateRows,
    int EstimatedOutputRows,
    bool FilterPushdown,
    IReadOnlyList<string> FilterPushdownFields,
    IReadOnlyList<string> ResidualFilterFields,
    bool SortUsesIndex,
    bool ProjectionCoveredByIndex,
    IReadOnlyList<DocumentQueryPlanCandidate> Candidates,
    string? GapReason);

/// <summary>
/// 文档查询规划候选访问路径。
/// </summary>
/// <param name="AccessPath">候选访问路径。</param>
/// <param name="IndexName">候选索引名；全表扫描时为 null。</param>
/// <param name="EstimatedCandidateRows">候选路径产生的候选行估算。</param>
/// <param name="Cost">候选路径的代价分数，数值越小越优。</param>
/// <param name="Selected">该候选是否被最终选中。</param>
/// <param name="FilterPushdownFields">该候选可下推的字段或 JSON path 列表。</param>
/// <param name="RejectReason">候选未被选中的原因；被选中时为 null。</param>
public sealed record DocumentQueryPlanCandidate(
    string AccessPath,
    string? IndexName,
    int EstimatedCandidateRows,
    int Cost,
    bool Selected,
    IReadOnlyList<string> FilterPushdownFields,
    string? RejectReason);

/// <summary>
/// 文档查询命中项。
/// </summary>
/// <param name="Id">文档 ID。</param>
/// <param name="Json">返回给调用方的 JSON 文本。</param>
/// <param name="Version">底层 KV 版本。</param>
public sealed record DocumentQueryItem(string Id, string Json, long Version);

/// <summary>
/// 文档查询结果。
/// </summary>
/// <param name="Items">当前页命中文档。</param>
/// <param name="MatchedCount">分页前的匹配文档数。</param>
/// <param name="AccessPath">实际采用的访问路径。</param>
/// <param name="IndexName">采用的索引名；全扫描时为 null。</param>
public sealed record DocumentQueryResult(
    IReadOnlyList<DocumentQueryItem> Items,
    int MatchedCount,
    string AccessPath,
    string? IndexName);
