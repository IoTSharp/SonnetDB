using SonnetDB.Storage.Format;

namespace SonnetDB.Query.Functions;

/// <summary>
/// 聚合函数可接受的时序 FIELD 类型集合。
/// </summary>
[Flags]
public enum AggregateFieldTypes : byte
{
    /// <summary>不接受任何 FIELD 类型。</summary>
    None = 0,

    /// <summary>64 位浮点字段。</summary>
    Float64 = 1 << 0,

    /// <summary>64 位整数字段。</summary>
    Int64 = 1 << 1,

    /// <summary>布尔字段。</summary>
    Boolean = 1 << 2,

    /// <summary>字符串字段。</summary>
    String = 1 << 3,

    /// <summary>向量字段。</summary>
    Vector = 1 << 4,

    /// <summary>地理点字段。</summary>
    GeoPoint = 1 << 5,

    /// <summary>数学意义上的数值字段。</summary>
    Numeric = Float64 | Int64,

    /// <summary>可稳定比较或按相等性统计的标量字段。</summary>
    Categorical = Numeric | Boolean | String,

    /// <summary>所有有效 FIELD 类型。</summary>
    All = Categorical | Vector | GeoPoint,
}

/// <summary>
/// 聚合字段类型能力的辅助方法。
/// </summary>
public static class AggregateFieldTypesExtensions
{
    /// <summary>
    /// 判断能力集合是否包含指定的时序字段类型。
    /// </summary>
    /// <param name="capabilities">聚合函数声明的字段类型能力。</param>
    /// <param name="fieldType">待检查的字段类型。</param>
    /// <returns>支持该字段类型时返回 <see langword="true"/>。</returns>
    public static bool Supports(this AggregateFieldTypes capabilities, FieldType fieldType)
    {
        var required = fieldType switch
        {
            FieldType.Float64 => AggregateFieldTypes.Float64,
            FieldType.Int64 => AggregateFieldTypes.Int64,
            FieldType.Boolean => AggregateFieldTypes.Boolean,
            FieldType.String => AggregateFieldTypes.String,
            FieldType.Vector => AggregateFieldTypes.Vector,
            FieldType.GeoPoint => AggregateFieldTypes.GeoPoint,
            _ => AggregateFieldTypes.None,
        };

        return required != AggregateFieldTypes.None && (capabilities & required) != 0;
    }
}
