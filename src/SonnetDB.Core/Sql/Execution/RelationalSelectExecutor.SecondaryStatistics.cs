using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    private const long StatisticsCategoricalReservationBytes = 256 * 1024;
    private const long StatisticsCategoricalInitialBytes = 256;

    // 除保留的键和值外，为字典扩容时同时存活的新旧条目数组及哈希桶留出空间。
    private const long StatisticsCategoricalKeyBytes = 160;

    private static bool IsSecondaryRelationalStatisticsFunction(string name)
        => name.ToLowerInvariant() is "spread" or "mode" or "distinct_count";

    private static SelectColumnInfo GetSecondaryStatisticsColumnInfo(string name, SelectColumnInfo input)
        => name.ToLowerInvariant() switch
        {
            "spread" => new SelectColumnInfo(TableColumnType.Float64, true),
            "mode" => input with { IsNullable = true, IsKey = false, IsAutoIncrement = false, IsRowVersion = false },
            "distinct_count" => new SelectColumnInfo(TableColumnType.Int64, false),
            _ => throw new InvalidOperationException($"未知的关系表统计聚合 '{name}'。"),
        };

    private static void ValidateSecondaryStatisticsInput(string name, TableColumnType? type)
    {
        if (type is null or TableColumnType.Int64 or TableColumnType.Float64 or TableColumnType.Decimal)
            return;
        if (!name.Equals("spread", StringComparison.OrdinalIgnoreCase)
            && type is (TableColumnType.Boolean or TableColumnType.String))
            return;
        throw new InvalidOperationException($"关系表聚合 '{name}' 不支持 {type} 输入。");
    }

    private static long SecondaryStatisticsReservationBytes(string name)
        => name.Equals("spread", StringComparison.OrdinalIgnoreCase)
            ? StatisticsScalarReservationBytes : StatisticsCategoricalReservationBytes;

    private static object? EvaluateSecondaryRelationalStatistics(
        Tsdb tsdb,
        FunctionCallExpression call,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?[]> rows,
        RelationalScope? outerScope,
        SubqueryMemo? memo)
    {
        if (call.Name.Equals("spread", StringComparison.OrdinalIgnoreCase))
        {
            var spread = new SpreadAccumulator();
            foreach (object?[] row in rows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (EvaluateScalar(tsdb, call.Arguments[0], columns, row, outerScope, memo) is not { } value)
                    continue;

                double sample = RequireDouble(value, call.Name);
                EnsureFiniteStatisticsValue(sample, call.Name);
                spread.Add(sample);
            }

            SqlExecutor.ThrowIfCancellationRequested();
            object? result = spread.Finalize();
            if (result is double spreadValue)
                EnsureFiniteStatisticsValue(spreadValue, call.Name);
            return result;
        }

        bool mode = call.Name.Equals("mode", StringComparison.OrdinalIgnoreCase);
        var counts = new Dictionary<object, long>();
        long retainedBytes = StatisticsCategoricalInitialBytes;
        Type? keyType = null;
        long bestCount = 0;
        object? bestKey = null;
        foreach (object?[] row in rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (EvaluateScalar(tsdb, call.Arguments[0], columns, row, outerScope, memo) is not { } value)
                continue;

            object key = NormalizeStatisticsKey(value, call.Name);
            keyType ??= key.GetType();
            if (key.GetType() != keyType)
                throw new InvalidOperationException($"关系表聚合 '{call.Name}' 的组内值类型必须一致，请显式 CAST。");

            long keyBytes = StatisticsCategoricalKeyBytes + (key is string text ? 2L * text.Length : 0L);
            if (keyBytes > StatisticsCategoricalReservationBytes - StatisticsCategoricalInitialBytes)
                throw new InvalidOperationException($"关系表聚合 '{call.Name}' 的单键超出 256 KiB 键状态预算。");

            if (!counts.TryGetValue(key, out long count))
            {
                if (keyBytes > StatisticsCategoricalReservationBytes - retainedBytes)
                    throw new InvalidOperationException($"关系表聚合 '{call.Name}' 超出 256 KiB 键状态预算。");
                retainedBytes += keyBytes;
                counts.Add(key, 1);
                count = 1;
            }
            else if (mode)
            {
                count = checked(count + 1);
                counts[key] = count;
            }

            if (mode && (count > bestCount || count == bestCount && CompareScalar(key, bestKey) is < 0))
            {
                bestKey = key;
                bestCount = count;
            }
        }

        SqlExecutor.ThrowIfCancellationRequested();
        return mode ? bestKey : (long)counts.Count;
    }

    private static object NormalizeStatisticsKey(object value, string functionName)
    {
        switch (value)
        {
            case long or decimal or bool or string:
                return value;
            case byte or sbyte or short or ushort or int or uint:
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            case ulong unsigned:
                return (decimal)unsigned;
            case float or double:
                double sample = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                EnsureFiniteStatisticsValue(sample, functionName);
                return sample;
            default:
                throw new InvalidOperationException($"关系表聚合 '{functionName}' 需要数值、BOOL 或 STRING 参数。");
        }
    }
}
