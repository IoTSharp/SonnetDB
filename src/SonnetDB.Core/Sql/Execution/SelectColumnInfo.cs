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
    bool IsRowVersion = false)
{
    /// <summary>声明的十进制精度；未声明或无法静态确定时为 null。</summary>
    public byte? NumericPrecision { get; init; }

    /// <summary>声明的十进制小数位数；未声明或无法静态确定时为 null。</summary>
    public byte? NumericScale { get; init; }

    /// <summary>Decimal 的 p/s 是否是必须执行的显式值域声明；用于服务器内部 CTAS 保真。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasExplicitDecimalDeclaration { get; init; }

    /// <summary>声明的最大列长度；无界或未知时为 null。</summary>
    public int? ColumnSize { get; init; }

    /// <summary>旧物化代际缺少声明；完整元数据访问必须先显式 REFRESH。</summary>
    public bool RequiresRefresh { get; init; }

    /// <summary>是否具有静态声明；false 仅表示非关系结果没有声明，保留原有值类型适配。</summary>
    public bool IsDeclared { get; init; } = true;

    internal static SelectColumnInfo FromColumn(TableColumn column)
        => new(column.DataType, column.IsNullable, column.IsPrimaryKey,
            column.IsAutoIncrement, column.IsRowVersion)
        {
            ColumnSize = column.StringLength,
            NumericPrecision = column.DataType == TableColumnType.Decimal ? column.DecimalPrecision : null,
            NumericScale = column.DataType == TableColumnType.Decimal ? column.DecimalScale : null,
            HasExplicitDecimalDeclaration = column.HasExplicitDecimalDeclaration,
        };
}
