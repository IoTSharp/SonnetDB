using System.Globalization;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;
using SonnetDB.Views;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 关系型 SELECT 执行器，覆盖关系表 JOIN、FROM 子查询和关系表聚合。
/// </summary>
internal static class RelationalSelectExecutor
{
    /// <summary>
    /// 执行顶层关系 SELECT，并为本次查询创建统一的子查询记忆表。
    /// </summary>
    /// <param name="tsdb">目标数据库。</param>
    /// <param name="statement">待执行的 SELECT AST。</param>
    /// <returns>关系查询结果。</returns>
    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
        => Execute(tsdb, statement, outerScope: null, new SubqueryMemo(metrics: null));

    /// <summary>
    /// 使用子查询执行指标运行关系查询，供回归测试和基准验证记忆化效果。
    /// </summary>
    /// <param name="tsdb">目标数据库。</param>
    /// <param name="statement">待执行的 SELECT AST。</param>
    /// <param name="metrics">接收实际子查询执行次数与缓存命中次数的指标。</param>
    /// <returns>关系查询结果。</returns>
    internal static SelectExecutionResult Execute(
        Tsdb tsdb,
        SelectStatement statement,
        RelationalSelectExecutionMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return Execute(tsdb, statement, outerScope: null, new SubqueryMemo(metrics));
    }

    /// <summary>
    /// 相关子查询入口：携带外层 (列, 行) 上下文执行子 SELECT。
    /// 子查询内部 WHERE / 投影解析标识符时，若当前内层关系命中 0 个匹配，
    /// 沿 <see cref="RelationalScope.Parent"/> 链逐层回退到外层，模拟 SQL 标准的作用域语义。
    /// </summary>
    private static SelectExecutionResult Execute(
        Tsdb tsdb,
        SelectStatement statement,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement.UnionStatements.Count != 0)
        {
            return SqlExecutor.ExecuteUnion(
                statement,
                branch => Execute(tsdb, branch, outerScope, memo));
        }

        if (outerScope is null
            && TryRewriteNonCorrelatedInSemijoin(
                tsdb,
                statement,
                memo,
                out var semijoinStatement,
                out var semijoinSchema))
        {
            return TableSqlExecutor.ExecuteSelect(tsdb, semijoinStatement, semijoinSchema);
        }

        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("关系型 SELECT 暂不支持 FROM 表值函数。");

        var inputPushdown = PlanRelationInputs(tsdb, statement, outerScope);
        var relation = LoadFrom(tsdb, statement, inputPushdown.From, memo);
        for (int joinIndex = 0; joinIndex < statement.JoinClauses.Count; joinIndex++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var join = statement.JoinClauses[joinIndex];
            var right = LoadJoin(tsdb, join, inputPushdown.Joins[joinIndex], memo);
            relation = Join(tsdb, relation, right, join.On, join.Kind, outerScope, memo);
        }

        if (statement.Where is not null)
        {
            relation = relation with
            {
                Rows = FilterRows(
                    tsdb,
                    relation.Columns,
                    relation.Rows,
                    statement.Where,
                    outerScope,
                    memo),
            };
        }

        if (ContainsAggregate(statement.Projections)
            || statement.GroupBy.Count > 0
            || statement.Having is not null)
        {
            relation = relation with { Rows = relation.Rows.ToArray() };
            var aggregateProjection = ExecuteAggregateProjection(tsdb, statement, relation, outerScope, memo);
            return ApplyOrderByAndPagination(aggregateProjection, statement.OrderByList, statement.Pagination);
        }

        bool canApplyRelationOrderBy = CanApplyRelationOrderBy(statement.OrderByList, relation);
        if (canApplyRelationOrderBy && statement.OrderByList.Count > 0)
        {
            relation = ApplyRelationOrderByAndPagination(
                tsdb,
                relation,
                statement.OrderByList,
                statement.Pagination,
                outerScope,
                memo);
        }

