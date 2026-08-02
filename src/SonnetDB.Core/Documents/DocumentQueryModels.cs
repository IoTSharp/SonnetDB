namespace SonnetDB.Documents;

/// <summary>
/// 文档查询计划，统一供 SQL SELECT 与 Document API 使用。
/// </summary>
/// <param name="Filter">可选过滤表达式；为 null 时匹配全部文档。</param>
/// <param name="Projection">可选投影；为 null 时返回完整文档。</param>
/// <param name="Sort">排序字段列表；为空时按文档 ID 稳定升序。</param>
/// <param name="Limit">最多返回的文档数；为 null 时不限制。</param>
/// <param name="Skip">跳过的文档数。</param>
/// <param name="Collation">字符串过滤与排序使用的基础校对模式。</param>
public sealed record DocumentQuery(
    DocumentFilter? Filter = null,
    DocumentProjection? Projection = null,
    IReadOnlyList<DocumentSort>? Sort = null,
    int? Limit = null,
    int Skip = 0,
    DocumentCollation Collation = DocumentCollation.Ordinal)
{
    /// <summary>排序字段列表；为空时按文档 ID 稳定升序。</summary>
    public IReadOnlyList<DocumentSort> Sort { get; } = Sort ?? Array.Empty<DocumentSort>();

    /// <summary>
    /// 使用默认 ordinal 校对模式创建文档查询，保留既有二进制构造入口。
    /// </summary>
    /// <param name="Filter">可选过滤表达式。</param>
    /// <param name="Projection">可选投影。</param>
    /// <param name="Sort">排序字段列表。</param>
    /// <param name="Limit">最多返回的文档数。</param>
    /// <param name="Skip">跳过的文档数。</param>
    public DocumentQuery(
        DocumentFilter? Filter,
        DocumentProjection? Projection,
        IReadOnlyList<DocumentSort>? Sort,
        int? Limit,
        int Skip)
        : this(Filter, Projection, Sort, Limit, Skip, DocumentCollation.Ordinal)
    {
    }

    /// <summary>
    /// 按旧版五字段形态解构查询，忽略新增的校对模式。
    /// </summary>
    /// <param name="Filter">过滤表达式。</param>
    /// <param name="Projection">投影。</param>
    /// <param name="Sort">排序字段列表。</param>
    /// <param name="Limit">最多返回的文档数。</param>
    /// <param name="Skip">跳过的文档数。</param>
    public void Deconstruct(
        out DocumentFilter? Filter,
        out DocumentProjection? Projection,
        out IReadOnlyList<DocumentSort>? Sort,
        out int? Limit,
        out int Skip)
    {
        Filter = this.Filter;
        Projection = this.Projection;
        Sort = this.Sort;
        Limit = this.Limit;
        Skip = this.Skip;
    }
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
/// <param name="Value">
/// 操作数：<see cref="DocumentFilterOperator.In"/>、<see cref="DocumentFilterOperator.NotIn"/> 与
/// <see cref="DocumentFilterOperator.All"/> 使用值列表，<see cref="DocumentFilterOperator.ElementMatch"/>
/// 使用子过滤表达式，<see cref="DocumentFilterOperator.Regex"/> 使用字符串或 <see cref="DocumentRegex"/>，
/// <see cref="DocumentFilterOperator.Type"/> 使用 <see cref="DocumentJsonType"/>、类型名或对应列表。
/// </param>
public sealed record DocumentFieldFilter(
    DocumentFieldRef Field,
    DocumentFilterOperator Operator,
    object? Value = null) : DocumentFilter;

/// <summary>
/// 文档正则查询操作数。
/// </summary>
/// <param name="Pattern">正则模式。</param>
/// <param name="Options">可选标志，支持 <c>i/c/m/s/x</c>。</param>
public sealed record DocumentRegex(string Pattern, string? Options = null);

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
    /// <summary>数组中是否至少有一个元素完整匹配子过滤表达式。</summary>
    ElementMatch,
    /// <summary>字符串是否匹配有界正则表达式。</summary>
    Regex,
    /// <summary>字段是否属于指定 JSON 类型。</summary>
    Type,
    /// <summary>数组长度是否等于指定非负整数。</summary>
    Size,
    /// <summary>数组是否包含操作数列表中的全部值。</summary>
    All,
}

/// <summary>
/// 文档 JSON 原生类型。
/// </summary>
public enum DocumentJsonType
{
    /// <summary>JSON null。</summary>
    Null,
    /// <summary>JSON 布尔值。</summary>
    Boolean,
    /// <summary>JSON 数值，不细分整数与浮点数。</summary>
    Number,
    /// <summary>JSON 字符串。</summary>
    String,
    /// <summary>JSON 对象。</summary>
    Object,
    /// <summary>JSON 数组。</summary>
    Array,
}

