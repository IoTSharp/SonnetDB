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

        // #216：本层查询的子查询记忆表——非相关子查询整段外层扫描只执行一次并缓存。
        var memo = new SubqueryMemo();

        if (statement.Where is not null)
        {
            var filteredRows = relation.Rows
                .Where(row => EvaluateBoolean(tsdb, statement.Where, relation.Columns, row, outerScope, memo))
                .ToArray();
            relation = relation with { Rows = filteredRows };
        }

        if (ContainsAggregate(statement.Projections)
            || statement.GroupBy.Count > 0
            || statement.Having is not null)
        {
            var aggregateProjection = ExecuteAggregateProjection(tsdb, statement, relation, outerScope);
            return ApplyOrderByAndPagination(aggregateProjection, statement.OrderByList, statement.Pagination);
        }

        var canApplyRelationOrderBy = CanApplyRelationOrderBy(statement.OrderByList, relation);
        var orderedRelation = canApplyRelationOrderBy
            ? ApplyRelationOrderBy(tsdb, relation, statement.OrderByList, outerScope)
            : relation;
        var projected = ExecuteRawProjection(tsdb, statement, orderedRelation, outerScope);
        if (statement.OrderByList.Count > 0 && !canApplyRelationOrderBy)
            return ApplyOrderByAndPagination(projected, statement.OrderByList, statement.Pagination);
        return ApplyPagination(projected, statement.Pagination);
    }

    /// <summary>
    /// 相关子查询求值时携带的外层作用域：当前层标识符未命中时，
    /// 沿父链向外层逐层回退（v1 用于 EXISTS / 标量子查询 WHERE 引用外层列）。
    /// </summary>
    private sealed record RelationalScope(
        IReadOnlyList<RelColumn> Columns,
        IReadOnlyList<object?> Row,
        RelationalScope? Parent = null,
        CorrelationProbe? Probe = null);

    /// <summary>
    /// 相关性探针（#216）：子查询执行期间若通过外层作用域链解析到任何列，则被 <see cref="Trip"/> 置位。
    /// 一次完整子查询执行后仍未置位，说明该子查询与当前外层行无关（非相关），其结果可被缓存复用。
    /// </summary>
    private sealed class CorrelationProbe
    {
        public bool Tripped { get; private set; }
        public void Trip() => Tripped = true;
    }

    /// <summary>
    /// 子查询结果记忆表（#216）：按子查询 <see cref="SelectStatement"/> AST 节点身份缓存。
    /// 非相关子查询整段外层扫描只执行一次；已判定为相关的子查询记入 <see cref="_correlated"/>，此后每行照常执行。
    /// 生命周期 = 一次顶层关系查询执行（跨其全部外层行）。
    /// </summary>
    private sealed class SubqueryMemo
    {
        private readonly Dictionary<SelectStatement, SelectExecutionResult> _cache = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SelectStatement> _correlated = new(ReferenceEqualityComparer.Instance);

        public bool TryGetCached(SelectStatement subquery, out SelectExecutionResult result)
            => _cache.TryGetValue(subquery, out result!);

        public bool IsKnownCorrelated(SelectStatement subquery) => _correlated.Contains(subquery);

        public void CacheNonCorrelated(SelectStatement subquery, SelectExecutionResult result)
            => _cache[subquery] = result;

        public void MarkCorrelated(SelectStatement subquery) => _correlated.Add(subquery);
    }

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
            .Select(column => new RelColumn(alias, column.Name, column.Name, column.DataType))
            .ToArray();
        // read-your-writes：叠加当前 ambient 轻事务对本表的缓冲写（#218）。
        var rows = TableSqlExecutor.LoadSelectCandidateRows(tsdb.Tables.Open(schema.Name), schema, where: null)
            .Select(row => row.Values.ToArray())
            .ToArray();
        return new Relation(columns, rows);
    }

    private static Relation LoadSubquery(Tsdb tsdb, SelectStatement subquery, string alias)
    {
        var result = SqlExecutor.ExecuteSelect(tsdb, subquery);
        var columns = result.Columns
            .Select(column => new RelColumn(alias, NormalizeSubqueryColumnName(column), column))
            .ToArray();
        var rows = result.Rows
            .Select(row => row.ToArray())
            .ToArray();
        return new Relation(columns, rows);
    }

    private static string NormalizeSubqueryColumnName(string column)
    {
        var dot = column.LastIndexOf('.');
        return dot > -1 && dot < column.Length - 1
            ? column[(dot + 1)..]
            : column;
    }

    private static Relation Join(Tsdb tsdb, Relation left, Relation right, SqlExpression on, JoinKind kind, RelationalScope? outerScope = null)
    {
        // #215：等值连接走哈希连接（O(N+M)），替换全物化嵌套循环笛卡尔积（O(N×M)）。
        // 仅当 ON 能拆出至少一组 left_col = right_col 等值键、且无相关子查询等复杂依赖时启用；
        // 否则回退嵌套循环。残差（非等值）合取项在候选对上再求值，保持语义完全一致。
        if (TryPlanHashJoin(left, right, on, out var keyPairs, out var residual))
            return HashJoin(tsdb, left, right, keyPairs, residual, kind, outerScope);

        return NestedLoopJoin(tsdb, left, right, on, kind, outerScope);
    }

    private static Relation NestedLoopJoin(Tsdb tsdb, Relation left, Relation right, SqlExpression on, JoinKind kind, RelationalScope? outerScope)
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

    /// <summary>一组等值连接键：左关系列下标 = 右关系列下标。</summary>
    private readonly record struct JoinKeyPair(int LeftColumnIndex, int RightColumnIndex);

    /// <summary>
    /// 尝试把 ON 谓词规划为哈希连接：拆出顶层 AND 合取，识别形如 <c>left_col = right_col</c> 的等值项
    /// （两侧均为唯一可解析的裸列引用，一侧属左关系、一侧属右关系）。至少一组等值键才启用哈希连接；
    /// 其余合取项作为残差 <paramref name="residual"/> 在候选对上再求值。含相关子查询等无法静态判定的项则整体放弃。
    /// </summary>
    private static bool TryPlanHashJoin(
        Relation left,
        Relation right,
        SqlExpression on,
        out List<JoinKeyPair> keyPairs,
        out List<SqlExpression> residual)
    {
        keyPairs = [];
        residual = [];

        foreach (var conjunct in FlattenAndExpr(on))
        {
            if (conjunct is BinaryExpression { Operator: SqlBinaryOperator.Equal, Left: var l, Right: var r }
                && l is IdentifierExpression li
                && r is IdentifierExpression ri
                && TryBindSide(left, right, li, out int lLeftIdx, out int lRightIdx)
                && TryBindSide(left, right, ri, out int rLeftIdx, out int rRightIdx))
            {
                // 一侧解析到左关系、另一侧解析到右关系，才是可哈希的等值连接键。
                if (lLeftIdx >= 0 && rRightIdx >= 0)
                {
                    keyPairs.Add(new JoinKeyPair(lLeftIdx, rRightIdx));
                    continue;
                }
                if (lRightIdx >= 0 && rLeftIdx >= 0)
                {
                    keyPairs.Add(new JoinKeyPair(rLeftIdx, lRightIdx));
                    continue;
                }
                // 两侧同属一关系（如 l.a = l.b）：不是连接键，作为残差保留。
                residual.Add(conjunct);
                continue;
            }

            // 非等值 / 非裸列比较：只有当它不引用无法静态解析的东西时才作残差；
            // 含子查询的项无法安全下推到候选对上（可能依赖外层），放弃哈希连接走嵌套循环。
            if (ContainsSubquery(conjunct))
            {
                keyPairs = [];
                residual = [];
                return false;
            }
            residual.Add(conjunct);
        }

        return keyPairs.Count > 0;
    }

    /// <summary>
    /// 判定标识符是解析到左关系还是右关系（唯一命中）。返回 true 且 leftIndex/rightIndex 之一 &gt;= 0。
    /// 两个关系都命中（歧义）或都不命中则返回 false。
    /// </summary>
    private static bool TryBindSide(Relation left, Relation right, IdentifierExpression id, out int leftIndex, out int rightIndex)
    {
        leftIndex = TryResolveInRelation(left, id) ?? -1;
        rightIndex = TryResolveInRelation(right, id) ?? -1;
        // 恰好命中一侧才可用（避免歧义列）。
        return (leftIndex >= 0) ^ (rightIndex >= 0);
    }

    private static Relation HashJoin(
        Tsdb tsdb,
        Relation left,
        Relation right,
        List<JoinKeyPair> keyPairs,
        List<SqlExpression> residual,
        JoinKind kind,
        RelationalScope? outerScope)
    {
        var columns = left.Columns.Concat(right.Columns).ToArray();
        var rows = new List<object?[]>();

        // build 侧：对右关系按连接键建哈希（key 含 NULL 的行不入表——NULL 不参与等值匹配）。
        var buildTable = new Dictionary<JoinValueKey, List<object?[]>>();
        foreach (var rightRow in right.Rows)
        {
            if (TryMakeKey(rightRow, keyPairs, useRight: true, out var key))
            {
                if (!buildTable.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    buildTable.Add(key, bucket);
                }
                bucket.Add(rightRow);
            }
        }

        bool hasResidual = residual.Count > 0;
        foreach (var leftRow in left.Rows)
        {
            bool matched = false;
            if (TryMakeKey(leftRow, keyPairs, useRight: false, out var probeKey)
                && buildTable.TryGetValue(probeKey, out var candidates))
            {
                foreach (var rightRow in candidates)
                {
                    var row = new object?[leftRow.Length + rightRow.Length];
                    Array.Copy(leftRow, row, leftRow.Length);
                    Array.Copy(rightRow, 0, row, leftRow.Length, rightRow.Length);

                    if (hasResidual && !ResidualHolds(tsdb, residual, columns, row, outerScope))
                        continue;

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

    private static bool ResidualHolds(Tsdb tsdb, List<SqlExpression> residual, IReadOnlyList<RelColumn> columns, IReadOnlyList<object?> row, RelationalScope? outerScope)
    {
        foreach (var conjunct in residual)
        {
            if (!EvaluateBoolean(tsdb, conjunct, columns, row, outerScope))
                return false;
        }
        return true;
    }

    /// <summary>提取一行在连接键上的取值构成哈希 key；任一键值为 NULL 返回 false（NULL 不匹配）。</summary>
    private static bool TryMakeKey(IReadOnlyList<object?> row, List<JoinKeyPair> keyPairs, bool useRight, out JoinValueKey key)
    {
        var values = new object?[keyPairs.Count];
        for (int i = 0; i < keyPairs.Count; i++)
        {
            int idx = useRight ? keyPairs[i].RightColumnIndex : keyPairs[i].LeftColumnIndex;
            var v = row[idx];
            if (v is null)
            {
                key = default;
                return false;
            }
            values[i] = v;
        }
        key = new JoinValueKey(values);
        return true;
    }

    /// <summary>多列连接键的值组合，基于 <see cref="ValuesEqual"/> / 归一化数值实现相等与哈希。</summary>
    private readonly struct JoinValueKey : IEquatable<JoinValueKey>
    {
        private readonly object?[] _values;
        public JoinValueKey(object?[] values) => _values = values;

        public bool Equals(JoinValueKey other)
        {
            if (_values.Length != other._values.Length)
                return false;
            for (int i = 0; i < _values.Length; i++)
            {
                if (!ValuesEqual(_values[i], other._values[i]))
                    return false;
            }
            return true;
        }

        public override bool Equals(object? obj) => obj is JoinValueKey k && Equals(k);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var v in _values)
                hash.Add(NormalizeForHash(v));
            return hash.ToHashCode();
        }

        // 数值统一按 double 归一化，使 1 (int) 与 1.0 (double) 落同一桶（与 ValuesEqual 的数值相等一致）。
        private static object NormalizeForHash(object? v) => v switch
        {
            null => 0,
            byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture),
            _ => v,
        };
    }

    private static IEnumerable<SqlExpression> FlattenAndExpr(SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } and)
        {
            foreach (var l in FlattenAndExpr(and.Left))
                yield return l;
            foreach (var r in FlattenAndExpr(and.Right))
                yield return r;
        }
        else
        {
            yield return expression;
        }
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

    private static bool CanApplyRelationOrderBy(IReadOnlyList<OrderBySpec> orderBy, Relation relation)
        => orderBy.All(order => order.Expression is IdentifierExpression id && TryResolveInRelation(relation, id) is not null);

    private static int? TryResolveInRelation(Relation relation, IdentifierExpression identifier)
    {
        int? matchIndex = null;
        int matchCount = 0;
        for (int i = 0; i < relation.Columns.Count; i++)
        {
            var column = relation.Columns[i];
            if (!NameEquals(column.Name, identifier.Name))
                continue;
            if (identifier.Qualifier is not null
                && !QualifierEquals(column.Qualifier, identifier.Qualifier))
                continue;
            matchIndex = i;
            matchCount++;
            if (matchCount > 1)
                return null;
        }
        return matchCount == 1 ? matchIndex : null;
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
            // Q15：优先用 schema 静态类型判定聚合输入是否整型——命中即省去全量预扫，
            // 且对大 long 保持整型累加不丢精度。仅当输入表达式静态类型未知
            // （算术 / 函数派生列 / 子查询列）时才回退逐行预扫。
            allIntegralByProjection[i] = InferAggregateInputIntegral(projections[i].Aggregate!, relation.Columns)
                ?? IsAggregateInputAllIntegral(
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
    /// 用 schema 静态类型判定聚合输入是否整型（Q15）。返回 <c>true</c>/<c>false</c> 表示可静态定论；
    /// <c>null</c> 表示输入表达式静态类型未知（算术 / 函数派生 / 子查询列等），需回退逐行预扫。
    /// <c>count(*)</c> 与常量输入按整型处理。
    /// </summary>
    private static bool? InferAggregateInputIntegral(AggregateSpec aggregate, IReadOnlyList<RelColumn> columns)
    {
        var fn = aggregate.Function;
        if (fn.IsStar) return true;
        if (fn.Arguments.Count == 0) return true;
        return InferExpressionIntegral(fn.Arguments[0], columns);
    }

    /// <summary>推断标量表达式的静态数值类别：整型 true、浮点 false、无法静态判定 null。</summary>
    private static bool? InferExpressionIntegral(SqlExpression expression, IReadOnlyList<RelColumn> columns)
    {
        switch (expression)
        {
            case LiteralExpression { Kind: SqlLiteralKind.Integer }:
                return true;
            case LiteralExpression { Kind: SqlLiteralKind.Float }:
                return false;
            case IdentifierExpression id:
                var idx = TryResolveColumnIndex(columns, id);
                if (idx is null) return null;
                return columns[idx.Value].StaticType switch
                {
                    TableColumnType.Int64 => true,
                    TableColumnType.Float64 => false,
                    // 非数值列（string/bool/…）交给聚合本身求值时报错，此处不声明整型倾向。
                    _ => null,
                };
            case UnaryExpression { Operator: SqlUnaryOperator.Negate } unary:
                return InferExpressionIntegral(unary.Operand, columns);
            case BinaryExpression binary when IsArithmeticOperator(binary.Operator):
                // 除法可能产生非整数结果，静态无法保证整型。
                if (binary.Operator == SqlBinaryOperator.Divide)
                    return false;
                var left = InferExpressionIntegral(binary.Left, columns);
                var right = InferExpressionIntegral(binary.Right, columns);
                if (left is null || right is null) return null;
                return left.Value && right.Value;
            default:
                return null;
        }
    }

    /// <summary>解析标识符到列下标（唯一命中返回下标，0/多命中返回 null），用于静态类型推断。</summary>
    private static int? TryResolveColumnIndex(IReadOnlyList<RelColumn> columns, IdentifierExpression identifier)
    {
        int? matchIndex = null;
        int matchCount = 0;
        for (int i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!NameEquals(column.Name, identifier.Name))
                continue;
            if (identifier.Qualifier is not null
                && !QualifierEquals(column.Qualifier, identifier.Qualifier))
                continue;
            matchIndex = i;
            matchCount++;
            if (matchCount > 1)
                return null;
        }
        return matchCount == 1 ? matchIndex : null;
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
        => EvaluateHavingKleene(tsdb, expression, columns, representative, group) == true;

    private static bool? EvaluateHavingKleene(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> representative,
        IReadOnlyList<object?[]> group)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == SqlBinaryOperator.And)
            {
                var left = EvaluateHavingKleene(tsdb, binary.Left, columns, representative, group);
                if (left == false) return false;
                var right = EvaluateHavingKleene(tsdb, binary.Right, columns, representative, group);
                if (right == false) return false;
                return left is null || right is null ? null : true;
            }
            if (binary.Operator == SqlBinaryOperator.Or)
            {
                var left = EvaluateHavingKleene(tsdb, binary.Left, columns, representative, group);
                if (left == true) return true;
                var right = EvaluateHavingKleene(tsdb, binary.Right, columns, representative, group);
                if (right == true) return true;
                return left is null || right is null ? null : false;
            }
            if (IsComparisonOperator(binary.Operator))
            {
                var left = EvaluateHavingScalar(tsdb, binary.Left, columns, representative, group);
                var right = EvaluateHavingScalar(tsdb, binary.Right, columns, representative, group);
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
                    _ => throw new InvalidOperationException($"HAVING 不支持的比较运算符 {binary.Operator}。"),
                };
            }
        }
        else if (expression is UnaryExpression { Operator: SqlUnaryOperator.Not } unary)
        {
            var operand = EvaluateHavingKleene(tsdb, unary.Operand, columns, representative, group);
            return operand is null ? null : !operand;
        }
        else if (expression is IsNullExpression isNull)
        {
            var isNullValue = EvaluateHavingScalar(tsdb, isNull.Operand, columns, representative, group) is null;
            return isNull.Negated ? !isNullValue : isNullValue;
        }
        else if (expression is InExpression inExpression)
        {
            return EvaluateIn(tsdb, inExpression, columns, representative);
        }

        var value = EvaluateHavingScalar(tsdb, expression, columns, representative, group);
        if (value is null)
            return null;
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
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
        => EvaluateKleene(tsdb, expression, columns, row, outerScope, memo) == true;

    private static bool? EvaluateKleene(
        Tsdb? tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                {
                    var left = EvaluateKleene(tsdb, binary.Left, columns, row, outerScope, memo);
                    if (left == false) return false;
                    var right = EvaluateKleene(tsdb, binary.Right, columns, row, outerScope, memo);
                    if (right == false) return false;
                    return left is null || right is null ? null : true;
                }
                if (binary.Operator == SqlBinaryOperator.Or)
                {
                    var left = EvaluateKleene(tsdb, binary.Left, columns, row, outerScope, memo);
                    if (left == true) return true;
                    var right = EvaluateKleene(tsdb, binary.Right, columns, row, outerScope, memo);
                    if (right == true) return true;
                    return left is null || right is null ? null : false;
                }
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(tsdb, binary, columns, row, outerScope, memo);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                {
                    var operand = EvaluateKleene(tsdb, unary.Operand, columns, row, outerScope, memo);
                    return operand is null ? null : !operand;
                }

            case IsNullExpression isNull:
                {
                    var isNullValue = EvaluateScalar(tsdb, isNull.Operand, columns, row, outerScope, memo) is null;
                    return isNull.Negated ? !isNullValue : isNullValue;
                }

            case InExpression inExpression:
                return EvaluateIn(tsdb, inExpression, columns, row, outerScope, memo);
        }

        var value = EvaluateScalar(tsdb, expression, columns, row, outerScope, memo);
        if (value is null)
            return null;
        if (TryConvertToBoolean(value, out var boolean))
            return boolean;
        throw new InvalidOperationException("WHERE / ON 表达式必须计算为布尔值。");
    }

    private static bool TryConvertToBoolean(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolean:
                result = boolean;
                return true;
            case byte number:
                result = number != 0;
                return true;
            case short number:
                result = number != 0;
                return true;
            case int number:
                result = number != 0;
                return true;
            case long number:
                result = number != 0;
                return true;
            case float number:
                result = number != 0;
                return true;
            case double number:
                result = number != 0;
                return true;
            case decimal number:
                result = number != 0;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool? EvaluateComparison(
        Tsdb? tsdb,
        BinaryExpression binary,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        var left = EvaluateScalar(tsdb, binary.Left, columns, row, outerScope, memo);
        var right = EvaluateScalar(tsdb, binary.Right, columns, row, outerScope, memo);

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

    private static bool? EvaluateIn(
        Tsdb? tsdb,
        InExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        var value = EvaluateScalar(tsdb, expression.Value, columns, row, outerScope, memo);
        if (value is null)
            return null;

        var sawNull = false;
        bool Matches(object? candidate)
        {
            if (candidate is null)
            {
                sawNull = true;
                return false;
            }
            return ValuesEqual(value, candidate);
        }

        bool matched;
        if (expression.Subquery is not null)
        {
            if (tsdb is null)
                throw new InvalidOperationException("IN 子查询需要数据库上下文。");

            var result = ExecuteSubqueryMemoized(tsdb, expression.Subquery, columns, row, outerScope, memo);
            if (result.Columns.Count != 1)
                throw new InvalidOperationException("IN 子查询必须只返回一列。");
            matched = result.Rows.Any(candidate => Matches(candidate[0]));
        }
        else
        {
            matched = expression.Values.Any(item => Matches(
                EvaluateScalar(tsdb, item, columns, row, outerScope, memo)));
        }

        if (!matched && sawNull)
            return null;

        return expression.Negated ? !matched : matched;
    }

    /// <summary>
    /// 执行子查询并记忆化（#216）：命中 memo 缓存直接复用；否则带相关性探针执行一次，
    /// 探针未置位（未读任何外层列）则缓存为非相关，供本层后续外层行复用；置位则标记相关、每行照常执行。
    /// memo 为 null（聚合/投影等无外层行迭代的上下文）时退化为普通执行。
    /// </summary>
    private static SelectExecutionResult ExecuteSubqueryMemoized(
        Tsdb tsdb,
        SelectStatement subquery,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope,
        SubqueryMemo? memo)
    {
        if (memo is not null && memo.TryGetCached(subquery, out var cached))
            return cached;

        if (memo is null || memo.IsKnownCorrelated(subquery))
        {
            var inner = new RelationalScope(columns, row, outerScope);
            return Execute(tsdb, subquery, inner);
        }

        // 首次评估：挂探针执行；未触外层则缓存为非相关。
        var probe = new CorrelationProbe();
        var probedScope = new RelationalScope(columns, row, outerScope, probe);
        var result = Execute(tsdb, subquery, probedScope);
        if (probe.Tripped)
            memo.MarkCorrelated(subquery);
        else
            memo.CacheNonCorrelated(subquery, result);
        return result;
    }

    private static object? EvaluateScalar(
        Tsdb? tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            DurationLiteralExpression duration => duration.Milliseconds,
            IdentifierExpression identifier => GetColumnValue(columns, row, identifier, outerScope),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(tsdb, unary.Operand, columns, row, outerScope, memo), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(tsdb, binary, columns, row, outerScope, memo),
            CaseExpression caseExpression => EvaluateCase(tsdb, caseExpression, columns, row, outerScope, memo),
            FunctionCallExpression function => EvaluateFunction(tsdb, function, columns, row, outerScope),
            SubqueryExpression subquery => EvaluateScalarSubquery(tsdb, subquery, columns, row, outerScope, memo),
            ExistsExpression exists => EvaluateExists(tsdb, exists, columns, row, outerScope, memo),
            _ => throw new InvalidOperationException($"关系表表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateCase(
        Tsdb? tsdb,
        CaseExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        foreach (var when in expression.WhenClauses)
        {
            if (EvaluateBoolean(tsdb, when.Condition, columns, row, outerScope, memo))
                return EvaluateScalar(tsdb, when.Result, columns, row, outerScope, memo);
        }

        return expression.Else is null
            ? null
            : EvaluateScalar(tsdb, expression.Else, columns, row, outerScope, memo);
    }

    private static object EvaluateArithmetic(
        Tsdb? tsdb,
        BinaryExpression binary,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        var leftValue = EvaluateScalar(tsdb, binary.Left, columns, row, outerScope, memo);
        var rightValue = EvaluateScalar(tsdb, binary.Right, columns, row, outerScope, memo);
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

        if (function.IsStar)
        {
            throw new InvalidOperationException($"关系表函数 {function.Name}(*) 非法。");
        }

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        if (string.Equals(function.Name, "lower", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
        {
            return EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope)?.ToString()?.ToLowerInvariant();
        }

        if (string.Equals(function.Name, "upper", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
        {
            return EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope)?.ToString()?.ToUpperInvariant();
        }

        throw new InvalidOperationException("关系表当前仅支持 json_value(json_column, '$.path')、lower(value)、upper(value) 函数。");
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
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        if (tsdb is null)
            throw new InvalidOperationException("ON / WHERE 中的子查询需要数据库上下文。");

        var result = ExecuteSubqueryMemoized(tsdb, subquery.Select, columns, row, outerScope, memo);
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
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        if (tsdb is null)
            throw new InvalidOperationException("EXISTS 子查询需要数据库上下文。");

        return ExecuteSubqueryMemoized(tsdb, exists.Select, columns, row, outerScope, memo).Rows.Count != 0;
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
            if (!NameEquals(column.Name, identifier.Name))
                continue;
            if (identifier.Qualifier is not null
                && !QualifierEquals(column.Qualifier, identifier.Qualifier))
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
                {
                    // #216：命中某外层作用域 = 相关子查询。置位从起点到命中层（含）路径上的所有探针，
                    // 使这些层判定为相关、不缓存该子查询结果。
                    var probeScope = outerScope;
                    while (probeScope is not null)
                    {
                        probeScope.Probe?.Trip();
                        if (ReferenceEquals(probeScope, scope))
                            break;
                        probeScope = probeScope.Parent;
                    }
                    return scope.Row[outerHit.Value];
                }
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
            if (!NameEquals(column.Name, identifier.Name))
                continue;
            if (identifier.Qualifier is not null
                && !QualifierEquals(column.Qualifier, identifier.Qualifier))
                continue;
            matchIndex = i;
            matchCount++;
            if (matchCount > 1)
                return null;
        }
        return matchCount == 1 ? matchIndex : null;
    }

    /// <summary>
    /// 融合 ORDER BY 与分页（#214）：ORDER BY + Fetch 上限时走有界 Top-N，避免全量排序仅取 k 行。
    /// </summary>
    private static SelectExecutionResult ApplyOrderByAndPagination(
        SelectExecutionResult result,
        IReadOnlyList<OrderBySpec> orderBy,
        PaginationSpec? pagination)
    {
        if (orderBy.Count == 0)
            return ApplyPagination(result, pagination);

        var sortItems = orderBy.Select(order =>
        {
            if (order.Expression is not IdentifierExpression id)
                throw new InvalidOperationException("关系型 ORDER BY 当前仅支持结果列名。");

            // ORDER BY 可能以 qualifier.name 形式书写（ORDER BY c.name）；与之匹配的结果列名
            // 可能是 "c.name"（由 FormatExpressionName 生成）或裸 "name"（用户用了 alias）。
            // 两种形式都试一遍，避免相关子查询写法因 ORDER BY 失配而被拒绝。
            string qualified = id.Qualifier is null ? id.Name : $"{id.Qualifier}.{id.Name}";
            int columnIndex = FindResultColumn(result.Columns, qualified);
            if (columnIndex < 0)
                columnIndex = FindResultColumn(result.Columns, id.Name);

            if (columnIndex < 0)
                throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{qualified}'。");

            return (ColumnIndex: columnIndex, order.Direction);
        }).ToArray();

        var comparer = new ResultRowSortComparer(sortItems);
        var rows = TopN.OrderByThenPaginate(result.Rows, comparer, pagination?.Offset ?? 0, pagination?.Fetch);
        return new SelectExecutionResult(result.Columns, rows);
    }

    private static Relation ApplyRelationOrderBy(
        Tsdb tsdb,
        Relation relation,
        IReadOnlyList<OrderBySpec> orderBy,
        RelationalScope? outerScope)
    {
        if (orderBy.Count == 0)
            return relation;

        var rows = relation.Rows
            .Select(row => new RelationSortRow(
                row,
                orderBy
                    .Select(order => EvaluateScalar(tsdb, order.Expression, relation.Columns, row, outerScope))
                    .ToArray()))
            .OrderBy(row => row, new RelationSortComparer(orderBy.Select(static order => order.Direction).ToArray()))
            .Select(static row => row.Row)
            .ToArray();

        return relation with { Rows = rows };
    }

    private sealed record RelationSortRow(object?[] Row, IReadOnlyList<object?> SortValues);

    private sealed class RelationSortComparer(IReadOnlyList<SortDirection> directions) : IComparer<RelationSortRow>
    {
        public int Compare(RelationSortRow? x, RelationSortRow? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            for (int i = 0; i < directions.Count; i++)
            {
                var comparison = ScalarComparer.Instance.Compare(x.SortValues[i], y.SortValues[i]);
                if (comparison != 0)
                    return directions[i] == SortDirection.Descending ? -comparison : comparison;
            }

            return 0;
        }
    }

    private sealed class ResultRowSortComparer(IReadOnlyList<(int ColumnIndex, SortDirection Direction)> sortItems)
        : IComparer<IReadOnlyList<object?>>
    {
        public int Compare(IReadOnlyList<object?>? x, IReadOnlyList<object?>? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            foreach (var item in sortItems)
            {
                var comparison = ScalarComparer.Instance.Compare(x[item.ColumnIndex], y[item.ColumnIndex]);
                if (comparison != 0)
                    return item.Direction == SortDirection.Descending ? -comparison : comparison;
            }

            return 0;
        }
    }

    private static int FindResultColumn(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (NameEquals(columns[i], name))
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
            InExpression inExpression => inExpression.Subquery is not null
                || ContainsSubquery(inExpression.Value)
                || inExpression.Values.Any(ContainsSubquery),
            CaseExpression caseExpression => caseExpression.WhenClauses.Any(when =>
                    ContainsSubquery(when.Condition) || ContainsSubquery(when.Result))
                || (caseExpression.Else is not null && ContainsSubquery(caseExpression.Else)),
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
                NameEquals(l.Name, r.Name)
                && QualifierEquals(l.Qualifier, r.Qualifier),
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
        => relation.Columns.Count(candidate => NameEquals(candidate.Name, column.Name)) > 1
            ? $"{column.Qualifier}.{column.Name}"
            : column.Name;

    /// <summary>
    /// 未加引号标识符的列名比较：大小写不敏感（<see cref="StringComparison.OrdinalIgnoreCase"/>），
    /// 与本执行器的限定符（qualifier）比较策略以及 measurement / 关系表投影路径保持一致（Q12）。
    /// </summary>
    private static bool NameEquals(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool QualifierEquals(string? left, string? right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record Relation(IReadOnlyList<RelColumn> Columns, IReadOnlyList<object?[]> Rows);

    /// <summary>
    /// 关系列描述。<see cref="StaticType"/> 为该列的 schema 静态类型（关系表列已知；子查询 /
    /// 表达式派生列为 null），用于聚合返回类型判定（Q15），避免额外全量预扫。
    /// </summary>
    private sealed record RelColumn(string Qualifier, string Name, string OutputName, TableColumnType? StaticType = null);

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
