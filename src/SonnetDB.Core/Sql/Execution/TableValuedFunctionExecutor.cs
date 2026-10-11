using System.Diagnostics;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Query.Functions.Forecasting;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// FROM 子句中的表值函数（Table-Valued Function，TVF）执行器；当前支持：
/// <list type="bullet">
///   <item><description>PR #55 引入的 <c>forecast(measurement, field, horizon, 'algo'[, season])</c>。</description></item>
///   <item><description>PR #60 引入的 <c>knn(measurement, column, query_vector, k[, metric])</c>。</description></item>
/// </list>
/// </summary>
internal static class TableValuedFunctionExecutor
{
    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("内部错误：TVF 调用为空。");

        if (SqlRowRetentionBudget.HasExecutionBudget)
        {
            if (tsdb.Functions.TryGetTableValuedFunction(call.Name, out _))
                throw new NotSupportedException("SQL 物化预算不支持用户表值函数回调。");
            if (string.Equals(call.Name, "knn", StringComparison.OrdinalIgnoreCase))
            {
                ValidateMaterializationSupported(tsdb, statement);
                return ExecuteBudgetedKnn(tsdb, statement, call);
            }
            if (string.Equals(call.Name, "json_each", StringComparison.OrdinalIgnoreCase)
                || string.Equals(call.Name, "json_table", StringComparison.OrdinalIgnoreCase))
                return JsonFileSqlExecutor.ExecuteTableValuedFunction(statement, call);
            throw new NotSupportedException("SQL 物化预算尚不支持此表值函数查询源。");
        }

        // 优先匹配用户注册的 TVF（PR #56）
        var udf = SonnetDB.Query.Functions.UserFunctionRegistry.Current;
        if (udf is not null && udf.TryGetTableValuedFunction(call.Name, out var executor))
            return executor(tsdb, statement);

