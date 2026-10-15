namespace SonnetDB.Tables;

/// <summary>
/// 关系表列定义。
/// </summary>
/// <param name="Name">列名。</param>
/// <param name="DataType">列数据类型。</param>
/// <param name="IsPrimaryKey">是否属于主键。</param>
/// <param name="IsNullable">是否允许 NULL；主键列始终不允许 NULL。</param>
/// <param name="Ordinal">列在 schema 中的声明顺序。</param>
/// <param name="IsRowVersion">是否为乐观并发版本列。</param>
public sealed record TableColumn(
    string Name,
    TableColumnType DataType,
    bool IsPrimaryKey,
    bool IsNullable,
    int Ordinal,
    bool IsRowVersion = false)
{
    /// <summary>字符串最大 UTF-16 代码单元数；未声明上限时为 null，超长值拒绝写入且不截断。</summary>
    public int? StringLength { get; init; }

    /// <summary>列注释；null 表示没有注释，空字符串表示显式空注释。</summary>
    public string? Comment { get; init; }

    /// <summary>DECIMAL/NUMERIC 总精度；非 Decimal 列为 0。</summary>
    public byte DecimalPrecision { get; init; }

    /// <summary>DECIMAL/NUMERIC 小数位数；非 Decimal 列为 0。</summary>
    public byte DecimalScale { get; init; }

    /// <summary>是否具有必须执行的显式 DECIMAL/NUMERIC 精度和小数位约束。</summary>
    public bool HasExplicitDecimalDeclaration { get; init; }

    /// <summary>
    /// 是否由数据库在插入时自动分配单调递增的整数值。
    /// </summary>
    public bool IsAutoIncrement { get; init; }

    /// <summary>
    /// 默认值表达式的规范化 SQL 文本；没有默认值时为 <c>null</c>。
    /// </summary>
    public string? DefaultExpressionSql { get; init; }
}
