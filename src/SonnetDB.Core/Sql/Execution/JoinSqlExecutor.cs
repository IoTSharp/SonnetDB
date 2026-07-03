using System.Globalization;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// MM4 时序 measurement JOIN 关系维表的执行器。
/// </summary>
internal static class JoinSqlExecutor
{
    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var join = statement.Join
            ?? throw new InvalidOperationException("内部错误：JOIN 执行器要求 SELECT 包含 JOIN 子句。");
        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("MM4 JOIN 暂不支持 FROM 表值函数。");
        if (statement.GroupBy.Count != 0)
            throw new InvalidOperationException("MM4 JOIN 第一版暂不支持 GROUP BY。");

        var measurementSchema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"JOIN 左侧必须是 measurement，'{statement.Measurement}' 不存在或不是 measurement。");
        var tableSchema = tsdb.Tables.Catalog.TryGet(join.TableName)
            ?? throw new InvalidOperationException(
                $"JOIN 右侧必须是关系表，table '{join.TableName}' 不存在。");

        var scope = JoinScope.Create(statement, join, measurementSchema, tableSchema);
        var joinKeys = ResolveJoinKeys(join.On, scope);
        var filterPlan = PlanFilters(statement.Where, scope);
        var measurementPushdown = filterPlan.MeasurementWhere;
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, measurementPushdown.TagFilter);

        var tableCandidateRows = TableSqlExecutor.LoadCandidateRows(
            tsdb.Tables.Open(tableSchema.Name),
            tableSchema,
            filterPlan.TableExpression);
        var tableRows = tableCandidateRows
            .Where(row => TableSqlExecutor.EvaluateWhere(filterPlan.TableExpression, tableSchema, row.Values))
            .ToArray();
        var tableHash = BuildTableHash(tableSchema, joinKeys.TableColumn, tableRows);

        var projections = BuildProjections(statement.Projections, scope);
        var measurementFields = CollectRequiredMeasurementFields(
            projections,
            filterPlan.ResidualExpression,
            statement.OrderBy,
            measurementSchema);

        var rows = new List<ResultRow>();
        foreach (var series in matchedSeries)
        {
            if (!series.Tags.TryGetValue(joinKeys.MeasurementTag.Name, out var joinValue))
                continue;
            if (!tableHash.TryGetValue(MakeJoinKey(joinValue), out var joinedTableRows))
                continue;

            var fieldLookups = LoadMeasurementFieldLookups(
                tsdb,
                measurementSchema,
                series,
                measurementFields,
                measurementPushdown.TimeRange);
            var timestamps = CollectTimestamps(fieldLookups);
            if (timestamps.Count == 0)
                continue;

            foreach (long timestamp in timestamps)
            {
                foreach (var tableRow in joinedTableRows)
                {
                    var context = new JoinRowContext(scope, series, timestamp, fieldLookups, tableRow);
                    if (!EvaluateWhere(filterPlan.ResidualExpression, context))
                        continue;

                    var output = new object?[projections.Count];
                    for (int i = 0; i < projections.Count; i++)
                        output[i] = EvaluateProjection(projections[i], context);

                    object? orderValue = statement.OrderBy is null
                        ? null
                        : EvaluateScalar(statement.OrderBy.Expression, context);
                    rows.Add(new ResultRow(output, orderValue));
                }
            }
        }

        var orderedRows = ApplyOrderBy(rows, statement.OrderBy)
            .Select(static row => row.Values)
            .Cast<IReadOnlyList<object?>>()
            .ToList();
        var result = new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            orderedRows);
        return ApplyPagination(result, statement.Pagination);
    }

    internal static JoinPlan ExplainPlan(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var join = statement.Join
            ?? throw new InvalidOperationException("内部错误：JOIN Explain 要求 SELECT 包含 JOIN 子句。");
        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("MM4 JOIN 暂不支持 FROM 表值函数。");

        var measurementSchema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"JOIN 左侧必须是 measurement，'{statement.Measurement}' 不存在或不是 measurement。");
        var tableSchema = tsdb.Tables.Catalog.TryGet(join.TableName)
            ?? throw new InvalidOperationException(
                $"JOIN 右侧必须是关系表，table '{join.TableName}' 不存在。");

        var scope = JoinScope.Create(statement, join, measurementSchema, tableSchema);
        _ = ResolveJoinKeys(join.On, scope);
        var filterPlan = PlanFilters(statement.Where, scope);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, filterPlan.MeasurementWhere.TagFilter);
        var tableStore = tsdb.Tables.Open(tableSchema.Name);
        var (tableAccessPath, tableIndexName, tableRows) =
            ExplainTableAccess(tableStore, tableSchema, filterPlan.TableExpression);

        string measurementAccessPath = filterPlan.MeasurementWhere.TagFilter.Count > 0 ? "tag_index" : "measurement_scan";
        string accessPath = $"measurement:{measurementAccessPath};table:{tableAccessPath};join:hash";
        string? indexName = tableIndexName is null ? null : $"{tableSchema.Name}.{tableIndexName}";
        return new JoinPlan(
            measurementSchema,
            tableSchema,
            filterPlan,
            matchedSeries.Count,
            tableRows,
            accessPath,
            indexName);
    }

    private static JoinKeys ResolveJoinKeys(SqlExpression on, JoinScope scope)
    {
        if (on is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
            throw new InvalidOperationException("MM4 JOIN 第一版仅支持 ON 中一个等值条件。");

        if (binary.Left is not IdentifierExpression left || binary.Right is not IdentifierExpression right)
            throw new InvalidOperationException("JOIN ON 等值两侧必须都是列引用。");

        var leftSide = scope.Resolve(left);
        var rightSide = scope.Resolve(right);
        if (leftSide.Source == rightSide.Source)
            throw new InvalidOperationException("JOIN ON 等值条件必须连接 measurement 列和关系表列。");

        var measurementRef = leftSide.Source == JoinSource.Measurement ? left : right;
        var tableRef = leftSide.Source == JoinSource.Table ? left : right;

        var measurementColumn = scope.MeasurementSchema.TryGetColumn(measurementRef.Name)
            ?? throw new InvalidOperationException($"JOIN ON 引用了未知 measurement 列 '{measurementRef.Name}'。");
        if (measurementColumn.Role != MeasurementColumnRole.Tag)
        {
            throw new InvalidOperationException(
                $"MM4 JOIN 第一版要求 measurement 侧连接键是 TAG 列；'{measurementRef.Name}' 是 {measurementColumn.Role} 列。");
        }

        var tableColumn = scope.TableSchema.TryGetColumn(tableRef.Name)
            ?? throw new InvalidOperationException($"JOIN ON 引用了未知 table 列 '{tableRef.Name}'。");
        return new JoinKeys(measurementColumn, tableColumn);
    }

    internal static CrossModelFilterPlan PlanFilters(SqlExpression? where, JoinScope scope)
        => CrossModelFilterPlanner.Plan(where, scope.MeasurementSchema, leaf =>
        {
            var result = CrossModelFilterSource.None;
            foreach (var identifier in EnumerateIdentifiers(leaf))
            {
                result |= scope.Resolve(identifier).Source switch
                {
                    JoinSource.Measurement => CrossModelFilterSource.Measurement,
                    JoinSource.Table => CrossModelFilterSource.Table,
                    _ => CrossModelFilterSource.None,
                };
            }

            return result;
        });

    internal static (string AccessPath, string? IndexName, int EstimatedRows) ExplainTableAccess(
        TableStore store,
        TableSchema schema,
        SqlExpression? where)
    {
        if (TableSqlExecutor.ChooseBestIndexForWhere(schema, where, out var values) is { } index)
            return (string.IsNullOrWhiteSpace(index.JsonPath) ? "secondary_index" : "json_path_index",
                index.Name,
                store.GetByIndex(index, values).Count);

        if (TryHasPrimaryKeyFilter(schema, where))
            return ("primary_key", "primary", values.Count == 0 ? 0 : 1);

        return ("table_scan", null, store.Scan().Count);
    }

    private static bool TryHasPrimaryKeyFilter(TableSchema schema, SqlExpression? where)
    {
        if (where is null)
            return false;

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in CrossModelFilterPlanner.FlattenAnd(where))
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
                continue;

            var identifier = binary.Left as IdentifierExpression ?? binary.Right as IdentifierExpression;
            if (identifier is not null)
                names.Add(identifier.Name);
        }

        return schema.PrimaryKey.All(names.Contains);
    }

    private static IReadOnlyDictionary<JoinKey, IReadOnlyList<TableRow>> BuildTableHash(
        TableSchema schema,
        TableColumn joinColumn,
        IReadOnlyList<TableRow> rows)
    {
        var result = new Dictionary<JoinKey, List<TableRow>>();
        foreach (var row in rows)
        {
            var value = row.Values[joinColumn.Ordinal];
            if (value is null)
                continue;

            var key = MakeJoinKey(value);
            if (!result.TryGetValue(key, out var bucket))
            {
                bucket = new List<TableRow>();
                result.Add(key, bucket);
            }

            bucket.Add(row);
        }

        return result.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<TableRow>)pair.Value.AsReadOnly());
    }

    private static IReadOnlyList<Projection> BuildProjections(IReadOnlyList<SelectItem> items, JoinScope scope)
    {
        var projections = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    AddStarProjections(projections, scope);
                    break;

                case IdentifierExpression identifier:
                    var resolved = scope.Resolve(identifier);
                    projections.Add(Projection.Column(
                        item.Alias ?? FormatIdentifierColumnName(identifier, resolved),
                        identifier,
                        resolved));
                    break;

                case LiteralExpression literal:
                    projections.Add(Projection.Constant(
                        item.Alias ?? FormatLiteralColumnName(literal),
                        EvaluateLiteral(literal)));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"MM4 JOIN 第一版暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return projections;
    }

    private static void AddStarProjections(List<Projection> projections, JoinScope scope)
    {
        projections.Add(Projection.Column("time", new IdentifierExpression("time", scope.MeasurementAlias), ResolvedIdentifier.MeasurementTime));
        foreach (var column in scope.MeasurementSchema.Columns)
        {
            var identifier = new IdentifierExpression(column.Name, scope.MeasurementAlias);
            projections.Add(Projection.Column(
                scope.TableSchema.TryGetColumn(column.Name) is null ? column.Name : $"{scope.MeasurementAlias}.{column.Name}",
                identifier,
                new ResolvedIdentifier(JoinSource.Measurement, MeasurementColumn: column)));
        }

        foreach (var column in scope.TableSchema.Columns)
        {
            var identifier = new IdentifierExpression(column.Name, scope.TableAlias);
            projections.Add(Projection.Column(
                scope.MeasurementSchema.TryGetColumn(column.Name) is null ? column.Name : $"{scope.TableAlias}.{column.Name}",
                identifier,
                new ResolvedIdentifier(JoinSource.Table, TableColumn: column)));
        }
    }

    private static IReadOnlyList<string> CollectRequiredMeasurementFields(
        IReadOnlyList<Projection> projections,
        SqlExpression? residualWhere,
        OrderBySpec? orderBy,
        MeasurementSchema schema)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var projection in projections)
        {
            if (projection.Resolved.MeasurementColumn is { Role: MeasurementColumnRole.Field } column)
                fields.Add(column.Name);
        }

        foreach (var expression in EnumerateOptionalExpressions(residualWhere, orderBy?.Expression))
        {
            foreach (var identifier in EnumerateIdentifiers(expression))
            {
                var column = schema.TryGetColumn(identifier.Name);
                if (column is { Role: MeasurementColumnRole.Field })
                    fields.Add(column.Name);
            }
        }

        if (fields.Count == 0)
            fields.Add(schema.FieldColumns.First().Name);
        return fields.ToArray();
    }

    private static Dictionary<string, Dictionary<long, FieldValue>> LoadMeasurementFieldLookups(
        Tsdb tsdb,
        MeasurementSchema schema,
        SeriesEntry series,
        IReadOnlyList<string> fields,
        TimeRange timeRange)
    {
        var result = new Dictionary<string, Dictionary<long, FieldValue>>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (schema.TryGetColumn(field) is not { Role: MeasurementColumnRole.Field })
                throw new InvalidOperationException($"JOIN 查询引用了未知 FIELD 列 '{field}'。");

            var points = tsdb.Query.Execute(new PointQuery(series.Id, field, timeRange)).ToList();
            var lookup = new Dictionary<long, FieldValue>(points.Count);
            foreach (var point in points)
                lookup[point.Timestamp] = point.Value;
            result[field] = lookup;
        }
        return result;
    }

    private static SortedSet<long> CollectTimestamps(IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        var timestamps = new SortedSet<long>();
        foreach (var (_, lookup) in fieldLookups)
            foreach (long timestamp in lookup.Keys)
                timestamps.Add(timestamp);
        return timestamps;
    }

    private static object? EvaluateProjection(Projection projection, JoinRowContext context)
        => projection.Kind switch
        {
            ProjectionKind.Column => EvaluateScalar(projection.Expression!, context),
            ProjectionKind.Constant => projection.ConstantValue,
            _ => throw new InvalidOperationException("未知 JOIN 投影类型。"),
        };

    private static bool EvaluateWhere(SqlExpression? expression, JoinRowContext context)
    {
        if (expression is null)
            return true;
        return EvaluateBoolean(expression, context);
    }

    private static bool EvaluateBoolean(SqlExpression expression, JoinRowContext context)
        => EvaluateKleene(expression, context) == true;

    private static bool? EvaluateKleene(SqlExpression expression, JoinRowContext context)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                {
                    var left = EvaluateKleene(binary.Left, context);
                    if (left == false) return false;
                    var right = EvaluateKleene(binary.Right, context);
                    if (right == false) return false;
                    return left is null || right is null ? null : true;
                }
                if (binary.Operator == SqlBinaryOperator.Or)
                {
                    var left = EvaluateKleene(binary.Left, context);
                    if (left == true) return true;
                    var right = EvaluateKleene(binary.Right, context);
                    if (right == true) return true;
                    return left is null || right is null ? null : false;
                }
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, context);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                {
                    var operand = EvaluateKleene(unary.Operand, context);
                    return operand is null ? null : !operand;
                }

            case IsNullExpression isNull:
                {
                    var isNullValue = EvaluateScalar(isNull.Operand, context) is null;
                    return isNull.Negated ? !isNullValue : isNullValue;
                }
        }

        var value = EvaluateScalar(expression, context);
        if (value is null)
            return null;
        if (value is bool b)
            return b;
        throw new InvalidOperationException("JOIN WHERE 表达式必须计算为布尔值。");
    }

    private static bool? EvaluateComparison(BinaryExpression binary, JoinRowContext context)
    {
        var left = EvaluateScalar(binary.Left, context);
        var right = EvaluateScalar(binary.Right, context);

        // 三值逻辑：任一操作数为 NULL，比较结果为 UNKNOWN。检测 NULL 只能用 IS [NOT] NULL。
        if (left is null || right is null)
            return null;

        int? compare = CompareScalar(left, right);

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => ValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !ValuesEqual(left, right),
            SqlBinaryOperator.LessThan => compare is < 0,
            SqlBinaryOperator.LessThanOrEqual => compare is <= 0,
            SqlBinaryOperator.GreaterThan => compare is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => compare is >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException($"不支持的比较运算符 {binary.Operator}。"),
        };
    }

    private static object? EvaluateScalar(SqlExpression expression, JoinRowContext context)
        => expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            IdentifierExpression identifier => context.GetValue(identifier),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(unary.Operand, context), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, context),
            _ => throw new InvalidOperationException(
                $"JOIN 表达式暂不支持 '{expression.GetType().Name}'。"),
        };

    private static object EvaluateArithmetic(BinaryExpression binary, JoinRowContext context)
    {
        var left = RequireDouble(EvaluateScalar(binary.Left, context), binary.Operator.ToString());
        var right = RequireDouble(EvaluateScalar(binary.Right, context), binary.Operator.ToString());
        return binary.Operator switch
        {
            SqlBinaryOperator.Add => left + right,
            SqlBinaryOperator.Subtract => left - right,
            SqlBinaryOperator.Multiply => left * right,
            SqlBinaryOperator.Divide => left / right,
            SqlBinaryOperator.Modulo => left % right,
            _ => throw new InvalidOperationException($"不支持的算术运算符 {binary.Operator}。"),
        };
    }

    private static IEnumerable<ResultRow> ApplyOrderBy(List<ResultRow> rows, OrderBySpec? orderBy)
    {
        if (orderBy is null)
            return rows;

        return orderBy.Direction == SortDirection.Descending
            ? rows.OrderByDescending(static row => row.OrderValue, ScalarComparer.Instance).ToArray()
            : rows.OrderBy(static row => row.OrderValue, ScalarComparer.Instance).ToArray();
    }

    private static SelectExecutionResult ApplyPagination(SelectExecutionResult result, PaginationSpec? pagination)
    {
        if (pagination is null)
            return result;

        int offset = pagination.Offset;
        if (offset >= result.Rows.Count)
            return new SelectExecutionResult(result.Columns, []);

        int take = pagination.Fetch ?? (result.Rows.Count - offset);
        if (take <= 0)
            return new SelectExecutionResult(result.Columns, []);

        return new SelectExecutionResult(
            result.Columns,
            result.Rows.Skip(offset).Take(Math.Min(take, result.Rows.Count - offset)).ToArray());
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifiers(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                yield return identifier;
                yield break;
            case UnaryExpression unary:
                foreach (var identifier in EnumerateIdentifiers(unary.Operand))
                    yield return identifier;
                yield break;
            case BinaryExpression binary:
                foreach (var identifier in EnumerateIdentifiers(binary.Left))
                    yield return identifier;
                foreach (var identifier in EnumerateIdentifiers(binary.Right))
                    yield return identifier;
                yield break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    foreach (var identifier in EnumerateIdentifiers(argument))
                        yield return identifier;
                yield break;
        }
    }

    private static IEnumerable<SqlExpression> EnumerateOptionalExpressions(params SqlExpression?[] expressions)
    {
        foreach (var expression in expressions)
        {
            if (expression is not null)
                yield return expression;
        }
    }

    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    private static string FormatIdentifierColumnName(IdentifierExpression identifier, ResolvedIdentifier resolved)
    {
        if (identifier.Qualifier is not null)
            return $"{identifier.Qualifier}.{identifier.Name}";
        return resolved.Source == JoinSource.Measurement && string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase)
            ? "time"
            : identifier.Name;
    }

    private static string FormatLiteralColumnName(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => "NULL",
        SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
        SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.String => literal.StringValue ?? string.Empty,
        _ => literal.Kind.ToString(),
    };

    private static JoinKey MakeJoinKey(object value)
        => value is string s
            ? new JoinKey(s)
            : new JoinKey(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

    private static object? UnboxFieldValue(FieldValue value) => value.Type switch
    {
        FieldType.Float64 => value.AsDouble(),
        FieldType.Int64 => value.AsLong(),
        FieldType.Boolean => value.AsBool(),
        FieldType.String => value.AsString(),
        FieldType.Vector => value.AsVector().ToArray(),
        FieldType.GeoPoint => value.AsGeoPoint(),
        _ => throw new InvalidOperationException($"不支持的 FieldType {value.Type}。"),
    };

    private static object? UnboxFieldValue(MeasurementColumn column, FieldValue value)
    {
        if (column.DataType == FieldType.Float64 && value.Type == FieldType.Int64)
            return (double)value.AsLong();
        return UnboxFieldValue(value);
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return leftBytes.AsSpan().SequenceEqual(rightBytes);

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        return Equals(left, right);
    }

    private static int? CompareScalar(object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        if (left is DateTime leftDate && right is DateTime rightDate)
            return leftDate.CompareTo(rightDate);

        if (left is string leftString && right is string rightString)
            return string.Compare(leftString, rightString, StringComparison.Ordinal);

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        throw new InvalidOperationException($"无法比较 {left.GetType().Name} 与 {right.GetType().Name}。");
    }

    private static double RequireDouble(object? value, string operatorName)
    {
        if (value is null)
            throw new InvalidOperationException($"运算 {operatorName} 不接受 NULL 参数。");
        if (!IsNumeric(value))
            throw new InvalidOperationException($"运算 {operatorName} 需要数值参数。");
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static bool IsNumeric(object value) => value is
        byte or sbyte or
        short or ushort or
        int or uint or
        long or ulong or
        float or double or decimal;

    private static bool IsComparisonOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Equal or
        SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.LessThan or
        SqlBinaryOperator.LessThanOrEqual or
        SqlBinaryOperator.GreaterThan or
        SqlBinaryOperator.GreaterThanOrEqual or
        SqlBinaryOperator.Like or
        SqlBinaryOperator.NotLike or
        SqlBinaryOperator.Regex or
        SqlBinaryOperator.NotRegex;

    private static bool IsArithmeticOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Add or
        SqlBinaryOperator.Subtract or
        SqlBinaryOperator.Multiply or
        SqlBinaryOperator.Divide or
        SqlBinaryOperator.Modulo;

    private sealed record JoinKeys(MeasurementColumn MeasurementTag, TableColumn TableColumn);

    internal sealed record JoinPlan(
        MeasurementSchema MeasurementSchema,
        TableSchema TableSchema,
        CrossModelFilterPlan FilterPlan,
        int MatchedSeriesCount,
        int TableCandidateRows,
        string AccessPath,
        string? IndexName);

    private sealed record Projection(
        ProjectionKind Kind,
        string ColumnName,
        IdentifierExpression? Expression,
        ResolvedIdentifier Resolved,
        object? ConstantValue)
    {
        public static Projection Column(string columnName, IdentifierExpression expression, ResolvedIdentifier resolved)
            => new(ProjectionKind.Column, columnName, expression, resolved, null);

        public static Projection Constant(string columnName, object? value)
            => new(ProjectionKind.Constant, columnName, null, default, value);
    }

    private enum ProjectionKind
    {
        Column,
        Constant,
    }

    private readonly record struct ResultRow(IReadOnlyList<object?> Values, object? OrderValue);

    private readonly record struct JoinKey(string Value);

    [Flags]
    internal enum JoinSource
    {
        None = 0,
        Measurement = 1,
        Table = 2,
    }

    internal readonly record struct ResolvedIdentifier(
        JoinSource Source,
        MeasurementColumn? MeasurementColumn = null,
        TableColumn? TableColumn = null)
    {
        public static ResolvedIdentifier MeasurementTime { get; } = new(JoinSource.Measurement);
    }

    internal sealed class JoinScope
    {
        private JoinScope(
            string measurementAlias,
            string tableAlias,
            MeasurementSchema measurementSchema,
            TableSchema tableSchema)
        {
            MeasurementAlias = measurementAlias;
            TableAlias = tableAlias;
            MeasurementSchema = measurementSchema;
            TableSchema = tableSchema;
        }

        public string MeasurementAlias { get; }

        public string TableAlias { get; }

        public MeasurementSchema MeasurementSchema { get; }

        public TableSchema TableSchema { get; }

        public static JoinScope Create(
            SelectStatement statement,
            JoinClause join,
            MeasurementSchema measurementSchema,
            TableSchema tableSchema)
        {
            var measurementAlias = statement.TableAlias ?? measurementSchema.Name;
            if (string.Equals(measurementAlias, join.Alias, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"JOIN 两侧别名不能相同：'{measurementAlias}'。");

            return new JoinScope(measurementAlias, join.Alias, measurementSchema, tableSchema);
        }

        public ResolvedIdentifier Resolve(IdentifierExpression identifier)
        {
            if (identifier.Qualifier is not null)
                return ResolveQualified(identifier);

            if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
                return ResolvedIdentifier.MeasurementTime;

            var measurementColumn = MeasurementSchema.TryGetColumn(identifier.Name);
            var tableColumn = TableSchema.TryGetColumn(identifier.Name);
            if (measurementColumn is not null && tableColumn is not null)
            {
                throw new InvalidOperationException(
                    $"JOIN 查询中未限定列名 '{identifier.Name}' 同时存在于 measurement 和 table；请使用 {MeasurementAlias}.{identifier.Name} 或 {TableAlias}.{identifier.Name}。");
            }

            if (measurementColumn is not null)
                return new ResolvedIdentifier(JoinSource.Measurement, MeasurementColumn: measurementColumn);
            if (tableColumn is not null)
                return new ResolvedIdentifier(JoinSource.Table, TableColumn: tableColumn);

            throw new InvalidOperationException($"JOIN 查询引用了未知列 '{identifier.Name}'。");
        }

        private ResolvedIdentifier ResolveQualified(IdentifierExpression identifier)
        {
            if (MatchesMeasurementQualifier(identifier.Qualifier!))
            {
                if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
                    return ResolvedIdentifier.MeasurementTime;
                var column = MeasurementSchema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"JOIN 查询引用了未知 measurement 列 '{identifier.Name}'。");
                return new ResolvedIdentifier(JoinSource.Measurement, MeasurementColumn: column);
            }

            if (MatchesTableQualifier(identifier.Qualifier!))
            {
                var column = TableSchema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"JOIN 查询引用了未知 table 列 '{identifier.Name}'。");
                return new ResolvedIdentifier(JoinSource.Table, TableColumn: column);
            }

            throw new InvalidOperationException($"JOIN 查询引用了未知别名 '{identifier.Qualifier}'。");
        }

        private bool MatchesMeasurementQualifier(string qualifier)
            => string.Equals(qualifier, MeasurementAlias, StringComparison.OrdinalIgnoreCase)
               || string.Equals(qualifier, MeasurementSchema.Name, StringComparison.OrdinalIgnoreCase);

        private bool MatchesTableQualifier(string qualifier)
            => string.Equals(qualifier, TableAlias, StringComparison.OrdinalIgnoreCase)
               || string.Equals(qualifier, TableSchema.Name, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class JoinRowContext
    {
        private readonly JoinScope _scope;
        private readonly SeriesEntry _series;
        private readonly long _timestamp;
        private readonly IReadOnlyDictionary<string, Dictionary<long, FieldValue>> _fieldLookups;
        private readonly TableRow _tableRow;

        public JoinRowContext(
            JoinScope scope,
            SeriesEntry series,
            long timestamp,
            IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups,
            TableRow tableRow)
        {
            _scope = scope;
            _series = series;
            _timestamp = timestamp;
            _fieldLookups = fieldLookups;
            _tableRow = tableRow;
        }

        public object? GetValue(IdentifierExpression identifier)
        {
            var resolved = _scope.Resolve(identifier);
            if (resolved.Source == JoinSource.Table)
                return _tableRow.Values[resolved.TableColumn!.Ordinal];

            if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
                return _timestamp;

            var column = resolved.MeasurementColumn
                ?? throw new InvalidOperationException($"JOIN 查询引用了未知 measurement 列 '{identifier.Name}'。");
            if (column.Role == MeasurementColumnRole.Tag)
                return _series.Tags.TryGetValue(column.Name, out var tagValue) ? tagValue : null;

            return _fieldLookups.TryGetValue(column.Name, out var lookup)
                   && lookup.TryGetValue(_timestamp, out var value)
                ? UnboxFieldValue(column, value)
                : null;
        }
    }

    private sealed class ScalarComparer : IComparer<object?>
    {
        public static ScalarComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            return CompareScalar(x, y) ?? 0;
        }
    }
}
