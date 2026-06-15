using System.Data.Common;
using System.Globalization;

namespace SonnetDB.Parity.Adapters;

/// <summary>
/// ADO.NET 结果集物化工具，把不同驱动返回的 CLR 类型收敛成 parity 可比较的稳定形态。
/// </summary>
internal static class RelationalResultMaterializer
{
    /// <summary>
    /// 读取当前 <paramref name="reader"/> 的全部行。
    /// </summary>
    /// <param name="reader">数据读取器。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>规范化查询结果。</returns>
    public static async Task<RelationalSqlResult> ReadAsync(DbDataReader reader, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var columns = new string[reader.FieldCount];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = reader.GetName(i);

        var rows = new List<RelationalSqlRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new object?[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
                values[i] = Normalize(reader.GetValue(i));
            rows.Add(new RelationalSqlRow(values));
        }

        return new RelationalSqlResult(columns, rows, reader.RecordsAffected);
    }

    private static object? Normalize(object? value)
    {
        if (value is null or DBNull)
            return null;

        return value switch
        {
            byte v => (long)v,
            short v => (long)v,
            int v => (long)v,
            long v => v,
            float v => (double)v,
            double v => v,
            decimal v => (double)v,
            bool v => v,
            string v => v,
            DateTime v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset v => v.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };
    }
}