/// <summary>
/// 文档查询支持的基础字符串校对模式。
/// </summary>
/// <remarks>
/// 校对模式只影响字符串值的过滤、数组值比较和排序；JSON 属性名与 path 始终按 ordinal 解析，
/// 正则大小写规则由 <see cref="DocumentRegex.Options"/> 单独控制。非 ordinal 模式不复用现有 ordinal path 索引。
/// </remarks>
public enum DocumentCollation
{
    /// <summary>按 Unicode 码元进行大小写敏感的 ordinal 比较。</summary>
    Ordinal,
    /// <summary>按 Unicode 码元进行大小写不敏感的 ordinal 比较。</summary>
    OrdinalIgnoreCase,
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

/// <summary>
/// 文档集合二级索引与全文索引相对主数据的一致性校验报告。
/// <para>
/// 二级索引是主文档的纯函数（<c>BuildIndexEntries</c>）：全表扫主文档重算期望条目集，与 KV
/// 中已存条目集对比。<see cref="DocumentIndexConsistencyEntry.MissingEntries"/>（欠包含）会导致查询
/// 静默漏行，是危险信号；<see cref="DocumentIndexConsistencyEntry.OrphanEntries"/>（过包含）由
/// planner 的 <c>Matches</c> 复检兜住、结果正确但浪费扫描。只要 open 时 <c>RebuildIndexesLocked</c>
/// 从主数据全量重建索引，崩溃 / torn write 造成的不一致都会在重开集合时自愈。
/// </para>
/// </summary>
/// <param name="CollectionName">文档集合名。</param>
/// <param name="DocumentCount">主数据文档总数。</param>
/// <param name="IsConsistent">是否无任何索引欠包含（无 Missing 条目）。</param>
/// <param name="Indexes">每个二级索引的一致性明细。</param>
/// <param name="FullTextIndexes">每个全文索引的文档计数一致性明细。</param>
/// <param name="VectorIndexes">每个向量索引的向量计数一致性明细。</param>
public sealed record DocumentIndexConsistencyReport(
    string CollectionName,
    int DocumentCount,
    bool IsConsistent,
    IReadOnlyList<DocumentIndexConsistencyEntry> Indexes,
    IReadOnlyList<DocumentFullTextConsistencyEntry> FullTextIndexes,
    IReadOnlyList<DocumentVectorConsistencyEntry> VectorIndexes);

/// <summary>
/// 单个文档二级索引的一致性明细。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="ExpectedEntries">按主数据重算得到的期望索引条目数。</param>
/// <param name="ActualEntries">KV 中当前存在的索引条目数。</param>
/// <param name="MissingEntries">期望存在但 KV 中缺失的条目数（欠包含，会静默漏行）。</param>
/// <param name="OrphanEntries">KV 中存在但主数据不再要求的条目数（过包含，planner 复检安全但浪费）。</param>
public sealed record DocumentIndexConsistencyEntry(
    string IndexName,
    int ExpectedEntries,
    int ActualEntries,
    int MissingEntries,
    int OrphanEntries)
{
    /// <summary>该索引是否一致（无欠包含也无过包含）。</summary>
    public bool IsConsistent => MissingEntries == 0 && OrphanEntries == 0;
}

/// <summary>
/// 单个全文索引相对主数据的文档计数一致性明细。
/// </summary>
/// <param name="IndexName">全文索引名。</param>
/// <param name="DocumentCount">主数据文档总数。</param>
/// <param name="IndexedDocumentCount">全文索引当前可见文档数。</param>
public sealed record DocumentFullTextConsistencyEntry(
    string IndexName,
    int DocumentCount,
    int IndexedDocumentCount)
{
    /// <summary>索引可见文档数是否与主数据一致。</summary>
    public bool IsConsistent => DocumentCount == IndexedDocumentCount;
}

/// <summary>
/// 单个向量索引相对主数据的向量计数一致性明细。
/// </summary>
/// <param name="IndexName">向量索引名。</param>
/// <param name="EligibleDocuments">主数据中含该索引 path 且维度匹配的文档数（应被索引的向量数）。</param>
/// <param name="IndexedVectors">向量索引当前持有的向量数。</param>
public sealed record DocumentVectorConsistencyEntry(
    string IndexName,
    int EligibleDocuments,
    int IndexedVectors)
{
    /// <summary>索引向量数是否与应索引的文档数一致。</summary>
    public bool IsConsistent => EligibleDocuments == IndexedVectors;
}