        return call.Name.ToLowerInvariant() switch
        {
            "forecast" => ExecuteForecast(tsdb, statement, call),
            "knn" => ExecuteKnn(tsdb, statement, call),
            "json_each" or "json_table" => JsonFileSqlExecutor.ExecuteTableValuedFunction(statement, call),
            _ => throw new InvalidOperationException(
                $"未知表值函数 '{call.Name}'；当前 FROM 子句支持 forecast(...) / knn(...) / json_each(...) 及通过 Tsdb.Functions 注册的 UDF。"),
        };
    }

    /// <summary>在读取向量快照前验证直接 measurement KNN 的累计物化预算范围。</summary>
    internal static void ValidateMaterializationSupported(Tsdb tsdb, SelectStatement statement)
    {
        var call = statement.TableValuedFunction;
        if (call is null || !string.Equals(call.Name, "knn", StringComparison.OrdinalIgnoreCase)
            || tsdb.Functions.TryGetTableValuedFunction(call.Name, out _))
            throw new NotSupportedException("SQL 物化预算仅支持内置 measurement knn 表值函数；其它 TVF 和用户回调尚不支持。");
        if (statement.OrderByList.Count != 0 || statement.GroupBy.Count != 0
            || statement.Having is not null || statement.Distinct || statement.IsRecursive
            || statement.JoinClauses.Count != 0 || statement.FromSubquery is not null
            || statement.CommonTableExpressions.Count != 0 || statement.SetOperationList.Count != 0
            || statement.GraphTable is not null)
            throw new NotSupportedException("KNN SQL 物化预算仅支持直接单源标量投影、TAG/时间过滤和 LIMIT/OFFSET；复杂查询尚不支持。");
        if (call.IsStar || call.IsDistinct || call.Over is not null || call.Arguments.Count is < 4 or > 5)
            throw new InvalidOperationException("knn(measurement, column, query_vector, k[, metric]) 需要 4~5 个普通参数。");

        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException($"knn(...) 引用的 measurement '{statement.Measurement}' 不存在。");
        if (call.Arguments[1] is not IdentifierExpression vectorIdentifier)
            throw new InvalidOperationException("knn 第 2 个参数必须是向量列名标识符。");
        var vectorColumn = schema.Resolve(vectorIdentifier.Name, vectorIdentifier.IsQuoted || vectorIdentifier.IsNameBound)
            ?? throw new InvalidOperationException($"knn 引用了未知列 '{vectorIdentifier.Name}'。");
        if (vectorColumn.Role != MeasurementColumnRole.Field || vectorColumn.DataType != FieldType.Vector)
            throw new InvalidOperationException($"knn 的列参数 '{vectorIdentifier.Name}' 必须是 VECTOR FIELD 列。");
        if (call.Arguments[2] is not VectorLiteralExpression vector)
            throw new InvalidOperationException("knn 第 3 个参数必须是向量字面量。");
        if (vector.Components.Count != vectorColumn.VectorDimension)
            throw new InvalidOperationException($"knn 查询向量维度 {vector.Components.Count} 与列声明维度 {vectorColumn.VectorDimension} 不一致。");
        _ = ResolveKnnK(call.Arguments[3]);
        if (call.Arguments.Count == 5)
            _ = ResolveKnnMetric(call.Arguments[4]);
        if (statement.Pagination is { } pagination)
        {
            _ = pagination.Offset;
            _ = pagination.Fetch;
        }

        var projection = CreateBudgetedKnnProjection(tsdb, schema, statement);
        int whereNodes = 128;
        ValidateBudgetedKnnScalar(tsdb, statement.Where, ref whereNodes,
            allowNow: true, resolveColumn: projection.ResolveColumn);
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        if (where.Residual is not null || where.GeoFilters.Count != 0)
            throw new NotSupportedException("KNN SQL 物化预算仅支持已经下推的 TAG 等值和时间范围过滤；残差与地理谓词尚不支持。");
    }

    private static void ValidateBudgetedKnnScalar(Tsdb tsdb, SqlExpression? expression, ref int remainingNodes,
        bool allowNow = false, Func<IdentifierExpression, int>? resolveColumn = null)
    {
        if (--remainingNodes < 0)
            throw new NotSupportedException("KNN SQL 物化预算的标量表达式超过节点上限。");
        switch (expression)
        {
            case null or LiteralExpression or DurationLiteralExpression:
                return;
            case IdentifierExpression identifier:
                _ = resolveColumn?.Invoke(identifier);
                return;
            case CastExpression cast when cast.TargetType is not (SqlDataType.Vector or SqlDataType.GeoPoint):
                ValidateBudgetedKnnScalar(tsdb, cast.Operand, ref remainingNodes, allowNow, resolveColumn);
                return;
            case UnaryExpression unary:
                ValidateBudgetedKnnScalar(tsdb, unary.Operand, ref remainingNodes, allowNow, resolveColumn);
                return;
            case BinaryExpression binary:
                ValidateBudgetedKnnScalar(tsdb, binary.Left, ref remainingNodes, allowNow, resolveColumn);
                ValidateBudgetedKnnScalar(tsdb, binary.Right, ref remainingNodes, allowNow, resolveColumn);
                return;
            case IsNullExpression isNull:
                ValidateBudgetedKnnScalar(tsdb, isNull.Operand, ref remainingNodes, allowNow, resolveColumn);
                return;
            case InExpression { Subquery: null } membership:
                ValidateBudgetedKnnScalar(tsdb, membership.Value, ref remainingNodes, allowNow, resolveColumn);
                foreach (var value in membership.Values)
                    ValidateBudgetedKnnScalar(tsdb, value, ref remainingNodes, allowNow, resolveColumn);
                return;
            case CaseExpression conditional:
                foreach (var clause in conditional.WhenClauses)
                {
                    ValidateBudgetedKnnScalar(tsdb, clause.Condition, ref remainingNodes, allowNow, resolveColumn);
                    ValidateBudgetedKnnScalar(tsdb, clause.Result, ref remainingNodes, allowNow, resolveColumn);
                }
                ValidateBudgetedKnnScalar(tsdb, conditional.Else, ref remainingNodes, allowNow, resolveColumn);
                return;
            case FunctionCallExpression function:
                if (function.Over is not null || function.IsStar || function.IsDistinct
                    || tsdb.Functions.TryGetScalar(function.Name, out _)
                    || tsdb.Functions.TryGetAggregate(function.Name, out _)
                    || tsdb.Functions.TryGetWindow(function.Name, out _)
                    || !(allowNow && string.Equals(function.Name, "now", StringComparison.OrdinalIgnoreCase)
                        && function.Arguments.Count == 0) && !FunctionRegistry.TryGetScalar(function.Name, out _))
                    throw new NotSupportedException("KNN SQL 物化预算仅支持内置标量函数；聚合、窗口和用户回调尚不支持。");
                SqlTableFunctionMaterialization.ValidateScalarArgumentCount(function);
                foreach (var argument in function.Arguments)
                    ValidateBudgetedKnnScalar(tsdb, argument, ref remainingNodes, allowNow, resolveColumn);
                return;
            default:
                throw new NotSupportedException("KNN SQL 物化预算仅支持标准只读标量；星号、子查询、向量及地理结果尚不支持。");
        }
    }

    private static BudgetedKnnProjection CreateBudgetedKnnProjection(
        Tsdb tsdb, MeasurementSchema schema, SelectStatement statement)
    {
        var tags = schema.Columns.Where(static c => c.Role == MeasurementColumnRole.Tag).ToArray();
        var fields = schema.Columns.Where(static c => c.Role == MeasurementColumnRole.Field).ToArray();
        string[] columns = ["time", "distance", .. tags.Select(static c => c.Name), .. fields.Select(static c => c.Name)];
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordinary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Length; i++)
        {
            if (!exact.TryAdd(columns[i], i)) exact[columns[i]] = -1;
            if (!ordinary.TryAdd(columns[i], i)) ordinary[columns[i]] = -1;
        }

        int ResolveColumn(IdentifierExpression identifier)
        {
            string qualifier = statement.TableAlias ?? statement.Measurement;
            if (identifier.Qualifier is not null && !string.Equals(identifier.Qualifier, qualifier,
                identifier.QualifierIsQuoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"knn(...) 引用了未知限定符 '{identifier.Qualifier}'。");
            var lookup = identifier.IsQuoted || identifier.IsNameBound ? exact : ordinary;
            if (!lookup.TryGetValue(identifier.Name, out int ordinal))
                throw new InvalidOperationException($"knn(...) 表值函数没有输出列 '{identifier.Name}'。");
            if (ordinal < 0)
                throw new InvalidOperationException($"knn(...) 输出列 '{identifier.Name}' 存在名称歧义。");
            if (ordinal >= 2 + tags.Length && fields[ordinal - 2 - tags.Length].DataType is not
                (FieldType.Int64 or FieldType.Float64 or FieldType.Boolean or FieldType.String))
                throw new NotSupportedException("KNN SQL 物化预算仅支持数值、布尔和字符串 FIELD 投影；向量与地理结果尚不支持。");
            return ordinal;
        }

        var projections = new List<TableValuedProjection>(statement.Projections.Count);
        int remainingNodes = 1024;
        foreach (var item in statement.Projections)
        {
            ValidateBudgetedKnnScalar(tsdb, item.Expression, ref remainingNodes);
            SqlProjectionExpressionEvaluator.Validate(item.Expression, identifier => ResolveColumn(identifier) >= 0, "knn(...) 表值函数");
            if (item.Expression is IdentifierExpression identifier)
            {
                int ordinal = ResolveColumn(identifier);
                projections.Add(new(item.Alias ?? columns[ordinal], ordinal, null));
            }
            else
                projections.Add(new(item.Alias ?? "expression", null, item.Expression));
        }
        return new(tags, fields, projections, ResolveColumn);
    }

    private sealed record BudgetedKnnProjection(
        IReadOnlyList<MeasurementColumn> Tags,
        IReadOnlyList<MeasurementColumn> Fields,
        IReadOnlyList<TableValuedProjection> Projections,
        Func<IdentifierExpression, int> ResolveColumn);

    private static SelectExecutionResult ExecuteBudgetedKnn(
        Tsdb tsdb, SelectStatement statement, FunctionCallExpression call)
    {
        var schema = tsdb.Measurements.TryGet(statement.Measurement)!;
        var projection = CreateBudgetedKnnProjection(tsdb, schema, statement);
        var columns = projection.Projections.Select(static p => p.ColumnName).ToArray();
        var rows = new List<IReadOnlyList<object?>>();
        if (statement.Pagination?.Fetch == 0)
            return new(columns, rows);
        var vectorId = (IdentifierExpression)call.Arguments[1];
        string vectorField = schema.Resolve(vectorId.Name, vectorId.IsQuoted || vectorId.IsNameBound)!.Name;
        float[] queryVector = ResolveQueryVector(call.Arguments[2]);
        int k = ResolveKnnK(call.Arguments[3]);
        var metric = call.Arguments.Count == 5 ? ResolveKnnMetric(call.Arguments[4]) : KnnMetric.Cosine;
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);

        long startedAt = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        object gate = tsdb.VectorReplacements.SyncRoot;
        bool lockTaken = false;
        try
        {
            // 最多 3000 次短等待且共用五分钟墙钟；等待中检查调用方取消。
            for (int attempt = 0; attempt < 3000 && !lockTaken; attempt++)
            {
                KnnExecutor.ThrowIfBudgetedCancelled(startedAt);
                lockTaken = Monitor.TryEnter(gate, millisecondsTimeout: 100);
            }
            if (!lockTaken)
                throw new RoutineExecutionException(RoutineErrorCodes.Cancelled, "KNN SQL 物化预算查询等待向量替换锁超时。");
            KnnExecutor.ThrowIfBudgetedCancelled(startedAt);
            var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter).ToList();
            IReadOnlyList<KnnSearchResult> hits;
            using (var snapshot = tsdb.AcquireReadSnapshot())
                hits = KnnExecutor.Execute(snapshot.AllMemTables(), snapshot.Snapshot.Index, snapshot.Readers,
                    matchedSeries, vectorField, queryVector.AsMemory(), k, metric, where.TimeRange,
                    tsdb.Tombstones, tsdb.VectorReplacements, tsdb.Query, deadline.Token);

            var hitSeriesIds = hits.Select(static hit => hit.SeriesId).ToHashSet();
            var seriesById = new Dictionary<ulong, SeriesEntry>();
            foreach (var entry in matchedSeries)
            {
                KnnExecutor.ThrowIfBudgetedCancelled(startedAt);
                if (hitSeriesIds.Contains(entry.Id))
                    seriesById.Add(entry.Id, entry);
            }

            int offset = Math.Min(statement.Pagination?.Offset ?? 0, hits.Count);
            int take = Math.Min(statement.Pagination?.Fetch ?? (hits.Count - offset), hits.Count - offset);
            for (int index = offset; index < offset + take; index++)
            {
                KnnExecutor.ThrowIfBudgetedCancelled(startedAt);
                var hit = hits[index];
                var series = seriesById[hit.SeriesId];
                var values = new Dictionary<int, object?>();
                object? ResolveValue(int ordinal)
                {
                    KnnExecutor.ThrowIfBudgetedCancelled(startedAt);
                    if (ordinal == 0) return hit.Timestamp;
                    if (ordinal == 1) return hit.Distance;
                    if (ordinal < 2 + projection.Tags.Count)
                        return series.Tags.TryGetValue(projection.Tags[ordinal - 2].Name, out var tag) ? tag : null;
                    if (values.TryGetValue(ordinal, out object? existing)) return existing;
                    string field = projection.Fields[ordinal - 2 - projection.Tags.Count].Name;
                    using var points = tsdb.Query.Execute(new PointQuery(hit.SeriesId, field,
                        new TimeRange(hit.Timestamp, hit.Timestamp))).GetEnumerator();
                    object? value = points.MoveNext() ? ConvertFieldValue(points.Current.Value) : null;
                    // 只读取此命中时间戳的首值；在缓存标量前计费，不建立区间 ToList 或向量副本。
                    SqlRowRetentionBudget.RetainValueForExecution(value);
                    values.Add(ordinal, value);
                    return value;
                }

                var row = new object?[projection.Projections.Count];
                for (int column = 0; column < row.Length; column++)
                {
                    KnnExecutor.ThrowIfBudgetedCancelled(startedAt);
                    var item = projection.Projections[column];
                    row[column] = item.SourceOrdinal is int ordinal ? ResolveValue(ordinal)
                        : SqlProjectionExpressionEvaluator.Evaluate(item.Expression!,
                            identifier => ResolveValue(projection.ResolveColumn(identifier)), "knn(...) 表值函数");
                }
                SqlRowRetentionBudget.RetainForExecution(row);
                rows.Add(row);
            }
            return new(columns, rows);
        }
        finally
        {
            if (lockTaken)
                Monitor.Exit(gate);
        }
    }

    // ── forecast(measurement, field, horizon, 'algo'[, season]) ───────────

    private static SelectExecutionResult ExecuteForecast(Tsdb tsdb, SelectStatement statement, FunctionCallExpression call)
    {
        if (call.IsStar)
            throw new InvalidOperationException("forecast(*) 非法。");
        if (call.Arguments.Count is < 4 or > 5)
            throw new InvalidOperationException(
                "forecast(measurement, field, horizon, 'algo'[, season]) 需要 4~5 个参数。");

        // 第 1 个参数：measurement（已由 parser 提取到 statement.Measurement）
        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"forecast(...) 引用的 measurement '{statement.Measurement}' 不存在。");

        // 第 2 个参数：field
        if (call.Arguments[1] is not IdentifierExpression fieldId)
            throw new InvalidOperationException("forecast 第 2 个参数必须是字段列名。");
        var fieldCol = schema.Resolve(fieldId.Name, fieldId.IsQuoted || fieldId.IsNameBound)
            ?? throw new InvalidOperationException(
                $"forecast 引用了未知字段 '{fieldId.Name}'。");
        if (fieldCol.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"forecast 第 2 个参数 '{fieldId.Name}' 必须是 FIELD 列。");
        if (fieldCol.DataType == FieldType.String)
            throw new InvalidOperationException(
                $"forecast 不支持 String 字段 '{fieldId.Name}'。");

        // 第 3 个参数：horizon（正整数字面量）
        int horizon = ResolvePositiveIntLiteral(call.Arguments[2], "horizon");

        // 第 4 个参数：算法
        var algorithm = ResolveAlgorithm(call.Arguments[3]);

        // 第 5 个参数（可选）：季节长度
        int season = 0;
        if (call.Arguments.Count == 5)
            season = ResolveNonNegativeIntLiteral(call.Arguments[4], "season");

        // WHERE 子句：复用普通 SELECT 的 tag/time 过滤。
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);

        // 输出列：time, value, lower, upper + 所有 tag 列（按 schema 顺序）
        var tagColumns = schema.Columns
            .Where(c => c.Role == MeasurementColumnRole.Tag)
            .ToList();
        // 防御性：tag 列名若与 forecast 内置输出列（time / value / lower / upper）冲突，
        // 普通输出列引用会存在大小写歧义，导致无法确定引用的是 tag 还是桶时间。
        // 这里在构表阶段显式报错，避免错列结果流回上层。
        foreach (var t in tagColumns)
        {
            if (string.Equals(t.Name, "time", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Name, "value", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Name, "lower", StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.Name, "upper", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"forecast(...) 不允许 tag 列名 '{t.Name}' 与内置输出列 time / value / lower / upper 冲突；请重命名 tag 或加列别名。");
            }
        }
        var sourceColumnNames = new List<string>(4 + tagColumns.Count) { "time", "value", "lower", "upper" };
        foreach (var t in tagColumns) sourceColumnNames.Add(t.Name);

        var rows = new List<IReadOnlyList<object?>>();

        foreach (var series in matchedSeries)
        {
            var points = QueryPoints(tsdb, series.Id, fieldCol.Name, where.TimeRange);
            if (points.Count < 2)
                continue;

            var ts = new long[points.Count];
            var values = new double[points.Count];
            for (int i = 0; i < points.Count; i++)
            {
                ts[i] = points[i].Timestamp;
                values[i] = points[i].Value.TryGetNumeric(out var d) ? d : double.NaN;
            }

            var forecast = TimeSeriesForecaster.Forecast(ts, values, horizon, algorithm, season);
            foreach (var p in forecast)
            {
                var row = new object?[sourceColumnNames.Count];
                row[0] = p.TimestampMs;
                row[1] = p.Value;
                row[2] = p.Lower;
                row[3] = p.Upper;
                for (int t = 0; t < tagColumns.Count; t++)
                    row[4 + t] = series.Tags.TryGetValue(tagColumns[t].Name, out var tv) ? tv : null;
                rows.Add(row);
            }
        }

        return ApplyTableValuedProjection("forecast", sourceColumnNames, rows, statement);
    }

    private static SelectExecutionResult ApplyTableValuedProjection(
        string functionName,
        IReadOnlyList<string> sourceColumns,
        IReadOnlyList<IReadOnlyList<object?>> sourceRows,
        SelectStatement statement)
    {
        var exact = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordinary = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sourceColumns.Count; i++)
        {
            if (!exact.TryAdd(sourceColumns[i], i))
                exact[sourceColumns[i]] = -1;
            if (!ordinary.TryAdd(sourceColumns[i], i))
                ordinary[sourceColumns[i]] = -1;
        }

        void ValidateQualifier(string? qualifier, bool quoted)
        {
            if (qualifier is null)
                return;
            string source = statement.TableAlias ?? statement.Measurement;
            if (!string.Equals(qualifier, source,
                quoted ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{functionName}(...) 引用了未知限定符 '{qualifier}'。");
        }

        int ResolveColumn(IdentifierExpression identifier)
        {
            ValidateQualifier(identifier.Qualifier, identifier.QualifierIsQuoted);
            var lookup = identifier.IsQuoted || identifier.IsNameBound ? exact : ordinary;
            if (!lookup.TryGetValue(identifier.Name, out int ordinal))
                throw new InvalidOperationException(
                    $"{functionName}(...) 表值函数没有输出列 '{identifier.Name}'。");
            if (ordinal < 0)
                throw new InvalidOperationException(
                    $"{functionName}(...) 输出列 '{identifier.Name}' 存在名称歧义；请用双引号精确引用或显式迁移冲突列。");
            return ordinal;
        }

        var projected = new List<TableValuedProjection>();
        foreach (var item in statement.Projections)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    for (int i = 0; i < sourceColumns.Count; i++)
                        projected.Add(new TableValuedProjection(sourceColumns[i], i, null));
                    break;

                case IdentifierExpression id:
                    int ordinal = ResolveColumn(id);
                    projected.Add(new TableValuedProjection(item.Alias ?? sourceColumns[ordinal], ordinal, null));
                    break;

                default:
                    string context = $"{functionName}(...) 表值函数";
                    SqlProjectionExpressionEvaluator.Validate(
                        item.Expression,
                        identifier => ResolveColumn(identifier) >= 0,
                        context);
                    projected.Add(new TableValuedProjection(
                        item.Alias ?? "expression", null, item.Expression));
                    break;
            }
        }

        var rows = new List<IReadOnlyList<object?>>(sourceRows.Count);
        foreach (var sourceRow in sourceRows)
        {
            var row = new object?[projected.Count];
            for (int i = 0; i < projected.Count; i++)
            {
                var projection = projected[i];
                row[i] = projection.SourceOrdinal is int ordinal
                    ? sourceRow[ordinal]
                    : SqlProjectionExpressionEvaluator.Evaluate(
                        projection.Expression!,
                        identifier => sourceRow[ResolveColumn(identifier)],
                        $"{functionName}(...) 表值函数");
            }
            rows.Add(row);
        }

        return new SelectExecutionResult(projected.Select(static p => p.ColumnName).ToArray(), rows);
    }

    /// <summary>
    /// 描述表值函数的直接列投影或动态表达式投影。
    /// </summary>
    private sealed record TableValuedProjection(
        string ColumnName,
        int? SourceOrdinal,
        SqlExpression? Expression);

    private static int ResolvePositiveIntLiteral(SqlExpression arg, string name)
    {
        if (arg is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: > 0 and <= int.MaxValue } lit)
            return (int)lit.IntegerValue;
        throw new InvalidOperationException($"forecast 参数 '{name}' 必须是正整数字面量。");
    }

    private static int ResolveNonNegativeIntLiteral(SqlExpression arg, string name)
    {
        if (arg is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: >= 0 and <= int.MaxValue } lit)
            return (int)lit.IntegerValue;
        throw new InvalidOperationException($"forecast 参数 '{name}' 必须是非负整数字面量。");
    }

    private static ForecastAlgorithm ResolveAlgorithm(SqlExpression arg)
    {
        if (arg is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { } s })
            throw new InvalidOperationException(
                "forecast 第 4 个参数必须是字符串字面量 'linear' / 'holt_winters'。");
        return s.ToLowerInvariant() switch
        {
            "linear" => ForecastAlgorithm.Linear,
            "holt_winters" or "holt-winters" or "holtwinters" or "hw" => ForecastAlgorithm.HoltWinters,
            _ => throw new InvalidOperationException(
                $"forecast 不支持算法 '{s}'，仅支持 'linear' / 'holt_winters'。"),
        };
    }

    private static IReadOnlyList<DataPoint> QueryPoints(Tsdb tsdb, ulong seriesId, string fieldName, TimeRange range)
    {
        var query = new PointQuery(seriesId, fieldName, range);
        return tsdb.Query.Execute(query).ToList();
    }

    // ── knn(measurement, column, query_vector, k[, metric]) ───────────────

    /// <summary>
    /// 执行 knn 表值函数。
    /// 语法：<c>SELECT * FROM knn(measurement, column, [f1, f2, ...], k[, 'metric']) [WHERE ...]</c>。
    /// 返回按距离升序排列的 (time, distance, ...tag_columns, ...field_columns) 结果集。
    /// </summary>
    private static SelectExecutionResult ExecuteKnn(Tsdb tsdb, SelectStatement statement, FunctionCallExpression call)
    {
        if (call.IsStar)
            throw new InvalidOperationException("knn(*) 非法。");
        if (call.Arguments.Count is < 4 or > 5)
            throw new InvalidOperationException(
                "knn(measurement, column, query_vector, k[, metric]) 需要 4~5 个参数。");

        // 第 1 个参数：measurement（已由 parser 提取到 statement.Measurement）
        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"knn(...) 引用的 measurement '{statement.Measurement}' 不存在。");

        // 第 2 个参数：向量列名
        if (call.Arguments[1] is not IdentifierExpression columnId)
            throw new InvalidOperationException("knn 第 2 个参数必须是向量列名标识符。");
        var vectorColumn = schema.Resolve(columnId.Name, columnId.IsQuoted || columnId.IsNameBound)
            ?? throw new InvalidOperationException($"knn 引用了未知列 '{columnId.Name}'。");

        // 第 3 个参数：查询向量
        float[] queryArray = ResolveQueryVector(call.Arguments[2]);

        // 第 4 个参数：k（正整数）
        int k = ResolveKnnK(call.Arguments[3]);

        // 第 5 个参数（可选）：距离度量
        var metric = call.Arguments.Count == 5
            ? ResolveKnnMetric(call.Arguments[4])
            : KnnMetric.Cosine;

        // WHERE 子句：tag 过滤 + 时间范围
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);

        var result = ExecuteKnnSearch(tsdb, statement.Measurement, vectorColumn.Name, queryArray, k, metric,
            where.TagFilter, where.TimeRange);
        return ApplyTableValuedProjection(
            "knn", result.Columns, result.Rows, statement);
    }

    /// <summary>
    /// KNN 检索共用内核（SQL knn TVF 与 vector search 帧 #239 共用编排）：
    /// 列/维度校验 → tag 过滤定位候选序列 → 单次读快照 KNN → tag/field 回填。
    /// 返回列固定为 (time, distance, ...tag_columns, ...field_columns)，按距离升序。
    /// </summary>
    internal static SelectExecutionResult ExecuteKnnSearch(
        Tsdb tsdb,
        string measurement,
        string column,
        float[] queryVector,
        int k,
        KnnMetric metric,
        IReadOnlyDictionary<string, string>? tagFilter,
        TimeRange timeRange)
    {
        lock (tsdb.VectorReplacements.SyncRoot)
            return ExecuteKnnSearchCore(tsdb, measurement, column, queryVector, k, metric, tagFilter, timeRange);
    }

    private static SelectExecutionResult ExecuteKnnSearchCore(
        Tsdb tsdb,
        string measurement,
        string column,
        float[] queryVector,
        int k,
        KnnMetric metric,
        IReadOnlyDictionary<string, string>? tagFilter,
        TimeRange timeRange)
    {
        var schema = tsdb.Measurements.TryGet(measurement)
            ?? throw new InvalidOperationException(
                $"knn(...) 引用的 measurement '{measurement}' 不存在。");

        var vectorCol = schema.TryGetColumn(column)
            ?? throw new InvalidOperationException(
                $"knn 引用了未知列 '{column}'。");
        if (vectorCol.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"knn 的列参数 '{column}' 必须是 FIELD 列。");
        if (vectorCol.DataType != FieldType.Vector)
            throw new InvalidOperationException(
                $"knn 的列参数 '{column}' 必须是 VECTOR 类型，实际为 {vectorCol.DataType}。");
        int dim = vectorCol.VectorDimension
            ?? throw new InvalidOperationException(
                $"VECTOR 列 '{column}' 缺少维度声明（schema 损坏）。");
        if (queryVector.Length != dim)
            throw new InvalidOperationException(
                $"knn 查询向量维度 {queryVector.Length} 与列 '{column}' 声明的维度 {dim} 不一致。");

        // tag 过滤键校验（SQL 路径已由 WhereClauseDecomposer 校验过，帧路径在此统一把关）
        if (tagFilter is not null)
        {
            foreach (var key in tagFilter.Keys)
            {
                var tagCol = schema.TryGetColumn(key);
                if (tagCol is null || tagCol.Role != MeasurementColumnRole.Tag)
                    throw new InvalidOperationException($"knn tag 过滤引用的列 '{key}' 不是 TAG 列。");
            }
        }

        var matchedSeries = tsdb.Catalog.Find(measurement, tagFilter).ToList();

        // 建立 seriesId → SeriesEntry 查找表（供结果行填充 tag 值）
        var seriesById = new Dictionary<ulong, SeriesEntry>(matchedSeries.Count);
        foreach (var se in matchedSeries)
            seriesById[se.Id] = se;

        // 构建输出列名：time, distance, ...tag_columns, ...field_columns
        var tagColumns = schema.Columns
            .Where(c => c.Role == MeasurementColumnRole.Tag)
            .ToList();
        var fieldColumns = schema.Columns
            .Where(c => c.Role == MeasurementColumnRole.Field)
            .ToList();

        var columnNames = new List<string>(2 + tagColumns.Count + fieldColumns.Count);
        columnNames.Add("time");
        columnNames.Add("distance");
        foreach (var tc in tagColumns) columnNames.Add(tc.Name);
        foreach (var fc in fieldColumns) columnNames.Add(fc.Name);

        // 执行 KNN 搜索：单次租约拿到 {MemTable(active+sealing) + 段读取器} 一致视图。
        IReadOnlyList<KnnSearchResult> knnResults;
        using (var readSnapshot = tsdb.AcquireReadSnapshot())
        {
            knnResults = KnnExecutor.Execute(
                readSnapshot.AllMemTables(),
                readSnapshot.Snapshot.Index,
                readSnapshot.Readers,
                matchedSeries,
                vectorCol.Name,
                queryVector.AsMemory(),
                k,
                metric,
                timeRange,
                tsdb.Tombstones,
                tsdb.VectorReplacements,
                tsdb.Query);
        }

        // 字段值预批量读取：按 (SeriesId × FieldColumn) 各发一次 QueryPoints，覆盖
        // 该 series 全部命中时间戳的最小-最大区间，避免原"每行每列一次精确点查"造成的
        // rows × fields 次往返；命中时间戳作为 HashSet 过滤，保证仅保留真正需要的点。
        Dictionary<(ulong Sid, string Field, long Ts), FieldValue>? fieldLookup = null;
        if (fieldColumns.Count > 0 && knnResults.Count > 0)
        {
            fieldLookup = new Dictionary<(ulong, string, long), FieldValue>(knnResults.Count * fieldColumns.Count);
            foreach (var bySeries in knnResults.GroupBy(static r => r.SeriesId))
            {
                ulong sid = bySeries.Key;
                long minTs = long.MaxValue, maxTs = long.MinValue;
                var tsSet = new HashSet<long>();
                foreach (var hit in bySeries)
                {
                    if (hit.Timestamp < minTs) minTs = hit.Timestamp;
                    if (hit.Timestamp > maxTs) maxTs = hit.Timestamp;
                    tsSet.Add(hit.Timestamp);
                }
                var range = new TimeRange(minTs, maxTs);
                for (int fi = 0; fi < fieldColumns.Count; fi++)
                {
                    string fname = fieldColumns[fi].Name;
                    foreach (var p in QueryPoints(tsdb, sid, fname, range))
                    {
                        if (!tsSet.Contains(p.Timestamp))
                            continue;
                        // M7 修复：同一 (sid, field, ts) 出现多次时（重复写入 / 段间未压缩）
                        // 旧的"每行单点查询"路径用 fieldPoints[0]——即"首次匹配的值"——作为返回。
                        // 这里也只接受首个，避免批量扫描把后续覆盖默默写回去导致结果集
                        // 表现不一致。压缩前后行为一致。
                        var key = (sid, fname, p.Timestamp);
                        if (!fieldLookup.ContainsKey(key))
                            fieldLookup[key] = p.Value;
                    }
                }
            }
        }

        // 构建结果行
        var rows = new List<IReadOnlyList<object?>>(knnResults.Count);
        foreach (var result in knnResults)
        {
            seriesById.TryGetValue(result.SeriesId, out var seriesEntry);
            var row = new object?[columnNames.Count];

            row[0] = result.Timestamp;
            row[1] = result.Distance;

            // tag 列
            for (int ti = 0; ti < tagColumns.Count; ti++)
            {
                row[2 + ti] = seriesEntry is not null
                    && seriesEntry.Tags.TryGetValue(tagColumns[ti].Name, out var tv)
                    ? tv
                    : null;
            }

            // field 列：从预构建的字典常数级查询。
            for (int fi = 0; fi < fieldColumns.Count; fi++)
            {
                string fname = fieldColumns[fi].Name;
                row[2 + tagColumns.Count + fi] = fieldLookup is not null
                    && fieldLookup.TryGetValue((result.SeriesId, fname, result.Timestamp), out var fv)
                    ? ConvertFieldValue(fv)
                    : null;
            }

            rows.Add(row);
        }

        return new SelectExecutionResult(columnNames, rows);
    }

    /// <summary>把查询向量字面量解析为 float[]（维度与列声明的一致性在 <see cref="ExecuteKnnSearch"/> 校验）。</summary>
    private static float[] ResolveQueryVector(SqlExpression arg)
    {
        if (arg is not VectorLiteralExpression vec)
            throw new InvalidOperationException(
                $"knn 第 3 个参数必须是向量字面量（例如 [0.1, 0.2, 0.3]）。");
        var arr = new float[vec.Components.Count];
        for (int i = 0; i < arr.Length; i++)
            arr[i] = (float)vec.Components[i];
        return arr;
    }

    /// <summary>解析 k 参数（正整数字面量）。</summary>
    private static int ResolveKnnK(SqlExpression arg)
    {
        if (arg is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: > 0 and <= int.MaxValue } lit)
            return (int)lit.IntegerValue;
        throw new InvalidOperationException("knn 参数 'k' 必须是正整数字面量。");
    }

    /// <summary>解析可选的 metric 参数字符串。</summary>
    private static KnnMetric ResolveKnnMetric(SqlExpression arg)
    {
        if (arg is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: { } s })
            throw new InvalidOperationException(
                "knn 第 5 个参数（metric）必须是字符串字面量：'cosine' / 'l2' / 'inner_product'。");
        return s.ToLowerInvariant() switch
        {
            "cosine" or "cosine_distance" => KnnMetric.Cosine,
            "l2" or "l2_distance" or "euclidean" => KnnMetric.L2,
            "inner_product" or "dot" or "ip" => KnnMetric.InnerProduct,
            _ => throw new InvalidOperationException(
                $"knn 不支持 metric '{s}'，仅支持 'cosine' / 'l2' / 'inner_product'。"),
        };
    }

    /// <summary>把 <see cref="FieldValue"/> 转换为结果行中的 object? 表示。</summary>
    private static object? ConvertFieldValue(FieldValue value) => value.Type switch
    {
        FieldType.Float64 => value.AsDouble(),
        FieldType.Int64 => value.AsLong(),
        FieldType.Boolean => value.AsBool(),
        FieldType.String => value.AsString(),
        FieldType.Vector => value.AsVector().ToArray(),
        FieldType.GeoPoint => value.AsGeoPoint(),
        _ => null,
    };
}
