using System.Globalization;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 关系型 SELECT 执行器，覆盖关系表 JOIN、FROM 子查询和关系表聚合。
/// </summary>
internal static class RelationalSelectExecutor
{
    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("关系型 SELECT 暂不支持 FROM 表值函数。");

        var relation = LoadFrom(tsdb, statement);
        foreach (var join in statement.JoinClauses)
        {
            var right = LoadJoin(tsdb, join);
            relation = Join(tsdb, relation, right, join.On);
        }

        if (statement.Where is not null)
        {
            var filteredRows = relation.Rows
                .Where(row => EvaluateBoolean(tsdb, statement.Where, relation.Columns, row))
                .ToArray();
            relation = relation with { Rows = filteredRows };
        }

        var projected = ContainsAggregate(statement.Projections)
            ? ExecuteAggregateProjection(tsdb, statement, relation)
            : ExecuteRawProjection(tsdb, statement, relation);

        return ApplyPagination(ApplyOrderBy(projected, statement.OrderBy), statement.Pagination);
    }

    public static bool NeedsRelationalPath(SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.FromSubquery is not null
            || statement.JoinClauses.Count != 0
            || statement.GroupBy.Count != 0
            || ContainsAggregate(statement.Projections)
            || ContainsSubquery(statement);
    }

    private static Relation LoadFrom(Tsdb tsdb, SelectStatement statement)
    {
        var alias = statement.TableAlias ?? statement.Measurement;
        if (statement.FromSubquery is not null)
            return LoadSubquery(tsdb, statement.FromSubquery, alias);

        var schema = tsdb.Tables.Catalog.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException($"table '{statement.Measurement}' 不存在。");
        return LoadTable(tsdb, schema, alias);
    }

    private static Relation LoadJoin(Tsdb tsdb, JoinClause join)
    {
        if (join.Subquery is not null)
            return LoadSubquery(tsdb, join.Subquery, join.Alias);

        var schema = tsdb.Tables.Catalog.TryGet(join.TableName)
            ?? throw new InvalidOperationException($"JOIN 右侧 table '{join.TableName}' 不存在。");
        return LoadTable(tsdb, schema, join.Alias);
    }

    private static Relation LoadTable(Tsdb tsdb, TableSchema schema, string alias)
    {
        var columns = schema.Columns
            .Select(column => new RelColumn(alias, column.Name, column.Name))
            .ToArray();
        var rows = tsdb.Tables.Open(schema.Name)
            .Scan()
            .Select(row => row.Values.ToArray())
            .ToArray();
        return new Relation(columns, rows);
    }

    private static Relation LoadSubquery(Tsdb tsdb, SelectStatement subquery, string alias)
    {
        var result = SqlExecutor.ExecuteSelect(tsdb, subquery);
        var columns = result.Columns
            .Select(column => new RelColumn(alias, column, column))
            .ToArray();
        var rows = result.Rows
            .Select(row => row.ToArray())
            .ToArray();
        return new Relation(columns, rows);
    }

    private static Relation Join(Tsdb tsdb, Relation left, Relation right, SqlExpression on)
    {
        var columns = left.Columns.Concat(right.Columns).ToArray();
        var rows = new List<object?[]>();
        foreach (var leftRow in left.Rows)
        {
            foreach (var rightRow in right.Rows)
            {
                var row = new object?[leftRow.Length + rightRow.Length];
                Array.Copy(leftRow, row, leftRow.Length);
                Array.Copy(rightRow, 0, row, leftRow.Length, rightRow.Length);
                if (EvaluateBoolean(tsdb, on, columns, row))
                    rows.Add(row);
            }
        }

        return new Relation(columns, rows);
    }

    private static SelectExecutionResult ExecuteRawProjection(
        Tsdb tsdb,
        SelectStatement statement,
        Relation relation)
    {
        var projections = BuildRawProjections(statement.Projections, relation);
        var rows = new List<IReadOnlyList<object?>>(relation.Rows.Count);
        foreach (var row in relation.Rows)
        {
            var output = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
                output[i] = EvaluateScalar(tsdb, projections[i].Expression, relation.Columns, row);
            rows.Add(output);
        }

        return new SelectExecutionResult(projections.Select(static p => p.Name).ToArray(), rows);
    }

    private static SelectExecutionResult ExecuteAggregateProjection(
        Tsdb tsdb,
        SelectStatement statement,
        Relation relation)
    {
        var projections = BuildAggregateProjections(statement.Projections, statement.GroupBy, relation);
        var groups = new Dictionary<GroupKey, List<object?[]>>();
        foreach (var row in relation.Rows)
        {
            var keyValues = statement.GroupBy
                .Select(group => EvaluateScalar(tsdb, group, relation.Columns, row))
                .ToArray();
            var key = new GroupKey(keyValues);
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<object?[]>();
                groups.Add(key, bucket);
            }
            bucket.Add(row);
        }

        if (groups.Count == 0 && statement.GroupBy.Count == 0)
            groups.Add(new GroupKey([]), []);

        var rows = new List<IReadOnlyList<object?>>(groups.Count);
        foreach (var group in groups.Values)
        {
            var representative = group.Count == 0
                ? Array.Empty<object?>()
                : group[0];
            var output = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
            {
                var projection = projections[i];
                output[i] = projection.Aggregate is null
                    ? EvaluateScalar(tsdb, projection.Expression, relation.Columns, representative)
                    : EvaluateAggregate(tsdb, projection.Aggregate, relation.Columns, group);
            }
            rows.Add(output);
        }

        return new SelectExecutionResult(projections.Select(static p => p.Name).ToArray(), rows);
    }

    private static IReadOnlyList<Projection> BuildRawProjections(IReadOnlyList<SelectItem> items, Relation relation)
    {
        var result = new List<Projection>();
        foreach (var item in items)
        {
            if (item.Expression is StarExpression)
            {
                if (item.Alias is not null)
                    throw new InvalidOperationException("'*' 不允许带 alias。");
                foreach (var column in relation.Columns)
                    result.Add(new Projection(FormatStarColumnName(column, relation), new IdentifierExpression(column.Name, column.Qualifier)));
                continue;
            }

            result.Add(new Projection(item.Alias ?? FormatExpressionName(item.Expression), item.Expression));
        }
        return result;
    }

    private static IReadOnlyList<Projection> BuildAggregateProjections(
        IReadOnlyList<SelectItem> items,
        IReadOnlyList<SqlExpression> groupBy,
        Relation relation)
    {
        var result = new List<Projection>();
        foreach (var item in items)
        {
            if (item.Expression is StarExpression)
                throw new InvalidOperationException("聚合查询不支持 SELECT *。");

            if (item.Expression is FunctionCallExpression function && IsAggregateFunction(function.Name))
            {
                result.Add(new Projection(
                    item.Alias ?? FormatExpressionName(function),
                    item.Expression,
                    new AggregateSpec(function)));
                continue;
            }

            if (!MatchesGroupBy(item.Expression, groupBy))
                throw new InvalidOperationException("关系表聚合查询中的非聚合投影必须出现在 GROUP BY 中。");

            result.Add(new Projection(item.Alias ?? FormatExpressionName(item.Expression), item.Expression));
        }

        _ = relation;
        return result;
    }

    private static object? EvaluateAggregate(
        Tsdb tsdb,
        AggregateSpec aggregate,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?[]> rows)
    {
        var fn = aggregate.Function;
        var name = fn.Name.ToLowerInvariant();
        if (name == "count")
        {
            if (fn.IsStar)
                return (long)rows.Count;
            RequireArgumentCount(fn, 1);
            return rows.LongCount(row => EvaluateScalar(tsdb, fn.Arguments[0], columns, row) is not null);
        }

        RequireArgumentCount(fn, 1);
        var values = rows
            .Select(row => EvaluateScalar(tsdb, fn.Arguments[0], columns, row))
            .Where(static value => value is not null)
            .Select(value => RequireDouble(value, fn.Name))
            .ToArray();

        return name switch
        {
            "sum" => values.Sum(),
            "min" => values.Length == 0 ? null : values.Min(),
            "max" => values.Length == 0 ? null : values.Max(),
            "avg" => values.Length == 0 ? null : values.Average(),
            _ => throw new InvalidOperationException($"关系表聚合暂不支持函数 '{fn.Name}'。"),
        };
    }

    private static bool EvaluateBoolean(Tsdb? tsdb, SqlExpression expression, IReadOnlyList<RelColumn> columns, IReadOnlyList<object?> row)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(tsdb, binary.Left, columns, row) && EvaluateBoolean(tsdb, binary.Right, columns, row);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(tsdb, binary.Left, columns, row) || EvaluateBoolean(tsdb, binary.Right, columns, row);
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(tsdb, binary, columns, row);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(tsdb, unary.Operand, columns, row);
        }

        var value = EvaluateScalar(tsdb, expression, columns, row);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("WHERE / ON 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(Tsdb? tsdb, BinaryExpression binary, IReadOnlyList<RelColumn> columns, IReadOnlyList<object?> row)
    {
        var left = EvaluateScalar(tsdb, binary.Left, columns, row);
        var right = EvaluateScalar(tsdb, binary.Right, columns, row);
        int? compare = CompareScalar(left, right);
        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => ValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !ValuesEqual(left, right),
            SqlBinaryOperator.LessThan => compare is < 0,
            SqlBinaryOperator.LessThanOrEqual => compare is <= 0,
            SqlBinaryOperator.GreaterThan => compare is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => compare is >= 0,
            _ => throw new InvalidOperationException($"不支持的比较运算符 {binary.Operator}。"),
        };
    }

    private static object? EvaluateScalar(
        Tsdb? tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            IdentifierExpression identifier => GetColumnValue(columns, row, identifier),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(tsdb, unary.Operand, columns, row), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(tsdb, binary, columns, row),
            FunctionCallExpression function => EvaluateFunction(tsdb, function, columns, row),
            SubqueryExpression subquery => EvaluateScalarSubquery(tsdb, subquery),
            _ => throw new InvalidOperationException($"关系表表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object EvaluateArithmetic(
        Tsdb? tsdb,
        BinaryExpression binary,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row)
    {
        var left = RequireDouble(EvaluateScalar(tsdb, binary.Left, columns, row), binary.Operator.ToString());
        var right = RequireDouble(EvaluateScalar(tsdb, binary.Right, columns, row), binary.Operator.ToString());
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

    private static object? EvaluateFunction(
        Tsdb? tsdb,
        FunctionCallExpression function,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row)
    {
        if (IsAggregateFunction(function.Name))
            throw new InvalidOperationException($"聚合函数 '{function.Name}' 只能出现在聚合投影中。");

        if (!string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            || function.IsStar
            || function.Arguments.Count != 2
            || function.Arguments[1] is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            throw new InvalidOperationException("关系表当前仅支持 json_value(json_column, '$.path') 函数。");
        }

        var json = EvaluateScalar(tsdb, function.Arguments[0], columns, row) as string;
        return JsonPathEvaluator.Evaluate(json, path!);
    }

    private static object? EvaluateScalarSubquery(Tsdb? tsdb, SubqueryExpression subquery)
    {
        if (tsdb is null)
            throw new InvalidOperationException("ON / WHERE 中的子查询需要数据库上下文。");

        var result = SqlExecutor.ExecuteSelect(tsdb, subquery.Select);
        if (result.Columns.Count != 1)
            throw new InvalidOperationException("标量子查询必须只返回一列。");
        if (result.Rows.Count == 0)
            return null;
        if (result.Rows.Count > 1)
            throw new InvalidOperationException("标量子查询最多只能返回一行。");
        return result.Rows[0][0];
    }

    private static object? GetColumnValue(
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        IdentifierExpression identifier)
    {
        var matches = new List<int>();
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!string.Equals(column.Name, identifier.Name, StringComparison.Ordinal))
                continue;
            if (identifier.Qualifier is not null
                && !string.Equals(column.Qualifier, identifier.Qualifier, StringComparison.OrdinalIgnoreCase))
                continue;
            matches.Add(i);
        }

        if (matches.Count == 0)
            throw new InvalidOperationException(identifier.Qualifier is null
                ? $"引用了未知列 '{identifier.Name}'。"
                : $"引用了未知列 '{identifier.Qualifier}.{identifier.Name}'。");
        if (matches.Count > 1)
            throw new InvalidOperationException($"未限定列名 '{identifier.Name}' 存在歧义，请使用表别名限定。");

        return row[matches[0]];
    }

    private static SelectExecutionResult ApplyOrderBy(SelectExecutionResult result, OrderBySpec? orderBy)
    {
        if (orderBy is null)
            return result;
        if (orderBy.Expression is not IdentifierExpression { Name: var name })
            throw new InvalidOperationException("关系型 ORDER BY 当前仅支持结果列名。");

        int columnIndex = -1;
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], name, StringComparison.Ordinal))
            {
                columnIndex = i;
                break;
            }
        }

        if (columnIndex < 0)
            throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{name}'。");

        var rows = orderBy.Direction == SortDirection.Descending
            ? result.Rows.OrderByDescending(row => row[columnIndex], ScalarComparer.Instance).ToArray()
            : result.Rows.OrderBy(row => row[columnIndex], ScalarComparer.Instance).ToArray();
        return new SelectExecutionResult(result.Columns, rows);
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

    private static bool ContainsAggregate(IReadOnlyList<SelectItem> items)
        => items.Any(static item => item.Expression is FunctionCallExpression function && IsAggregateFunction(function.Name));

    private static bool ContainsSubquery(SelectStatement statement)
    {
        foreach (var item in statement.Projections)
            if (ContainsSubquery(item.Expression))
                return true;
        if (statement.Where is not null && ContainsSubquery(statement.Where))
            return true;
        if (statement.OrderBy is not null && ContainsSubquery(statement.OrderBy.Expression))
            return true;
        foreach (var join in statement.JoinClauses)
            if (ContainsSubquery(join.On) || (join.Subquery is not null && ContainsSubquery(join.Subquery)))
                return true;
        return statement.FromSubquery is not null && ContainsSubquery(statement.FromSubquery);
    }

    private static bool ContainsSubquery(SqlExpression expression)
        => expression switch
        {
            SubqueryExpression => true,
            UnaryExpression unary => ContainsSubquery(unary.Operand),
            BinaryExpression binary => ContainsSubquery(binary.Left) || ContainsSubquery(binary.Right),
            FunctionCallExpression function => function.Arguments.Any(ContainsSubquery),
            NamedArgumentExpression named => ContainsSubquery(named.Value),
            _ => false,
        };

    private static bool IsAggregateFunction(string name)
        => string.Equals(name, "count", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "sum", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "min", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "max", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "avg", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesGroupBy(SqlExpression expression, IReadOnlyList<SqlExpression> groupBy)
        => groupBy.Any(group => ExpressionEquals(expression, group));

    private static bool ExpressionEquals(SqlExpression left, SqlExpression right)
        => left switch
        {
            IdentifierExpression l when right is IdentifierExpression r =>
                string.Equals(l.Name, r.Name, StringComparison.Ordinal)
                && string.Equals(l.Qualifier, r.Qualifier, StringComparison.OrdinalIgnoreCase),
            _ => Equals(left, right),
        };

    private static bool IsComparisonOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Equal or
        SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.LessThan or
        SqlBinaryOperator.LessThanOrEqual or
        SqlBinaryOperator.GreaterThan or
        SqlBinaryOperator.GreaterThanOrEqual;

    private static bool IsArithmeticOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Add or
        SqlBinaryOperator.Subtract or
        SqlBinaryOperator.Multiply or
        SqlBinaryOperator.Divide or
        SqlBinaryOperator.Modulo;

    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    private static void RequireArgumentCount(FunctionCallExpression function, int count)
    {
        if (function.IsStar || function.Arguments.Count != count)
            throw new InvalidOperationException($"函数 '{function.Name}' 期望 {count} 个参数。");
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

    private static string FormatExpressionName(SqlExpression expression) => expression switch
    {
        IdentifierExpression identifier => identifier.Qualifier is null ? identifier.Name : $"{identifier.Qualifier}.{identifier.Name}",
        LiteralExpression literal => FormatLiteralColumnName(literal),
        FunctionCallExpression function => FormatFunctionColumnName(function),
        _ => expression.GetType().Name,
    };

    private static string FormatFunctionColumnName(FunctionCallExpression function)
    {
        if (function.IsStar)
            return $"{function.Name.ToLowerInvariant()}(*)";
        if (function.Arguments.Count == 1 && function.Arguments[0] is IdentifierExpression identifier)
            return $"{function.Name.ToLowerInvariant()}({identifier.Name})";
        return function.Name.ToLowerInvariant();
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

    private static string FormatStarColumnName(RelColumn column, Relation relation)
        => relation.Columns.Count(candidate => string.Equals(candidate.Name, column.Name, StringComparison.Ordinal)) > 1
            ? $"{column.Qualifier}.{column.Name}"
            : column.Name;

    private sealed record Relation(IReadOnlyList<RelColumn> Columns, IReadOnlyList<object?[]> Rows);

    private sealed record RelColumn(string Qualifier, string Name, string OutputName);

    private sealed record Projection(string Name, SqlExpression Expression, AggregateSpec? Aggregate = null);

    private sealed record AggregateSpec(FunctionCallExpression Function);

    private sealed class GroupKey : IEquatable<GroupKey>
    {
        private readonly object?[] _values;

        public GroupKey(object?[] values) => _values = values;

        public bool Equals(GroupKey? other)
        {
            if (other is null || other._values.Length != _values.Length)
                return false;
            for (int i = 0; i < _values.Length; i++)
                if (!ValuesEqual(_values[i], other._values[i]))
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as GroupKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in _values)
            {
                if (value is null)
                {
                    hash.Add(0);
                }
                else if (IsNumeric(value))
                {
                    hash.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                }
                else
                {
                    hash.Add(value);
                }
            }
            return hash.ToHashCode();
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