        (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) projected =
            ProjectRawRows(tsdb, statement, relation, outerScope, memo);
        if (statement.OrderByList.Count > 0 && !canApplyRelationOrderBy)
        {
            return ApplyOrderByAndPagination(
                projected.Columns,
                projected.Rows,
                statement.OrderByList,
                statement.Pagination);
        }
        if (canApplyRelationOrderBy && statement.OrderByList.Count > 0)
            return new SelectExecutionResult(projected.Columns, projected.Rows.ToArray());
        return ApplyPagination(projected.Columns, projected.Rows, statement.Pagination);
    }

    /// <summary>
    /// 把可证明非相关的单表正向 IN 子查询一次性物化为键集合，再交回普通表访问规划执行 MultiGet。
    /// </summary>
    internal static bool TryRewriteNonCorrelatedInSemijoin(
        Tsdb tsdb,
        SelectStatement statement,
        out SelectStatement rewritten,
        out TableSchema schema)
        => TryRewriteNonCorrelatedInSemijoin(
            tsdb,
            statement,
            memo: null,
            out rewritten,
            out schema);

    /// <summary>运行时 semijoin 重写核心；可选记录内表实际执行次数。</summary>
    private static bool TryRewriteNonCorrelatedInSemijoin(
        Tsdb tsdb,
        SelectStatement statement,
        SubqueryMemo? memo,
        out SelectStatement rewritten,
        out TableSchema schema)
    {
        rewritten = statement;
        schema = null!;
        if (statement.Where is null
            || statement.FromSubquery is not null
            || statement.JoinClauses.Count != 0
            || tsdb.Tables.Catalog.TryGet(statement.Measurement) is not { } tableSchema)
        {
            return false;
        }
        schema = tableSchema;

        InExpression? target = null;
        foreach (var conjunct in FlattenAndExpr(statement.Where))
        {
            if (conjunct is InExpression
                {
                    Negated: false,
                    Subquery: not null,
                    Value: IdentifierExpression,
                } candidate)
            {
                if (target is not null)
                    return false;
                target = candidate;
                continue;
            }

            if (ContainsSubquery(conjunct))
                return false;
        }

        if (target?.Subquery is not { } subquery
            || !TableInSubqueryExecutor.IsNonCorrelated(
                tsdb,
                subquery,
                tableSchema,
                statement.TableAlias ?? statement.Measurement))
        {
            return false;
        }

        var emptyReplacement = target with { Values = [], Subquery = null };
        SqlExpression preflightWhere = ReplaceTopLevelConjunct(
            statement.Where,
            target,
            emptyReplacement);
        var preflight = statement with { Where = preflightWhere };
        if (NeedsRelationalPath(preflight)
            || !TableSqlExecutor.TryChooseInAccessPlan(tableSchema, preflightWhere, out _))
        {
            return false;
        }

        memo?.RecordExecution();
        SelectExecutionResult inner = SqlExecutor.ExecuteSelect(tsdb, subquery);
        if (inner.Columns.Count != 1)
            throw new InvalidOperationException("SELECT 的 IN 子查询必须只返回一列。");

        var values = new SqlExpression[inner.Rows.Count];
        for (int i = 0; i < inner.Rows.Count; i++)
        {
            IReadOnlyList<object?> row = inner.Rows[i];
            if (row.Count != 1)
                throw new InvalidOperationException("SELECT 的 IN 子查询必须只返回一列。");
            values[i] = new MaterializedSubqueryValueExpression(row[0]);
        }

        var replacement = target with { Values = values, Subquery = null };
        SqlExpression rewrittenWhere = ReplaceTopLevelConjunct(statement.Where, target, replacement);
        rewritten = statement with { Where = rewrittenWhere };
        return true;
    }

    /// <summary>在顶层 AND 树中按节点身份替换已识别的 IN 子查询。</summary>
    private static SqlExpression ReplaceTopLevelConjunct(
        SqlExpression expression,
        SqlExpression target,
        SqlExpression replacement)
    {
        if (ReferenceEquals(expression, target))
            return replacement;
        if (expression is not BinaryExpression { Operator: SqlBinaryOperator.And } binary)
            return expression;

        SqlExpression left = ReplaceTopLevelConjunct(binary.Left, target, replacement);
        SqlExpression right = ReplaceTopLevelConjunct(binary.Right, target, replacement);
        return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
            ? binary
            : binary with { Left = left, Right = right };
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
    /// 生命周期 = 一次顶层关系查询执行，并由所有递归表达式子查询共享。
    /// </summary>
    private sealed class SubqueryMemo
    {
        private readonly Dictionary<SelectStatement, SelectExecutionResult> _cache = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SelectStatement, bool> _existsCache = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SelectStatement> _correlated = new(ReferenceEqualityComparer.Instance);
        private readonly RelationalSelectExecutionMetrics? _metrics;

        public SubqueryMemo(RelationalSelectExecutionMetrics? metrics) => _metrics = metrics;

        public bool TryGetCached(SelectStatement subquery, out SelectExecutionResult result)
            => _cache.TryGetValue(subquery, out result!);

        /// <summary>尝试读取非相关 EXISTS 的布尔缓存。</summary>
        public bool TryGetExistsCached(SelectStatement subquery, out bool result)
            => _existsCache.TryGetValue(subquery, out result);

        public bool IsKnownCorrelated(SelectStatement subquery) => _correlated.Contains(subquery);

        public void CacheNonCorrelated(SelectStatement subquery, SelectExecutionResult result)
            => _cache[subquery] = result;

        /// <summary>缓存一次已证明非相关的 EXISTS 结果。</summary>
        public void CacheNonCorrelatedExists(SelectStatement subquery, bool result)
            => _existsCache[subquery] = result;

        public void MarkCorrelated(SelectStatement subquery) => _correlated.Add(subquery);

        public void RecordExecution() => _metrics?.RecordSubqueryExecution();

        public void RecordCacheHit() => _metrics?.RecordSubqueryCacheHit();

        /// <summary>转发一次 EXISTS 快速路径的执行证据。</summary>
        public void RecordExistsFastPath(
            TableExistsAccessPlan plan,
            int candidateRows,
            int examinedRows,
            bool earlyExit)
        {
            _metrics?.RecordExistsFastPath(plan, examinedRows, earlyExit);
            SqlExecutionTelemetry.RecordAccessPath(plan.AccessPath, plan.IndexName, plan.FallbackReason);
            SqlExecutionTelemetry.RecordCandidateRows(candidateRows);
            SqlExecutionTelemetry.RecordExaminedRows(examinedRows);
        }

        /// <summary>转发一次 EXISTS 完整关系路径回退原因。</summary>
        public void RecordExistsFallback(string reason, bool hasResidualPredicate)
        {
            _metrics?.RecordExistsFallback(reason, hasResidualPredicate);
            SqlExecutionTelemetry.RecordAccessPath("relational_fallback", fallbackReason: reason);
        }

        /// <summary>记录一次关系输入谓词、投影或 LIMIT 下推的执行证据。</summary>
        public void RecordRelationInput(
            RelationInputPlan plan,
            int sourceColumns,
            int projectedColumns,
            int candidateRows,
            int retainedRows)
            => _metrics?.RecordRelationInput(
                plan.Predicate is not null,
                plan.RowLimit is not null,
                sourceColumns,
                projectedColumns,
                candidateRows,
                retainedRows);
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

    /// <summary>供 EXPLAIN 判断当前关系查询是否包含阻塞聚合边界。</summary>
    internal static bool ContainsAggregateForExplain(SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return ContainsAggregate(statement.Projections);
    }

    /// <summary>
    /// 为独立的单表 EXISTS 生成与运行时快速路径共用的访问计划描述，不执行业务数据扫描。
    /// </summary>
    /// <param name="tsdb">目标数据库。</param>
    /// <param name="subquery">EXISTS 内层 SELECT。</param>
    /// <returns>访问路径、索引、残余谓词和回退原因。</returns>
    internal static RelationalExistsExplainPlan ExplainExists(Tsdb tsdb, SelectStatement subquery)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(subquery);

        if (!TryGetSingleTableExistsSchema(tsdb, subquery, out var schema, out string fallbackReason))
            return BuildExistsFallbackPlan(tsdb, subquery, fallbackReason);
        if (!TryBindExistsWhere(
            subquery.Where,
            schema,
            subquery.TableAlias ?? subquery.Measurement,
            outerScope: null,
            out var boundWhere))
        {
            return BuildExistsFallbackPlan(tsdb, subquery, "correlated_value_requires_runtime_binding");
        }

        TableStore store = tsdb.Tables.Open(schema.Name);
        var access = TableSqlExecutor.PlanExistsAccessForExplain(store, schema, boundWhere);
        long tableRows = EstimateExistsTableRows(tsdb, schema);
        TableAccessCostEstimate? costEstimate = access.UsesPrimaryKey
            ? new TableAccessCostEstimate(
                "primary_key", "primary", 1, 1, 0, 1,
                "catalog", null, null, "primary_key rows<=1", null, null)
            : access.InPlan is { } inPlan
                ? new TableAccessCostEstimate(
                    access.AccessPath,
                    access.IndexName,
                    Math.Min(tableRows, inPlan.Values.Count),
                    Math.Max(1, Math.Min(tableRows, inPlan.Values.Count)),
                    0,
                    Math.Max(1, Math.Min(tableRows, inPlan.Values.Count)),
                    "catalog",
                    null,
                    null,
                    $"{access.AccessPath} rows<={Math.Min(tableRows, inPlan.Values.Count)}",
                    access.FallbackReason,
                    null)
                : TableCostPlanner.Estimate(
                    store,
                    schema,
                    boundWhere,
                    allowAutomaticRefresh: false);
        long estimatedRows = EstimateExistsCandidateRows(store, schema, access, tableRows);
        return new RelationalExistsExplainPlan(
            Measurement: schema.Name,
            AccessPath: access.AccessPath,
            IndexName: access.IndexName,
            EstimatedCandidateRows: estimatedRows,
            EarlyExit: true,
            HasResidualPredicate: access.HasResidualPredicate,
            FallbackReason: access.FallbackReason,
            EstimatedRowWidth: costEstimate?.EstimatedRowWidth,
            EstimatedLogicalReads: costEstimate?.EstimatedLogicalReads,
            EstimatedCost: costEstimate?.EstimatedCost,
            EstimateSource: costEstimate?.EstimateSource,
            StatisticsSequence: costEstimate?.StatisticsSequence,
            StatisticsFreshnessMilliseconds: costEstimate?.StatisticsFreshnessMilliseconds,
            CandidatePlans: costEstimate?.CandidatePlans);
    }

    /// <summary>使用新鲜统计估算 EXISTS 残余复检候选；统计不可用时保持稳定上界。</summary>
    private static long EstimateExistsCandidateRows(
        TableStore store,
        TableSchema schema,
        TableExistsAccessPlan access,
        long tableRows)
    {
        if (tableRows == 0)
            return 0;
        if (access.PredicateCovered
            || access.UsesPrimaryKey
            || access.IndexPlan is { Index.IsUnique: true, IsFullEquality: true })
        {
            return 1;
        }

        if (access.InPlan is { } inPlan)
            return Math.Min(tableRows, inPlan.Values.Count);

        TableStatisticsState state = store.GetStatisticsState();
        if (access.IndexPlan is { } indexPlan
            && state is { Statistics: { } statistics, IsStale: false })
        {
            return TableCostPlanner.EstimateIndexRows(tableRows, schema, indexPlan, statistics);
        }

        return tableRows;
    }

    /// <summary>
    /// 估算当前事务可见的表行数；只叠加已规范化写集的净行数变化，不读取业务行。
    /// </summary>
    private static long EstimateExistsTableRows(Tsdb tsdb, TableSchema schema)
    {
        long rows = tsdb.Tables.Open(schema.Name).RowCount;
        if (SqlTransactionContext.Current is not { } transaction
            || !transaction.TryGetBufferedMutations(schema.Name, out var mutations))
        {
            return rows;
        }

        foreach (var mutation in mutations)
        {
            if (mutation.PrimaryKeyValues is null && mutation.NewValues is not null)
                rows++;
            else if (mutation.PrimaryKeyValues is not null && mutation.NewValues is null)
                rows--;
        }

        return Math.Max(0, rows);
    }

    /// <summary>
    /// 构造完整关系执行器回退计划，明确该路径会先物化关系输入。
    /// </summary>
    private static RelationalExistsExplainPlan BuildExistsFallbackPlan(
        Tsdb tsdb,
        SelectStatement subquery,
        string fallbackReason)
    {
        var schema = tsdb.Tables.Catalog.TryGet(subquery.Measurement);
        long estimatedRows = schema is null ? 0 : EstimateExistsTableRows(tsdb, schema);
        return new RelationalExistsExplainPlan(
            Measurement: subquery.Measurement,
            AccessPath: "relational_fallback",
            IndexName: null,
            EstimatedCandidateRows: estimatedRows,
            EarlyExit: false,
            HasResidualPredicate: subquery.Where is not null,
            FallbackReason: fallbackReason);
    }

    private sealed record RelationInputDescriptor(string Alias, TableSchema Schema);

    private sealed record RelationInputPlan(
        SqlExpression? Predicate,
        IReadOnlySet<string>? RequiredColumns,
        int? RowLimit)
    {
        public static RelationInputPlan Disabled { get; } = new(null, null, null);
    }

    private sealed record RelationInputPushdownPlan(
        RelationInputPlan From,
        IReadOnlyList<RelationInputPlan> Joins)
    {
        public static RelationInputPushdownPlan Disabled(int joinCount)
        {
            var joins = new RelationInputPlan[joinCount];
            Array.Fill(joins, RelationInputPlan.Disabled);
            return new RelationInputPushdownPlan(RelationInputPlan.Disabled, joins);
        }
    }

    private static RelationInputPushdownPlan PlanRelationInputs(
        Tsdb tsdb,
        SelectStatement statement,
        RelationalScope? outerScope)
    {
        if (statement.JoinClauses.Count == 0 || outerScope is not null)
            return RelationInputPushdownPlan.Disabled(statement.JoinClauses.Count);

        var inputs = new List<RelationInputDescriptor>(statement.JoinClauses.Count + 1);
        if (!TryCreateInputDescriptor(
                tsdb,
                statement.Measurement,
                statement.TableAlias ?? statement.Measurement,
                statement.FromSubquery,
                out var from))
        {
            return RelationInputPushdownPlan.Disabled(statement.JoinClauses.Count);
        }
        inputs.Add(from);

        foreach (var join in statement.JoinClauses)
        {
            if (!TryCreateInputDescriptor(tsdb, join.TableName, join.Alias, join.Subquery, out var input))
                return RelationInputPushdownPlan.Disabled(statement.JoinClauses.Count);
            inputs.Add(input);
        }

        if (HasDuplicateInputAlias(inputs))
        {
            return RelationInputPushdownPlan.Disabled(statement.JoinClauses.Count);
        }

        bool hasSubquery = ContainsSubquery(statement);
        var pushedPredicates = new List<SqlExpression>[inputs.Count];
        for (int i = 0; i < pushedPredicates.Length; i++)
            pushedPredicates[i] = [];

        if (!hasSubquery && statement.Where is not null && HasOnlyInnerJoins(statement.JoinClauses))
        {
            foreach (var conjunct in FlattenAndExpr(statement.Where))
            {
                if (TryResolveExpressionInput(conjunct, inputs, out int inputIndex, out var normalized))
                    pushedPredicates[inputIndex].Add(normalized);
            }
        }

        var requiredColumns = new HashSet<string>[inputs.Count];
        for (int i = 0; i < requiredColumns.Length; i++)
            requiredColumns[i] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool projectionPushdown = !hasSubquery
            && !HasStarProjection(statement.Projections)
            && TryCollectRequiredColumns(statement, inputs, requiredColumns);

        int? fromLimit = TryGetSafeFromInputLimit(statement, hasSubquery);
        var plans = new RelationInputPlan[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
        {
            plans[i] = new RelationInputPlan(
                CombineConjuncts(pushedPredicates[i]),
                projectionPushdown ? requiredColumns[i] : null,
                i == 0 ? fromLimit : null);
        }

        return new RelationInputPushdownPlan(plans[0], plans[1..]);
    }

    private static bool HasDuplicateInputAlias(IReadOnlyList<RelationInputDescriptor> inputs)
    {
        for (int i = 0; i < inputs.Count; i++)
        {
            for (int j = i + 1; j < inputs.Count; j++)
            {
                if (NameEquals(inputs[i].Alias, inputs[j].Alias))
                    return true;
            }
        }
        return false;
    }

    private static bool HasOnlyInnerJoins(IReadOnlyList<JoinClause> joins)
    {
        foreach (var join in joins)
        {
            if (join.Kind != JoinKind.Inner)
                return false;
        }
        return true;
    }

    private static bool HasStarProjection(IReadOnlyList<SelectItem> projections)
    {
        foreach (var projection in projections)
        {
            if (projection.Expression is StarExpression)
                return true;
        }
        return false;
    }

    private static bool TryCreateInputDescriptor(
        Tsdb tsdb,
        string sourceName,
        string alias,
        SelectStatement? subquery,
        out RelationInputDescriptor descriptor)
    {
        descriptor = null!;
        if (subquery is not null)
            return false;

        var schema = tsdb.Tables.Catalog.TryGet(sourceName);
        if (schema is null)
            return false;

        descriptor = new RelationInputDescriptor(alias, schema);
        return true;
    }

    private static bool TryResolveExpressionInput(
        SqlExpression expression,
        IReadOnlyList<RelationInputDescriptor> inputs,
        out int inputIndex,
        out SqlExpression normalized)
    {
        inputIndex = -1;
        normalized = expression;
        if (ContainsFunctionCall(expression))
            return false;

        bool foundIdentifier = false;
        foreach (var identifier in EnumerateLocalIdentifiers(expression))
        {
            if (!TryResolveIdentifierInput(identifier, inputs, out int resolved))
                return false;
            if (foundIdentifier && inputIndex != resolved)
                return false;
            foundIdentifier = true;
            inputIndex = resolved;
        }
        if (!foundIdentifier)
            return false;

        normalized = NormalizeInputExpression(expression, inputs[inputIndex]);
        return true;
    }

    private static bool ContainsFunctionCall(SqlExpression expression)
    {
        switch (expression)
        {
            case FunctionCallExpression:
                return true;
            case UnaryExpression unary:
                return ContainsFunctionCall(unary.Operand);
            case BinaryExpression binary:
                return ContainsFunctionCall(binary.Left) || ContainsFunctionCall(binary.Right);
            case IsNullExpression isNull:
                return ContainsFunctionCall(isNull.Operand);
            case InExpression inExpression:
                if (ContainsFunctionCall(inExpression.Value))
                    return true;
                foreach (var value in inExpression.Values)
                {
                    if (ContainsFunctionCall(value))
                        return true;
                }
                return false;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    if (ContainsFunctionCall(clause.Condition)
                        || ContainsFunctionCall(clause.Result))
                    {
                        return true;
                    }
                }
                return caseExpression.Else is not null
                    && ContainsFunctionCall(caseExpression.Else);
            case NamedArgumentExpression named:
                return ContainsFunctionCall(named.Value);
            default:
                return false;
        }
    }

    private static bool TryCollectRequiredColumns(
        SelectStatement statement,
        IReadOnlyList<RelationInputDescriptor> inputs,
        IReadOnlyList<HashSet<string>> requiredColumns)
    {
        foreach (var expression in EnumerateRelationExpressions(statement))
        {
            foreach (var identifier in EnumerateLocalIdentifiers(expression))
            {
                if (!TryResolveIdentifierInput(identifier, inputs, out int inputIndex))
                    return false;
                requiredColumns[inputIndex].Add(identifier.Name);
            }
        }
        return true;
    }

    private static IEnumerable<SqlExpression> EnumerateRelationExpressions(SelectStatement statement)
    {
        foreach (var projection in statement.Projections)
            yield return projection.Expression;
        if (statement.Where is not null)
            yield return statement.Where;
        foreach (var groupBy in statement.GroupBy)
            yield return groupBy;
        if (statement.Having is not null)
            yield return statement.Having;
        foreach (var orderBy in statement.OrderByList)
            yield return orderBy.Expression;
        foreach (var join in statement.JoinClauses)
            yield return join.On;
    }

    private static IEnumerable<IdentifierExpression> EnumerateLocalIdentifiers(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                yield return identifier;
                yield break;
            case UnaryExpression unary:
                foreach (var identifier in EnumerateLocalIdentifiers(unary.Operand))
                    yield return identifier;
                yield break;
            case BinaryExpression binary:
                foreach (var identifier in EnumerateLocalIdentifiers(binary.Left))
                    yield return identifier;
                foreach (var identifier in EnumerateLocalIdentifiers(binary.Right))
                    yield return identifier;
                yield break;
            case IsNullExpression isNull:
                foreach (var identifier in EnumerateLocalIdentifiers(isNull.Operand))
                    yield return identifier;
                yield break;
            case InExpression inExpression:
                foreach (var identifier in EnumerateLocalIdentifiers(inExpression.Value))
                    yield return identifier;
                foreach (var value in inExpression.Values)
                    foreach (var identifier in EnumerateLocalIdentifiers(value))
                        yield return identifier;
                yield break;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    foreach (var identifier in EnumerateLocalIdentifiers(clause.Condition))
                        yield return identifier;
                    foreach (var identifier in EnumerateLocalIdentifiers(clause.Result))
                        yield return identifier;
                }
                if (caseExpression.Else is not null)
                    foreach (var identifier in EnumerateLocalIdentifiers(caseExpression.Else))
                        yield return identifier;
                yield break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    foreach (var identifier in EnumerateLocalIdentifiers(argument))
                        yield return identifier;
                yield break;
            case NamedArgumentExpression named:
                foreach (var identifier in EnumerateLocalIdentifiers(named.Value))
                    yield return identifier;
                yield break;
            case SubqueryExpression or ExistsExpression:
                yield break;
        }
    }

    private static bool TryResolveIdentifierInput(
        IdentifierExpression identifier,
        IReadOnlyList<RelationInputDescriptor> inputs,
        out int inputIndex)
    {
        inputIndex = -1;
        int matches = 0;
        for (int i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (identifier.Qualifier is not null
                && !NameEquals(identifier.Qualifier, input.Alias))
            {
                continue;
            }
            if (!TryGetCanonicalColumn(input.Schema, identifier.Name, out _))
                continue;

            inputIndex = i;
            matches++;
            if (matches > 1)
                return false;
        }
        return matches == 1;
    }

    private static bool TryGetCanonicalColumn(
        TableSchema schema,
        string name,
        out TableColumn column)
    {
        column = null!;
        int matches = 0;
        foreach (var candidate in schema.Columns)
        {
            if (!NameEquals(candidate.Name, name))
                continue;
            column = candidate;
            matches++;
            if (matches > 1)
                return false;
        }
        return matches == 1;
    }

    private static SqlExpression NormalizeInputExpression(
        SqlExpression expression,
        RelationInputDescriptor input)
        => expression switch
        {
            IdentifierExpression identifier => identifier with
            {
                Name = GetCanonicalColumn(input.Schema, identifier.Name).Name,
                Qualifier = input.Schema.Name,
            },
            UnaryExpression unary => unary with
            {
                Operand = NormalizeInputExpression(unary.Operand, input),
            },
            BinaryExpression binary => binary with
            {
                Left = NormalizeInputExpression(binary.Left, input),
                Right = NormalizeInputExpression(binary.Right, input),
            },
            IsNullExpression isNull => isNull with
            {
                Operand = NormalizeInputExpression(isNull.Operand, input),
            },
            InExpression inExpression => inExpression with
            {
                Value = NormalizeInputExpression(inExpression.Value, input),
                Values = NormalizeInputExpressions(inExpression.Values, input),
            },
            CaseExpression caseExpression => caseExpression with
            {
                WhenClauses = NormalizeCaseClauses(caseExpression.WhenClauses, input),
                Else = caseExpression.Else is null
                    ? null
                    : NormalizeInputExpression(caseExpression.Else, input),
            },
            FunctionCallExpression function => function with
            {
                Arguments = NormalizeInputExpressions(function.Arguments, input),
            },
            NamedArgumentExpression named => named with
            {
                Value = NormalizeInputExpression(named.Value, input),
            },
            _ => expression,
        };

    private static TableColumn GetCanonicalColumn(TableSchema schema, string name)
        => TryGetCanonicalColumn(schema, name, out var column)
            ? column
            : throw new InvalidOperationException($"关系输入列 '{name}' 的绑定不再唯一。");

    private static SqlExpression[] NormalizeInputExpressions(
        IReadOnlyList<SqlExpression> expressions,
        RelationInputDescriptor input)
    {
        var normalized = new SqlExpression[expressions.Count];
        for (int i = 0; i < expressions.Count; i++)
            normalized[i] = NormalizeInputExpression(expressions[i], input);
        return normalized;
    }

    private static CaseWhenClause[] NormalizeCaseClauses(
        IReadOnlyList<CaseWhenClause> clauses,
        RelationInputDescriptor input)
    {
        var normalized = new CaseWhenClause[clauses.Count];
        for (int i = 0; i < clauses.Count; i++)
        {
            normalized[i] = clauses[i] with
            {
                Condition = NormalizeInputExpression(clauses[i].Condition, input),
                Result = NormalizeInputExpression(clauses[i].Result, input),
            };
        }
        return normalized;
    }

    private static SqlExpression? CombineConjuncts(IReadOnlyList<SqlExpression> conjuncts)
    {
        if (conjuncts.Count == 0)
            return null;

        SqlExpression combined = conjuncts[0];
        for (int i = 1; i < conjuncts.Count; i++)
            combined = new BinaryExpression(SqlBinaryOperator.And, combined, conjuncts[i]);
        return combined;
    }

    private static int? TryGetSafeFromInputLimit(SelectStatement statement, bool hasSubquery)
    {
        if (hasSubquery
            || statement.Pagination?.Fetch is not int fetch
            || statement.OrderByList.Count != 0
            || statement.Distinct
            || statement.Where is not null
            || statement.GroupBy.Count != 0
            || statement.Having is not null
            || ContainsAggregate(statement.Projections)
            || !HasOnlyLeftJoins(statement.JoinClauses))
        {
            return null;
        }

        try
        {
            return checked(statement.Pagination.Offset + fetch);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static bool HasOnlyLeftJoins(IReadOnlyList<JoinClause> joins)
    {
        foreach (var join in joins)
        {
            if (join.Kind != JoinKind.Left)
                return false;
        }
        return true;
    }

    private static Relation LoadFrom(
        Tsdb tsdb,
        SelectStatement statement,
        RelationInputPlan plan,
        SubqueryMemo memo)
    {
        if (string.IsNullOrEmpty(statement.Measurement) && statement.FromSubquery is null)
            return new Relation(Array.Empty<RelColumn>(), [Array.Empty<object?>()]);

        var alias = statement.TableAlias ?? statement.Measurement;
        if (statement.FromSubquery is not null)
            return LoadSubquery(tsdb, statement.FromSubquery, alias);

        var schema = tsdb.Tables.Catalog.TryGet(statement.Measurement);
        if (schema is not null)
            return LoadTable(tsdb, schema, alias, plan, memo);
        if (tsdb.MaterializedViews.Catalog.TryGet(statement.Measurement) is not null)
            return LoadMaterializedView(tsdb.MaterializedViews, statement.Measurement, alias);
        throw new InvalidOperationException($"table/materialized view '{statement.Measurement}' 不存在。");
    }

    private static Relation LoadJoin(
        Tsdb tsdb,
        JoinClause join,
        RelationInputPlan plan,
        SubqueryMemo memo)
    {
        if (join.Subquery is not null)
            return LoadSubquery(tsdb, join.Subquery, join.Alias);

        var schema = tsdb.Tables.Catalog.TryGet(join.TableName);
        if (schema is not null)
            return LoadTable(tsdb, schema, join.Alias, plan, memo);
        if (tsdb.MaterializedViews.Catalog.TryGet(join.TableName) is not null)
            return LoadMaterializedView(tsdb.MaterializedViews, join.TableName, join.Alias);
        throw new InvalidOperationException($"JOIN 右侧 table/materialized view '{join.TableName}' 不存在。");
    }

    private static Relation LoadTable(
        Tsdb tsdb,
        TableSchema schema,
        string alias,
        RelationInputPlan plan,
        SubqueryMemo memo)
    {
        var selectedColumns = SelectInputColumns(schema, plan.RequiredColumns);
        var columns = new RelColumn[selectedColumns.Length];
        for (int i = 0; i < selectedColumns.Length; i++)
        {
            var column = selectedColumns[i];
            columns[i] = new RelColumn(alias, column.Name, column.Name, column.DataType);
        }
        if (plan.RowLimit == 0)
        {
            memo.RecordRelationInput(
                plan,
                schema.Columns.Count,
                selectedColumns.Length,
                candidateRows: 0,
                retainedRows: 0);
            return new Relation(columns, []);
        }
        // read-your-writes：叠加当前 ambient 轻事务对本表的缓冲写（#218）。
        IEnumerable<TableRow> candidates = TableSqlExecutor.EnumerateSelectCandidateRows(
            tsdb.Tables.Open(schema.Name),
            schema,
            plan.Predicate,
            plan.RequiredColumns);
        return new Relation(columns, ProjectRows());

        IEnumerable<object?[]> ProjectRows()
        {
            int candidateCount = 0;
            int retainedCount = 0;
            try
            {
                foreach (TableRow candidate in candidates)
                {
                    candidateCount++;
                    SqlExecutor.ThrowIfCancellationRequested();
                    if (!TableSqlExecutor.EvaluateWhere(plan.Predicate, schema, candidate.Values))
                        continue;

                    var row = new object?[selectedColumns.Length];
                    for (int i = 0; i < selectedColumns.Length; i++)
                        row[i] = candidate.Values[selectedColumns[i].Ordinal];
                    retainedCount++;
                    yield return row;
                    if (plan.RowLimit is int rowLimit && retainedCount >= rowLimit)
                        yield break;
                }
            }
            finally
            {
                memo.RecordRelationInput(
                    plan,
                    schema.Columns.Count,
                    selectedColumns.Length,
                    candidateCount,
                    retainedCount);
            }
        }
    }

    private static TableColumn[] SelectInputColumns(
        TableSchema schema,
        IReadOnlySet<string>? requiredColumns)
    {
        if (requiredColumns is null)
            return schema.Columns.ToArray();

        var selected = new TableColumn[requiredColumns.Count];
        int index = 0;
        foreach (var column in schema.Columns)
        {
            if (requiredColumns.Contains(column.Name))
                selected[index++] = column;
        }
        if (index != selected.Length)
            throw new InvalidOperationException("关系输入投影包含无法绑定的列。");
        return selected;
    }

    private static Relation LoadMaterializedView(
        MaterializedViewManager manager,
        string name,
        string alias)
    {
        SelectExecutionResult snapshot = manager.ReadSnapshot(name);
        var columns = snapshot.Columns
            .Select(column => new RelColumn(alias, NormalizeSubqueryColumnName(column), column))
            .ToArray();
        var rows = snapshot.Rows
            .Select(static row => row.ToArray())
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

    private static Relation Join(
        Tsdb tsdb,
        Relation left,
        Relation right,
        SqlExpression on,
        JoinKind kind,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        // #215：等值连接走哈希连接（O(N+M)），替换全物化嵌套循环笛卡尔积（O(N×M)）。
        // 仅当 ON 能拆出至少一组 left_col = right_col 等值键、且无相关子查询等复杂依赖时启用；
        // 否则回退嵌套循环。残差（非等值）合取项在候选对上再求值，保持语义完全一致。
        if (TryPlanHashJoin(left, right, on, out var keyPairs, out var residual))
            return HashJoin(tsdb, left, right, keyPairs, residual, kind, outerScope, memo);

        return NestedLoopJoin(tsdb, left, right, on, kind, outerScope, memo);
    }

    private static Relation NestedLoopJoin(
        Tsdb tsdb,
        Relation left,
        Relation right,
        SqlExpression on,
        JoinKind kind,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var columns = left.Columns.Concat(right.Columns).ToArray();
        return new Relation(columns, JoinRows());

        IEnumerable<object?[]> JoinRows()
        {
            object?[][] rightRows = right.Rows.ToArray();
            foreach (object?[] leftRow in left.Rows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                var matched = false;
                foreach (object?[] rightRow in rightRows)
                {
                    SqlExecutor.ThrowIfCancellationRequested();
                    var row = new object?[leftRow.Length + rightRow.Length];
                    Array.Copy(leftRow, row, leftRow.Length);
                    Array.Copy(rightRow, 0, row, leftRow.Length, rightRow.Length);
                    if (EvaluateBoolean(tsdb, on, columns, row, outerScope, memo))
                    {
                        matched = true;
                        yield return row;
                    }
                }

                if (!matched && kind == JoinKind.Left)
                {
                    var row = new object?[leftRow.Length + right.Columns.Count];
                    Array.Copy(leftRow, row, leftRow.Length);
                    yield return row;
                }
            }
        }
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
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var columns = left.Columns.Concat(right.Columns).ToArray();
        return new Relation(columns, JoinRows());

        IEnumerable<object?[]> JoinRows()
        {
            var buildTable = new Dictionary<JoinValueKey, List<object?[]>>();
            foreach (object?[] rightRow in right.Rows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
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
            foreach (object?[] leftRow in left.Rows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                bool matched = false;
                if (TryMakeKey(leftRow, keyPairs, useRight: false, out var probeKey)
                    && buildTable.TryGetValue(probeKey, out var candidates))
                {
                    foreach (object?[] rightRow in candidates)
                    {
                        SqlExecutor.ThrowIfCancellationRequested();
                        var row = new object?[leftRow.Length + rightRow.Length];
                        Array.Copy(leftRow, row, leftRow.Length);
                        Array.Copy(rightRow, 0, row, leftRow.Length, rightRow.Length);

                        if (hasResidual && !ResidualHolds(tsdb, residual, columns, row, outerScope, memo))
                            continue;

                        matched = true;
                        yield return row;
                    }
                }

                if (!matched && kind == JoinKind.Left)
                {
                    var row = new object?[leftRow.Length + right.Columns.Count];
                    Array.Copy(leftRow, row, leftRow.Length);
                    yield return row;
                }
            }
        }
    }

    private static bool ResidualHolds(
        Tsdb tsdb,
        List<SqlExpression> residual,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        foreach (var conjunct in residual)
        {
            if (!EvaluateBoolean(tsdb, conjunct, columns, row, outerScope, memo))
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

    private static IEnumerable<object?[]> FilterRows(
        Tsdb tsdb,
        IReadOnlyList<RelColumn> columns,
        IEnumerable<object?[]> rows,
        SqlExpression predicate,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        foreach (object?[] row in rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (EvaluateBoolean(tsdb, predicate, columns, row, outerScope, memo))
                yield return row;
        }
    }

    private static (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows) ProjectRawRows(
        Tsdb tsdb,
        SelectStatement statement,
        Relation relation,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var projections = BuildRawProjections(statement.Projections, relation);
        return (projections.Select(static projection => projection.Name).ToArray(), ProjectRows());

        IEnumerable<IReadOnlyList<object?>> ProjectRows()
        {
            foreach (object?[] row in relation.Rows)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                var output = new object?[projections.Count];
                for (int i = 0; i < projections.Count; i++)
                    output[i] = EvaluateScalar(tsdb, projections[i].Expression, relation.Columns, row, outerScope, memo);
                yield return output;
            }
        }
    }

    private static bool CanApplyRelationOrderBy(IReadOnlyList<OrderBySpec> orderBy, Relation relation)
        => orderBy.All(order => CanEvaluateAgainstRelation(order.Expression, relation));

    /// <summary>
    /// 判断排序表达式是否能在投影前直接基于关系行求值；子查询内部拥有独立列作用域，
    /// 仅需把当前关系作为其外层作用域。无法解析的裸标识符保留给投影别名排序路径。
    /// </summary>
    private static bool CanEvaluateAgainstRelation(SqlExpression expression, Relation relation)
    {
        return expression switch
        {
            LiteralExpression or DurationLiteralExpression or SubqueryExpression or ExistsExpression => true,
            IdentifierExpression identifier => TryResolveInRelation(relation, identifier) is not null,
            UnaryExpression unary => CanEvaluateAgainstRelation(unary.Operand, relation),
            BinaryExpression binary => CanEvaluateAgainstRelation(binary.Left, relation)
                && CanEvaluateAgainstRelation(binary.Right, relation),
            IsNullExpression isNull => CanEvaluateAgainstRelation(isNull.Operand, relation),
            InExpression inExpression => CanEvaluateAgainstRelation(inExpression.Value, relation)
                && (inExpression.Subquery is not null
                    || inExpression.Values.All(value => CanEvaluateAgainstRelation(value, relation))),
            CaseExpression caseExpression => caseExpression.WhenClauses.All(when =>
                    CanEvaluateAgainstRelation(when.Condition, relation)
                    && CanEvaluateAgainstRelation(when.Result, relation))
                && (caseExpression.Else is null || CanEvaluateAgainstRelation(caseExpression.Else, relation)),
            FunctionCallExpression function when !function.IsStar =>
                function.Arguments.All(argument => CanEvaluateAgainstRelation(argument, relation)),
            NamedArgumentExpression named => CanEvaluateAgainstRelation(named.Value, relation),
            _ => false,
        };
    }

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
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var projections = BuildAggregateProjections(statement.Projections, statement.GroupBy, relation);
        var groups = new Dictionary<GroupKey, List<object?[]>>();
        foreach (var row in relation.Rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var keyValues = statement.GroupBy
                .Select(group => EvaluateScalar(tsdb, group, relation.Columns, row, outerScope, memo))
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
        var allIntegralByNestedAggregate = new Dictionary<FunctionCallExpression, bool>(
            ReferenceEqualityComparer.Instance);
        for (int i = 0; i < projections.Count; i++)
        {
            if (projections[i].Aggregate is not null)
            {
                allIntegralByProjection ??= new bool[projections.Count];
                allIntegralByProjection[i] = IsIntegralAggregateInput(
                    tsdb, projections[i].Aggregate!, relation, outerScope, memo);
                continue;
            }

            foreach (var function in EnumerateAggregateCalls(projections[i].Expression))
            {
                var aggregate = new AggregateSpec(function);
                allIntegralByNestedAggregate[function] = IsIntegralAggregateInput(
                    tsdb, aggregate, relation, outerScope, memo);
            }
        }

        if (statement.Having is not null)
        {
            // HAVING 与投影共享同一套全结果集类型推断，避免大整数聚合在谓词中降为 Double。
            foreach (var function in EnumerateAggregateCalls(statement.Having))
            {
                var aggregate = new AggregateSpec(function);
                allIntegralByNestedAggregate[function] = IsIntegralAggregateInput(
                    tsdb, aggregate, relation, outerScope, memo);
            }
        }

        foreach (var group in groups.Values)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var representative = group.Count == 0
                ? Array.Empty<object?>()
                : group[0];

            if (statement.Having is not null
                && !EvaluateHavingPredicate(
                    tsdb, statement.Having, relation.Columns, representative, group,
                    allIntegralByNestedAggregate, outerScope, memo))
            {
                continue;
            }

            var output = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
            {
                var projection = projections[i];
                output[i] = projection.Aggregate is null
                    ? ContainsAggregate(projection.Expression)
                        ? EvaluateAggregateProjectionExpression(
                            tsdb, projection.Expression, relation.Columns, representative, group,
                            allIntegralByNestedAggregate, outerScope, memo)
                        : EvaluateScalar(tsdb, projection.Expression, relation.Columns, representative, outerScope, memo)
                    : EvaluateAggregate(tsdb, projection.Aggregate, relation.Columns, group,
                        allIntegralInput: allIntegralByProjection?[i] ?? false,
                        outerScope,
                        memo);
            }
            rows.Add(output);
        }

        return new SelectExecutionResult(projections.Select(static p => p.Name).ToArray(), rows);
    }

    /// <summary>
    /// 先内联投影表达式中的聚合调用，再按普通标量表达式求值，例如 count(*) + 1。
    /// </summary>
    private static object? EvaluateAggregateProjectionExpression(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> representative,
        IReadOnlyList<object?[]> group,
        IReadOnlyDictionary<FunctionCallExpression, bool> allIntegralByAggregate,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var inlined = InlineAggregates(
            tsdb, expression, columns, group, outerScope, memo, allIntegralByAggregate);
        return EvaluateScalar(tsdb, inlined, columns, representative, outerScope, memo);
    }

    /// <summary>
    /// 统一判定关系聚合输入是否全为整数，优先使用 schema 静态类型，无法确定时才扫描完整结果集。
    /// </summary>
    private static bool IsIntegralAggregateInput(
        Tsdb tsdb,
        AggregateSpec aggregate,
        Relation relation,
        RelationalScope? outerScope,
        SubqueryMemo memo)
        => InferAggregateInputIntegral(aggregate, relation.Columns)
            ?? IsAggregateInputAllIntegral(
                tsdb, aggregate, relation.Columns, relation.Rows, outerScope, memo);

    /// <summary>
    /// 判定某个聚合的输入表达式在 <paramref name="allRows"/> 全集合上是否只产出整数（或 null）。
    /// 这是为了让 sum/min/max 的返回类型在整张结果集上保持一致（M3 修复）：要么全 long，要么全 double。
    /// </summary>
    private static bool IsAggregateInputAllIntegral(
        Tsdb tsdb,
        AggregateSpec aggregate,
        IReadOnlyList<RelColumn> columns,
        IEnumerable<object?[]> allRows,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var fn = aggregate.Function;
        if (fn.IsStar) return true; // count(*) 不关心输入类型
        if (fn.Arguments.Count == 0) return true;

        foreach (var row in allRows)
        {
            var v = EvaluateScalar(tsdb, fn.Arguments[0], columns, row, outerScope, memo);
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
        IReadOnlyList<object?[]> group,
        IReadOnlyDictionary<FunctionCallExpression, bool> allIntegralByAggregate,
        RelationalScope? outerScope,
        SubqueryMemo memo)
        => EvaluateHavingKleene(
            tsdb, expression, columns, representative, group,
            allIntegralByAggregate, outerScope, memo) == true;

    private static bool? EvaluateHavingKleene(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> representative,
        IReadOnlyList<object?[]> group,
        IReadOnlyDictionary<FunctionCallExpression, bool> allIntegralByAggregate,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == SqlBinaryOperator.And)
            {
                var left = EvaluateHavingKleene(
                    tsdb, binary.Left, columns, representative, group,
                    allIntegralByAggregate, outerScope, memo);
                if (left == false) return false;
                var right = EvaluateHavingKleene(
                    tsdb, binary.Right, columns, representative, group,
                    allIntegralByAggregate, outerScope, memo);
                if (right == false) return false;
                return left is null || right is null ? null : true;
            }
            if (binary.Operator == SqlBinaryOperator.Or)
            {
                var left = EvaluateHavingKleene(
                    tsdb, binary.Left, columns, representative, group,
                    allIntegralByAggregate, outerScope, memo);
                if (left == true) return true;
                var right = EvaluateHavingKleene(
                    tsdb, binary.Right, columns, representative, group,
                    allIntegralByAggregate, outerScope, memo);
                if (right == true) return true;
                return left is null || right is null ? null : false;
            }
            if (IsComparisonOperator(binary.Operator))
            {
                var left = EvaluateHavingScalar(
                    tsdb, binary.Left, columns, representative, group,
                    allIntegralByAggregate, outerScope, memo);
                var right = EvaluateHavingScalar(
                    tsdb, binary.Right, columns, representative, group,
                    allIntegralByAggregate, outerScope, memo);
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
            var operand = EvaluateHavingKleene(
                tsdb, unary.Operand, columns, representative, group,
                allIntegralByAggregate, outerScope, memo);
            return operand is null ? null : !operand;
        }
        else if (expression is IsNullExpression isNull)
        {
            var isNullValue = EvaluateHavingScalar(
                tsdb, isNull.Operand, columns, representative, group,
                allIntegralByAggregate, outerScope, memo) is null;
            return isNull.Negated ? !isNullValue : isNullValue;
        }
        else if (expression is InExpression inExpression)
        {
            var inlined = (InExpression)InlineAggregates(
                tsdb, inExpression, columns, group, outerScope, memo, allIntegralByAggregate);
            return EvaluateIn(tsdb, inlined, columns, representative, outerScope, memo);
        }

        var value = EvaluateHavingScalar(
            tsdb, expression, columns, representative, group,
            allIntegralByAggregate, outerScope, memo);
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
        IReadOnlyList<object?[]> group,
        IReadOnlyDictionary<FunctionCallExpression, bool> allIntegralByAggregate,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        var inlined = InlineAggregates(
            tsdb, expression, columns, group, outerScope, memo, allIntegralByAggregate);
        return EvaluateScalar(tsdb, inlined, columns, representative, outerScope, memo);
    }

    /// <summary>
    /// 递归把表达式树里所有聚合函数调用就地求值，并替换为对应字面量。
    /// 非聚合节点递归克隆子节点；标量函数参数中嵌套的聚合也会被替换。
    /// </summary>
    private static SqlExpression InlineAggregates(
        Tsdb tsdb,
        SqlExpression expression,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?[]> group,
        RelationalScope? outerScope,
        SubqueryMemo memo,
        IReadOnlyDictionary<FunctionCallExpression, bool>? allIntegralByAggregate = null)
    {
        switch (expression)
        {
            case FunctionCallExpression aggCall when IsAggregateFunction(aggCall.Name):
                {
                    bool allIntegralInput = allIntegralByAggregate is not null
                        && allIntegralByAggregate.TryGetValue(aggCall, out var integral)
                        && integral;
                    var value = EvaluateAggregate(tsdb, new AggregateSpec(aggCall), columns, group,
                        allIntegralInput, outerScope, memo);
                    return WrapValueAsLiteral(value);
                }
            case BinaryExpression binary:
                {
                    var left = InlineAggregates(
                        tsdb, binary.Left, columns, group, outerScope, memo, allIntegralByAggregate);
                    var right = InlineAggregates(
                        tsdb, binary.Right, columns, group, outerScope, memo, allIntegralByAggregate);
                    if (ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right))
                        return expression;
                    return binary with { Left = left, Right = right };
                }
            case UnaryExpression unary:
                {
                    var operand = InlineAggregates(
                        tsdb, unary.Operand, columns, group, outerScope, memo, allIntegralByAggregate);
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
                        args[i] = InlineAggregates(
                            tsdb, scalarCall.Arguments[i], columns, group, outerScope, memo,
                            allIntegralByAggregate);
                        if (!ReferenceEquals(args[i], scalarCall.Arguments[i]))
                            changed = true;
                    }
                    return changed ? scalarCall with { Arguments = args } : expression;
                }
            case CaseExpression caseExpression:
                {
                    var clauses = caseExpression.WhenClauses
                        .Select(clause => clause with
                        {
                            Condition = InlineAggregates(
                                tsdb, clause.Condition, columns, group, outerScope, memo,
                                allIntegralByAggregate),
                            Result = InlineAggregates(
                                tsdb, clause.Result, columns, group, outerScope, memo,
                                allIntegralByAggregate),
                        })
                        .ToArray();
                    var elseExpression = caseExpression.Else is null
                        ? null
                        : InlineAggregates(
                            tsdb, caseExpression.Else, columns, group, outerScope, memo,
                            allIntegralByAggregate);
                    return caseExpression with { WhenClauses = clauses, Else = elseExpression };
                }
            case IsNullExpression isNull:
                return isNull with
                {
                    Operand = InlineAggregates(
                        tsdb, isNull.Operand, columns, group, outerScope, memo,
                        allIntegralByAggregate),
                };
            case InExpression inExpression:
                return inExpression with
                {
                    Value = InlineAggregates(
                        tsdb, inExpression.Value, columns, group, outerScope, memo,
                        allIntegralByAggregate),
                    Values = inExpression.Values.Select(value => InlineAggregates(
                        tsdb, value, columns, group, outerScope, memo,
                        allIntegralByAggregate)).ToArray(),
                };
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

            if (ContainsAggregate(item.Expression))
            {
                result.Add(new Projection(item.Alias ?? FormatExpressionName(item.Expression), item.Expression));
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
        bool allIntegralInput = false,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        var fn = aggregate.Function;
        var name = fn.Name.ToLowerInvariant();
        if (name == "count")
        {
            if (fn.IsStar)
                return (long)rows.Count;
            RequireArgumentCount(fn, 1);
            return rows.LongCount(row => EvaluateScalar(tsdb, fn.Arguments[0], columns, row, outerScope, memo) is not null);
        }

        RequireArgumentCount(fn, 1);
        var rawValues = rows
            .Select(row => EvaluateScalar(tsdb, fn.Arguments[0], columns, row, outerScope, memo))
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
        var sawNull = false;
        bool Matches(object? candidate)
        {
            if (value is null || candidate is null)
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
    /// 同一顶层 SELECT 的 WHERE、投影、排序、JOIN 和函数参数共享 memo，递归子查询也沿用该实例。
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
        {
            memo.RecordCacheHit();
            return cached;
        }

        if (memo is null || memo.IsKnownCorrelated(subquery))
        {
            var inner = new RelationalScope(columns, row, outerScope);
            memo?.RecordExecution();
            return Execute(tsdb, subquery, inner, memo ?? new SubqueryMemo(metrics: null));
        }

        // 首次评估：挂探针执行；未触外层则缓存为非相关。
        var probe = new CorrelationProbe();
        var probedScope = new RelationalScope(columns, row, outerScope, probe);
        memo.RecordExecution();
        var result = Execute(tsdb, subquery, probedScope, memo);
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
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => SqlScalarOperations.Negate(EvaluateScalar(tsdb, unary.Operand, columns, row, outerScope, memo)),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(tsdb, binary, columns, row, outerScope, memo),
            BinaryExpression binary when binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or
                || IsComparisonOperator(binary.Operator) => EvaluateKleene(tsdb, binary, columns, row, outerScope, memo),
            UnaryExpression { Operator: SqlUnaryOperator.Not } unary => EvaluateKleene(tsdb, unary, columns, row, outerScope, memo),
            IsNullExpression isNull => EvaluateKleene(tsdb, isNull, columns, row, outerScope, memo),
            InExpression inExpression => EvaluateKleene(tsdb, inExpression, columns, row, outerScope, memo),
            CaseExpression caseExpression => EvaluateCase(tsdb, caseExpression, columns, row, outerScope, memo),
            FunctionCallExpression function => EvaluateFunction(tsdb, function, columns, row, outerScope, memo),
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

    /// <summary>
    /// 在关系结果行及可选外层作用域中计算共享数值算术表达式。
    /// </summary>
    private static object? EvaluateArithmetic(
        Tsdb? tsdb,
        BinaryExpression binary,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
    {
        var leftValue = EvaluateScalar(tsdb, binary.Left, columns, row, outerScope, memo);
        var rightValue = EvaluateScalar(tsdb, binary.Right, columns, row, outerScope, memo);
        return SqlScalarOperations.EvaluateArithmetic(binary.Operator, leftValue, rightValue);
    }

    private static object? EvaluateFunction(
        Tsdb? tsdb,
        FunctionCallExpression function,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope = null,
        SubqueryMemo? memo = null)
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
            var json = EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope, memo) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        if (string.Equals(function.Name, "lower", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
        {
            return EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope, memo)?.ToString()?.ToLowerInvariant();
        }

        if (string.Equals(function.Name, "upper", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 1)
        {
            return EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope, memo)?.ToString()?.ToUpperInvariant();
        }

        if (string.Equals(function.Name, "coalesce", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count > 0)
        {
            foreach (var argument in function.Arguments)
            {
                var value = EvaluateScalar(tsdb, argument, columns, row, outerScope);
                if (value is not null)
                    return value;
            }

            return null;
        }

        if (string.Equals(function.Name, "regexp_like", StringComparison.OrdinalIgnoreCase))
        {
            if (function.Arguments.Count is < 2 or > 3)
                throw new InvalidOperationException("函数 regexp_like 需要 2~3 个参数。");
            return RegexPatternMatcher.IsMatch(
                EvaluateScalar(tsdb, function.Arguments[0], columns, row, outerScope, memo),
                EvaluateScalar(tsdb, function.Arguments[1], columns, row, outerScope, memo),
                function.Arguments.Count == 3
                    ? EvaluateScalar(tsdb, function.Arguments[2], columns, row, outerScope, memo)
                    : null);
        }

        if (FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction))
        {
            var arguments = function.Arguments
                .Select(argument => EvaluateScalar(tsdb, argument, columns, row, outerScope, memo))
                .ToArray();
            return scalarFunction.Evaluate(arguments);
        }

        throw new InvalidOperationException($"关系表不支持标量函数 '{function.Name}'。");
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

        return ExecuteExistsMemoized(tsdb, exists.Select, columns, row, outerScope, memo);
    }

    /// <summary>
    /// 按子查询 AST 身份记忆非相关 EXISTS 的布尔结果；相关查询继续逐外层行执行。
    /// </summary>
    private static bool ExecuteExistsMemoized(
        Tsdb tsdb,
        SelectStatement subquery,
        IReadOnlyList<RelColumn> columns,
        IReadOnlyList<object?> row,
        RelationalScope? outerScope,
        SubqueryMemo? memo)
    {
        if (memo is not null && memo.TryGetCached(subquery, out var cachedRows))
        {
            memo.RecordCacheHit();
            return cachedRows.Rows.Count != 0;
        }
        if (memo is not null && memo.TryGetExistsCached(subquery, out bool cachedExists))
        {
            memo.RecordCacheHit();
            return cachedExists;
        }

        var activeMemo = memo ?? new SubqueryMemo(metrics: null);
        if (memo is null || memo.IsKnownCorrelated(subquery))
        {
            var innerScope = new RelationalScope(columns, row, outerScope);
            memo?.RecordExecution();
            return ExecuteExistsCore(tsdb, subquery, innerScope, activeMemo);
        }

        // 先绑定外层值再读取候选，保证索引 miss 时也能识别相关性，避免错误缓存 false。
        var probe = new CorrelationProbe();
        var probedScope = new RelationalScope(columns, row, outerScope, probe);
        memo.RecordExecution();
        bool result = ExecuteExistsCore(tsdb, subquery, probedScope, activeMemo);
        if (probe.Tripped)
            memo.MarkCorrelated(subquery);
        else
            memo.CacheNonCorrelatedExists(subquery, result);
        return result;
    }

    /// <summary>
    /// 优先执行单表 EXISTS 快速路径；无法证明等价时回退完整关系子查询执行器。
    /// </summary>
    private static bool ExecuteExistsCore(
        Tsdb tsdb,
        SelectStatement subquery,
        RelationalScope outerScope,
        SubqueryMemo memo)
    {
        if (TryExecuteSingleTableExists(tsdb, subquery, outerScope, memo, out bool result, out string fallbackReason))
            return result;

        memo.RecordExistsFallback(fallbackReason, subquery.Where is not null);
        return Execute(tsdb, subquery, outerScope, memo).Rows.Count != 0;
    }

    /// <summary>
    /// 对可证明等价的普通单表 EXISTS 复用 PK/二级索引候选规划，并在首个真值行停止。
    /// </summary>
    private static bool TryExecuteSingleTableExists(
        Tsdb tsdb,
        SelectStatement subquery,
        RelationalScope outerScope,
        SubqueryMemo memo,
        out bool result,
        out string fallbackReason)
    {
        result = false;
        if (!TryGetSingleTableExistsSchema(tsdb, subquery, out var schema, out fallbackReason))
            return false;
        if (!TryBindExistsWhere(subquery.Where, schema, subquery.TableAlias ?? subquery.Measurement, outerScope, out var boundWhere))
        {
            fallbackReason = "outer_reference_not_safely_bindable";
            return false;
        }

        SqlExecutor.ThrowIfCancellationRequested();
        var candidates = TableSqlExecutor.LoadExistsCandidateRows(
            tsdb.Tables.Open(schema.Name),
            schema,
            boundWhere);
        int examinedRows = 0;
        foreach (var candidate in candidates.Rows)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            examinedRows++;
            if (!TableSqlExecutor.EvaluateWhere(boundWhere, schema, candidate.Values))
                continue;

            memo.RecordExistsFastPath(candidates.Plan, candidates.Rows.Count, examinedRows, earlyExit: true);
            result = true;
            fallbackReason = string.Empty;
            return true;
        }

        memo.RecordExistsFastPath(candidates.Plan, candidates.Rows.Count, examinedRows, earlyExit: false);
        fallbackReason = string.Empty;
        return true;
    }

    /// <summary>
    /// 校验 EXISTS 子查询是否属于无副作用、无阻塞算子的普通单表安全形状。
    /// </summary>
    private static bool TryGetSingleTableExistsSchema(
        Tsdb tsdb,
        SelectStatement subquery,
        out TableSchema schema,
        out string fallbackReason)
    {
        schema = null!;
        if (subquery.FromSubquery is not null
            || subquery.TableValuedFunction is not null
            || subquery.JoinClauses.Count != 0
            || subquery.UnionStatements.Count != 0)
        {
            fallbackReason = "complex_source";
            return false;
        }
        if (subquery.GroupBy.Count != 0
            || subquery.Having is not null
            || subquery.Distinct)
        {
            fallbackReason = "aggregate_or_distinct";
            return false;
        }
        if (subquery.OrderByList.Count != 0 || subquery.Pagination is not null)
        {
            fallbackReason = "ordering_or_pagination";
            return false;
        }

        schema = tsdb.Tables.Catalog.TryGet(subquery.Measurement)!;
        if (schema is null)
        {
            fallbackReason = "source_is_not_table";
            return false;
        }

        string qualifier = subquery.TableAlias ?? subquery.Measurement;
        foreach (var projection in subquery.Projections)
        {
            if (!IsSafeExistsProjection(projection, schema, qualifier))
            {
                fallbackReason = "projection_requires_evaluation";
                return false;
            }
        }

        fallbackReason = string.Empty;
        return true;
    }

    /// <summary>
    /// EXISTS 可跳过常量、星号及有效内表列投影；其他表达式保留旧执行路径以维持异常语义。
    /// </summary>
    private static bool IsSafeExistsProjection(SelectItem projection, TableSchema schema, string qualifier)
    {
        if (projection.Expression is StarExpression)
            return projection.Alias is null;
        if (projection.Expression is LiteralExpression)
            return true;
        return projection.Expression is IdentifierExpression identifier
            && IsInnerTableIdentifier(identifier, schema, qualifier);
    }

    /// <summary>
    /// 把 WHERE 中解析到外层作用域的标识符绑定为运行时值，内表列保持原 AST。
    /// </summary>
    private static bool TryBindExistsWhere(
        SqlExpression? where,
        TableSchema schema,
        string qualifier,
        RelationalScope? outerScope,
        out SqlExpression? boundWhere)
    {
        if (where is null)
        {
            boundWhere = null;
            return true;
        }

        if (TryBindExistsExpression(where, schema, qualifier, outerScope, out var bound))
        {
            boundWhere = bound;
            return true;
        }

        boundWhere = null;
        return false;
    }

    /// <summary>
    /// 递归绑定 EXISTS 谓词；嵌套子查询和单表执行器不支持的节点返回 false 触发回退。
    /// </summary>
    private static bool TryBindExistsExpression(
        SqlExpression expression,
        TableSchema schema,
        string qualifier,
        RelationalScope? outerScope,
        out SqlExpression bound)
    {
        switch (expression)
        {
            case LiteralExpression or DurationLiteralExpression:
                bound = expression;
                return true;

            case IdentifierExpression identifier:
                if (TryGetInnerTableColumn(identifier, schema, qualifier, out var innerColumn, out bool ambiguous))
                {
                    bound = string.Equals(identifier.Name, innerColumn.Name, StringComparison.Ordinal)
                        ? identifier
                        : identifier with { Name = innerColumn.Name };
                    return true;
                }
                if (ambiguous)
                {
                    bound = expression;
                    return false;
                }
                if (outerScope is null || !TryResolveOuterValue(outerScope, identifier, out var outerValue))
                {
                    bound = expression;
                    return false;
                }
                bound = new MaterializedSubqueryValueExpression(outerValue);
                return true;

            case BinaryExpression binary:
                if (!TryBindExistsExpression(binary.Left, schema, qualifier, outerScope, out var left)
                    || !TryBindExistsExpression(binary.Right, schema, qualifier, outerScope, out var right))
                {
                    bound = expression;
                    return false;
                }
                bound = ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : binary with { Left = left, Right = right };
                return true;

            case UnaryExpression unary:
                if (!TryBindExistsExpression(unary.Operand, schema, qualifier, outerScope, out var operand))
                {
                    bound = expression;
                    return false;
                }
                bound = ReferenceEquals(operand, unary.Operand) ? unary : unary with { Operand = operand };
                return true;

            case IsNullExpression isNull:
                if (!TryBindExistsExpression(isNull.Operand, schema, qualifier, outerScope, out var nullOperand))
                {
                    bound = expression;
                    return false;
                }
                bound = ReferenceEquals(nullOperand, isNull.Operand)
                    ? isNull
                    : isNull with { Operand = nullOperand };
                return true;

            case InExpression { Subquery: null } inExpression:
                if (!TryBindExistsExpression(inExpression.Value, schema, qualifier, outerScope, out var inValue)
                    || !TryBindExistsExpressionList(
                        inExpression.Values,
                        schema,
                        qualifier,
                        outerScope,
                        out var inValues))
                {
                    bound = expression;
                    return false;
                }
                bound = ReferenceEquals(inValue, inExpression.Value) && ReferenceEquals(inValues, inExpression.Values)
                    ? inExpression
                    : inExpression with { Value = inValue, Values = inValues };
                return true;

            case FunctionCallExpression function:
                if (!TryBindExistsExpressionList(
                    function.Arguments,
                    schema,
                    qualifier,
                    outerScope,
                    out var arguments))
                {
                    bound = expression;
                    return false;
                }
                bound = ReferenceEquals(arguments, function.Arguments)
                    ? function
                    : function with { Arguments = arguments };
                return true;

            case CaseExpression caseExpression:
                return TryBindExistsCase(caseExpression, schema, qualifier, outerScope, out bound);

            default:
                bound = expression;
                return false;
        }
    }

    /// <summary>
    /// 绑定表达式列表，并仅在至少一项变化时分配副本。
    /// </summary>
    private static bool TryBindExistsExpressionList(
        IReadOnlyList<SqlExpression> expressions,
        TableSchema schema,
        string qualifier,
        RelationalScope? outerScope,
        out IReadOnlyList<SqlExpression> boundExpressions)
    {
        SqlExpression[]? copy = null;
        for (int i = 0; i < expressions.Count; i++)
        {
            if (!TryBindExistsExpression(expressions[i], schema, qualifier, outerScope, out var bound))
            {
                boundExpressions = expressions;
                return false;
            }
            if (!ReferenceEquals(bound, expressions[i]))
            {
                copy ??= expressions.ToArray();
                copy[i] = bound;
            }
        }

        boundExpressions = copy ?? expressions;
        return true;
    }

    /// <summary>
    /// 绑定 CASE 的条件、结果和 ELSE 分支，保持未变化节点的引用身份。
    /// </summary>
    private static bool TryBindExistsCase(
        CaseExpression expression,
        TableSchema schema,
        string qualifier,
        RelationalScope? outerScope,
        out SqlExpression bound)
    {
        CaseWhenClause[]? clauses = null;
        for (int i = 0; i < expression.WhenClauses.Count; i++)
        {
            var clause = expression.WhenClauses[i];
            if (!TryBindExistsExpression(clause.Condition, schema, qualifier, outerScope, out var condition)
                || !TryBindExistsExpression(clause.Result, schema, qualifier, outerScope, out var result))
            {
                bound = expression;
                return false;
            }
            if (!ReferenceEquals(condition, clause.Condition) || !ReferenceEquals(result, clause.Result))
            {
                clauses ??= expression.WhenClauses.ToArray();
                clauses[i] = clause with { Condition = condition, Result = result };
            }
        }

        SqlExpression? elseExpression = null;
        if (expression.Else is not null
            && !TryBindExistsExpression(expression.Else, schema, qualifier, outerScope, out elseExpression))
        {
            bound = expression;
            return false;
        }

        bound = clauses is null && ReferenceEquals(elseExpression, expression.Else)
            ? expression
            : expression with { WhenClauses = clauses ?? expression.WhenClauses, Else = elseExpression };
        return true;
    }

    /// <summary>
    /// 判断标识符是否由当前内表解析；未限定同名列始终优先绑定内层。
    /// </summary>
    private static bool IsInnerTableIdentifier(
        IdentifierExpression identifier,
        TableSchema schema,
        string qualifier)
        => TryGetInnerTableColumn(identifier, schema, qualifier, out _, out _);

    /// <summary>
    /// 按关系执行器的大小写不敏感规则解析唯一内表列，并显式报告多列歧义。
    /// </summary>
    private static bool TryGetInnerTableColumn(
        IdentifierExpression identifier,
        TableSchema schema,
        string qualifier,
        out TableColumn column,
        out bool ambiguous)
    {
        column = null!;
        ambiguous = false;
        if (identifier.Qualifier is not null && !QualifierEquals(identifier.Qualifier, qualifier))
            return false;

        TableColumn? match = null;
        foreach (var schemaColumn in schema.Columns)
        {
            if (!NameEquals(schemaColumn.Name, identifier.Name))
                continue;
            if (match is not null)
            {
                ambiguous = true;
                return false;
            }
            match = schemaColumn;
        }

        if (match is null)
            return false;

        column = match;
        return true;
    }

    /// <summary>
    /// 沿外层作用域解析唯一列值，并置位从起点到命中层的相关性探针。
    /// </summary>
    private static bool TryResolveOuterValue(
        RelationalScope outerScope,
        IdentifierExpression identifier,
        out object? value)
    {
        for (RelationalScope? scope = outerScope; scope is not null; scope = scope.Parent)
        {
            int? hit = TryResolveInScope(scope, identifier);
            if (!hit.HasValue)
                continue;

            for (RelationalScope? probeScope = outerScope;
                 probeScope is not null;
                 probeScope = probeScope.Parent)
            {
                probeScope.Probe?.Trip();
                if (ReferenceEquals(probeScope, scope))
                    break;
            }

            value = scope.Row[hit.Value];
            return true;
        }

        value = null;
        return false;
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

    private static SelectExecutionResult ApplyOrderByAndPagination(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<object?>> rows,
        IReadOnlyList<OrderBySpec> orderBy,
        PaginationSpec? pagination)
    {
        var materializedColumns = columns as string[] ?? columns.ToArray();
        var sortItems = orderBy.Select(order =>
        {
            if (order.Expression is not IdentifierExpression id)
                throw new InvalidOperationException("关系型 ORDER BY 当前仅支持结果列名。");
            string qualified = id.Qualifier is null ? id.Name : $"{id.Qualifier}.{id.Name}";
            int columnIndex = FindResultColumn(materializedColumns, qualified);
            if (columnIndex < 0)
                columnIndex = FindResultColumn(materializedColumns, id.Name);
            if (columnIndex < 0)
                throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{qualified}'。");
            return (ColumnIndex: columnIndex, order.Direction);
        }).ToArray();
        var comparer = new ResultRowSortComparer(sortItems);
        IReadOnlyList<IReadOnlyList<object?>> selected = TopN.OrderByThenPaginate(
            rows,
            comparer,
            pagination?.Offset ?? 0,
            pagination?.Fetch);
        return new SelectExecutionResult(materializedColumns, selected);
    }

    private static Relation ApplyRelationOrderByAndPagination(
        Tsdb tsdb,
        Relation relation,
        IReadOnlyList<OrderBySpec> orderBy,
        PaginationSpec? pagination,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        if (orderBy.Count == 0)
            return relation;

        IEnumerable<RelationSortRow> candidates = relation.Rows
            .Select(row => new RelationSortRow(
                row,
                orderBy
                    .Select(order => EvaluateScalar(tsdb, order.Expression, relation.Columns, row, outerScope, memo))
                    .ToArray()));
        var comparer = new RelationSortComparer(orderBy.Select(static order => order.Direction).ToArray());
        RelationSortRow[] selected = TopN.OrderByThenPaginate(
            candidates,
            comparer,
            pagination?.Offset ?? 0,
            pagination?.Fetch);
        IEnumerable<object?[]> rows = selected.Select(static row => row.Row);

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

    private static SelectExecutionResult ApplyPagination(
        IReadOnlyList<string> columns,
        IEnumerable<IReadOnlyList<object?>> rows,
        PaginationSpec? pagination)
    {
        IEnumerable<IReadOnlyList<object?>> selected = rows;
        if (pagination is not null)
        {
            selected = selected.Skip(pagination.Offset);
            if (pagination.Fetch is int fetch)
                selected = selected.Take(fetch);
        }
        return new SelectExecutionResult(columns, selected.ToArray());
    }

    private static bool ContainsAggregate(IReadOnlyList<SelectItem> items)
        => items.Any(static item => ContainsAggregate(item.Expression));

    /// <summary>
    /// 递归判断标量表达式树中是否包含聚合函数调用。
    /// </summary>
    private static bool ContainsAggregate(SqlExpression expression)
        => expression switch
        {
            FunctionCallExpression function when IsAggregateFunction(function.Name) => true,
            FunctionCallExpression function => function.Arguments.Any(ContainsAggregate),
            UnaryExpression unary => ContainsAggregate(unary.Operand),
            BinaryExpression binary => ContainsAggregate(binary.Left) || ContainsAggregate(binary.Right),
            CaseExpression caseExpression => caseExpression.WhenClauses.Any(when =>
                    ContainsAggregate(when.Condition) || ContainsAggregate(when.Result))
                || (caseExpression.Else is not null && ContainsAggregate(caseExpression.Else)),
            IsNullExpression isNull => ContainsAggregate(isNull.Operand),
            InExpression inExpression => ContainsAggregate(inExpression.Value)
                || inExpression.Values.Any(ContainsAggregate),
            _ => false,
        };

    /// <summary>
    /// 递归枚举关系投影表达式中的聚合调用，供复合聚合表达式预先确定返回数值类型。
    /// </summary>
    private static IEnumerable<FunctionCallExpression> EnumerateAggregateCalls(SqlExpression expression)
    {
        switch (expression)
        {
            case FunctionCallExpression function when IsAggregateFunction(function.Name):
                yield return function;
                yield break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    foreach (var aggregate in EnumerateAggregateCalls(argument))
                        yield return aggregate;
                yield break;
            case UnaryExpression unary:
                foreach (var aggregate in EnumerateAggregateCalls(unary.Operand))
                    yield return aggregate;
                yield break;
            case BinaryExpression binary:
                foreach (var aggregate in EnumerateAggregateCalls(binary.Left))
                    yield return aggregate;
                foreach (var aggregate in EnumerateAggregateCalls(binary.Right))
                    yield return aggregate;
                yield break;
            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    foreach (var aggregate in EnumerateAggregateCalls(clause.Condition))
                        yield return aggregate;
                    foreach (var aggregate in EnumerateAggregateCalls(clause.Result))
                        yield return aggregate;
                }
                if (caseExpression.Else is not null)
                    foreach (var aggregate in EnumerateAggregateCalls(caseExpression.Else))
                        yield return aggregate;
                yield break;
            case IsNullExpression isNull:
                foreach (var aggregate in EnumerateAggregateCalls(isNull.Operand))
                    yield return aggregate;
                yield break;
            case InExpression inExpression:
                foreach (var aggregate in EnumerateAggregateCalls(inExpression.Value))
                    yield return aggregate;
                foreach (var value in inExpression.Values)
                    foreach (var aggregate in EnumerateAggregateCalls(value))
                        yield return aggregate;
                yield break;
        }
    }

    private static bool ContainsSubquery(SelectStatement statement)
    {
        foreach (var item in statement.Projections)
            if (ContainsSubquery(item.Expression))
                return true;
        if (statement.Where is not null && ContainsSubquery(statement.Where))
            return true;
        if (statement.GroupBy.Any(ContainsSubquery))
            return true;
        if (statement.Having is not null && ContainsSubquery(statement.Having))
            return true;
        if (statement.OrderByList.Any(order => ContainsSubquery(order.Expression)))
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
            IsNullExpression isNull => ContainsSubquery(isNull.Operand),
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
        => SqlScalarComparer.ValuesEqual(left, right);

    private static int? CompareScalar(object? left, object? right)
        => SqlScalarComparer.Compare(left, right);

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

    private sealed record Relation(IReadOnlyList<RelColumn> Columns, IEnumerable<object?[]> Rows);

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

/// <summary>
/// 单表 EXISTS 的 EXPLAIN 描述，字段直接对应运行时快速路径证据。
/// </summary>
/// <param name="Measurement">内层关系表名。</param>
/// <param name="AccessPath">运行时将使用的访问路径。</param>
/// <param name="IndexName">命中的索引名。</param>
/// <param name="EstimatedCandidateRows">基于当前行数上界的候选估算。</param>
/// <param name="EarlyExit">候选复检是否启用首个真值行早停；不表示存储层流式读取。</param>
/// <param name="HasResidualPredicate">是否仍需完整谓词复检。</param>
/// <param name="FallbackReason">索引或快速路径回退原因。</param>
internal sealed record RelationalExistsExplainPlan(
    string Measurement,
    string AccessPath,
    string? IndexName,
    long EstimatedCandidateRows,
    bool EarlyExit,
    bool HasResidualPredicate,
    string? FallbackReason,
    double? EstimatedRowWidth = null,
    long? EstimatedLogicalReads = null,
    double? EstimatedCost = null,
    string? EstimateSource = null,
    long? StatisticsSequence = null,
    long? StatisticsFreshnessMilliseconds = null,
    string? CandidatePlans = null);

/// <summary>
/// 关系 SELECT 子查询记忆化的内部执行指标，仅用于回归测试和性能基准。
/// </summary>
internal sealed class RelationalSelectExecutionMetrics
{
    /// <summary>未命中缓存、实际进入子查询执行器的次数。</summary>
    public int SubqueryExecutionCount { get; private set; }

    /// <summary>非相关子查询结果的缓存命中次数。</summary>
    public int SubqueryCacheHitCount { get; private set; }

    /// <summary>成功进入单表 EXISTS 快速路径的执行次数。</summary>
    public int ExistsFastPathExecutionCount { get; private set; }

    /// <summary>EXISTS 快速路径实际复检的候选行总数。</summary>
    public long ExistsRowsExamined { get; private set; }

    /// <summary>EXISTS 在首个真值候选处提前停止的次数。</summary>
    public int ExistsEarlyExitCount { get; private set; }

    /// <summary>因查询形状不安全而回退完整关系执行器的次数。</summary>
    public int ExistsFallbackExecutionCount { get; private set; }

    /// <summary>最近一次 EXISTS 快速路径实际使用的访问路径。</summary>
    public string? LastExistsAccessPath { get; private set; }

    /// <summary>最近一次 EXISTS 快速路径实际使用的索引名。</summary>
    public string? LastExistsIndexName { get; private set; }

    /// <summary>最近一次 EXISTS 访问是否仍需执行残余谓词。</summary>
    public bool? LastExistsHasResidualPredicate { get; private set; }

    /// <summary>最近一次 EXISTS 回退完整关系执行器的稳定原因。</summary>
    public string? LastExistsFallbackReason { get; private set; }

    /// <summary>实际收到单表 WHERE 谓词的关系输入数。</summary>
    public int InputPredicatePushdownCount { get; private set; }

    /// <summary>实际裁剪了至少一列的关系输入数。</summary>
    public int InputProjectionPushdownCount { get; private set; }

    /// <summary>实际收到安全 LIMIT 窗口的关系输入数。</summary>
    public int InputLimitPushdownCount { get; private set; }

    /// <summary>关系输入访问路径返回的候选行总数。</summary>
    public long InputCandidateRows { get; private set; }

    /// <summary>关系输入谓词复检及安全 LIMIT 后保留的行总数。</summary>
    public long InputRetainedRows { get; private set; }

    /// <summary>关系输入裁剪前的列数总和。</summary>
    public long InputSourceColumns { get; private set; }

    /// <summary>关系输入裁剪后的列数总和。</summary>
    public long InputProjectedColumns { get; private set; }

    /// <summary>记录一次实际子查询执行。</summary>
    internal void RecordSubqueryExecution() => SubqueryExecutionCount++;

    /// <summary>记录一次非相关子查询缓存命中。</summary>
    internal void RecordSubqueryCacheHit() => SubqueryCacheHitCount++;

    /// <summary>记录一次 EXISTS 快速路径及其候选检查证据。</summary>
    internal void RecordExistsFastPath(TableExistsAccessPlan plan, int examinedRows, bool earlyExit)
    {
        ExistsFastPathExecutionCount++;
        ExistsRowsExamined += examinedRows;
        if (earlyExit)
            ExistsEarlyExitCount++;
        LastExistsAccessPath = plan.AccessPath;
        LastExistsIndexName = plan.IndexName;
        LastExistsHasResidualPredicate = plan.HasResidualPredicate;
        LastExistsFallbackReason = plan.FallbackReason;
    }

    /// <summary>记录一次无法证明等价的 EXISTS 回退。</summary>
    internal void RecordExistsFallback(string reason, bool hasResidualPredicate)
    {
        ExistsFallbackExecutionCount++;
        LastExistsAccessPath = "relational_fallback";
        LastExistsIndexName = null;
        LastExistsHasResidualPredicate = hasResidualPredicate;
        LastExistsFallbackReason = reason;
    }

    /// <summary>累加一次关系输入下推的执行证据。</summary>
    internal void RecordRelationInput(
        bool predicatePushed,
        bool limitPushed,
        int sourceColumns,
        int projectedColumns,
        int candidateRows,
        int retainedRows)
    {
        if (predicatePushed)
            InputPredicatePushdownCount++;
        if (projectedColumns < sourceColumns)
            InputProjectionPushdownCount++;
        if (limitPushed)
            InputLimitPushdownCount++;
        InputCandidateRows += candidateRows;
        InputRetainedRows += retainedRows;
        InputSourceColumns += sourceColumns;
        InputProjectedColumns += projectedColumns;
    }
}
