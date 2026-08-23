namespace SonnetDB.Graphs;

/// <summary>
/// 图查询的逻辑计划基类。计划只描述语义，不携带存储实现细节。
/// </summary>
public abstract record GraphLogicalPlan;

/// <summary>
/// 顶点扫描或索引 seek 计划。
/// </summary>
/// <param name="LabelId">可选顶点标签；为空时扫描全部顶点。</param>
/// <param name="PropertyId">可选精确属性索引列。</param>
/// <param name="PropertyValue">与 <paramref name="PropertyId"/> 配对的属性值。</param>
/// <param name="Options">结果页和结果预算。</param>
public sealed record GraphNodeScanPlan(
    LabelId? LabelId = null,
    int? PropertyId = null,
    GraphPropertyValue? PropertyValue = null,
    GraphCursorOptions? Options = null) : GraphLogicalPlan;

/// <summary>
/// 边扫描或索引 seek 计划。
/// </summary>
/// <param name="LabelId">可选边标签；为空时扫描全部边。</param>
/// <param name="PropertyId">可选精确属性索引列。</param>
/// <param name="PropertyValue">与 <paramref name="PropertyId"/> 配对的属性值。</param>
/// <param name="Options">结果页和结果预算。</param>
public sealed record GraphEdgeScanPlan(
    LabelId? LabelId = null,
    int? PropertyId = null,
    GraphPropertyValue? PropertyValue = null,
    GraphCursorOptions? Options = null) : GraphLogicalPlan;

/// <summary>
/// 单跳邻接扩展计划。
/// </summary>
/// <param name="AnchorId">扩展起点。</param>
/// <param name="Direction">出、入或双向。</param>
/// <param name="EdgeLabelId">可选边标签过滤。</param>
/// <param name="Options">结果页和结果预算。</param>
public sealed record GraphExpandPlan(
    GraphElementId AnchorId,
    GraphDirection Direction = GraphDirection.Outgoing,
    LabelId? EdgeLabelId = null,
    GraphCursorOptions? Options = null) : GraphLogicalPlan
{
    /// <summary>可选的目标顶点 label/property 精确匹配谓词。</summary>
    public GraphVertexPredicate? TargetPredicate { get; init; }
}

/// <summary>路径计划的搜索顺序。</summary>
public enum GraphPathSearchMode : byte
{
    /// <summary>广度优先。</summary>
    BreadthFirst = 1,
    /// <summary>深度优先。</summary>
    DepthFirst = 2,
}

/// <summary>
/// 固定或受限可变长度路径计划。
/// </summary>
/// <param name="StartId">起点。</param>
/// <param name="Mode">广度优先或深度优先。</param>
/// <param name="MinDepth">最小 hop 数。</param>
/// <param name="MaxDepth">最大 hop 数。</param>
/// <param name="Direction">扩展方向。</param>
/// <param name="EdgeLabelId">可选边标签过滤。</param>
/// <param name="Options">路径预算和分页参数。</param>
public sealed record GraphPathPlan(
    GraphElementId StartId,
    GraphPathSearchMode Mode = GraphPathSearchMode.BreadthFirst,
    int MinDepth = 0,
    int MaxDepth = 6,
    GraphDirection Direction = GraphDirection.Outgoing,
    LabelId? EdgeLabelId = null,
    GraphTraversalOptions? Options = null) : GraphLogicalPlan
{
    /// <summary>
    /// 广度优先搜索时是否按终点全局去重。关闭后仍保留路径内 uniqueness，
    /// 供带最小深度约束的 shortest path 在关系投影层选择首条合法路径。
    /// </summary>
    public bool DeduplicateBreadthFirstEndpoints { get; init; } = true;
}

/// <summary>关系映射顶点的 scan 或 key seek 计划。</summary>
internal sealed record RelationalGraphNodePlan(
    string VertexTable,
    IReadOnlyList<object?>? KeyValues = null,
    RelationalGraphAccessOptions? Options = null) : GraphLogicalPlan;

/// <summary>关系映射边表的 endpoint 扩展计划。</summary>
internal sealed record RelationalGraphExpandPlan(
    string EdgeTable,
    GraphDirection Direction,
    IReadOnlyList<object?> EndpointKeyValues,
    RelationalGraphAccessOptions? Options = null) : GraphLogicalPlan;

internal sealed class GraphTraversalDiagnostics
{
    internal long ExpansionCount { get; set; }

    internal long GeneratedPathCount { get; set; }

    internal int PeakFrontier { get; set; }
}
