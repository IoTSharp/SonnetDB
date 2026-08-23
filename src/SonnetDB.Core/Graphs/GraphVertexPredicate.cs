namespace SonnetDB.Graphs;

/// <summary>
/// 邻接扩展目标顶点的精确匹配谓词。
/// </summary>
public sealed class GraphVertexPredicate
{
    /// <summary>
    /// 创建目标顶点谓词。至少需要标签或属性条件之一；属性标识和值必须成对提供。
    /// </summary>
    /// <param name="labelId">目标顶点必须包含的可选标签。</param>
    /// <param name="propertyId">目标顶点必须包含的可选属性标识符。</param>
    /// <param name="propertyValue">与 <paramref name="propertyId"/> 精确相等的属性值。</param>
    public GraphVertexPredicate(
        LabelId? labelId = null,
        int? propertyId = null,
        GraphPropertyValue? propertyValue = null)
    {
        if (labelId is null && propertyId is null && propertyValue is null)
            throw new ArgumentException("目标顶点谓词至少需要 label 或 property 条件。");
        if (labelId is { Value: <= 0 })
            throw new ArgumentOutOfRangeException(nameof(labelId));
        if ((propertyId is null) != (propertyValue is null))
            throw new ArgumentException("PropertyId 与 PropertyValue 必须同时提供。");
        if (propertyId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(propertyId));

        LabelId = labelId;
        PropertyId = propertyId;
        PropertyValue = propertyValue;
    }

    /// <summary>目标顶点必须包含的可选标签。</summary>
    public LabelId? LabelId { get; }

    /// <summary>目标顶点必须包含的可选属性标识符。</summary>
    public int? PropertyId { get; }

    /// <summary>目标顶点属性必须精确匹配的可选值。</summary>
    public GraphPropertyValue? PropertyValue { get; }

    internal bool Matches(GraphVertex vertex)
    {
        if (LabelId is { } labelId && !vertex.Labels.Contains(labelId))
            return false;
        if (PropertyId is not { } propertyId)
            return true;

        GraphPropertyValue expected = PropertyValue!.Value;
        foreach (GraphProperty property in vertex.Properties)
        {
            if (property.PropertyId == propertyId)
                return property.Value == expected;
            if (property.PropertyId > propertyId)
                break;
        }

        return false;
    }
}
