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
        => Execute(tsdb, statement, outerScope: null);

    /// <summary>
    /// 相关子查询入口：携带外层 (列, 行) 上下文执行子 SELECT。
    /// 子查询内部 WHERE / 投影解析标识符时，若当前内层关系命中 0 个匹配，
    /// 沿 <see cref="RelationalScope.Parent"/> 链逐层回退到外层，模拟 SQL 标准的作用域语义。
    /// </summary>
    private static SelectExecutionResult Execute(
        Tsdb tsdb,
        SelectStatement statement,
        RelationalScope? outerScope)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("关系型 SELECT 暂不支持 FROM 表值函数。");

        var relation = LoadFrom(tsdb, statement);
        foreach (var join in statement.JoinClauses)
        {
            var right = LoadJoin(tsdb, join);
            relation = Join(tsdb, relation, right, join.On, join.Kind, outerScope);
        }

        if (statement.Where is not null)
        {
            var filteredRows = relation.Rows
                .Where(row => EvaluateBoolean(tsdb, statement.Where, relation.Columns, row, outerScope))
                .ToArray();
            relation = relation with { Rows = filteredRows };
        }

        var projected = (ContainsAggregate(statement.Projections)
                || statement.GroupBy.Count > 0
                || statement.Having is not null)
            ? ExecuteAggregateProjection(tsdb, statement, relation, outerScope)
            : ExecuteRawProjection(tsdb, statement, relation, outerScope);

        return ApplyPagination(ApplyOrderBy(projected, statement.OrderBy), statement.Pagination);
    }

    /// <summary>
    /// 相关子查询求值时携带的外层作用域：当前层标识符未命中时，
    /// 沿父链向外层逐层回退（v1 用于 EXISTS / 标量子查询 WHERE 引用外层列）。
    /// </summary>
    private sealed record RelationalScope(
        IReadOnlyList<RelColumn> Columns,
        IReadOnlyList<object?> Row,
        RelationalScope? Parent = null);

    public static bool NeedsRelationalPath(SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return statement.FromSubquery is not null
            || statement.JoinClauses.Count != 0
            || statement.GroupBy.Count != 0
            || statement.Having is not null
            || ContainsAggregate(statement.Projections)
            || ContainsSubquery(statement);
    }

    private static Relation LoadFrom(Tsdb tsdb, SelectStatement statement)
    {
        if (string.IsNullOrEmpty(statement.Measurement) && statement.FromSubquery is null)
            return new Relation(Array.Empty<RelColumn>(), [Array.Empty<object?>()]);

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

    private static Relation Join(Tsdb tsdb, Relation left, Relation right, SqlExpression on, JoinKind kind, RelationalScope? outerScope = null)
    {
        var columns = left.Columns.Concat(right.Columns).ToArray();
        var rows = new List<object?[]>();
        foreach (var leftRow in left.Rows)
        {
            var matched = false;
            foreach (var rightRow in right.Rows)
            {
                var row = new object?[leftRow.Length + rightRow.Length];
                Array.Copy(leftRow, row, leftRow.Length);
                Array.Copy(rightRow, 0, row, leftRow.Length, rightRow.Length);
                // M2 修复：JOIN ON 中如果出现引用外层列的标量子查询 / EXISTS，
                // 旧实现丢掉 outerScope —— 那种写法会在 GetColumnValue 里报"未知列"。
                // 现在把当前 SELECT 的 outerScope 透传给 JOIN ON 求值。
                if (EvaluateBoolean(tsdb, on, columns, row, outerScope))
                {
                    matched = true;
                    rows.Add(row);
                }
            }

            if (!matched && kind == JoinKind.Left)
            {
                var row = new object?[leftRow.Length + right.Columns.Count];
                Array.Copy(leftRow, row, leftRow.Length);
                rows.Add(row);
            }
        }

        return new Relation(columns, rows);
    }

    private static SelectExecutionResult ExecuteRawProjection(
        Tsdb tsdb,
        SelectStatement statement,
        Relation relation,
        RelationalScope? outerScope = null)
    {
        var projections = BuildRawProjections(statement.Projections, relation);
        var rows = new List<IReadOnlyList<object?>>(relation.Rows.Count);
        foreach (var row in relation.Rows)
        {
            var output = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
                output[i] = EvaluateScalar(tsdb, projections[i].Expression, relation.Columns, row, outerScope);
            rows.Add(output);
        }

        return new SelectExecutionResult(projections.Select(static p => p.Name).ToArray(), rows);
    }

    private static SelectExecutionResult ExecuteAggregateProjection(
        Tsdb tsdb,
        SelectStatement statement,
        Relation relation,
        RelationalScope? outerScope = null)
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

        // 预先决定每个聚合 spec 的输入是不是"全行全空非空值都是整数类型"。
        // 这个判断必须跨所有组、整个结果集计算一次，否则不同组各自看自己的子集会得到
        // 不一致的结论：A 组返回 long 120、B 组返回 double 120.0，同一列异质类型。
        bool[]? allIntegralByProjection = null;
        for (int i = 0; i < projections.Count; i++)
        {
            if (projections[i].Aggregate is null) continue;
            allIntegralByProjection ??= new bool[projections.Count];
            allIntegralByProjection[i] = IsAggregateInputAllIntegral(
                tsdb,
                projections[i].Aggregate!,
                relation.Columns,
                relation.Rows);
        }

        foreach (var group in groups.Values)
        {
            var representative = group.Count == 0
                ? Array.Empty<object?>()
                : group[0];

            if (statement.Having is not null
                && !EvaluateHavingPredicate(tsdb, statement.Having, relation.Columns, representative, group))
            {
                continue;
            }

            var output = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
            {
                var projection = projections[i];
                output[i] = projection.Aggregate is null
                    ? EvaluateScalar(tsdb, projection.Expression, relation.Columns, representative)
                    : EvaluateAggregate(tsdb, projection.Aggregate, relation.Columns, group,
                        allIntegralInput: allIntegralByProjection?[i] ?? false);
            }
            rows.Add(output);
        }

        return new SelectExecutionResult(projections.Select(static p => p.Name).ToArray(), rows);
    }

    /// <summary>
    /// 判定某个聚合的输入表达式在 <paramref name="allRows"/> 全集合上是否只产出整数（或 null）。
    /// 这是为了让 sum/min/max 的返回类型在整张结果集上保持一致（M3 修复）：要么全 long，要么全 double。
    /// </summary>
    private static bool IsAggregateInputAllIntegral(
        Tsdb tsdb,
        AggregateSpec aggregate,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?[]> allRows)
    {
        var fn = aggregate.Function;
        if (fn.IsStar) return true; // count(*) 不关心输入类型
        if (fn.Arguments.Count == 0) return true;

        foreach (var row in allRows)
        {
            var v = EvaluateScalar(tsdb, fn.Arguments[0], columns, row);
            if (v is null) continue;
            if (v is not (byte or short or int or long))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 评估 HAVING 表达式。区别于 WHERE：可在叶子节点引用聚合函数（如 <c>sum(amount) &gt;= 100</c>），
    /// 此时按当前分组（<paramref name="group"/>）现场计算聚合；非聚合叶子节点退回到组内代表行求值。
    /// </summary>
    private static bool EvaluateHavingPredicate(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> representative,
        IReadOnlyList<object?[]> group)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == SqlBinaryOperator.And)
                return EvaluateHavingPredicate(tsdb, binary.Left, columns, representative, group)
                    && EvaluateHavingPredicate(tsdb, binary.Right, columns, representative, group);
            if (binary.Operator == SqlBinaryOperator.Or)
                return EvaluateHavingPredicate(tsdb, binary.Left, columns, representative, group)
                    || EvaluateHavingPredicate(tsdb, binary.Right, columns, representative, group);
            if (IsComparisonOperator(binary.Operator))
            {
                var left = EvaluateHavingScalar(tsdb, binary.Left, columns, representative, group);
                var right = EvaluateHavingScalar(tsdb, binary.Right, columns, representative, group);
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
                    _ => throw new InvalidOperationException($"HAVING 不支持的比较运算符 {binary.Operator}。"),
                };
            }
        }
        else if (expression is UnaryExpression { Operator: SqlUnaryOperator.Not } unary)
        {
            return !EvaluateHavingPredicate(tsdb, unary.Operand, columns, representative, group);
        }

        var value = EvaluateHavingScalar(tsdb, expression, columns, representative, group);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("HAVING 表达式必须计算为布尔值。");
    }

    /// <summary>
    /// HAVING 标量求值：先把表达式树里出现的聚合函数调用全部就地计算并替换成字面量，
    /// 再用普通 <see cref="EvaluateScalar"/> 在代表行作用域里求剩余表达式。
    /// 这样 <c>HAVING sum(x)+1 &gt; 10</c> / <c>HAVING abs(sum(x)) &gt; 5</c> 这类
    /// 把聚合包在算术或外层函数里的写法都能正常工作——旧实现只识别顶层裸聚合调用，
    /// 任何包装都会让聚合走 <see cref="EvaluateFunction"/> 分支并抛出。
    /// </summary>
    private static object? EvaluateHavingScalar(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> representative,
        IReadOnlyList<object?[]> group)
    {
        var inlined = InlineAggregates(tsdb, expression, columns, group);
        return EvaluateScalar(tsdb, inlined, columns, representative);
    }

    /// <summary>
    /// 递归把表达式树里所有聚合函数调用就地求值，并替换为对应字面量。
    /// 非聚合节点递归克隆子节点；标量函数参数中嵌套的聚合也会被替换。
    /// </summary>
    private static SqlExpression InlineAggregates(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?[]> group)
    {
        switch (expression)
        {
            case FunctionCallExpression aggCall when IsAggregateFunction(aggCall.Name):
            {
                var value = EvaluateAggregate(tsdb, new AggregateSpec(aggCall), columns, group);
                return WrapValueAsLiteral(value);
            }
            case BinaryExpression binary:
            {
                var left = InlineAggregates(tsdb, binary.Left, columns, group);
                var right = InlineAggregates(tsdb, binary.Right, columns, group);
                if (ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right))
                    return expression;
                return binary with { Left = left, Right = right };
            }
            case UnaryExpression unary:
            {
                var operand = InlineAggregates(tsdb, unary.Operand, columns, group);
                if (ReferenceEquals(operand, unary.Operand))
                    return expression;
                return unary with { Operand = operand };
            }
            case FunctionCallExpression scalarCall when !scalarCall.IsStar:
            {
                var args = new SqlExpression[scalarCall.Arguments.Count];
                bool changed = false;
                for (int i = 0; i < scalarCall.Arguments.Count; i++)
                {
                    args[i] = InlineAggregates(tsdb, scalarCall.Arguments[i], columns, group);
                    if (!ReferenceEquals(args[i], scalarCall.Arguments[i]))
                        changed = true;
                }
                return changed ? scalarCall with { Arguments = args } : expression;
            }
            default:
                return expression;
        }
    }

    private static LiteralExpression WrapValueAsLiteral(object? value)
    {
        return value switch
        {
            null => LiteralExpression.Null(),
            bool b => LiteralExpression.Bool(b),
            long l => LiteralExpression.Integer(l),
            int i => LiteralExpression.Integer(i),
            short s => LiteralExpression.Integer(s),
            byte by => LiteralExpression.Integer(by),
            double d => LiteralExpression.Float(d),
            float f => LiteralExpression.Float(f),
            decimal m => LiteralExpression.Float((double)m),
            string str => LiteralExpression.String(str),
            _ => throw new InvalidOperationException(
                $"HAVING 内联聚合结果类型 '{value.GetType().Name}' 暂不支持。"),
        };
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
        IReadOnlyList<object?[]> rows,
        bool allIntegralInput = false)
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
        var rawValues = rows
            .Select(row => EvaluateScalar(tsdb, fn.Arguments[0], columns, row))
            .Where(static value => value is not null)
            .ToArray();

        // 保留整数类型：当调用方已确认所有非空输入跨整个结果集都是 byte/short/int/long 时，
        // sum/min/max 在所有组上一致返回 long——与 Postgres 等关系库一致，避免同列异质类型
        // （组 A 返回 long 120，组 B 因有一个 double 返回 120.0）。
        if (allIntegralInput && rawValues.Length > 0 && (name == "sum" || name == "min" || name == "max"))
        {
            long[] longs = rawValues.Select(static v => Convert.ToInt64(v)).ToArray();
            return name switch
            {
                "sum" => SumLongsWithOverflowPromotion(longs),
                "min" => longs.Min(),
                "max" => longs.Max(),
                _ => throw new InvalidOperationException($"unreachable: integral aggregate {name}"),
            };
        }

        var values = rawValues
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

    /// <summary>
    /// 累加 long 数组；若任意中间结果溢出 <see cref="long"/> 范围，自动提升为 <see cref="double"/>
    /// 并继续累加剩余元素——避免向上层抛 <see cref="OverflowException"/>，匹配 Postgres
    /// sum(bigint) -&gt; numeric 的"溢出即扩位"语义；M4 修复 LINQ <c>longs.Sum()</c> 的 checked 行为。
    /// </summary>
    private static object SumLongsWithOverflowPromotion(long[] longs)
    {
        long sum = 0;
        for (int i = 0; i < longs.Length; i++)
        {
            try
            {
                sum = checked(sum + longs[i]);
            }
            catch (OverflowException)
            {
                double promoted = sum;
                for (; i < longs.Length; i++) promoted += longs[i];
                return promoted;
            }
        }
        return sum;
    }

    private static bool EvaluateBoolean(
        Tsdb? tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(tsdb, binary.Left, columns, row, outerScope)
                        && EvaluateBoolean(tsdb, binary.Right, columns, row, outerScope);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(tsdb, binary.Left, columns, row, outerScope)
                        || EvaluateBoolean(tsdb, binary.Right, columns, row, outerScope);
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(tsdb, binary, columns, row, outerScope);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(tsdb, unary.Operand, columns, row, outerScope);
        }

        var value = EvaluateScalar(tsdb, expression, columns, row, outerScope);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("WHERE / ON 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(
        Tsdb? tsdb,
        BinaryExpression binary,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
    {
        var left = EvaluateScalar(tsdb, binary.Left, columns, row, outerScope);
        var right = EvaluateScalar(tsdb, binary.Right, columns, row, outerScope);
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

    private static object? EvaluateScalar(
        Tsdb? tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            IdentifierExpression identifier => GetColumnValue(columns, row, identifier, outerScope),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(tsdb, unary.Operand, columns, row, outerScope), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(tsdb, binary, columns, row, outerScope),
            FunctionCallExpression function => EvaluateFunction(tsdb, function, columns, row, outerScope),
            SubqueryExpression subquery => EvaluateScalarSubquery(tsdb, subquery, columns, row, outerScope),
            ExistsExpression exists => EvaluateExists(tsdb, exists, columns, row, outerScope),
            _ => throw new InvalidOperationException($"关系表表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object EvaluateArithmetic(
        Tsdb? tsdb,
        BinaryExpression binary,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
    {
        var leftValue = EvaluateScalar(tsdb, binary.Left, columns, row, outerScope);
        var rightValue = EvaluateScalar(tsdb, binary.Right, columns, row, outerScope);
        if (binary.Operator == SqlBinaryOperator.Add
            && (leftValue is string || rightValue is string))
        {
            return Convert.ToString(leftValue, CultureInfo.InvariantCulture)
                + Convert.ToString(rightValue, CultureInfo.InvariantCulture);
        }

        var left = RequireDouble(leftValue, binary.Operator.ToString());
        var right = RequireDouble(rightValue, binary.Operator.ToString());
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
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
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

        var json = EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope) as string;
        return JsonPathEvaluator.Evaluate(json, path!);
    }

    /// <summary>
    /// 计算标量子查询。若子查询是相关子查询（引用外层列），会自动通过 <see cref="RelationalScope"/>
    /// 链回退到外层；非相关子查询则等价于早期实现，单独执行一次。
    /// </summary>
    private static object? EvaluateScalarSubquery(
        Tsdb? tsdb,
        SubqueryExpression subquery,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
    {
        if (tsdb is null)
            throw new InvalidOperationException("ON / WHERE 中的子查询需要数据库上下文。");

        var inner = new RelationalScope(columns, row, outerScope);
        var result = Execute(tsdb, subquery.Select, inner);
        if (result.Columns.Count != 1)
            throw new InvalidOperationException("标量子查询必须只返回一列。");
        if (result.Rows.Count == 0)
            return null;
        if (result.Rows.Count > 1)
            throw new InvalidOperationException("标量子查询最多只能返回一行。");
        return result.Rows[0][0];
    }

    private static bool EvaluateExists(
        Tsdb? tsdb,
        ExistsExpression exists,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null)
    {
        if (tsdb is null)
            throw new InvalidOperationException("EXISTS 子查询需要数据库上下文。");

        var inner = new RelationalScope(columns, row, outerScope);
        return Execute(tsdb, exists.Select, inner).Rows.Count != 0;
    }

    private static object? GetColumnValue(
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        IdentifierExpression identifier,
        RelationalScope? outerScope = null)
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
        {
            // 内层未命中——若处于相关子查询，沿外层作用域链回退（SQL 标准的列解析顺序）。
            var scope = outerScope;
            while (scope is not null)
            {
                int? outerHit = TryResolveInScope(scope, identifier);
                if (outerHit.HasValue)
                    return scope.Row[outerHit.Value];
                scope = scope.Parent;
            }
            throw new InvalidOperationException(identifier.Qualifier is null
                ? $"引用了未知列 '{identifier.Name}'。"
                : $"引用了未知列 '{identifier.Qualifier}.{identifier.Name}'。");
        }
        if (matches.Count > 1)
            throw new InvalidOperationException($"未限定列名 '{identifier.Name}' 存在歧义，请使用表别名限定。");

        return row[matches[0]];
    }

    /// <summary>在单个外层 scope 中尝试解析列名；命中唯一列返回索引，0/多命中返回 null（多匹配视为该层不可见，留给上层判断）。</summary>
    private static int? TryResolveInScope(RelationalScope scope, IdentifierExpression identifier)
    {
        int matchIndex = -1;
        int matchCount = 0;
        for (int i = 0; i < scope.Columns.Count; i++)
        {
            var column = scope.Columns[i];
            if (!string.Equals(column.Name, identifier.Name, StringComparison.Ordinal))
                continue;
            if (identifier.Qualifier is not null
                && !string.Equals(column.Qualifier, identifier.Qualifier, StringComparison.OrdinalIgnoreCase))
                continue;
            matchIndex = i;
            matchCount++;
            if (matchCount > 1)
                return null;
        }
        return matchCount == 1 ? matchIndex : null;
    }

    private static SelectExecutionResult ApplyOrderBy(SelectExecutionResult result, OrderBySpec? orderBy)
    {
        if (orderBy is null)
            return result;
        if (orderBy.Expression is not IdentifierExpression id)
            throw new InvalidOperationException("关系型 ORDER BY 当前仅支持结果列名。");

        // ORDER BY 可能以 qualifier.name 形式书写（ORDER BY c.name）；与之匹配的结果列名
        // 可能是 "c.name"（由 FormatExpressionName 生成）或裸 "name"（用户用了 alias）。
        // 两种形式都试一遍，避免相关子查询写法因 ORDER BY 失配而被拒绝。
        string qualified = id.Qualifier is null ? id.Name : $"{id.Qualifier}.{id.Name}";
        int columnIndex = FindResultColumn(result.Columns, qualified);
        if (columnIndex < 0 && id.Qualifier is not null)
            columnIndex = FindResultColumn(result.Columns, id.Name);

        if (columnIndex < 0)
            throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{qualified}'。");

        var rows = orderBy.Direction == SortDirection.Descending
            ? result.Rows.OrderByDescending(row => row[columnIndex], ScalarComparer.Instance).ToArray()
            : result.Rows.OrderBy(row => row[columnIndex], ScalarComparer.Instance).ToArray();
        return new SelectExecutionResult(result.Columns, rows);
    }

    private static int FindResultColumn(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.Ordinal))
                return i;
        }
        return -1;
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
            ExistsExpression => true,
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
