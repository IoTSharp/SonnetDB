using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>EXPLAIN</c> / MCP <c>explain_sql</c> 共用的扫描估算结果。
/// </summary>
public sealed record SqlExplainExecutionResult(
    string? Database,
    string StatementType,
    string? Measurement,
    int MatchedSeriesCount,
    int EstimatedSegmentCount,
    int EstimatedBlockCount,
    long EstimatedScannedRows,
    long EstimatedMemTableRows,
    long EstimatedSegmentRows,
    bool HasTimeFilter,
    int TagFilterCount);

/// <summary>
/// 为只读 SQL 估算查询将扫描的段数、block 数和行数。
/// </summary>
public static class SqlExplainPlanner
{
    private static readonly IReadOnlyList<string> _keyValueColumns =
        new List<string>(2) { "key", "value" }.AsReadOnly();

    private readonly record struct ExplainWhereClause(
        IReadOnlyDictionary<string, string> TagFilter,
        TimeRange TimeRange);

    /// <summary>
    /// 解释一条只读 SQL AST。
    /// </summary>
    /// <param name="databaseName">当前数据库名；嵌入式场景未知时可为 <c>null</c>。</param>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="statement">被解释的语句；可直接传入 <see cref="ExplainStatement"/>。</param>
    /// <returns>扫描估算摘要。</returns>
    public static SqlExplainExecutionResult Explain(string? databaseName, Tsdb tsdb, SqlStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement is ExplainStatement explain)
            statement = explain.Statement;

