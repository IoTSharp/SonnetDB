using SonnetDB.Graphs.Storage;

namespace SonnetDB.Graphs;

/// <summary>
/// 图元素上的类型化属性。
/// </summary>
public readonly record struct GraphProperty
{
    /// <summary>
    /// 创建图属性。
    /// </summary>
    /// <param name="propertyId">大于零的稳定属性标识符。</param>
    /// <param name="value">属性值。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="propertyId"/> 不是正数。</exception>
    public GraphProperty(int propertyId, GraphPropertyValue value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        PropertyId = propertyId;
        Value = value;
    }

    /// <summary>大于零的稳定属性标识符。</summary>
    public int PropertyId { get; }

    /// <summary>属性值。</summary>
    public GraphPropertyValue Value { get; }
}

/// <summary>
/// 原生属性图顶点的不可变快照。
/// </summary>
public sealed class GraphVertex
{
    /// <summary>创建不可变顶点快照。</summary>
    /// <param name="id">顶点标识符。</param>
    /// <param name="elementVersion">大于零的元素版本。</param>
    /// <param name="labels">顶点标签。</param>
    /// <param name="properties">顶点属性。</param>
    public GraphVertex(
        GraphElementId id,
        long elementVersion,
        IReadOnlyList<LabelId> labels,
        IReadOnlyList<GraphProperty> properties)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementVersion);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        ElementVersion = elementVersion;
        Labels = GraphElementRecordCodec.NormalizeLabels(labels, nameof(labels));
        Properties = GraphElementRecordCodec.NormalizeProperties(properties, nameof(properties));
    }

    /// <summary>顶点标识符。</summary>
    public GraphElementId Id { get; }

    /// <summary>用于乐观写入的元素版本。</summary>
    public long ElementVersion { get; }

    /// <summary>按标识符升序排列的标签。</summary>
    public IReadOnlyList<LabelId> Labels { get; }

    /// <summary>按属性标识符升序排列的属性。</summary>
    public IReadOnlyList<GraphProperty> Properties { get; }
}

/// <summary>
/// 原生属性图边的不可变快照。
/// </summary>
public sealed class GraphEdge
{
    /// <summary>创建不可变边快照。</summary>
    /// <param name="id">边标识符。</param>
    /// <param name="elementVersion">大于零的元素版本。</param>
    /// <param name="sourceId">源顶点标识符。</param>
    /// <param name="targetId">目标顶点标识符。</param>
    /// <param name="labelId">边类型标签。</param>
    /// <param name="properties">边属性。</param>
    public GraphEdge(
        GraphElementId id,
        long elementVersion,
        GraphElementId sourceId,
        GraphElementId targetId,
        LabelId labelId,
        IReadOnlyList<GraphProperty> properties)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (sourceId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (targetId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetId));
        if (labelId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(labelId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementVersion);
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        ElementVersion = elementVersion;
        SourceId = sourceId;
        TargetId = targetId;
        LabelId = labelId;
        Properties = GraphElementRecordCodec.NormalizeProperties(properties, nameof(properties));
    }

    /// <summary>边标识符。</summary>
    public GraphElementId Id { get; }

    /// <summary>用于乐观写入的元素版本。</summary>
    public long ElementVersion { get; }

    /// <summary>源顶点标识符。</summary>
    public GraphElementId SourceId { get; }

    /// <summary>目标顶点标识符。</summary>
    public GraphElementId TargetId { get; }

    /// <summary>边类型标签。</summary>
    public LabelId LabelId { get; }

    /// <summary>按属性标识符升序排列的属性。</summary>
    public IReadOnlyList<GraphProperty> Properties { get; }
}

/// <summary>图邻接扩展方向。</summary>
public enum GraphDirection : byte
{
    /// <summary>从源顶点沿出边扩展。</summary>
    Outgoing = 1,

    /// <summary>从目标顶点沿入边扩展。</summary>
    Incoming = 2,

    /// <summary>同时沿出边和入边扩展；自环只返回一次。</summary>
    Both = 3,
}

/// <summary>图元素类别。</summary>
public enum GraphElementType : byte
{
    /// <summary>顶点。</summary>
    Vertex = 1,

    /// <summary>边。</summary>
    Edge = 2,
}

/// <summary>
/// 一次邻接扩展命中。
/// </summary>
public sealed class GraphExpansion
{
    /// <summary>创建邻接扩展快照。</summary>
    /// <param name="anchorId">发起扩展的顶点。</param>
    /// <param name="neighborId">命中的相邻顶点。</param>
    /// <param name="direction">相对于锚点的单向命中方向。</param>
    /// <param name="edge">命中的边。</param>
    public GraphExpansion(
        GraphElementId anchorId,
        GraphElementId neighborId,
        GraphDirection direction,
        GraphEdge edge)
    {
        if (anchorId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        if (neighborId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(neighborId));
        ArgumentNullException.ThrowIfNull(edge);
        if (direction is not (GraphDirection.Outgoing or GraphDirection.Incoming))
            throw new ArgumentOutOfRangeException(nameof(direction));
        bool matches = direction == GraphDirection.Outgoing
            ? edge.SourceId == anchorId && edge.TargetId == neighborId
            : edge.TargetId == anchorId && edge.SourceId == neighborId;
        if (!matches)
            throw new ArgumentException("Graph expansion 的 anchor、neighbor、direction 与 edge 不一致。", nameof(edge));
        AnchorId = anchorId;
        NeighborId = neighborId;
        Direction = direction;
        Edge = edge;
    }

    /// <summary>发起扩展的顶点。</summary>
    public GraphElementId AnchorId { get; }

    /// <summary>扩展命中的相邻顶点。</summary>
    public GraphElementId NeighborId { get; }

    /// <summary>该命中相对于 anchor 的方向。</summary>
    public GraphDirection Direction { get; }

    /// <summary>连接 anchor 与 neighbor 的边快照。</summary>
    public GraphEdge Edge { get; }
}
