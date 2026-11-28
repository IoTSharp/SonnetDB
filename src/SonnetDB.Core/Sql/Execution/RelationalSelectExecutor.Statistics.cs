using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    // 为最多 2048 个质心的缓冲、排序副本和 List 扩容时的新旧数组一起预留预算。
    private const int StatisticsMaximumCentroids = 2_048;
    private const long StatisticsDigestReservationBytes = 128 * 1024;
    private const long StatisticsScalarReservationBytes = 128;
    private const int StatisticsHistogramMaximumBins = 1_024;
    private const long StatisticsHistogramReservationBytes = 512 * 1024;
    private const long StatisticsDigestJsonReservationBytes = StatisticsDigestReservationBytes + 512 * 1024;

    private static bool IsRelationalStatisticsFunction(string name)
        => (IsSecondaryRelationalStatisticsFunction(name)
            || name.ToLowerInvariant() is "median" or "stddev" or "variance" or "percentile"
                or "p50" or "p90" or "p95" or "p99" or "histogram" or "tdigest_agg")
            && UserFunctionRegistry.Current?.TryGetScalar(name, out _) != true
            && UserFunctionRegistry.Current?.TryGetWindow(name, out _) != true;

    private static bool ContainsRelationalStatistics(SqlExpression expression)
        => EnumerateAggregateCalls(expression).Any(call =>
            IsRelationalStatisticsFunction(call.Name) || call.Arguments.Any(ContainsRelationalStatistics));

    private static bool RequiresIntegralAggregateInput(SqlExpression expression)
        => EnumerateAggregateCalls(expression).Any(call =>
            !IsRelationalStatisticsFunction(call.Name) && !StringAggregateFunction.IsBuiltIn(call.Name));

    private static FunctionCallExpression[] ValidateRelationalStatisticsShapes(SelectStatement statement)
    {
        if ((statement.Where is not null && ContainsRelationalStatistics(statement.Where))
            || statement.GroupBy.Any(ContainsRelationalStatistics)
            || statement.JoinClauses.Any(join => ContainsRelationalStatistics(join.On)))
        {
            throw new InvalidOperationException("关系表统计聚合不能用于 WHERE、GROUP BY 或 JOIN ON。");
        }
        foreach (OrderBySpec order in statement.OrderByList)
        {
            if (ContainsRelationalStatistics(order.Expression)
                && (order.Expression is not FunctionCallExpression call || !IsRelationalStatisticsFunction(call.Name)))
                throw new InvalidOperationException("请为含统计聚合的 ORDER BY 表达式指定投影别名，或直接使用统计聚合调用。");
        }

        var calls = new List<FunctionCallExpression>();
        IEnumerable<SqlExpression> expressions = statement.Projections.Select(item => item.Expression);
        if (statement.Having is { } having)
            expressions = expressions.Append(having);
        expressions = expressions.Concat(statement.OrderByList.Select(order => order.Expression));
        foreach (SqlExpression expression in expressions)
        {
            foreach (FunctionCallExpression call in EnumerateAggregateCalls(expression))
            {
                bool isStatistics = IsRelationalStatisticsFunction(call.Name);
                if (call.Arguments.Any(argument => isStatistics
                        ? EnumerateAggregateCalls(argument).Any()
                        : ContainsRelationalStatistics(argument)))
                    throw new InvalidOperationException("关系表统计聚合不支持嵌套聚合。");

                if (!isStatistics)
                    continue;

                if (call.IsDistinct || call.Over is not null
                    || call.Arguments.Any(argument => argument is NamedArgumentExpression))
                    throw new InvalidOperationException($"关系表聚合 '{call.Name}' 不支持 DISTINCT、OVER 或命名参数。");
                string name = call.Name.ToLowerInvariant();
                RequireArgumentCount(call, name is "percentile" or "histogram" ? 2 : 1);
                if (name == "histogram")
                    _ = RelationalHistogramWidth(call);
                else
                    _ = RelationalStatisticsQuantile(call);
                calls.Add(call);
            }
        }
        return calls.ToArray();
    }

    private static void ValidateRelationalStatisticsTypes(
        IReadOnlyList<FunctionCallExpression> calls, IReadOnlyList<RelColumn> columns)
    {
        foreach (FunctionCallExpression call in calls)
        {
            TableColumnType? type = InferSelectColumnInfo(call.Arguments[0], columns).DataType;
            if (IsSecondaryRelationalStatisticsFunction(call.Name))
                ValidateSecondaryStatisticsInput(call.Name, type);
            else if (type is not (null or TableColumnType.Int64 or TableColumnType.Float64 or TableColumnType.Decimal))
                throw new InvalidOperationException($"关系表聚合 '{call.Name}' 需要数值参数。");
        }
    }

    private static SelectColumnInfo GetRelationalStatisticsColumnInfo(
        FunctionCallExpression call, IReadOnlyList<RelColumn> columns)
    {
        string name = call.Name.ToLowerInvariant();
        if (IsSecondaryRelationalStatisticsFunction(name))
        {
            SelectColumnInfo input = call.Arguments.Count == 1
                ? InferSelectColumnInfo(call.Arguments[0], columns)
                : new SelectColumnInfo(null);
            return GetSecondaryStatisticsColumnInfo(name, input);
        }
        return new SelectColumnInfo(name is "histogram" or "tdigest_agg"
            ? TableColumnType.String : TableColumnType.Float64, true);
    }

    private static object? EvaluateStatisticsConstant(SqlExpression expression)
        => expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            CastExpression
            {
                IsParameterValue: true,
                Operand: LiteralExpression parameter,
                TargetType: SqlDataType.Int64 or SqlDataType.Float64 or SqlDataType.Decimal,
            } cast => SqlCastOperations.Convert(EvaluateLiteral(parameter), cast.TargetType),
            _ => null,
        };

    private static double RelationalHistogramWidth(FunctionCallExpression call)
    {
        if (EvaluateStatisticsConstant(call.Arguments[1]) is { } value && IsNumeric(value))
        {
            double width = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (width > 0 && double.IsFinite(width))
                return width;
        }
        throw new InvalidOperationException("关系表 histogram(value, width) 的 width 必须是正有限数值常量。");
    }

    private static double? RelationalStatisticsQuantile(FunctionCallExpression call)
    {
        switch (call.Name.ToLowerInvariant())
        {
            case "median":
            case "p50":
                return 0.5;
            case "p90":
                return 0.90;
            case "p95":
                return 0.95;
            case "p99":
                return 0.99;
            case "percentile":
                object? rankValue = EvaluateStatisticsConstant(call.Arguments[1]);
                if (rankValue is { } value && IsNumeric(value)
                    && (value is not decimal decimalRank || decimalRank <= 100m))
                {
                    double rank = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    double quantile = rank / 100.0;
                    if (rank <= 100 && quantile > 0)
                        return quantile;
                }
                throw new InvalidOperationException(
                    "关系表 percentile(value, q) 的 q 必须是 (0, 100] 内可表示的数值常量。");
            default:
                return null;
        }
    }

    private static object? EvaluateRelationalStatistics(
        Tsdb tsdb,
        FunctionCallExpression call,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?[]> rows,
        RelationalScope? outerScope,
        SubqueryMemo? memo)
    {
        string name = call.Name.ToLowerInvariant();
        if (IsSecondaryRelationalStatisticsFunction(name))
            return EvaluateSecondaryRelationalStatistics(tsdb, call, columns, rows, outerScope, memo);

        if (rows.Count == 0)
            return null;

        bool digestJson = name == "tdigest_agg";
        HistogramAccumulator? histogram = name == "histogram"
            ? new HistogramAccumulator(RelationalHistogramWidth(call)) : null;
        double? quantile = RelationalStatisticsQuantile(call);
        TDigest? digest = digestJson || quantile is < 1 ? new TDigest(stableInterpolation: true) : null;
        WelfordAccumulator? moments = name is "stddev" or "variance" ? new WelfordAccumulator() : null;
        int scaleExponent = int.MinValue;
        double origin = 0;
        double maximum = double.NegativeInfinity;
        foreach (object?[] row in rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            object? value = EvaluateScalar(tsdb, call.Arguments[0], columns, row, outerScope, memo);
            if (value is null)
                continue;

            double sample = RequireDouble(value, call.Name);
            EnsureFiniteStatisticsValue(sample, call.Name);
            if (histogram is not null)
            {
                histogram.Add(sample, StatisticsHistogramMaximumBins);
            }
            else if (digest is not null)
            {
                if (digest.CentroidCount >= StatisticsMaximumCentroids)
                    throw new InvalidOperationException($"关系表聚合 '{call.Name}' 超出 TDigest 状态容量。");
                digest.Add(sample);
            }
            else if (moments is not null)
            {
                // 先减去同尺度的首样本，防止相邻大数的均值舍入到其中一个输入。
                if (moments.Count == 0)
                    origin = sample;
                if (sample != 0)
                {
                    int sampleExponent = Math.ILogB(sample);
                    if (sampleExponent > scaleExponent)
                    {
                        if (scaleExponent != int.MinValue)
                            moments.RescalePowerOfTwo(scaleExponent - sampleExponent);
                        scaleExponent = sampleExponent;
                    }
                }
                moments.Add(scaleExponent == int.MinValue
                    ? 0 : Math.ScaleB(sample, -scaleExponent) - Math.ScaleB(origin, -scaleExponent));
            }
            else
                maximum = Math.Max(maximum, sample);
        }

        SqlExecutor.ThrowIfCancellationRequested();
        if (histogram is not null)
            return RetainStatisticsText(call.Name, histogram.Finalize() as string);
        if (digestJson && digest is { Count: > 0 })
            return RetainStatisticsText(call.Name, digest.ToJson());

        double result;
        if (moments is not null)
        {
            if (moments.Count < 2)
                return null;
            if (scaleExponent == int.MinValue)
                return 0d;
            // 先对归一后的方差开根，可保留方差本身超出 Float64 时仍能表示的标准差。
            result = call.Name.Equals("stddev", StringComparison.OrdinalIgnoreCase)
                ? Math.ScaleB(moments.SampleStdDev, scaleExponent)
                : Math.ScaleB(moments.SampleVariance, 2 * scaleExponent);
        }
        else if (digest is not null)
        {
            if (digest.Count == 0)
                return null;
            result = digest.Quantile(quantile!.Value);
        }
        else
        {
            if (double.IsNegativeInfinity(maximum))
                return null;
            result = maximum;
        }
        EnsureFiniteStatisticsValue(result, call.Name);
        return result;
    }

    private static int ResolveAggregateOrderColumn(
        OrderBySpec order, IReadOnlyList<Projection> projections, IReadOnlyList<string> columnNames)
    {
        if (order.Expression is FunctionCallExpression call && IsRelationalStatisticsFunction(call.Name))
        {
            for (int i = 0; i < projections.Count; i++)
            {
                if (projections[i].Aggregate is { Function: var projected }
                    && call.Name.Equals(projected.Name, StringComparison.OrdinalIgnoreCase)
                    && call.Arguments.Count == projected.Arguments.Count
                    && call.Arguments.Zip(projected.Arguments, EquivalentStatisticsArgument).All(static equal => equal))
                    return i;
            }
            return -1;
        }

        if (order.Expression is not IdentifierExpression id)
            throw new InvalidOperationException("关系型聚合 ORDER BY 需要结果列名或统计聚合表达式。");

        string qualified = id.Qualifier is null ? id.Name : $"{id.Qualifier}.{id.Name}";
        int index = FindResultColumn(columnNames, qualified, id.IsNameBound || id.IsQuoted);
        if (index < 0)
            index = FindResultColumn(columnNames, id.Name, id.IsNameBound || id.IsQuoted);
        if (index < 0)
            throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{qualified}'。");
        return index;
    }

    private static bool EquivalentStatisticsArgument(SqlExpression left, SqlExpression right)
        => (left, right) switch
        {
            (FunctionCallExpression l, FunctionCallExpression r) when l.Over is null && r.Over is null
                => l.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase) && l.IsDistinct == r.IsDistinct
                    && l.IsStar == r.IsStar && l.Arguments.Count == r.Arguments.Count
                    && l.Arguments.Zip(r.Arguments, EquivalentStatisticsArgument).All(static equal => equal),
            _ => ExpressionEquals(left, right),
        };

    private static void EnsureFiniteStatisticsValue(double value, string functionName)
    {
        if (!double.IsFinite(value))
            throw new InvalidOperationException($"关系表聚合 '{functionName}' 的输入和结果必须为有限 Float64。");
    }

    private static string? RetainStatisticsText(string name, string? result)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        if (result is null || SqlQueryResources.Current is not { } resources)
            return result;

        var reservation = resources.CreateReservation();
        if (!reservation.TryReserve(24L + 2L * result.Length))
        {
            reservation.Dispose();
            throw new InvalidOperationException($"关系表聚合 '{name}' 的结果超出 SQL 查询内存预算。");
        }
        // 成功结果保留在根查询预算中，和字符串聚合采用同一生命周期。
        return result;
    }

    private static SqlQueryResources.SqlOperatorMemoryReservation? ReserveStatisticsState(
        SqlQueryResources? resources, IReadOnlyList<FunctionCallExpression> calls)
    {
        if (resources is null || calls.Count == 0)
            return null;

        long bytes = calls.Max(call => call.Name.ToLowerInvariant() switch
        {
            "histogram" => StatisticsHistogramReservationBytes,
            "tdigest_agg" => StatisticsDigestJsonReservationBytes,
            string name when IsSecondaryRelationalStatisticsFunction(name) => SecondaryStatisticsReservationBytes(name),
            _ => RelationalStatisticsQuantile(call) is < 1
                ? StatisticsDigestReservationBytes : StatisticsScalarReservationBytes,
        });
        var reservation = resources.CreateReservation();
        if (reservation.TryReserve(bytes))
            return reservation;

        reservation.Dispose();
        throw new InvalidOperationException($"关系表统计聚合状态超出 SQL 查询内存预算（需要 {bytes} 字节）。");
    }
}
