using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
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
        var fieldCol = schema.TryGetColumn(fieldId.Name)
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
        var columnNames = new List<string>(4 + tagColumns.Count) { "time", "value", "lower", "upper" };
        // 防御性：tag 列名若与 forecast 内置输出列（time / value / lower / upper）冲突，
        // 后续 ResolveForecastProjectionPlan 的 OrdinalIgnoreCase 查表会被 tag 覆盖（last-write-wins），
        // 导致 SELECT time FROM forecast(...) 返回 tag 而非桶时间——静默错列。
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
            columnNames.Add(t.Name);
        }

        // SELECT 列表支持两种形式：
        //   1) SELECT *        → 输出 TVF 全部列（time/value/lower/upper + tag 列）。
        //   2) SELECT col[,..] → 列引用必须命中 TVF 输出列名（time/value/lower/upper 或 tag 名），
        //                        v1 暂不支持外层表达式 / 函数调用 / 字面量列。
        var projectionPlan = ResolveForecastProjectionPlan(statement.Projections, columnNames);

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
                var wide = new object?[columnNames.Count];
                wide[0] = p.TimestampMs;
                wide[1] = p.Value;
                wide[2] = p.Lower;
                wide[3] = p.Upper;
                for (int t = 0; t < tagColumns.Count; t++)
                    wide[4 + t] = series.Tags.TryGetValue(tagColumns[t].Name, out var tv) ? tv : null;
                rows.Add(projectionPlan.Apply(wide));
            }
        }

        return new SelectExecutionResult(projectionPlan.OutputColumns, rows);
    }

    /// <summary>
    /// 解析 forecast(...) 外层 SELECT 投影列表，返回从 TVF 完整宽表到输出投影的映射。
    /// </summary>
    private static ForecastProjectionPlan ResolveForecastProjectionPlan(
        IReadOnlyList<SelectItem> projections,
        IReadOnlyList<string> wideColumnNames)
    {
        if (IsSelectStar(projections))
            return ForecastProjectionPlan.PassThrough(wideColumnNames);

        var indexLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < wideColumnNames.Count; i++)
            indexLookup[wideColumnNames[i]] = i;

        var indices = new int[projections.Count];
        var outputNames = new string[projections.Count];
        for (int i = 0; i < projections.Count; i++)
        {
            var item = projections[i];
            if (item.Expression is not IdentifierExpression id)
                throw new InvalidOperationException(
                    "forecast(...) 外层 SELECT 仅支持 * 或简单列引用（time / value / lower / upper / tag 列）；"
                    + "v1 暂不支持表达式或函数调用投影。");
            if (!indexLookup.TryGetValue(id.Name, out int idx))
                throw new InvalidOperationException(
                    $"forecast(...) 不输出列 '{id.Name}'；可用列：{string.Join(", ", wideColumnNames)}。");
            indices[i] = idx;
            outputNames[i] = item.Alias ?? id.Name;
        }

        return new ForecastProjectionPlan(indices, outputNames);
    }

    private sealed class ForecastProjectionPlan
    {
        private readonly int[]? _indices;
        public IReadOnlyList<string> OutputColumns { get; }

        private ForecastProjectionPlan(IReadOnlyList<string> passThroughColumns)
        {
            _indices = null;
            OutputColumns = passThroughColumns;
        }

        public ForecastProjectionPlan(int[] indices, IReadOnlyList<string> outputColumns)
        {
            _indices = indices;
            OutputColumns = outputColumns;
        }

        public static ForecastProjectionPlan PassThrough(IReadOnlyList<string> wideColumns)
            => new(wideColumns);

        public IReadOnlyList<object?> Apply(object?[] wideRow)
        {
            if (_indices is null)
                return wideRow;
            var projected = new object?[_indices.Length];
            for (int i = 0; i < _indices.Length; i++)
                projected[i] = wideRow[_indices[i]];
            return projected;
        }
    }

    private static bool IsSelectStar(IReadOnlyList<SelectItem> projections)
        => projections.Count == 1 && projections[0].Expression is StarExpression && projections[0].Alias is null;

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
        var vectorCol = schema.TryGetColumn(columnId.Name)
            ?? throw new InvalidOperationException(
                $"knn 引用了未知列 '{columnId.Name}'。");
        if (vectorCol.Role != MeasurementColumnRole.Field)
            throw new InvalidOperationException(
                $"knn 的列参数 '{columnId.Name}' 必须是 FIELD 列。");
        if (vectorCol.DataType != FieldType.Vector)
            throw new InvalidOperationException(
                $"knn 的列参数 '{columnId.Name}' 必须是 VECTOR 类型，实际为 {vectorCol.DataType}。");
        int dim = vectorCol.VectorDimension
            ?? throw new InvalidOperationException(
                $"VECTOR 列 '{columnId.Name}' 缺少维度声明（schema 损坏）。");

        // 第 3 个参数：查询向量
        float[] queryArray = ResolveQueryVector(call.Arguments[2], dim, columnId.Name);

        // 第 4 个参数：k（正整数）
        int k = ResolveKnnK(call.Arguments[3]);

        // 第 5 个参数（可选）：距离度量
        var metric = call.Arguments.Count == 5
            ? ResolveKnnMetric(call.Arguments[4])
            : KnnMetric.Cosine;

        // SELECT * 校验
        if (!IsSelectStar(statement.Projections))
            throw new InvalidOperationException(
                "knn(...) 表值函数当前仅支持 SELECT *；请在外层查询投影具体列。");

        // WHERE 子句：tag 过滤 + 时间范围
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter).ToList();

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

        // 执行 KNN 搜索
        var knnResults = KnnExecutor.Execute(
            tsdb.MemTable,
            tsdb.Segments.Readers,
            matchedSeries,
            vectorCol.Name,
            queryArray.AsMemory(),
            k,
            metric,
            where.TimeRange,
            tsdb.Tombstones);

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
                        if (tsSet.Contains(p.Timestamp))
                            fieldLookup[(sid, fname, p.Timestamp)] = p.Value;
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

    /// <summary>把查询向量字面量解析为 float[] 并校验维度。</summary>
    private static float[] ResolveQueryVector(SqlExpression arg, int expectedDim, string columnName)
    {
        if (arg is not VectorLiteralExpression vec)
            throw new InvalidOperationException(
                $"knn 第 3 个参数必须是向量字面量（例如 [0.1, 0.2, 0.3]）。");
        if (vec.Components.Count != expectedDim)
            throw new InvalidOperationException(
                $"knn 查询向量维度 {vec.Components.Count} 与列 '{columnName}' 声明的维度 {expectedDim} 不一致。");
        var arr = new float[expectedDim];
        for (int i = 0; i < expectedDim; i++)
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
