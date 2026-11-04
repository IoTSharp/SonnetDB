using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>提供保留 UTC ticks 的原生日期映射，以及显式旧 DATETIME 列的完整毫秒合同。</summary>
public sealed class SonnetDbDateTimeTypeMapping : DateTimeTypeMapping
{
    /// <summary>创建日期映射；显式旧列继续使用仅接受完整毫秒的参数类型。</summary>
    /// <param name="storeType">原生 DATETIME2 或显式旧 DATETIME 存储声明。</param>
    public SonnetDbDateTimeTypeMapping(string storeType = "DATETIME2")
        : base(storeType, string.Equals(storeType, "DATETIME", StringComparison.OrdinalIgnoreCase)
            ? System.Data.DbType.DateTime : System.Data.DbType.DateTime2)
    {
    }

    private SonnetDbDateTimeTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new SonnetDbDateTimeTypeMapping(parameters);

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
    {
        var date = (DateTime)value;
        var utc = date.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc)
            : date.ToUniversalTime();
        var legacy = string.Equals(StoreType, "DATETIME", StringComparison.OrdinalIgnoreCase);
        if (legacy && utc.Ticks % TimeSpan.TicksPerMillisecond != 0)
            throw new ArgumentException("显式 DATETIME 列仅接受完整毫秒；请使用 DATETIME2 保留 ticks。", nameof(value));
        return "CAST('" + utc.ToString("O", CultureInfo.InvariantCulture) + (legacy ? "' AS DATETIME)" : "' AS DATETIME2)");
    }
}
