using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>SELECT 输出列可静态确认的类型及列属性；未知类型为 null。</summary>
/// <param name="DataType">声明类型；无法静态推断时为 null。</param>
/// <param name="IsNullable">是否可空；无法静态确认时为 null。</param>
/// <param name="IsKey">是否直接投影主键列。</param>
/// <param name="IsAutoIncrement">是否直接投影自动递增列。</param>
/// <param name="IsRowVersion">是否直接投影 ROWVERSION 列。</param>
public sealed record SelectColumnInfo(
    TableColumnType? DataType,
    bool? IsNullable = null,
    bool IsKey = false,
    bool IsAutoIncrement = false,
    bool IsRowVersion = false);
