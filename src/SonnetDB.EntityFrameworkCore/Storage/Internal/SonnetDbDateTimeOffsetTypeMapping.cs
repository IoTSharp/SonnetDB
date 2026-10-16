using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>提供原生 UTC ticks 日期偏移值，以及显式旧 DATETIME 列的兼容映射。</summary>
public sealed class SonnetDbDateTimeOffsetTypeMapping : DateTimeOffsetTypeMapping
{
    /// <summary>创建日期偏移映射；旧存储声明仅影响列布局，不改变表达式的原生值类型。</summary>
    /// <param name="storeType">原生 DATETIMEOFFSET 或显式旧 DATETIME 存储声明。</param>
    public SonnetDbDateTimeOffsetTypeMapping(string storeType = "DATETIMEOFFSET")
        : base(storeType, System.Data.DbType.DateTimeOffset)
    {
    }

    private SonnetDbDateTimeOffsetTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new SonnetDbDateTimeOffsetTypeMapping(parameters);

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
        => "CAST('" + ((DateTimeOffset)value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            + "' AS DATETIMEOFFSET)";
}
