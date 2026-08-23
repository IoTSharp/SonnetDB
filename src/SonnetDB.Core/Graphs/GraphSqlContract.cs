namespace SonnetDB.Graphs;

/// <summary>
/// 原生属性图 SQL 合同版本与固定索引策略。
/// </summary>
/// <remarks>
/// V1 使用正整数 label/property ID，并为全部 label 与非空 property 自动维护等值索引。
/// 索引是 Graph V1 的派生投影，不提供会改变物理布局的按名称创建或删除语义。
/// </remarks>
public static class GraphSqlContract
{
    /// <summary>当前公开 SQL 合同版本。</summary>
    public const int CurrentVersion = 1;

    /// <summary>当前公开 SQL 合同的稳定名称。</summary>
    public const string CurrentName = "graph_sql_v1";

    /// <summary>动态属性列的固定前缀，例如 <c>property_7</c>。</summary>
    public const string PropertyColumnPrefix = "property_";

    /// <summary>label 索引策略：全部 label 自动维护等值索引。</summary>
    public const string LabelIndexPolicy = "automatic_all_labels";

    /// <summary>property 索引策略：全部非空 property 自动维护等值索引。</summary>
    public const string PropertyIndexPolicy = "automatic_all_non_null_properties";

    internal static bool TryParsePropertyColumn(string name, out int propertyId)
    {
        propertyId = 0;
        return name.StartsWith(PropertyColumnPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                name.AsSpan(PropertyColumnPrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out propertyId)
            && propertyId > 0;
    }
}