        using var _ = UserFunctionRegistry.EnterScope(tsdb.Functions);
        return statement switch
        {
            ShowMeasurementsStatement => ExplainShowMeasurements(databaseName, tsdb),
            DescribeMeasurementStatement describe => ExplainDescribeMeasurement(databaseName, tsdb, describe.Name),
            SelectStatement select => ExplainSelect(databaseName, tsdb, select),
            _ => throw new InvalidOperationException(
                "EXPLAIN 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT]。"),
        };
    }

    /// <summary>
    /// 把解释结果格式化为 SQL Console 可直接展示的 <see cref="SelectExecutionResult"/>。
    /// </summary>
    public static SelectExecutionResult ToSelectExecutionResult(SqlExplainExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = new List<IReadOnlyList<object?>>(11)
        {
            new object?[] { "database", result.Database },
            new object?[] { "statement_type", result.StatementType },
            new object?[] { "measurement", result.Measurement },
            new object?[] { "matched_series_count", result.MatchedSeriesCount },
            new object?[] { "estimated_segment_count", result.EstimatedSegmentCount },
            new object?[] { "estimated_block_count", result.EstimatedBlockCount },
            new object?[] { "estimated_scanned_rows", result.EstimatedScannedRows },
            new object?[] { "estimated_memtable_rows", result.EstimatedMemTableRows },
            new object?[] { "estimated_segment_rows", result.EstimatedSegmentRows },
            new object?[] { "has_time_filter", result.HasTimeFilter },
            new object?[] { "tag_filter_count", result.TagFilterCount },
        };

        return new SelectExecutionResult(_keyValueColumns, rows);
    }

    private static SqlExplainExecutionResult ExplainShowMeasurements(string? databaseName, Tsdb tsdb)
    {
        var measurementCount = tsdb.Measurements.Snapshot().Count;
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "show_measurements",
            Measurement: null,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: measurementCount,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0);
    }

    private static SqlExplainExecutionResult ExplainDescribeMeasurement(
        string? databaseName,
        Tsdb tsdb,
        string measurementName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(measurementName);

        var schema = tsdb.Measurements.TryGet(measurementName)
            ?? throw new InvalidOperationException($"measurement '{measurementName}' 不存在。");

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "describe_measurement",
            Measurement: schema.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: schema.Columns.Count,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0);
    }

    private static SqlExplainExecutionResult ExplainSelect(
        string? databaseName,
        Tsdb tsdb,
        SelectStatement statement)
    {
        if (statement.TableValuedFunction is FunctionCallExpression { Name: var tvfName }
            && !string.Equals(tvfName, "forecast", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tvfName, "knn", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"EXPLAIN 暂不支持表值函数 '{tvfName}'；当前仅支持普通 SELECT、forecast(...) 与 knn(...)。");
        }

        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var where = DecomposeWhereClause(statement.Where, schema, nowMs);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);
        var fields = ResolveScannedFields(statement, schema);

        var segmentIds = new HashSet<long>();
        var estimatedSegmentRows = 0L;
        var estimatedMemTableRows = 0L;
        var estimatedBlockCount = 0;

        foreach (var series in matchedSeries)
        {
            foreach (var fieldName in fields)
            {
                estimatedMemTableRows += CountMemTableRows(tsdb.MemTable, series.Id, fieldName, where.TimeRange);

                var candidates = tsdb.Segments.Index.LookupCandidates(
                    series.Id,
                    fieldName,
                    where.TimeRange.FromInclusive,
                    where.TimeRange.ToInclusive);

                foreach (var candidate in candidates)
                {
                    segmentIds.Add(candidate.SegmentId);
                    estimatedBlockCount++;
                    estimatedSegmentRows += EstimateBlockRows(candidate.Descriptor, where.TimeRange);
                }
            }
        }

        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "select",
            Measurement: statement.Measurement,
            MatchedSeriesCount: matchedSeries.Count,
            EstimatedSegmentCount: segmentIds.Count,
            EstimatedBlockCount: estimatedBlockCount,
            EstimatedScannedRows: checked(estimatedMemTableRows + estimatedSegmentRows),
            EstimatedMemTableRows: estimatedMemTableRows,
            EstimatedSegmentRows: estimatedSegmentRows,
            HasTimeFilter: where.TimeRange != TimeRange.All,
            TagFilterCount: where.TagFilter.Count);
    }

    private static IReadOnlyList<string> ResolveScannedFields(SelectStatement statement, MeasurementSchema schema)
    {
        if (statement.TableValuedFunction is not null)
            return ResolveTvfFields(statement, schema);

        var fields = new HashSet<string>(StringComparer.Ordinal);
        var hasAggregate = false;
        var hasNonAggregate = false;

        foreach (var projection in statement.Projections)
            CollectProjectionFields(projection.Expression, schema, fields, ref hasAggregate, ref hasNonAggregate);

        ValidateGroupBy(statement.GroupBy, hasAggregate);

        if (hasAggregate && hasNonAggregate)
        {
            throw new InvalidOperationException(
                "SELECT 中不允许同时出现聚合函数与非聚合列（v1 不支持 GROUP BY 列）。");
        }

        if (!hasAggregate && fields.Count == 0)
        {
            var probeField = schema.FieldColumns.FirstOrDefault()
                ?? throw new InvalidOperationException("Measurement schema 至少需要一个 FIELD 列。");
            fields.Add(probeField.Name);
        }

        return fields.ToArray();
    }

    private static IReadOnlyList<string> ResolveTvfFields(SelectStatement statement, MeasurementSchema schema)
    {
        var tvf = statement.TableValuedFunction
            ?? throw new InvalidOperationException("内部错误：缺少表值函数调用。");

        if (string.Equals(tvf.Name, "forecast", StringComparison.OrdinalIgnoreCase))
        {
            if (tvf.Arguments.Count < 2 || tvf.Arguments[1] is not IdentifierExpression fieldId)
                throw new InvalidOperationException("forecast 第 2 个参数必须是字段列名。");

            var column = schema.TryGetColumn(fieldId.Name)
                ?? throw new InvalidOperationException($"forecast 引用了未知字段 '{fieldId.Name}'。");
            if (column.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException($"forecast 第 2 个参数 '{fieldId.Name}' 必须是 FIELD 列。");
            return [column.Name];
        }

        if (string.Equals(tvf.Name, "knn", StringComparison.OrdinalIgnoreCase))
        {
            if (tvf.Arguments.Count < 2 || tvf.Arguments[1] is not IdentifierExpression columnId)
                throw new InvalidOperationException("knn 第 2 个参数必须是向量列名标识符。");

            var column = schema.TryGetColumn(columnId.Name)
                ?? throw new InvalidOperationException($"knn 引用了未知列 '{columnId.Name}'。");
            if (column.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException($"knn 的列参数 '{columnId.Name}' 必须是 FIELD 列。");
            return [column.Name];
        }

        throw new InvalidOperationException(
            $"EXPLAIN 暂不支持表值函数 '{tvf.Name}'；当前仅支持 forecast(...) 与 knn(...)。");
    }

    private static void CollectProjectionFields(
        SqlExpression expression,
        MeasurementSchema schema,
        HashSet<string> fields,
        ref bool hasAggregate,
        ref bool hasNonAggregate)
    {
        switch (expression)
        {
            case StarExpression:
                hasNonAggregate = true;
                foreach (var field in schema.FieldColumns)
                    fields.Add(field.Name);
                return;

            case IdentifierExpression identifier:
                hasNonAggregate = true;
                if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
                    return;

                var column = schema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"SELECT 中引用了未知列 '{identifier.Name}'。");
                if (column.Role == MeasurementColumnRole.Field)
                    fields.Add(column.Name);
                return;

            case FunctionCallExpression function:
                var kind = FunctionRegistry.GetFunctionKind(function.Name);
                switch (kind)
                {
                    case FunctionKind.Aggregate:
                        hasAggregate = true;
                        CollectAggregateFields(function, schema, fields);
                        return;

                    case FunctionKind.Scalar:
                        hasNonAggregate = true;
                        foreach (var dependency in GetScalarFieldDependencies(function))
                        {
                            var scalarColumn = schema.TryGetColumn(dependency)
                                ?? throw new InvalidOperationException($"SELECT 中引用了未知列 '{dependency}'。");
                            if (scalarColumn.Role == MeasurementColumnRole.Field)
                                fields.Add(scalarColumn.Name);
                        }
                        return;

                    case FunctionKind.Window:
                        hasNonAggregate = true;
                        if (!FunctionRegistry.TryGetWindow(function.Name, out var windowFunction))
                            throw new InvalidOperationException($"未知窗口函数 '{function.Name}'。");
                        var evaluator = windowFunction.CreateEvaluator(function, schema);
                        fields.Add(evaluator.FieldName);
                        return;

                    case FunctionKind.Unknown:
                        throw new InvalidOperationException(
                            $"未知函数 '{function.Name}'；当前仅支持内置 aggregate/scalar/window 函数。");

                    default:
                        throw new InvalidOperationException($"当前 EXPLAIN 不支持投影函数 '{function.Name}'。");
                }

            default:
                throw new InvalidOperationException(
                    $"不支持的投影表达式类型 '{expression.GetType().Name}'。");
        }
    }

    private static void CollectAggregateFields(
        FunctionCallExpression function,
        MeasurementSchema schema,
        HashSet<string> fields)
    {
        if (!FunctionRegistry.TryGetAggregate(function.Name, out var aggregate))
            throw new InvalidOperationException($"未知聚合函数 '{function.Name}'。");

        var fieldName = aggregate.ResolveFieldName(function, schema);
        if (fieldName is not null)
        {
            fields.Add(fieldName);
            return;
        }

        foreach (var field in schema.FieldColumns)
        {
            if (field.DataType != FieldType.String)
                fields.Add(field.Name);
        }
    }

    private static IEnumerable<string> GetScalarFieldDependencies(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier when !string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase):
                yield return identifier.Name;
                yield break;

            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var dependency in GetScalarFieldDependencies(argument))
                        yield return dependency;
                }
                yield break;

            case UnaryExpression unary:
                foreach (var dependency in GetScalarFieldDependencies(unary.Operand))
                    yield return dependency;
                yield break;

            case BinaryExpression binary:
                foreach (var dependency in GetScalarFieldDependencies(binary.Left))
                    yield return dependency;
                foreach (var dependency in GetScalarFieldDependencies(binary.Right))
                    yield return dependency;
                yield break;

            default:
                yield break;
        }
    }

    private static void ValidateGroupBy(IReadOnlyList<SqlExpression> groupBy, bool hasAggregate)
    {
        if (groupBy.Count == 0)
            return;

        if (!hasAggregate)
            throw new InvalidOperationException("GROUP BY time(...) 仅在聚合查询中有效。");

        if (groupBy.Count != 1
            || groupBy[0] is not FunctionCallExpression
            {
                Name: var name,
                IsStar: false,
                Arguments.Count: 1,
                Arguments: [DurationLiteralExpression]
            }
            || !string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前仅支持 GROUP BY time(duration)。");
        }
    }

    private static long CountMemTableRows(MemTable memTable, ulong seriesId, string fieldName, TimeRange timeRange)
    {
        ArgumentNullException.ThrowIfNull(memTable);
        ArgumentException.ThrowIfNullOrEmpty(fieldName);

        var bucket = memTable.TryGet(new SeriesFieldKey(seriesId, fieldName));
        if (bucket is null || bucket.Count == 0)
            return 0;

        if (timeRange.FromInclusive <= bucket.MinTimestamp && timeRange.ToInclusive >= bucket.MaxTimestamp)
            return bucket.Count;

        return bucket.SnapshotRange(timeRange.FromInclusive, timeRange.ToInclusive).Length;
    }

    private static long EstimateBlockRows(
        in SonnetDB.Storage.Segments.BlockDescriptor descriptor,
        TimeRange timeRange)
    {
        if (descriptor.Count <= 0)
            return 0;

        if (descriptor.MinTimestamp >= timeRange.FromInclusive && descriptor.MaxTimestamp <= timeRange.ToInclusive)
            return descriptor.Count;

        var overlapStart = Math.Max(descriptor.MinTimestamp, timeRange.FromInclusive);
        var overlapEnd = Math.Min(descriptor.MaxTimestamp, timeRange.ToInclusive);
        if (overlapStart > overlapEnd)
            return 0;

        if (descriptor.MinTimestamp == descriptor.MaxTimestamp)
            return descriptor.Count;

        var overlapSpan = ((decimal)overlapEnd - overlapStart) + 1m;
        var totalSpan = ((decimal)descriptor.MaxTimestamp - descriptor.MinTimestamp) + 1m;
        var estimate = decimal.Ceiling(descriptor.Count * overlapSpan / totalSpan);
        return Math.Clamp((long)estimate, 1L, descriptor.Count);
    }

    private static ExplainWhereClause DecomposeWhereClause(
        SqlExpression? where,
        MeasurementSchema schema,
        long nowMs)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var tagFilter = new Dictionary<string, string>(StringComparer.Ordinal);
        long fromInclusive = long.MinValue;
        long toInclusive = long.MaxValue;

        if (where is not null)
        {
            foreach (var leaf in FlattenAnd(where))
                ApplyWhereLeaf(leaf, schema, tagFilter, ref fromInclusive, ref toInclusive, nowMs);
        }

        if (fromInclusive > toInclusive)
            throw new InvalidOperationException(
                $"WHERE 子句的时间窗为空：[from={fromInclusive}, to={toInclusive}]。");

        return new ExplainWhereClause(tagFilter, new TimeRange(fromInclusive, toInclusive));
    }

    private static IEnumerable<SqlExpression> FlattenAnd(SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } andExpression)
        {
            foreach (var left in FlattenAnd(andExpression.Left))
                yield return left;
            foreach (var right in FlattenAnd(andExpression.Right))
                yield return right;
            yield break;
        }

        yield return expression;
    }

    private static void ApplyWhereLeaf(
        SqlExpression leaf,
        MeasurementSchema schema,
        Dictionary<string, string> tagFilter,
        ref long fromInclusive,
        ref long toInclusive,
        long nowMs)
    {
        if (leaf is not BinaryExpression binary || !IsComparisonOperator(binary.Operator))
            throw new InvalidOperationException($"WHERE 仅支持 tag = 'literal' 与 time 比较，且通过 AND 连接。表达式：{leaf}。");

        var (left, right, op) = NormalizeComparison(binary);
        if (left is IdentifierExpression { Name: var leftName }
            && string.Equals(leftName, "time", StringComparison.OrdinalIgnoreCase))
        {
            ApplyTimeComparison(op, right, ref fromInclusive, ref toInclusive, nowMs);
            return;
        }

        if (op == SqlBinaryOperator.Equal
            && left is IdentifierExpression { Name: var tagName }
            && right is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var tagValue })
        {
            var column = schema.TryGetColumn(tagName)
                ?? throw new InvalidOperationException($"WHERE 中引用了未知列 '{tagName}'。");
            if (column.Role != MeasurementColumnRole.Tag)
                throw new InvalidOperationException($"WHERE 中只支持 tag 列等值过滤；'{tagName}' 是 {column.Role} 列。");

            if (tagFilter.TryGetValue(tagName, out var existing))
            {
                if (!string.Equals(existing, tagValue, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"WHERE 中 tag '{tagName}' 被同时约束为 '{existing}' 和 '{tagValue}'，结果集为空。");
                }
            }
            else
            {
                tagFilter[tagName] = tagValue!;
            }

            return;
        }

        throw new InvalidOperationException($"WHERE 谓词不在 v1 支持范围内。表达式：{leaf}。");
    }

    private static void ApplyTimeComparison(
        SqlBinaryOperator op,
        SqlExpression right,
        ref long fromInclusive,
        ref long toInclusive,
        long nowMs)
    {
        var timestamp = TimeExpressionEvaluator.Evaluate(right, nowMs);

        switch (op)
        {
            case SqlBinaryOperator.Equal:
                if (timestamp > fromInclusive) fromInclusive = timestamp;
                if (timestamp < toInclusive) toInclusive = timestamp;
                return;

            case SqlBinaryOperator.GreaterThanOrEqual:
                if (timestamp > fromInclusive) fromInclusive = timestamp;
                return;

            case SqlBinaryOperator.GreaterThan:
                if (timestamp == long.MaxValue)
                    throw new InvalidOperationException("'time > long.MaxValue' 永远为假。");
                if (timestamp + 1 > fromInclusive) fromInclusive = timestamp + 1;
                return;

            case SqlBinaryOperator.LessThanOrEqual:
                if (timestamp < toInclusive) toInclusive = timestamp;
                return;

            case SqlBinaryOperator.LessThan:
                if (timestamp == long.MinValue)
                    throw new InvalidOperationException("'time < long.MinValue' 永远为假。");
                if (timestamp - 1 < toInclusive) toInclusive = timestamp - 1;
                return;

            case SqlBinaryOperator.NotEqual:
                throw new InvalidOperationException("WHERE 中暂不支持 'time != X'（v1）。");

            default:
                throw new InvalidOperationException($"不支持的 time 比较运算符 {op}。");
        }
    }

    private static (SqlExpression Left, SqlExpression Right, SqlBinaryOperator Operator) NormalizeComparison(
        BinaryExpression expression)
    {
        if (expression.Left is not IdentifierExpression && expression.Right is IdentifierExpression)
            return (expression.Right, expression.Left, FlipComparison(expression.Operator));

        return (expression.Left, expression.Right, expression.Operator);
    }

    private static SqlBinaryOperator FlipComparison(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.Equal => SqlBinaryOperator.Equal,
        SqlBinaryOperator.NotEqual => SqlBinaryOperator.NotEqual,
        SqlBinaryOperator.LessThan => SqlBinaryOperator.GreaterThan,
        SqlBinaryOperator.LessThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
        SqlBinaryOperator.GreaterThan => SqlBinaryOperator.LessThan,
        SqlBinaryOperator.GreaterThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
        _ => op,
    };

    private static bool IsComparisonOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Equal or
        SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.LessThan or
        SqlBinaryOperator.LessThanOrEqual or
        SqlBinaryOperator.GreaterThan or
        SqlBinaryOperator.GreaterThanOrEqual;
}
