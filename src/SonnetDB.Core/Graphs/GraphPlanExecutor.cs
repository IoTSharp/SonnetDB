namespace SonnetDB.Graphs;

/// <summary>
/// Graph Logical Plan 的统一 pull 执行入口。
/// </summary>
public static class GraphPlanExecutor
{
    /// <summary>在关系映射 statement snapshot 上执行顶点 scan/seek 计划。</summary>
    internal static RelationalGraphCursor Execute(
        RelationalGraphReadSession session,
        RelationalGraphNodePlan plan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        return session.OpenNodeCursor(plan);
    }

    /// <summary>在关系映射 statement snapshot 上执行 endpoint expand 计划。</summary>
    internal static RelationalGraphCursor Execute(
        RelationalGraphReadSession session,
        RelationalGraphExpandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        return session.OpenExpandCursor(plan);
    }

    /// <summary>执行顶点扫描/seek 计划。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="plan">顶点计划。</param>
    /// <returns>顶点结果游标。</returns>
    public static GraphCursor<GraphVertex> Execute(
        GraphReadSession session,
        GraphNodeScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PropertyId is not null && plan.PropertyValue is null)
            throw new ArgumentException("PropertyId 与 PropertyValue 必须同时提供。", nameof(plan));
        if (plan.PropertyId is null && plan.PropertyValue is not null)
            throw new ArgumentException("PropertyId 与 PropertyValue 必须同时提供。", nameof(plan));

        return plan switch
        {
            { LabelId: { } label, PropertyId: { } propertyId, PropertyValue: { } value } =>
                session.SeekVerticesCore(label, propertyId, value, plan.Options),
            { LabelId: { } label } => session.SeekVerticesByLabelCore(label, plan.Options),
            { PropertyId: not null } => throw new InvalidOperationException(
                "属性 seek 必须同时指定 label；没有 label 时请使用 GraphNodeScanPlan 的全量扫描。"),
            _ => session.ScanVerticesCore(plan.Options),
        };
    }

    /// <summary>执行边扫描/seek 计划。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="plan">边计划。</param>
    /// <returns>边结果游标。</returns>
    public static GraphCursor<GraphEdge> Execute(
        GraphReadSession session,
        GraphEdgeScanPlan plan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PropertyId is not null && plan.PropertyValue is null)
            throw new ArgumentException("PropertyId 与 PropertyValue 必须同时提供。", nameof(plan));
        if (plan.PropertyId is null && plan.PropertyValue is not null)
            throw new ArgumentException("PropertyId 与 PropertyValue 必须同时提供。", nameof(plan));

        return plan switch
        {
            { LabelId: { } label, PropertyId: { } propertyId, PropertyValue: { } value } =>
                session.SeekEdgesCore(label, propertyId, value, plan.Options),
            { LabelId: { } label } => session.SeekEdgesByLabelCore(label, plan.Options),
            { PropertyId: not null } => throw new InvalidOperationException(
                "属性 seek 必须同时指定 label；没有 label 时请使用 GraphEdgeScanPlan 的全量扫描。"),
            _ => session.ScanEdgesCore(plan.Options),
        };
    }

    /// <summary>执行单跳邻接扩展计划。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="plan">扩展计划。</param>
    /// <returns>邻接扩展结果游标。</returns>
    public static GraphCursor<GraphExpansion> Execute(
        GraphReadSession session,
        GraphExpandPlan plan)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        return session.ExpandCore(
            plan.AnchorId,
            plan.Direction,
            plan.EdgeLabelId,
            plan.TargetPredicate,
            plan.Options);
    }

    /// <summary>执行路径计划。</summary>
    /// <param name="session">稳定图读会话。</param>
    /// <param name="plan">路径计划。</param>
    /// <returns>路径结果游标。</returns>
    public static GraphCursor<GraphPath> Execute(
        GraphReadSession session,
        GraphPathPlan plan)
        => ExecutePath(session, plan, diagnostics: null);

    internal static GraphCursor<GraphPath> Execute(
        GraphReadSession session,
        GraphPathPlan plan,
        GraphTraversalDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return ExecutePath(session, plan, diagnostics);
    }

    private static GraphCursor<GraphPath> ExecutePath(
        GraphReadSession session,
        GraphPathPlan plan,
        GraphTraversalDiagnostics? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(plan);
        GraphTraversalOptions options = plan.Options ?? new GraphTraversalOptions();
        if (!Enum.IsDefined(plan.Mode))
            throw new ArgumentOutOfRangeException(nameof(plan), "未知的图路径搜索模式。");
        return session.OpenPathPlan(plan, options, diagnostics);
    }
}
