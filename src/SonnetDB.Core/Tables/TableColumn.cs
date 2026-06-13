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
    bool IsRowVersion = false);
