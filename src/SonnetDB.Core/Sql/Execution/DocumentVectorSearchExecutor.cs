using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Routines;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 文档集合纯向量搜索表值函数执行器。
/// </summary>
internal static class DocumentVectorSearchExecutor
{
    private const string FunctionName = "vector_search";
    private const int DefaultK = 20;
    private static readonly TimeSpan BudgetedSearchTimeout = TimeSpan.FromMinutes(5);
    private static readonly AsyncLocal<Action?> _distanceComputedTestHook = new();
    private static readonly AsyncLocal<Action?> _sortComparisonTestHook = new();

    internal static Action? DistanceComputedTestHook
    {
        get => _distanceComputedTestHook.Value;
        set => _distanceComputedTestHook.Value = value;
    }

    internal static Action? SortComparisonTestHook
    {
        get => _sortComparisonTestHook.Value;
        set => _sortComparisonTestHook.Value = value;
    }

    public static bool IsVectorSearch(SelectStatement statement)
        => statement.TableValuedFunction is { Name: var name }
            && string.Equals(name, FunctionName, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateMaterializationSupported(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        if (!IsVectorSearch(statement) || statement.OrderByList.Count > 1
            || statement.GroupBy.Count != 0 || statement.Having is not null || statement.Distinct
            || statement.IsRecursive || statement.JoinClauses.Count != 0 || statement.FromSubquery is not null
            || statement.CommonTableExpressions.Count != 0 || statement.SetOperationList.Count != 0
            || statement.GraphTable is not null)
        {
            throw new NotSupportedException(
                "vector_search SQL 物化预算仅支持直接单源、元数据 WHERE、标量投影、距离升序和 LIMIT/OFFSET；聚合、窗口、JOIN、去重及嵌套查询尚不支持。");
        }

        var schema = tsdb.Documents.Catalog.TryGet(statement.Measurement)
            ?? throw new NotSupportedException("vector_search SQL 物化预算要求 source 为文档集合。");
        if (tsdb.Functions.TryGetTableValuedFunction(FunctionName, out _))
            throw new NotSupportedException("vector_search SQL 物化预算不支持用户表值函数回调。");
        _ = BindOptions(schema, statement.TableValuedFunction!);
        Projection[] projections = BuildProjections(statement.Projections);
        int expressionNodes = 1024;
        foreach (SelectItem projection in statement.Projections)
            ValidateBudgetedScalar(tsdb, projection.Expression, ref expressionNodes, allowStar: true);
        ValidateBudgetedScalar(tsdb, statement.Where, ref expressionNodes, allowBoolean: true);
        int predicateNodes = 128;
        if (statement.Where is { } where && !IsMetadataExpression(where, ref predicateNodes))
            throw new NotSupportedException("vector_search SQL 物化预算尚不支持距离、分数或一般函数残余 WHERE。");
        if (statement.OrderByList.Count != 0)
        {
            OrderBySpec orderBy = statement.OrderByList[0];
            ValidateBudgetedScalar(tsdb, orderBy.Expression, ref expressionNodes);
            if (!IsDefaultDistanceOrder(orderBy)
                || !IsBudgetedDistanceExpression(ResolveOrderByExpression(orderBy.Expression, projections)))
                throw new NotSupportedException("vector_search SQL 物化预算仅支持默认距离升序；自定义排序尚不支持。");
        }
        if (statement.Pagination is { } pagination)
        {
            _ = pagination.Offset;
            _ = pagination.Fetch;
        }
    }

    private static bool IsBudgetedDistanceExpression(SqlExpression expression)
        => expression switch
        {
            IdentifierExpression { Qualifier: null, Name: var name } =>
                name.Equals("vector_distance", StringComparison.OrdinalIgnoreCase),
            FunctionCallExpression { Arguments.Count: 0, Name: var name } =>
                name.Equals("vector_distance", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static void ValidateBudgetedScalar(
        Tsdb tsdb, SqlExpression? expression, ref int remainingNodes, bool allowStar = false,
        bool allowBoolean = false, int depth = 0)
    {
        if (--remainingNodes < 0 || depth > 64)
            throw new NotSupportedException("vector_search SQL 物化预算的标量表达式超过 1024 个节点或 64 层上限。");
        switch (expression)
        {
            case null or LiteralExpression:
            case StarExpression when allowStar:
            case IdentifierExpression { Qualifier: null }:
                return;
            case CastExpression cast when cast.TargetType is not (SqlDataType.Vector or SqlDataType.GeoPoint):
                ValidateBudgetedScalar(tsdb, cast.Operand, ref remainingNodes, depth: depth + 1);
                return;
            case UnaryExpression unary when unary.Operator == SqlUnaryOperator.Negate
                || (allowBoolean && unary.Operator == SqlUnaryOperator.Not):
                ValidateBudgetedScalar(tsdb, unary.Operand, ref remainingNodes,
                    allowBoolean: unary.Operator == SqlUnaryOperator.Not, depth: depth + 1);
                return;
            case BinaryExpression binary when IsArithmeticOperator(binary.Operator)
                || (allowBoolean && (IsComparisonOperator(binary.Operator)
                    || binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or)):
                bool booleanOperands = binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or;
                ValidateBudgetedScalar(tsdb, binary.Left, ref remainingNodes,
                    allowBoolean: booleanOperands, depth: depth + 1);
                ValidateBudgetedScalar(tsdb, binary.Right, ref remainingNodes,
                    allowBoolean: booleanOperands, depth: depth + 1);
                return;
            case IsNullExpression isNull when allowBoolean:
                ValidateBudgetedScalar(tsdb, isNull.Operand, ref remainingNodes, depth: depth + 1);
                return;
            case FunctionCallExpression function:
                if (function.Over is not null || function.IsStar || function.IsDistinct
                    || tsdb.Functions.TryGetScalar(function.Name, out _)
                    || tsdb.Functions.TryGetAggregate(function.Name, out _)
                    || tsdb.Functions.TryGetWindow(function.Name, out _))
                    throw new NotSupportedException("vector_search SQL 物化预算不支持窗口、聚合或用户自定义函数回调。");
                if (function.Name.Equals("vector_distance", StringComparison.OrdinalIgnoreCase)
                    || function.Name.Equals("vector_score", StringComparison.OrdinalIgnoreCase))
                {
                    if (function.Arguments.Count != 0)
                        throw new NotSupportedException("vector_search SQL 物化预算要求距离与分数函数不带参数。");
                }
                else if (function.Name.Equals("json_value", StringComparison.OrdinalIgnoreCase))
                {
                    if (function.Arguments.Count != 2 || function.Arguments[1] is not
                        LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { } path })
                        throw new NotSupportedException("vector_search SQL 物化预算要求 json_value 使用固定 JSON path。");
                    _ = JsonPath.Parse(path);
                }
                else if (!FunctionRegistry.TryGetScalar(function.Name, out _))
                    throw new NotSupportedException($"vector_search SQL 物化预算不支持函数 '{function.Name}'。");
                SqlTableFunctionMaterialization.ValidateScalarArgumentCount(function);
                foreach (SqlExpression argument in function.Arguments)
                    ValidateBudgetedScalar(tsdb, argument, ref remainingNodes, depth: depth + 1);
                return;
            default:
                throw new NotSupportedException("vector_search SQL 物化预算仅支持已知只读标量表达式。");
        }
    }

    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (SqlRowRetentionBudget.HasExecutionBudget)
        {
            ValidateMaterializationSupported(tsdb, statement);
            return ExecuteBudgeted(tsdb, statement);
        }

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("vector_search 只能出现在 FROM 表值函数中。");
        if (statement.GroupBy.Count != 0)
            throw new InvalidOperationException("vector_search(...) 暂不支持 GROUP BY。");

        var schema = tsdb.Documents.Catalog.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"vector_search(...) 的 source '{statement.Measurement}' 必须是 document collection。");

        var options = BindOptions(schema, call);
        var store = tsdb.Documents.Open(schema.Name);
        var projections = BuildProjections(statement.Projections);

        var indexRows = TryScoreRowsFromIndex(store, schema, statement, options);
        int predicateNodes = 128;
        SqlExpression? metadataFilter = indexRows is null && statement.Where is { } where
            && IsMetadataExpression(where, ref predicateNodes)
                ? where
                : null;
        var rows = indexRows ?? (CanUseBoundedScan(statement, metadataFilter)
            ? ScoreRowsBounded(store, options, metadataFilter)
            : ScoreRows(store, options, metadataFilter));
        rows = ApplyWhere(rows, metadataFilter is null ? statement.Where : null);
        rows = ApplyOrderBy(rows, statement.OrderBy, projections);
        rows = rows.Take(options.K).ToList();
        rows = ApplyPagination(rows, statement.Pagination);

        var resultRows = new List<IReadOnlyList<object?>>(rows.Count);
        foreach (var row in rows)
        {
            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateScalar(projections[i].Expression, row);
            resultRows.Add(output);
        }

        return new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            resultRows);
    }

    private static SelectExecutionResult ExecuteBudgeted(Tsdb tsdb, SelectStatement statement)
    {
        long startedAt = Stopwatch.GetTimestamp();
        ThrowIfBudgetedCancelled(startedAt);
        Projection[] projections = BuildProjections(statement.Projections);
        string[] columns = projections.Select(static projection => projection.ColumnName).ToArray();
        var resultRows = new List<IReadOnlyList<object?>>();
        int? fetch = statement.Pagination?.Fetch;
        if (fetch == 0)
            return new SelectExecutionResult(columns, resultRows);

        var schema = tsdb.Documents.Catalog.TryGet(statement.Measurement)!;
        VectorSearchOptions options = BindOptions(schema, statement.TableValuedFunction!);
        DocumentCollectionStore store = tsdb.Documents.Open(schema.Name);
        long visibleAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        IEnumerable<VectorSearchRow> candidates = (IEnumerable<VectorSearchRow>?)TryScoreBudgetedIndex(
            store, schema, statement, options, startedAt, visibleAtUnixMs)
            ?? ScoreBudgetedScan(store, options, statement.Where, startedAt, visibleAtUnixMs);

        // 候选队列之外的排序快照也累计计费；被 OFFSET 排除的候选已经实际保留过。
        var rows = new List<VectorSearchRow>();
        foreach (VectorSearchRow row in candidates)
        {
            ThrowIfBudgetedCancelled(startedAt);
            RetainCandidate(row);
            rows.Add(row);
        }
        try
        {
            Action? sortComparison = SortComparisonTestHook;
            rows.Sort((left, right) =>
            {
                sortComparison?.Invoke();
                ThrowIfBudgetedCancelled(startedAt);
                return CompareDistanceThenId(left, right);
            });
        }
        catch (InvalidOperationException error) when (error.InnerException is OperationCanceledException or RoutineExecutionException)
        {
            // List.Sort 包装比较器异常；恢复根取消或固定墙钟截止的原异常合同。
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }

        int offset = statement.Pagination?.Offset ?? 0;
        for (int rowIndex = offset; rowIndex < rows.Count && (!fetch.HasValue || resultRows.Count < fetch.Value); rowIndex++)
        {
            ThrowIfBudgetedCancelled(startedAt);
            var output = new object?[projections.Length];
            for (int projectionIndex = 0; projectionIndex < projections.Length; projectionIndex++)
            {
                ThrowIfBudgetedCancelled(startedAt);
                output[projectionIndex] = EvaluateScalar(projections[projectionIndex].Expression, rows[rowIndex]);
            }
            SqlRowRetentionBudget.RetainForExecution(output);
            resultRows.Add(output);
        }
        ThrowIfBudgetedCancelled(startedAt);
        return new SelectExecutionResult(columns, resultRows);
    }

    private static IReadOnlyList<VectorSearchRow>? TryScoreBudgetedIndex(
        DocumentCollectionStore store, DocumentCollectionSchema schema, SelectStatement statement,
        VectorSearchOptions options, long startedAt, long visibleAtUnixMs)
    {
        DocumentVectorIndex? index = TryResolveMatchingIndex(schema, statement, options);
        if (index is null || schema.Indexes.Any(static definition => definition.IsTtl))
            return null;
        SqlExecutionTelemetry.RecordAccessPath("document_vector_index_budgeted");
        ThrowIfBudgetedCancelled(startedAt);
        // ANN 的内部图、页解码与搜索工作集属于存储层；返回 hit 转为 SQL 候选时逐行准入。
        IReadOnlyList<(string Id, double Distance)> hits = store.SearchVector(index, options.QueryVector, options.K);
        var rows = new List<VectorSearchRow>();
        foreach (var (id, distance) in hits)
        {
            ThrowIfBudgetedCancelled(startedAt);
            SqlRowRetentionBudget.RetainForExecution([id, distance]);
            IReadOnlyList<DocumentRow> page = store.ReadForSqlMaterialization(
                afterId: null, visibleAtUnixMs, out bool expired, id);
            if (page.Count == 0)
                continue;
            SqlExecutionTelemetry.RecordCandidateRows(1);
            if (expired)
                continue;
            SqlExecutionTelemetry.RecordExaminedRows(1);
            var row = new VectorSearchRow(page[0], distance, DistanceToScore(options.Metric, distance));
            RetainCandidate(row);
            rows.Add(row);
        }
        return rows;
    }

    private static IEnumerable<VectorSearchRow> ScoreBudgetedScan(
        DocumentCollectionStore store, VectorSearchOptions options, SqlExpression? metadataFilter,
        long startedAt, long visibleAtUnixMs)
    {
        SqlExecutionTelemetry.RecordAccessPath("document_vector_scan_budgeted");
        Action? distanceComputed = DistanceComputedTestHook;
        var candidates = new PriorityQueue<VectorSearchRow, TopKPriority>(TopKPriorityComparer.Instance);
        string? afterId = null;
        // 严格递增 ID、Int32 最大读取次数、五分钟墙钟和根取消共同限定扫描。
        for (int read = 0; read < int.MaxValue; read++)
        {
            ThrowIfBudgetedCancelled(startedAt);
            IReadOnlyList<DocumentRow> page = store.ReadForSqlMaterialization(afterId, visibleAtUnixMs, out bool expired);
            if (page.Count == 0)
                return candidates.UnorderedItems.Select(static item => item.Element);
            DocumentRow document = page[0];
            afterId = document.Id;
            SqlExecutionTelemetry.RecordCandidateRows(1);
            if (expired)
                continue;
            SqlExecutionTelemetry.RecordExaminedRows(1);
            if (!DocumentVectorReader.TryReadVector(document.Json, options.VectorPath, out var vector))
                continue;
            if (vector.Length != options.QueryVector.Length)
                throw new InvalidOperationException(
                    $"vector_search 文档 '{document.Id}' 的向量维度 {vector.Length} 与查询向量维度 {options.QueryVector.Length} 不一致。");
            if (metadataFilter is not null && !EvaluateBoolean(metadataFilter, new VectorSearchRow(document, 0d, 0d)))
                continue;
            double distance = VectorDistance.Compute(options.Metric, options.QueryVector, vector);
            distanceComputed?.Invoke();
            ThrowIfBudgetedCancelled(startedAt);
            var row = new VectorSearchRow(document, distance, DistanceToScore(options.Metric, distance));
            if (candidates.Count >= options.K && CompareDistanceThenId(row, candidates.Peek()) >= 0)
                continue;
            RetainCandidate(row);
            if (candidates.Count >= options.K)
                candidates.Dequeue();
            candidates.Enqueue(row, new TopKPriority(distance, document.Id));
        }
        throw new InvalidOperationException("vector_search SQL 扫描候选文档数量超过 Int32 上限。");
    }

    private static void RetainCandidate(VectorSearchRow row)
        => SqlRowRetentionBudget.RetainForExecution(
            [row.Document.Id, row.Document.Json, row.VectorDistance, row.VectorScore]);

    private static void ThrowIfBudgetedCancelled(long startedAt)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) >= BudgetedSearchTimeout)
            throw new RoutineExecutionException(RoutineErrorCodes.Cancelled,
                "vector_search SQL 物化预算查询已超过五分钟执行上限。",
                new TimeoutException("vector_search SQL 物化预算查询已超过五分钟执行上限。"));
    }

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(
        Tsdb tsdb,
        SelectStatement statement,
        DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("vector_search 只能出现在 FROM 表值函数中。");
        var options = BindOptions(schema, call);
        var index = TryResolveMatchingIndex(schema, statement, options);
        if (SqlRowRetentionBudget.HasExecutionBudget)
        {
            ValidateMaterializationSupported(tsdb, statement);
            // 显式预算 EXPLAIN 不调用 Count / ANN 索引统计，避免隐式全集合 TTL 清理。
            return index is null || schema.Indexes.Any(static definition => definition.IsTtl)
                ? ("document_vector_scan_budgeted", options.VectorPath.Text, 0)
                : ("document_vector_index_budgeted", index.Name, 0);
        }
        var store = tsdb.Documents.Open(schema.Name);
        return index is null
            ? ("document_vector_scan", options.VectorPath.Text, store.Count())
            : ("document_vector_index", index.Name, store.GetVectorIndexedCount(index));
    }

    /// <summary>
    /// 当集合存在与查询 (path, metric, dim) 匹配的向量索引、且查询无 WHERE、无自定义 ORDER BY（默认按距离升序）时，
    /// 走持久 ANN 索引替代全表暴力扫。有 WHERE 时不能走 ANN——暴力扫先按谓词过滤再取 Top-K，ANN 先取 Top-K
    /// 会漏掉被过滤器排除但距离更近的行，语义不等价。返回 null 表示回落暴力扫。
    /// </summary>
    private static IReadOnlyList<VectorSearchRow>? TryScoreRowsFromIndex(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        SelectStatement statement,
        VectorSearchOptions options)
    {
        var index = TryResolveMatchingIndex(schema, statement, options);
        if (index is null)
            return null;

        var hits = store.SearchVector(index, options.QueryVector, options.K);
        var rows = new List<VectorSearchRow>(hits.Count);
        foreach (var (id, distance) in hits)
        {
            var document = store.Get(id);
            if (document is null)
                continue;

            double score = DistanceToScore(options.Metric, distance);
            rows.Add(new VectorSearchRow(document, distance, score));
        }

        return rows;
    }

    private static bool CanUseBoundedScan(SelectStatement statement, SqlExpression? metadataFilter)
        => (statement.OrderBy is null || IsDefaultDistanceOrder(statement.OrderBy))
            && (statement.Where is null || metadataFilter is not null);

    private static DocumentVectorIndex? TryResolveMatchingIndex(
        DocumentCollectionSchema schema,
        SelectStatement statement,
        VectorSearchOptions options)
    {
        if (statement.Where is not null)
            return null;
        if (statement.OrderBy is not null && !IsDefaultDistanceOrder(statement.OrderBy))
            return null;

        foreach (var index in schema.VectorIndexes)
        {
            if (string.Equals(index.Path, options.VectorPath.Text, StringComparison.Ordinal)
                && index.Metric == options.Metric
                && index.Dimensions == options.QueryVector.Length)
            {
                return index;
            }
        }

        return null;
    }

    private static bool IsDefaultDistanceOrder(OrderBySpec orderBy)
        => orderBy.Direction != SortDirection.Descending
            && orderBy.Expression switch
            {
                IdentifierExpression { Qualifier: null, Name: var name } =>
                    string.Equals(name, "vector_distance", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "distance", StringComparison.OrdinalIgnoreCase),
                FunctionCallExpression { Name: var fn, Arguments.Count: 0 } =>
                    string.Equals(fn, "vector_distance", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

    private static VectorSearchOptions BindOptions(
        DocumentCollectionSchema schema,
        FunctionCallExpression call)
    {
        if (call.IsStar)
            throw new InvalidOperationException("vector_search(*) 非法。");

        var args = BindArguments(call);
        var source = RequireIdentifierArgument(args, "source");
        if (!string.Equals(source, schema.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"vector_search source '{source}' 与解析出的 document collection '{schema.Name}' 不一致。");
        }

        var vector = RequireVectorArgument(args, "vector", "query_vector");
        var vectorField = NormalizeJsonPath(GetFieldArgument(args, "$.embedding", "vector_field", "embedding_field"));
        var k = GetPositiveIntArgument(args, DefaultK, "k", "top_k");
        var metric = GetMetricArgument(args);
        return new VectorSearchOptions(JsonPath.Parse(vectorField), vector, k, metric);
    }

    private static IReadOnlyList<VectorSearchRow> ScoreRows(
        DocumentCollectionStore store,
        VectorSearchOptions options,
        SqlExpression? metadataFilter)
    {
        CancellationToken cancellationToken = SqlQueryResources.Current?.CancellationToken ?? default;
        cancellationToken.ThrowIfCancellationRequested();
        Action? distanceComputed = DistanceComputedTestHook;
        var rows = new List<VectorSearchRow>();
        foreach (var documentRow in store.Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DocumentVectorReader.TryReadVector(documentRow.Json, options.VectorPath, out var vector))
                continue;
            if (vector.Length != options.QueryVector.Length)
            {
                throw new InvalidOperationException(
                    $"vector_search 文档 '{documentRow.Id}' 的向量维度 {vector.Length} 与查询向量维度 {options.QueryVector.Length} 不一致。");
            }

            // Keep vector validation, NULL behavior and short-circuit order unchanged.
            if (metadataFilter is not null
                && !EvaluateBoolean(metadataFilter, new VectorSearchRow(documentRow, 0d, 0d)))
            {
                continue;
            }

            double distance = VectorDistance.Compute(options.Metric, options.QueryVector, vector);
            distanceComputed?.Invoke();
            double score = DistanceToScore(options.Metric, distance);
            rows.Add(new VectorSearchRow(documentRow, distance, score));
        }

        return rows;
    }

    /// <summary>
    /// 在精确扫描路径上只保留当前最优 K 个候选，避免为默认距离排序物化整个集合。
    /// 该路径只接受没有 WHERE 或可在计算距离前求值的元数据谓词；涉及距离/分数的残余谓词
    /// 仍使用 <see cref="ScoreRows"/> 以保持其完整候选集语义。
    /// </summary>
    private static IReadOnlyList<VectorSearchRow> ScoreRowsBounded(
        DocumentCollectionStore store,
        VectorSearchOptions options,
        SqlExpression? metadataFilter)
    {
        CancellationToken cancellationToken = SqlQueryResources.Current?.CancellationToken ?? default;
        cancellationToken.ThrowIfCancellationRequested();
        Action? distanceComputed = DistanceComputedTestHook;
        var candidates = new PriorityQueue<VectorSearchRow, TopKPriority>(TopKPriorityComparer.Instance);
        foreach (var documentRow in store.Scan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DocumentVectorReader.TryReadVector(documentRow.Json, options.VectorPath, out var vector))
                continue;
            if (vector.Length != options.QueryVector.Length)
            {
                throw new InvalidOperationException(
                    $"vector_search 文档 '{documentRow.Id}' 的向量维度 {vector.Length} 与查询向量维度 {options.QueryVector.Length} 不一致。");
            }

            // 元数据谓词在距离计算前求值，与 ScoreRows 的短路顺序保持一致。
            if (metadataFilter is not null
                && !EvaluateBoolean(metadataFilter, new VectorSearchRow(documentRow, 0d, 0d)))
            {
                continue;
            }

            double distance = VectorDistance.Compute(options.Metric, options.QueryVector, vector);
            distanceComputed?.Invoke();
            var row = new VectorSearchRow(documentRow, distance, DistanceToScore(options.Metric, distance));
            if (candidates.Count < options.K)
            {
                candidates.Enqueue(row, new TopKPriority(distance, documentRow.Id));
                continue;
            }

            VectorSearchRow worst = candidates.Peek();
            if (CompareDistanceThenId(row, worst) < 0)
            {
                candidates.Dequeue();
                candidates.Enqueue(row, new TopKPriority(distance, documentRow.Id));
            }
        }

        return candidates.UnorderedItems
            .Select(static item => item.Element)
            .OrderBy(static row => row.VectorDistance)
            .ThenBy(static row => row.Document.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static int CompareDistanceThenId(VectorSearchRow left, VectorSearchRow right)
    {
        int distance = left.VectorDistance.CompareTo(right.VectorDistance);
        return distance != 0
            ? distance
            : string.Compare(left.Document.Id, right.Document.Id, StringComparison.Ordinal);
    }

    private static bool IsMetadataExpression(SqlExpression expression, ref int remainingNodes)
    {
        if (--remainingNodes < 0)
            return false;

        return expression switch
        {
            LiteralExpression => true,
            IdentifierExpression { Qualifier: null, Name: var name } =>
                !string.Equals(name, "vector_distance", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "vector_score", StringComparison.OrdinalIgnoreCase),
            BinaryExpression binary when binary.Operator is SqlBinaryOperator.And or SqlBinaryOperator.Or
                || binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                    or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual
                    or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual =>
                IsMetadataExpression(binary.Left, ref remainingNodes)
                && IsMetadataExpression(binary.Right, ref remainingNodes),
            UnaryExpression { Operator: SqlUnaryOperator.Not } unary =>
                IsMetadataExpression(unary.Operand, ref remainingNodes),
            IsNullExpression isNull => IsMetadataExpression(isNull.Operand, ref remainingNodes),
            FunctionCallExpression { IsStar: false, Arguments.Count: 2 } function
                when string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
                && function.Arguments[0] is IdentifierExpression { Qualifier: null, Name: var source }
                && (string.Equals(source, "document", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(source, "json", StringComparison.OrdinalIgnoreCase))
                && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String } => true,
            _ => false,
        };
    }

    private static List<VectorSearchRow> ApplyWhere(
        IReadOnlyList<VectorSearchRow> rows,
        SqlExpression? where)
    {
        if (where is null)
            return rows.ToList();

        var filtered = new List<VectorSearchRow>(rows.Count);
        foreach (var row in rows)
        {
            if (EvaluateBoolean(where, row))
                filtered.Add(row);
        }

        return filtered;
    }

    private static List<VectorSearchRow> ApplyOrderBy(
        IReadOnlyList<VectorSearchRow> rows,
        OrderBySpec? orderBy,
        IReadOnlyList<Projection> projections)
    {
        if (orderBy is null)
        {
            return rows
                .OrderBy(static r => r.VectorDistance)
                .ThenBy(static r => r.Document.Id, StringComparer.Ordinal)
                .ToList();
        }

        var expression = ResolveOrderByExpression(orderBy.Expression, projections);
        var ordered = orderBy.Direction == SortDirection.Descending
            ? rows.OrderByDescending(row => EvaluateScalar(expression, row), ScalarComparer.Instance)
            : rows.OrderBy(row => EvaluateScalar(expression, row), ScalarComparer.Instance);
        return ordered
            .ThenBy(static r => r.Document.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static SqlExpression ResolveOrderByExpression(
        SqlExpression orderBy,
        IReadOnlyList<Projection> projections)
    {
        if (orderBy is not IdentifierExpression { Qualifier: null, Name: var name })
            return orderBy;

        foreach (var projection in projections)
        {
            if (string.Equals(projection.ColumnName, name, StringComparison.OrdinalIgnoreCase))
                return projection.Expression;
        }

        return orderBy;
    }

    private static List<VectorSearchRow> ApplyPagination(
        IReadOnlyList<VectorSearchRow> rows,
        PaginationSpec? pagination)
    {
        if (pagination is null)
            return rows.ToList();
        if (pagination.Offset >= rows.Count)
            return [];

        int take = pagination.Fetch ?? (rows.Count - pagination.Offset);
        return rows.Skip(pagination.Offset).Take(Math.Min(take, rows.Count - pagination.Offset)).ToList();
    }

    private static Projection[] BuildProjections(IReadOnlyList<SelectItem> items)
    {
        var projections = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    projections.Add(new Projection("id", new IdentifierExpression("id")));
                    projections.Add(new Projection("document", new IdentifierExpression("document")));
                    projections.Add(new Projection("vector_distance", new IdentifierExpression("vector_distance")));
                    projections.Add(new Projection("vector_score", new IdentifierExpression("vector_score")));
                    break;

                case IdentifierExpression id:
                    projections.Add(new Projection(item.Alias ?? id.Name, item.Expression));
                    break;

                case FunctionCallExpression function:
                    projections.Add(new Projection(item.Alias ?? FormatFunctionColumnName(function), item.Expression));
                    break;

                case LiteralExpression literal:
                    projections.Add(new Projection(item.Alias ?? FormatLiteralColumnName(literal), item.Expression));
                    break;

                default:
                    projections.Add(new Projection(item.Alias ?? "expression", item.Expression));
                    break;
            }
        }

        return [.. projections];
    }

    private static bool EvaluateBoolean(SqlExpression expression, VectorSearchRow row)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(binary.Left, row) && EvaluateBoolean(binary.Right, row);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(binary.Left, row) || EvaluateBoolean(binary.Right, row);
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, row);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(unary.Operand, row);

            case IsNullExpression isNull:
                var isNullValue = EvaluateScalar(isNull.Operand, row) is null;
                return isNull.Negated ? !isNullValue : isNullValue;
        }

        var value = EvaluateScalar(expression, row);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("vector_search WHERE 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(BinaryExpression binary, VectorSearchRow row)
    {
        var left = EvaluateScalar(binary.Left, row);
        var right = EvaluateScalar(binary.Right, row);
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

    private static object? EvaluateScalar(SqlExpression expression, VectorSearchRow row)
        => expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            CastExpression cast => SqlCastOperations.Convert(EvaluateScalar(cast.Operand, row), cast.TargetType),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => SqlScalarOperations.Negate(EvaluateScalar(unary.Operand, row)),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, row),
            _ => throw new InvalidOperationException(
                $"vector_search 表达式暂不支持 '{expression.GetType().Name}'。"),
        };

    private static object? EvaluateFunction(FunctionCallExpression function, VectorSearchRow row)
    {
        if (function.IsStar)
            throw new InvalidOperationException($"vector_search 不支持函数 {function.Name}(*)。");

        if (string.Equals(function.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.VectorDistance);
        if (string.Equals(function.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.VectorScore);

        if (string.Equals(function.Name, "regexp_like", StringComparison.OrdinalIgnoreCase))
        {
            if (function.Arguments.Count is < 2 or > 3)
                throw new InvalidOperationException("函数 regexp_like 需要 2~3 个参数。");
            return RegexPatternMatcher.IsMatch(
                EvaluateScalar(function.Arguments[0], row),
                EvaluateScalar(function.Arguments[1], row),
                function.Arguments.Count == 3 ? EvaluateScalar(function.Arguments[2], row) : null);
        }

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(function.Arguments[0], row) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        if (FunctionRegistry.TryGetScalar(function.Name, out var scalarFunction))
        {
            var arguments = function.Arguments.Select(argument => EvaluateScalar(argument, row)).ToArray();
            return scalarFunction.Evaluate(arguments);
        }

        throw new InvalidOperationException($"vector_search 不支持标量函数 '{function.Name}'。");
    }

    private static object? RequireNoArguments(FunctionCallExpression function, object? value)
    {
        if (function.Arguments.Count != 0)
            throw new InvalidOperationException($"函数 {function.Name}() 不接受参数。");
        return value;
    }

    /// <summary>
    /// 在一条文档向量搜索结果上计算共享数值算术表达式。
    /// </summary>
    private static object? EvaluateArithmetic(BinaryExpression binary, VectorSearchRow row)
    {
        var left = EvaluateScalar(binary.Left, row);
        var right = EvaluateScalar(binary.Right, row);
        return SqlScalarOperations.EvaluateArithmetic(binary.Operator, left, right);
    }

    private static object? GetIdentifierValue(IdentifierExpression identifier, VectorSearchRow row)
    {
        if (identifier.Qualifier is not null)
            throw new InvalidOperationException("vector_search 当前不支持限定列名。");

        if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
            return row.Document.Id;
        if (string.Equals(identifier.Name, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "json", StringComparison.OrdinalIgnoreCase))
            return row.Document.Json;
        if (string.Equals(identifier.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return row.VectorDistance;
        if (string.Equals(identifier.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return row.VectorScore;

        return JsonPathEvaluator.Evaluate(row.Document.Json, "$." + identifier.Name);
    }

    private static Dictionary<string, SqlExpression> BindArguments(FunctionCallExpression call)
    {
        var result = new Dictionary<string, SqlExpression>(StringComparer.OrdinalIgnoreCase);
        bool hasNamed = call.Arguments.Any(static arg => arg is NamedArgumentExpression);
        if (hasNamed)
        {
            foreach (var argument in call.Arguments)
            {
                if (argument is not NamedArgumentExpression named)
                    throw new InvalidOperationException("vector_search(...) 使用命名参数时，所有参数都必须写成 name => value。");
                if (!result.TryAdd(named.Name, named.Value))
                    throw new InvalidOperationException($"vector_search(...) 参数 '{named.Name}' 重复。");
            }

            return result;
        }

        if (call.Arguments.Count is < 2 or > 3)
            throw new InvalidOperationException("vector_search(source, vector[, k]) 或 vector_search(source => ..., vector => ..., k => ...) 需要 source/vector 参数。");

        result["source"] = call.Arguments[0];
        result["vector"] = call.Arguments[1];
        if (call.Arguments.Count == 3)
            result["k"] = call.Arguments[2];
        return result;
    }

    private static string RequireIdentifierArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        string name)
    {
        if (!TryGetArgument(args, out var expression, name))
            throw new InvalidOperationException($"vector_search 缺少必填参数 '{name}'。");
        return ExpressionToName(expression, name);
    }

    private static float[] RequireVectorArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            throw new InvalidOperationException($"vector_search 缺少必填参数 '{names[0]}'。");
        if (expression is not VectorLiteralExpression vector)
            throw new InvalidOperationException($"vector_search 参数 '{names[0]}' 必须是向量字面量。");

        var result = new float[vector.Components.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = (float)vector.Components[i];
        return result;
    }

    private static int GetPositiveIntArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        int defaultValue,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            return defaultValue;
        if (expression is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: > 0 and <= int.MaxValue } literal)
            return (int)literal.IntegerValue;
        throw new InvalidOperationException($"vector_search 参数 '{names[0]}' 必须是正整数字面量。");
    }

    private static string GetFieldArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        string defaultValue,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            return defaultValue;
        if (expression is StarExpression)
            return "*";
        return ExpressionToName(expression, names[0]);
    }

    private static KnnMetric GetMetricArgument(IReadOnlyDictionary<string, SqlExpression> args)
    {
        if (!TryGetArgument(args, out var expression, "metric"))
            return KnnMetric.Cosine;
        if (expression is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var metric })
            throw new InvalidOperationException("vector_search 参数 'metric' 必须是字符串字面量。");
        return metric!.ToLowerInvariant() switch
        {
            "cosine" or "cosine_distance" => KnnMetric.Cosine,
            "l2" or "l2_distance" or "euclidean" => KnnMetric.L2,
            "inner_product" or "dot" or "ip" => KnnMetric.InnerProduct,
            _ => throw new InvalidOperationException(
                $"vector_search 不支持 metric '{metric}'，仅支持 'cosine' / 'l2' / 'inner_product'。"),
        };
    }

    private static bool TryGetArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        out SqlExpression expression,
        params ReadOnlySpan<string> names)
    {
        foreach (string name in names)
        {
            if (args.TryGetValue(name, out expression!))
                return true;
        }

        expression = null!;
        return false;
    }

    private static string ExpressionToName(SqlExpression expression, string argumentName)
        => expression switch
        {
            IdentifierExpression identifier => identifier.Name,
            LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var value } => value!,
            _ => throw new InvalidOperationException($"vector_search 参数 '{argumentName}' 必须是标识符或字符串字面量。"),
        };

    private static string NormalizeJsonPath(string path)
    {
        if (path == "*")
            throw new InvalidOperationException("vector_search 的 vector_field 不能是 '*'。");
        return path.StartsWith('$') ? JsonPath.Parse(path).Text : JsonPath.Parse("$." + path).Text;
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

    private static double DistanceToScore(KnnMetric metric, double distance)
    {
        if (metric == KnnMetric.Cosine)
            return Math.Clamp(1d - (distance / 2d), 0d, 1d);
        if (metric == KnnMetric.InnerProduct)
        {
            if (distance <= -60d)
                return 1d;
            if (distance >= 60d)
                return 0d;
            return 1d / (1d + Math.Exp(distance));
        }

        return 1d / (1d + Math.Max(0d, distance));
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

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
        SqlBinaryOperator.Modulo or
        SqlBinaryOperator.BitwiseAnd or
        SqlBinaryOperator.BitwiseOr;

    private static string FormatLiteralColumnName(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => "NULL",
        SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
        SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.String => literal.StringValue ?? string.Empty,
        _ => literal.Kind.ToString(),
    };

    private static string FormatFunctionColumnName(FunctionCallExpression function)
        => function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path }
            ? path!
            : function.Name;

    private sealed record Projection(string ColumnName, SqlExpression Expression);

    private sealed record VectorSearchOptions(
        JsonPath VectorPath,
        float[] QueryVector,
        int K,
        KnnMetric Metric);

    private sealed record VectorSearchRow(
        DocumentRow Document,
        double VectorDistance,
        double VectorScore);

    private readonly record struct TopKPriority(double Distance, string Id);

    private sealed class TopKPriorityComparer : IComparer<TopKPriority>
    {
        public static TopKPriorityComparer Instance { get; } = new();

        public int Compare(TopKPriority left, TopKPriority right)
        {
            // PriorityQueue dequeues the smallest priority. Reverse the natural order
            // so the least desirable (farthest, then lexicographically largest) hit
            // remains at the head and can be evicted in O(log K).
            int distance = right.Distance.CompareTo(left.Distance);
            return distance != 0
                ? distance
                : string.Compare(right.Id, left.Id, StringComparison.Ordinal);
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
