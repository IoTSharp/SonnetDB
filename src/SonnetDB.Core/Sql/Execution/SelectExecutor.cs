using SonnetDB.Catalog;
using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Query.Functions;
using SonnetDB.Query.Functions.Forecasting;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>SELECT</c> 语句执行的内部辅助：处理投影分类、原始模式行构建、聚合模式桶合并。
/// 公共入口仍是 <see cref="SqlExecutor.ExecuteSelect"/>。
/// </summary>
internal static class SelectExecutor
{
    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ValidateTableAliasReferences(statement);

        if (statement.TableValuedFunction is not null)
            return ApplyOrderByAndPagination(TableValuedFunctionExecutor.Execute(tsdb, statement), statement.OrderBy, statement.Pagination);

        var schema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"Measurement '{statement.Measurement}' 不存在；请先执行 CREATE MEASUREMENT。");

        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        var matchedSeries = tsdb.Catalog.Find(statement.Measurement, where.TagFilter);

        // 分类投影
        var classified = ClassifyProjections(statement.Projections, schema);

        bool hasAggregate = classified.Any(p => p.Kind == ProjectionKind.Aggregate);
        var groupByTime = ResolveGroupByTime(statement.GroupBy);

        if (hasAggregate && HasUnsupportedNonAggregateProjection(classified, groupByTime))
            throw new InvalidOperationException(
                "SELECT 中不允许同时出现聚合函数与非聚合列（GROUP BY time(...) 查询中仅允许额外投影 time 作为 bucket 起始时间）。");

        if (groupByTime is not null && !hasAggregate)
            throw new InvalidOperationException(
                "GROUP BY time(...) 仅在聚合查询中有效。");

        if (!hasAggregate
            && groupByTime is null
            && TryExecuteLatestPointFastPath(
                tsdb,
                classified,
                matchedSeries,
                where,
                statement.OrderBy,
                statement.Pagination,
                out var latestResult))
        {
            return latestResult;
        }

        SelectExecutionResult result = hasAggregate
            ? ExecuteAggregate(tsdb, schema, classified, matchedSeries, where, groupByTime)
            : ExecuteRaw(tsdb, schema, classified, matchedSeries, where);

        return ApplyOrderByAndPagination(result, statement.OrderBy, statement.Pagination);
    }

    private static bool TryExecuteLatestPointFastPath(
        Tsdb tsdb,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<SeriesEntry> matchedSeries,
        WhereClause where,
        OrderBySpec? orderBy,
        PaginationSpec? pagination,
        out SelectExecutionResult result)
    {
        result = null!;

        if (matchedSeries.Count != 1
            || where.GeoFilters.Count != 0
            || where.Residual is not null
            || orderBy is not { Direction: SortDirection.Descending }
            || orderBy.Expression is not IdentifierExpression { Name: var orderColumn }
            || !string.Equals(orderColumn, "time", StringComparison.OrdinalIgnoreCase)
            || pagination is not { Offset: 0, Fetch: 1 })
        {
            return false;
        }

        if (!HasProjectionKind(projections, ProjectionKind.Time)
            || HasProjectionKind(projections, ProjectionKind.Aggregate)
            || HasProjectionKind(projections, ProjectionKind.Scalar)
            || HasProjectionKind(projections, ProjectionKind.Window))
        {
            return false;
        }

        MeasurementColumn? fieldColumn = null;
        for (int i = 0; i < projections.Count; i++)
        {
            var projection = projections[i];
            if (projection.Kind != ProjectionKind.Field)
                continue;

            if (fieldColumn is not null)
                return false;

            fieldColumn = projection.Column;
        }

        if (fieldColumn is null)
            return false;

        var series = matchedSeries[0];
        var columns = projections.Select(static p => p.ColumnName).ToList();
        if (!tsdb.Query.TryGetLatestPoint(series.Id, fieldColumn.Name, where.TimeRange, out var latestPoint))
        {
            result = new SelectExecutionResult(columns, []);
            return true;
        }

        var row = new object?[projections.Count];
        for (int i = 0; i < projections.Count; i++)
        {
            var projection = projections[i];
            row[i] = projection.Kind switch
            {
                ProjectionKind.Time => latestPoint.Timestamp,
                ProjectionKind.Tag => series.Tags.TryGetValue(projection.Column!.Name, out var tagValue) ? tagValue : null,
                ProjectionKind.Field => UnboxFieldValue(projection.Column!, latestPoint.Value),
                ProjectionKind.Constant => projection.ConstantValue,
                _ => throw new InvalidOperationException("内部错误：latest fast-path 收到了不支持的投影类型。"),
            };
        }

        result = new SelectExecutionResult(columns, new List<IReadOnlyList<object?>> { row });
        return true;
    }

    private static bool HasProjectionKind(IReadOnlyList<Projection> projections, ProjectionKind kind)
    {
        for (int i = 0; i < projections.Count; i++)
        {
            if (projections[i].Kind == kind)
                return true;
        }

        return false;
    }

    private static void ValidateTableAliasReferences(SelectStatement statement)
    {
        foreach (var identifier in EnumerateIdentifierReferences(statement))
        {
            if (identifier.Qualifier is null)
                continue;

            if (statement.TableAlias is null)
            {
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 要求 FROM 子句声明单表别名。");
            }

            if (!string.Equals(identifier.Qualifier, statement.TableAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 引用了未知别名 '{identifier.Qualifier}'；当前查询只声明了别名 '{statement.TableAlias}'。");
            }
        }
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifierReferences(SelectStatement statement)
    {
        foreach (var projection in statement.Projections)
        {
            foreach (var identifier in EnumerateIdentifierReferences(projection.Expression))
                yield return identifier;
        }

        if (statement.Where is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.Where))
                yield return identifier;
        }

        foreach (var groupBy in statement.GroupBy)
        {
            foreach (var identifier in EnumerateIdentifierReferences(groupBy))
                yield return identifier;
        }

        if (statement.TableValuedFunction is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.TableValuedFunction))
                yield return identifier;
        }

        if (statement.OrderBy is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.OrderBy.Expression))
                yield return identifier;
        }
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifierReferences(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                yield return identifier;
                yield break;

            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(argument))
                        yield return identifier;
                }
                yield break;

            case UnaryExpression unary:
                foreach (var identifier in EnumerateIdentifierReferences(unary.Operand))
                    yield return identifier;
                yield break;

            case BinaryExpression binary:
                foreach (var identifier in EnumerateIdentifierReferences(binary.Left))
                    yield return identifier;
                foreach (var identifier in EnumerateIdentifierReferences(binary.Right))
                    yield return identifier;
                yield break;

            default:
                yield break;
        }
    }

    /// <summary>
    /// 融合 ORDER BY time 与分页（#214）：有 Fetch 上限时走有界 Top-N，避免全量排序仅取 k 行。
    /// </summary>
    private static SelectExecutionResult ApplyOrderByAndPagination(
        SelectExecutionResult result,
        OrderBySpec? orderBy,
        PaginationSpec? pagination)
    {
        if (orderBy is null)
            return ApplyPagination(result, pagination);

        if (orderBy.Expression is not IdentifierExpression { Name: var name }
            || !string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("当前仅支持 ORDER BY time [ASC|DESC]。");
        }

        int timeColumnIndex = -1;
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], "time", StringComparison.OrdinalIgnoreCase))
            {
                timeColumnIndex = i;
                break;
            }
        }

        if (timeColumnIndex < 0)
            throw new InvalidOperationException("ORDER BY time 要求 SELECT 结果中包含 time 列。");

        bool descending = orderBy.Direction == SortDirection.Descending;
        var comparer = new TimeColumnComparer(timeColumnIndex, descending);

        var rows = TopN.OrderByThenPaginate(result.Rows, comparer, pagination?.Offset ?? 0, pagination?.Fetch);
        return new SelectExecutionResult(result.Columns, rows);
    }

    private sealed class TimeColumnComparer(int timeColumnIndex, bool descending)
        : IComparer<IReadOnlyList<object?>>
    {
        public int Compare(IReadOnlyList<object?>? x, IReadOnlyList<object?>? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            int c = RequireOrderByTimestamp(x[timeColumnIndex]).CompareTo(RequireOrderByTimestamp(y[timeColumnIndex]));
            return descending ? -c : c;
        }
    }

    private static long RequireOrderByTimestamp(object? value)
    {
        if (value is long timestamp)
            return timestamp;
        throw new InvalidOperationException("ORDER BY time 要求 time 列为整数时间戳。");
    }

    private static SelectExecutionResult ApplyPagination(SelectExecutionResult result, PaginationSpec? pagination)
    {
        if (pagination is null)
            return result;

        var offset = pagination.Offset;
        if (offset >= result.Rows.Count)
            return new SelectExecutionResult(result.Columns, []);

        int take = pagination.Fetch ?? (result.Rows.Count - offset);
        if (take <= 0)
            return new SelectExecutionResult(result.Columns, []);

        int actualTake = Math.Min(take, result.Rows.Count - offset);
        var slicedRows = result.Rows.Skip(offset).Take(actualTake).ToList();
        return new SelectExecutionResult(result.Columns, slicedRows);
    }

    // ── 投影分类 ───────────────────────────────────────────────────────────

    private enum ProjectionKind
    {
        Time,
        Tag,
        Field,
        Constant,
        Aggregate,
        Scalar,
        Window,
    }

    private sealed record Projection(
        string ColumnName,
        ProjectionKind Kind,
        MeasurementColumn? Column,
        FunctionCallExpression? Function,
        IScalarFunction? ScalarFunction = null,
        IWindowFunction? WindowFunction = null,
        object? ConstantValue = null);

    private static IReadOnlyList<Projection> ClassifyProjections(
        IReadOnlyList<SelectItem> items,
        MeasurementSchema schema)
    {
        var result = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    // 展开为 time + 所有 tag 列 + 所有 field 列
                    result.Add(new Projection("time", ProjectionKind.Time, null, null));
                    foreach (var col in schema.Columns)
                        result.Add(new Projection(
                            col.Name,
                            col.Role == MeasurementColumnRole.Tag ? ProjectionKind.Tag : ProjectionKind.Field,
                            col, null));
                    break;

                case IdentifierExpression id:
                    result.Add(BuildIdentifierProjection(id.Name, item.Alias, schema));
                    break;

                case LiteralExpression literal:
                    result.Add(new Projection(
                        item.Alias ?? FormatLiteralColumnName(literal),
                        ProjectionKind.Constant,
                        null,
                        null,
                        ConstantValue: EvaluateLiteral(literal)));
                    break;

                case FunctionCallExpression fn:
                    var kind = FunctionRegistry.GetFunctionKind(fn.Name);
                    if (kind == FunctionKind.Aggregate)
                    {
                        var aggColumnName = item.Alias ?? FormatFunctionColumnName(fn);
                        result.Add(new Projection(aggColumnName, ProjectionKind.Aggregate, null, fn));
                        break;
                    }

                    if (kind == FunctionKind.Scalar && FunctionRegistry.TryGetScalar(fn.Name, out var scalarFunction))
                    {
                        var scalarColumnName = item.Alias ?? FormatFunctionColumnName(fn);
                        result.Add(new Projection(scalarColumnName, ProjectionKind.Scalar, null, fn, scalarFunction));
                        break;
                    }

                    if (kind == FunctionKind.Window && FunctionRegistry.TryGetWindow(fn.Name, out var windowFunction))
                    {
                        var windowColumnName = item.Alias ?? FormatFunctionColumnName(fn);
                        result.Add(new Projection(
                            windowColumnName, ProjectionKind.Window, null, fn,
                            ScalarFunction: null, WindowFunction: windowFunction));
                        break;
                    }

                    if (kind == FunctionKind.TableValued)
                        throw new InvalidOperationException(
                            $"函数 '{fn.Name}' 已保留给后续里程碑，当前 SELECT 尚不支持。"
                        );

                    throw new InvalidOperationException(
                        $"未知函数 '{fn.Name}'；当前仅支持内置 aggregate/scalar 函数。"
                    );

                default:
                    throw new InvalidOperationException(
                        $"不支持的投影表达式类型 '{item.Expression.GetType().Name}'。");
            }
        }
        return result;
    }

    private static Projection BuildIdentifierProjection(string name, string? alias, MeasurementSchema schema)
    {
        if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
            return new Projection(alias ?? "time", ProjectionKind.Time, null, null);

        var col = schema.TryGetColumn(name)
            ?? throw new InvalidOperationException(
                $"SELECT 中引用了未知列 '{name}'。");
        var kind = col.Role == MeasurementColumnRole.Tag ? ProjectionKind.Tag : ProjectionKind.Field;
        return new Projection(alias ?? name, kind, col, null);
    }

    private static bool HasUnsupportedNonAggregateProjection(
        IReadOnlyList<Projection> projections,
        TimeBucketSpec? groupByTime)
    {
        foreach (var projection in projections)
        {
            if (projection.Kind == ProjectionKind.Aggregate)
                continue;

            if (groupByTime is not null && projection.Kind == ProjectionKind.Time)
                continue;

            return true;
        }

        return false;
    }

    private static string FormatFunctionColumnName(FunctionCallExpression fn)
    {
        if (fn.IsStar) return $"{fn.Name.ToLowerInvariant()}(*)";
        if (fn.Arguments.Count == 1 && fn.Arguments[0] is IdentifierExpression id)
            return $"{fn.Name.ToLowerInvariant()}({id.Name})";
        if (fn.Arguments.Count == 1 && fn.Arguments[0] is LiteralExpression literal)
            return $"{fn.Name.ToLowerInvariant()}({FormatLiteralColumnName(literal)})";
        return fn.Name.ToLowerInvariant();
    }

    private static string FormatLiteralColumnName(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => "NULL",
        SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
        SqlLiteralKind.Integer => literal.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqlLiteralKind.Float => literal.FloatValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SqlLiteralKind.String => literal.StringValue ?? string.Empty,
        _ => literal.Kind.ToString(),
    };

    // ── 原始模式 ───────────────────────────────────────────────────────────

    private static SelectExecutionResult ExecuteRaw(
        Tsdb tsdb,
        MeasurementSchema schema,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<SeriesEntry> matchedSeries,
        WhereClause where)
    {
        // 预先为每个窗口投影构造 evaluator（只构造一次：参数校验在此完成）。
        var windowEvaluators = new IWindowEvaluator?[projections.Count];
        for (int i = 0; i < projections.Count; i++)
        {
            if (projections[i].Kind == ProjectionKind.Window)
            {
                windowEvaluators[i] = projections[i].WindowFunction!.CreateEvaluator(
                    projections[i].Function!, schema);
            }
        }
        bool useStreamingWindowPath = CanUseStreamingWindowPath(windowEvaluators);
        // 有残差谓词时强制走物化路径：残差会跳过部分时间戳，流式窗口状态按 ts 连续推进会被破坏（#217）。
        if (where.Residual is not null)
            useStreamingWindowPath = false;

        // 收集 raw 模式中所有需要查询的 field 列（含残差谓词引用的 field 列）。
        var fieldCols = projections
            .Where(p => p.Kind == ProjectionKind.Field)
            .Select(p => p.Column!.Name)
            .Concat(GetScalarFieldDependencies(projections))
            .Concat(windowEvaluators.OfType<IWindowEvaluator>().Select(e => e.FieldName))
            .Concat(where.GeoFilters.Select(f => f.FieldName))
            .Concat(GetResidualFieldDependencies(where.Residual, schema))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var rows = new List<IReadOnlyList<object?>>();
        var geoFiltersByField = where.GeoFilters
            .GroupBy(f => f.FieldName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        foreach (var series in matchedSeries)
        {
            // 每个 series 内：以所有目标 field 列时间戳的并集作为行集合（外连接）。
            // 缺失字段输出 null。若没有 field 投影，则用 schema 第一个 field 的时间戳作为时间轴。
            var fieldData = new Dictionary<string, IReadOnlyList<DataPoint>>(StringComparer.Ordinal);
            if (fieldCols.Count == 0)
            {
                var probeField = schema.FieldColumns.First().Name;
                fieldData[probeField] = QueryPoints(tsdb, series.Id, probeField, where.TimeRange);
            }
            else
            {
                foreach (var fname in fieldCols)
                {
                    geoFiltersByField.TryGetValue(fname, out var geoFilters);
                    fieldData[fname] = QueryPoints(tsdb, series.Id, fname, where.TimeRange, geoFilters);
                }
            }

            // 时间戳并集
            var timestampSet = new SortedSet<long>();
            foreach (var (_, list) in fieldData)
                foreach (var dp in list) timestampSet.Add(dp.Timestamp);
            if (timestampSet.Count == 0) continue;

            // 每个 field 的 ts→value 字典（按需）
            var fieldLookups = new Dictionary<string, Dictionary<long, FieldValue>>(StringComparer.Ordinal);
            foreach (var fname in fieldCols)
            {
                var dict = new Dictionary<long, FieldValue>(fieldData[fname].Count);
                foreach (var dp in fieldData[fname]) dict[dp.Timestamp] = dp.Value;
                fieldLookups[fname] = dict;
            }

            if (useStreamingWindowPath)
            {
                AppendStreamingRawRows(rows, projections, windowEvaluators, timestampSet, series, fieldLookups);
            }
            else
            {
                AppendMaterializedRawRows(rows, projections, windowEvaluators, timestampSet, series, fieldLookups, where.Residual);
            }
        }

        var columnNames = projections.Select(p => p.ColumnName).ToList();
        return new SelectExecutionResult(columnNames, rows);
    }

    private static bool CanUseStreamingWindowPath(IReadOnlyList<IWindowEvaluator?> windowEvaluators)
    {
        for (int i = 0; i < windowEvaluators.Count; i++)
        {
            if (windowEvaluators[i] is not null and not IWindowStreamingEvaluator)
                return false;
        }

        return true;
    }

    private static void AppendStreamingRawRows(
        List<IReadOnlyList<object?>> rows,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<IWindowEvaluator?> windowEvaluators,
        SortedSet<long> timestampSet,
        SeriesEntry series,
        Dictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        var windowStates = new IWindowState?[windowEvaluators.Count];
        for (int i = 0; i < windowEvaluators.Count; i++)
        {
            if (windowEvaluators[i] is IWindowStreamingEvaluator streaming)
                windowStates[i] = streaming.CreateState();
        }

        foreach (long ts in timestampSet)
        {
            var row = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
            {
                var p = projections[i];
                row[i] = p.Kind switch
                {
                    ProjectionKind.Time => ts,
                    ProjectionKind.Tag => series.Tags.TryGetValue(p.Column!.Name, out var tagVal) ? tagVal : null,
                    ProjectionKind.Field => fieldLookups[p.Column!.Name].TryGetValue(ts, out var v)
                        ? UnboxFieldValue(p.Column, v)
                        : null,
                    ProjectionKind.Constant => p.ConstantValue,
                    ProjectionKind.Scalar => EvaluateScalarProjection(p, ts, series, fieldLookups),
                    ProjectionKind.Window => EvaluateStreamingWindowProjection(
                        windowEvaluators[i]!, windowStates[i]!, ts, fieldLookups),
                    _ => throw new InvalidOperationException("内部错误：不应在 raw 模式出现聚合投影。"),
                };
            }
            rows.Add(row);
        }
    }

    private static object? EvaluateStreamingWindowProjection(
        IWindowEvaluator evaluator,
        IWindowState state,
        long timestamp,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        FieldValue? input = fieldLookups.TryGetValue(evaluator.FieldName, out var lookup)
            && lookup.TryGetValue(timestamp, out var value)
                ? value
                : null;

        return state.Update(timestamp, input).ToObject();
    }

    private static void AppendMaterializedRawRows(
        List<IReadOnlyList<object?>> rows,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<IWindowEvaluator?> windowEvaluators,
        SortedSet<long> timestampSet,
        SeriesEntry series,
        Dictionary<string, Dictionary<long, FieldValue>> fieldLookups,
        SqlExpression? residual)
    {
        var timestamps = timestampSet.ToArray();

        // 为每个窗口投影预计算输出（与 timestamps 数组同长度，逐行对齐）。
        // 数值型 evaluator 优先走 double[] typed 输出，避免先构造 object?[] 造成每行装箱。
        var windowOutputs = new WindowProjectionOutput?[projections.Count];
        for (int i = 0; i < projections.Count; i++)
        {
            var evaluator = windowEvaluators[i];
            if (evaluator is null) continue;

            var alignedValues = new FieldValue?[timestamps.Length];
            if (fieldLookups.TryGetValue(evaluator.FieldName, out var lookup))
            {
                for (int row = 0; row < timestamps.Length; row++)
                {
                    if (lookup.TryGetValue(timestamps[row], out var v))
                        alignedValues[row] = v;
                }
            }

            windowOutputs[i] = evaluator is IWindowDoubleEvaluator doubleEvaluator
                ? WindowProjectionOutput.FromDouble(doubleEvaluator.ComputeDouble(timestamps, alignedValues))
                : WindowProjectionOutput.FromObject(evaluator.Compute(timestamps, alignedValues));
        }

        for (int rowIdx = 0; rowIdx < timestamps.Length; rowIdx++)
        {
            long ts = timestamps[rowIdx];

            // #217：残差谓词逐点过滤——仅保留在该时间戳上确定为 TRUE 的行。
            if (residual is not null && !ResidualHoldsAtPoint(residual, ts, series, fieldLookups))
                continue;

            var row = new object?[projections.Count];
            for (int i = 0; i < projections.Count; i++)
            {
                var p = projections[i];
                row[i] = p.Kind switch
                {
                    ProjectionKind.Time => ts,
                    ProjectionKind.Tag => series.Tags.TryGetValue(p.Column!.Name, out var tagVal) ? tagVal : null,
                    ProjectionKind.Field => fieldLookups[p.Column!.Name].TryGetValue(ts, out var v)
                        ? UnboxFieldValue(p.Column, v)
                        : null,
                    ProjectionKind.Constant => p.ConstantValue,
                    ProjectionKind.Scalar => EvaluateScalarProjection(p, ts, series, fieldLookups),
                    ProjectionKind.Window => windowOutputs[i]!.GetValue(rowIdx),
                    _ => throw new InvalidOperationException("内部错误：不应在 raw 模式出现聚合投影。"),
                };
            }
            rows.Add(row);
        }
    }

    private sealed class WindowProjectionOutput
    {
        private readonly object?[]? _objectValues;
        private readonly WindowDoubleOutput? _doubleValues;

        private WindowProjectionOutput(object?[] objectValues)
        {
            _objectValues = objectValues;
        }

        private WindowProjectionOutput(WindowDoubleOutput doubleValues)
        {
            _doubleValues = doubleValues;
        }

        public static WindowProjectionOutput FromObject(object?[] values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return new WindowProjectionOutput(values);
        }

        public static WindowProjectionOutput FromDouble(WindowDoubleOutput values)
        {
            ArgumentNullException.ThrowIfNull(values);
            return new WindowProjectionOutput(values);
        }

        public object? GetValue(int rowIndex)
        {
            if (_doubleValues is { } typed)
                return typed.TryGetValue(rowIndex, out double value) ? value : null;

            return _objectValues![rowIndex];
        }
    }

    private static IEnumerable<string> GetScalarFieldDependencies(IReadOnlyList<Projection> projections)
    {
        foreach (var projection in projections)
        {
            if (projection.Kind != ProjectionKind.Scalar || projection.Function is null)
                continue;

            foreach (var fieldName in GetScalarFieldDependencies(projection.Function))
                yield return fieldName;
        }
    }

    private static IEnumerable<string> GetScalarFieldDependencies(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression id when !string.Equals(id.Name, "time", StringComparison.OrdinalIgnoreCase):
                yield return id.Name;
                yield break;
            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments)
                    foreach (var fieldName in GetScalarFieldDependencies(arg))
                        yield return fieldName;
                yield break;
            case UnaryExpression unary:
                foreach (var fieldName in GetScalarFieldDependencies(unary.Operand))
                    yield return fieldName;
                yield break;
            case BinaryExpression binary:
                foreach (var fieldName in GetScalarFieldDependencies(binary.Left))
                    yield return fieldName;
                foreach (var fieldName in GetScalarFieldDependencies(binary.Right))
                    yield return fieldName;
                yield break;
            default:
                yield break;
        }
    }

    /// <summary>
    /// 收集残差谓词引用的 field 列名（#217）——这些列需查询进 fieldLookups 供逐点求值。
    /// 只产出 schema 中确实是 FIELD 角色的列；tag 列（常量于 SeriesEntry）与 <c>time</c> 不产出。
    /// </summary>
    private static IEnumerable<string> GetResidualFieldDependencies(SqlExpression? residual, MeasurementSchema schema)
    {
        if (residual is null)
            yield break;

        foreach (var name in CollectIdentifierNames(residual))
        {
            if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
                continue;
            if (schema.TryGetColumn(name) is { Role: MeasurementColumnRole.Field })
                yield return name;
        }
    }

    /// <summary>递归收集表达式中出现的全部标识符名（含残差可能出现的 IS NULL / IN / NOT / 比较等节点）。</summary>
    private static IEnumerable<string> CollectIdentifierNames(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression id:
                yield return id.Name;
                yield break;
            case BinaryExpression binary:
                foreach (var n in CollectIdentifierNames(binary.Left)) yield return n;
                foreach (var n in CollectIdentifierNames(binary.Right)) yield return n;
                yield break;
            case UnaryExpression unary:
                foreach (var n in CollectIdentifierNames(unary.Operand)) yield return n;
                yield break;
            case IsNullExpression isNull:
                foreach (var n in CollectIdentifierNames(isNull.Operand)) yield return n;
                yield break;
            case InExpression inExpr:
                foreach (var n in CollectIdentifierNames(inExpr.Value)) yield return n;
                foreach (var item in inExpr.Values)
                    foreach (var n in CollectIdentifierNames(item)) yield return n;
                yield break;
            case FunctionCallExpression fn:
                foreach (var arg in fn.Arguments)
                    foreach (var n in CollectIdentifierNames(arg)) yield return n;
                yield break;
            default:
                yield break;
        }
    }

    /// <summary>为某 series 构建残差引用 field 列的 ts→FieldValue 查表，供聚合路径逐点残差求值（#217）。</summary>
    private static Dictionary<string, Dictionary<long, FieldValue>> BuildResidualLookups(
        Tsdb tsdb,
        SeriesEntry series,
        TimeRange timeRange,
        IReadOnlyList<string> residualFieldCols)
    {
        var lookups = new Dictionary<string, Dictionary<long, FieldValue>>(StringComparer.Ordinal);
        foreach (var fname in residualFieldCols)
        {
            var pts = QueryPoints(tsdb, series.Id, fname, timeRange);
            var dict = new Dictionary<long, FieldValue>(pts.Count);
            foreach (var dp in pts)
                dict[dp.Timestamp] = dp.Value;
            lookups[fname] = dict;
        }
        return lookups;
    }

    /// <summary>
    /// 收集某 series 上残差 WHERE 谓词逐点求值确定为 TRUE 的时间戳集合（#219 值定向 DELETE 复用）。
    /// 语义与 #217 物化路径完全一致：tag/time 已由 <paramref name="where"/> 下推过滤，此处只对时间窗内
    /// 所有 field 列出现过的时间戳并集逐点求值残差三值 Kleene，仅保留确定 TRUE 的时刻。
    /// </summary>
    internal static IReadOnlyList<long> CollectResidualMatchedTimestamps(
        Tsdb tsdb,
        MeasurementSchema schema,
        SeriesEntry series,
        WhereClause where)
    {
        if (where.Residual is null)
            throw new InvalidOperationException("内部错误：无残差谓词时不应走逐点匹配路径。");

        var residualFieldCols = GetResidualFieldDependencies(where.Residual, schema)
            .Distinct(StringComparer.Ordinal).ToList();
        var residualLookups = BuildResidualLookups(tsdb, series, where.TimeRange, residualFieldCols);

        // 时间窗内该 series 所有 field 列写过的时间戳并集 = 候选行/时刻集合。
        var candidateTimestamps = new HashSet<long>();
        foreach (var col in schema.FieldColumns)
        {
            var geoFilters = where.GeoFilters
                .Where(f => string.Equals(f.FieldName, col.Name, StringComparison.Ordinal))
                .ToArray();
            foreach (var dp in QueryPoints(tsdb, series.Id, col.Name, where.TimeRange, geoFilters))
                candidateTimestamps.Add(dp.Timestamp);
        }

        var matched = new List<long>();
        foreach (var ts in candidateTimestamps)
        {
            if (ResidualHoldsAtPoint(where.Residual, ts, series, residualLookups))
                matched.Add(ts);
        }
        return matched;
    }

    /// <summary>
    /// 校验残差谓词只引用已知列（time / tag / field）。DELETE 用它在无数据时也能对未知列硬报错，
    /// 而非静默零删除（逐点路径在无候选点时不会触发未知列错误）。
    /// </summary>
    internal static void ValidateResidualColumns(SqlExpression residual, MeasurementSchema schema)
    {
        foreach (var name in CollectIdentifierNames(residual))
        {
            if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
                continue;
            if (schema.TryGetColumn(name) is null)
                throw new InvalidOperationException($"WHERE 中引用了未知列 '{name}'。");
        }
    }

    private static object? EvaluateScalarProjection(
        Projection projection,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        var scalarFunction = projection.ScalarFunction
            ?? throw new InvalidOperationException("内部错误：缺少标量函数实现。");
        var function = projection.Function
            ?? throw new InvalidOperationException("内部错误：缺少函数调用表达式。");

        var args = new object?[function.Arguments.Count];
        for (int i = 0; i < function.Arguments.Count; i++)
            args[i] = EvaluateScalarArgument(function.Arguments[i], timestamp, series, fieldLookups);

        return scalarFunction.Evaluate(args);
    }

    private static object? EvaluateScalarArgument(
        SqlExpression expression,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        return expression switch
        {
            IdentifierExpression id when string.Equals(id.Name, "time", StringComparison.OrdinalIgnoreCase)
                => timestamp,
            IdentifierExpression id when series.Tags.TryGetValue(id.Name, out var tagValue)
                => tagValue,
            IdentifierExpression id when fieldLookups.TryGetValue(id.Name, out var values)
                => values.TryGetValue(timestamp, out var value) ? UnboxFieldValue(value) : null,
            IdentifierExpression id when fieldLookups.ContainsKey(id.Name)
                => null,
            IdentifierExpression id
                => throw new InvalidOperationException($"SELECT 中引用了未知列 '{id.Name}'。"),
            LiteralExpression literal => EvaluateLiteral(literal),
            UnaryExpression unary => EvaluateUnaryExpression(unary, timestamp, series, fieldLookups),
            BinaryExpression binary => EvaluateBinaryExpression(binary, timestamp, series, fieldLookups),
            VectorLiteralExpression vector => EvaluateVectorLiteral(vector),
            GeoPointLiteralExpression geoPoint => GeoPoint.Create(geoPoint.Lat, geoPoint.Lon),
            FunctionCallExpression nested when FunctionRegistry.TryGetScalar(nested.Name, out var scalarFunction)
                => EvaluateNestedScalarFunction(nested, scalarFunction, timestamp, series, fieldLookups),
            FunctionCallExpression nested
                => throw new InvalidOperationException($"标量上下文不支持函数 '{nested.Name}'。"),
            _ => throw new InvalidOperationException(
                $"不支持的标量表达式类型 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateNestedScalarFunction(
        FunctionCallExpression function,
        IScalarFunction scalarFunction,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        if (function.IsStar)
            throw new InvalidOperationException($"标量函数 {function.Name}(*) 非法。");

        var args = new object?[function.Arguments.Count];
        for (int i = 0; i < function.Arguments.Count; i++)
            args[i] = EvaluateScalarArgument(function.Arguments[i], timestamp, series, fieldLookups);
        return scalarFunction.Evaluate(args);
    }

    // ── 残差谓词逐点求值（#217，三值 Kleene 逻辑）──────────────────────────────

    /// <summary>
    /// 在某数据点 (timestamp) 上求值残差 WHERE 谓词，仅当结果确定为 TRUE 时保留该点；
    /// UNKNOWN（NULL 传播）与 FALSE 一样丢弃（与 #197 关系路径三值语义一致）。
    /// </summary>
    private static bool ResidualHoldsAtPoint(
        SqlExpression residual,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
        => EvaluateResidualKleene(residual, timestamp, series, fieldLookups) == true;

    private static bool? EvaluateResidualKleene(
        SqlExpression expression,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                {
                    var l = EvaluateResidualKleene(binary.Left, timestamp, series, fieldLookups);
                    if (l == false) return false;
                    var r = EvaluateResidualKleene(binary.Right, timestamp, series, fieldLookups);
                    if (r == false) return false;
                    return l is null || r is null ? null : true;
                }
                if (binary.Operator == SqlBinaryOperator.Or)
                {
                    var l = EvaluateResidualKleene(binary.Left, timestamp, series, fieldLookups);
                    if (l == true) return true;
                    var r = EvaluateResidualKleene(binary.Right, timestamp, series, fieldLookups);
                    if (r == true) return true;
                    return l is null || r is null ? null : false;
                }
                if (IsResidualComparisonOperator(binary.Operator))
                    return EvaluateResidualComparison(binary, timestamp, series, fieldLookups);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } notExpr:
                {
                    var operand = EvaluateResidualKleene(notExpr.Operand, timestamp, series, fieldLookups);
                    return operand is null ? null : !operand;
                }

            case IsNullExpression isNull:
                {
                    var isNullValue = EvaluateScalarArgument(isNull.Operand, timestamp, series, fieldLookups) is null;
                    return isNull.Negated ? !isNullValue : isNullValue;
                }

            case InExpression inExpr:
                return EvaluateResidualIn(inExpr, timestamp, series, fieldLookups);
        }

        var value = EvaluateScalarArgument(expression, timestamp, series, fieldLookups);
        if (value is null)
            return null;
        if (value is bool b)
            return b;
        throw new InvalidOperationException("WHERE 残差谓词必须计算为布尔值。");
    }

    private static bool? EvaluateResidualComparison(
        BinaryExpression binary,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        var left = EvaluateScalarArgument(binary.Left, timestamp, series, fieldLookups);
        var right = EvaluateScalarArgument(binary.Right, timestamp, series, fieldLookups);

        // 三值逻辑：任一操作数为 NULL，比较结果为 UNKNOWN。检测 NULL 用 IS [NOT] NULL。
        if (left is null || right is null)
            return null;

        int? compare = CompareResidualValues(left, right);
        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => ResidualValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !ResidualValuesEqual(left, right),
            SqlBinaryOperator.LessThan => compare is < 0,
            SqlBinaryOperator.LessThanOrEqual => compare is <= 0,
            SqlBinaryOperator.GreaterThan => compare is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => compare is >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException($"WHERE 残差不支持比较运算符 {binary.Operator}。"),
        };
    }

    private static bool? EvaluateResidualIn(
        InExpression expression,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        if (expression.Subquery is not null)
            throw new InvalidOperationException("measurement WHERE 不支持 IN 子查询。");

        var value = EvaluateScalarArgument(expression.Value, timestamp, series, fieldLookups);
        if (value is null)
            return null;

        var sawNull = false;
        foreach (var item in expression.Values)
        {
            var candidate = EvaluateScalarArgument(item, timestamp, series, fieldLookups);
            if (candidate is null) { sawNull = true; continue; }
            if (ResidualValuesEqual(value, candidate))
                return expression.Negated ? false : true;
        }

        if (sawNull)
            return null;
        return expression.Negated ? true : false;
    }

    private static bool IsResidualComparisonOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual or
        SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual or
        SqlBinaryOperator.Like or SqlBinaryOperator.NotLike or
        SqlBinaryOperator.Regex or SqlBinaryOperator.NotRegex;

    private static bool ResidualValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        if (IsResidualNumeric(left) && IsResidualNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));
        return Equals(left, right);
    }

    private static int? CompareResidualValues(object? left, object? right)
    {
        if (left is null || right is null)
            return null;
        if (IsResidualNumeric(left) && IsResidualNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));
        if (left is string ls && right is string rs)
            return string.Compare(ls, rs, StringComparison.Ordinal);
        if (left is bool lb && right is bool rb)
            return lb.CompareTo(rb);
        return null;
    }

    private static bool IsResidualNumeric(object value) => value is
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;


    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    private static float[] EvaluateVectorLiteral(VectorLiteralExpression vector)
    {
        var result = new float[vector.Components.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = checked((float)vector.Components[i]);
        return result;
    }

    private static object? EvaluateUnaryExpression(
        UnaryExpression expression,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        var operand = EvaluateScalarArgument(expression.Operand, timestamp, series, fieldLookups);
        return expression.Operator switch
        {
            SqlUnaryOperator.Negate => -RequireDouble(operand, "一元负号"),
            SqlUnaryOperator.Not => !RequireBoolean(operand, "NOT"),
            _ => throw new InvalidOperationException($"不支持的一元运算 {expression.Operator}。"),
        };
    }

    private static object? EvaluateBinaryExpression(
        BinaryExpression expression,
        long timestamp,
        SeriesEntry series,
        IReadOnlyDictionary<string, Dictionary<long, FieldValue>> fieldLookups)
    {
        var left = EvaluateScalarArgument(expression.Left, timestamp, series, fieldLookups);
        var right = EvaluateScalarArgument(expression.Right, timestamp, series, fieldLookups);

        return expression.Operator switch
        {
            SqlBinaryOperator.Add => RequireDouble(left, "+") + RequireDouble(right, "+"),
            SqlBinaryOperator.Subtract => RequireDouble(left, "-") - RequireDouble(right, "-"),
            SqlBinaryOperator.Multiply => RequireDouble(left, "*") * RequireDouble(right, "*"),
            SqlBinaryOperator.Divide => RequireDouble(left, "/") / RequireDouble(right, "/"),
            SqlBinaryOperator.Modulo => RequireDouble(left, "%") % RequireDouble(right, "%"),
            _ => throw new InvalidOperationException($"标量函数参数内不支持运算 {expression.Operator}。"),
        };
    }

    private static bool RequireBoolean(object? value, string operatorName)
    {
        if (value is bool b) return b;
        throw new InvalidOperationException($"运算 {operatorName} 需要布尔参数。");
    }

    private static double RequireDouble(object? value, string functionName)
    {
        return value switch
        {
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => ul,
            float f => f,
            double d => d,
            decimal m => (double)m,
            null => throw new InvalidOperationException($"函数/运算 {functionName} 不接受 NULL 参数。"),
            _ => throw new InvalidOperationException($"函数/运算 {functionName} 需要数值参数。"),
        };
    }

    private static TimeBucketSpec? ResolveGroupByTime(IReadOnlyList<SqlExpression> groupBy)
    {
        if (groupBy.Count == 0)
            return null;

        if (groupBy.Count != 1 || groupBy[0] is not FunctionCallExpression fn
            || !string.Equals(fn.Name, "time", StringComparison.OrdinalIgnoreCase)
            || fn.IsStar
            || fn.Arguments.Count != 1
            || fn.Arguments[0] is not DurationLiteralExpression duration)
        {
            throw new InvalidOperationException("当前仅支持 GROUP BY time(duration)。");
        }

        if (duration.Milliseconds <= 0)
            throw new InvalidOperationException("GROUP BY time(...) 桶大小必须 > 0。");

        return new TimeBucketSpec(duration.Milliseconds);
    }

    private static IReadOnlyList<DataPoint> QueryPoints(Tsdb tsdb, ulong seriesId, string fieldName, TimeRange range)
    {
        var query = new PointQuery(seriesId, fieldName, range);
        return tsdb.Query.Execute(query).ToList();
    }

    private static IReadOnlyList<DataPoint> QueryPoints(
        Tsdb tsdb,
        ulong seriesId,
        string fieldName,
        TimeRange range,
        IReadOnlyList<GeoPointWhereFilter>? geoFilters)
    {
        if (geoFilters is null || geoFilters.Count == 0)
            return QueryPoints(tsdb, seriesId, fieldName, range);

        var query = new PointQuery(seriesId, fieldName, range, GeoFilter: geoFilters[0].QueryFilter);
        return tsdb.Query.Execute(query)
            .Where(dp => MatchesGeoFilters(dp, geoFilters))
            .ToList();
    }

    private static bool MatchesGeoFilters(DataPoint point, IReadOnlyList<GeoPointWhereFilter> geoFilters)
    {
        var value = point.Value.AsGeoPoint();
        for (int i = 0; i < geoFilters.Count; i++)
        {
            var filter = geoFilters[i];
            if (!FunctionRegistry.TryGetScalar(filter.Predicate.Name, out var scalar))
                return false;

            var args = new object?[filter.Predicate.Arguments.Count];
            args[0] = value;
            for (int a = 1; a < args.Length; a++)
                args[a] = EvaluateLiteral((LiteralExpression)filter.Predicate.Arguments[a]);

            if (scalar.Evaluate(args) is not true)
                return false;
        }
        return true;
    }

    private static object UnboxFieldValue(FieldValue v) => v.Type switch
    {
        FieldType.Float64 => v.AsDouble(),
        FieldType.Int64 => v.AsLong(),
        FieldType.Boolean => v.AsBool(),
        FieldType.String => v.AsString(),
        FieldType.Vector => v.AsVector().ToArray(),
        FieldType.GeoPoint => v.AsGeoPoint(),
        _ => throw new InvalidOperationException($"不支持的 FieldType {v.Type}。"),
    };

    private static object UnboxFieldValue(MeasurementColumn column, FieldValue value)
    {
        if (column.DataType == FieldType.Float64 && value.Type == FieldType.Int64)
            return (double)value.AsLong();
        return UnboxFieldValue(value);
    }

    // ── 聚合模式 ───────────────────────────────────────────────────────────

    private static SelectExecutionResult ExecuteAggregate(
        Tsdb tsdb,
        MeasurementSchema schema,
        IReadOnlyList<Projection> projections,
        IReadOnlyList<SeriesEntry> matchedSeries,
        WhereClause where,
        TimeBucketSpec? groupByTime)
    {
        long bucketSizeMs = groupByTime?.BucketSizeMs ?? 0;

        // 解析每个聚合投影：legacy 7 个聚合走 BucketState 快路径；
        // 扩展聚合（PR #52：stddev / percentile / tdigest_agg / ...）走 IAggregateAccumulator。
        var aggregateProjections = projections
            .Where(static p => p.Kind == ProjectionKind.Aggregate)
            .ToList();

        var aggSpecs = aggregateProjections.Select(p =>
        {
            var fn = p.Function!;
            var spec = ResolveAggregateSpec(fn, p.ColumnName, schema);
            if (spec.LegacyAggregator is Aggregator.First or Aggregator.Last
                && matchedSeries.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{spec.LegacyAggregator} 聚合在多 series 场景下尚未支持（v1）；请用 WHERE 过滤到单一 series。");
            }
            return spec;
        }).ToList();

        // 为每个 (bucketStart, specIdx) 维护 AggSlot：legacy 用 BucketState，扩展聚合用 IAggregateAccumulator。
        var bucketAccumulators = new SortedDictionary<long, AggSlot[]>();

        // #217：残差谓词按数据点过滤——预建每 series 的 ts→FieldValue 查表（含残差引用的 field 列）。
        var residualFieldCols = where.Residual is null
            ? []
            : GetResidualFieldDependencies(where.Residual, schema).Distinct(StringComparer.Ordinal).ToList();

        for (int specIdx = 0; specIdx < aggSpecs.Count; specIdx++)
        {
            var spec = aggSpecs[specIdx];

            if (spec.IsCountStar)
            {
                // count(*) 语义：计"行/时刻"数，而非逐 field 列累加 field 值点。
                AccumulateCountStar(
                    tsdb, schema, matchedSeries, where, bucketSizeMs, aggSpecs, specIdx, bucketAccumulators);
                continue;
            }

            var fields = new[] { spec.FieldName! };

            foreach (var series in matchedSeries)
            {
                // 残差查表（每 series 构建一次，供逐点残差求值）。
                var residualLookups = BuildResidualLookups(tsdb, series, where.TimeRange, residualFieldCols);

                foreach (var fname in fields)
                {
                    var col = schema.TryGetColumn(fname);

                    var geoFilters = where.GeoFilters
                        .Where(f => string.Equals(f.FieldName, fname, StringComparison.Ordinal))
                        .ToArray();

                    // 有残差时禁用扩展聚合 sidecar 快路径（它绕过逐点迭代，无法应用残差过滤）。
                    if (where.Residual is null && CanUseExtendedAggregateSketchPath(spec, bucketSizeMs, geoFilters))
                    {
                        bool existed = bucketAccumulators.TryGetValue(long.MinValue, out var slots);
                        slots ??= CreateAggSlots(aggSpecs);

                        if (slots[specIdx].TryUpdateExtendedFromSidecar(
                                tsdb,
                                series.Id,
                                fname,
                                where.TimeRange,
                                out long observedCount))
                        {
                            if (!existed && observedCount > 0)
                                bucketAccumulators[long.MinValue] = slots;

                            continue;
                        }
                    }

                    var points = QueryPoints(tsdb, series.Id, fname, where.TimeRange, geoFilters);
                    foreach (var dp in points)
                    {
                        // 残差逐点过滤：仅纳入残差在该时间戳上确定为 TRUE 的点。
                        if (where.Residual is not null
                            && !ResidualHoldsAtPoint(where.Residual, dp.Timestamp, series, residualLookups))
                            continue;

                        long bucketStart = bucketSizeMs > 0
                            ? TimeBucket.Floor(dp.Timestamp, bucketSizeMs)
                            : long.MinValue;

                        if (!bucketAccumulators.TryGetValue(bucketStart, out var slots))
                        {
                            slots = CreateAggSlots(aggSpecs);
                            bucketAccumulators[bucketStart] = slots;
                        }

                        // count(field) 只需时间戳；其他聚合需要把字段值转为 double
                        bool needsValue = spec.LegacyAggregator != Aggregator.Count;
                        if (!needsValue)
                        {
                            slots[specIdx].UpdateCount(dp.Timestamp);
                        }
                        else
                        {
                            slots[specIdx].Update(dp.Timestamp, dp.Value, col);
                        }
                    }
                }
            }
        }

        var rows = new List<IReadOnlyList<object?>>(bucketAccumulators.Count);
        foreach (var (bucketStart, slots) in bucketAccumulators)
        {
            var row = new object?[projections.Count];
            var aggregateIndex = 0;
            for (int i = 0; i < projections.Count; i++)
            {
                row[i] = projections[i].Kind switch
                {
                    ProjectionKind.Time => bucketStart,
                    ProjectionKind.Aggregate => slots[aggregateIndex++].Finalize(),
                    _ => throw new InvalidOperationException("内部错误：聚合模式仅支持聚合函数与 GROUP BY time(...) 的 time 投影。"),
                };
            }

            rows.Add(row);
        }

        var columnNames = projections.Select(p => p.ColumnName).ToList();
        return new SelectExecutionResult(columnNames, rows);
    }

    /// <summary>
    /// <c>count(*)</c> 语义：计"行/时刻"数——把每个 series 下所有 field 列写过的时间戳取并集去重后计数，
    /// 而非旧实现遍历每个 field 列逐点累加（M 个 field × N 时刻会误返 M×N）。多 series 场景下不同
    /// series 的同一时间戳属于不同的行，分别计入。GROUP BY time(...) 时按桶分别取时间戳并集。
    /// </summary>
    private static void AccumulateCountStar(
        Tsdb tsdb,
        MeasurementSchema schema,
        IReadOnlyList<SeriesEntry> matchedSeries,
        WhereClause where,
        long bucketSizeMs,
        IReadOnlyList<AggSpec> aggSpecs,
        int specIdx,
        SortedDictionary<long, AggSlot[]> bucketAccumulators)
    {
        var fieldNames = schema.FieldColumns.Select(c => c.Name).ToList();

        var residualFieldCols = where.Residual is null
            ? []
            : GetResidualFieldDependencies(where.Residual, schema).Distinct(StringComparer.Ordinal).ToList();

        foreach (var series in matchedSeries)
        {
            // 每个 bucket 下该 series 所有 field 列出现过的时间戳并集 = 行/时刻集合。
            var bucketTimestamps = new Dictionary<long, HashSet<long>>();

            foreach (var fname in fieldNames)
            {
                var geoFilters = where.GeoFilters
                    .Where(f => string.Equals(f.FieldName, fname, StringComparison.Ordinal))
                    .ToArray();

                var points = QueryPoints(tsdb, series.Id, fname, where.TimeRange, geoFilters);
                foreach (var dp in points)
                {
                    long bucketStart = bucketSizeMs > 0
                        ? TimeBucket.Floor(dp.Timestamp, bucketSizeMs)
                        : long.MinValue;

                    if (!bucketTimestamps.TryGetValue(bucketStart, out var set))
                    {
                        set = [];
                        bucketTimestamps[bucketStart] = set;
                    }
                    set.Add(dp.Timestamp);
                }
            }

            // #217：残差谓词过滤——count(*) 只计残差在该时刻确定为 TRUE 的行。
            var residualLookups = BuildResidualLookups(tsdb, series, where.TimeRange, residualFieldCols);

            foreach (var (bucketStart, timestamps) in bucketTimestamps)
            {
                if (!bucketAccumulators.TryGetValue(bucketStart, out var slots))
                {
                    slots = CreateAggSlots(aggSpecs);
                    bucketAccumulators[bucketStart] = slots;
                }

                foreach (var ts in timestamps)
                {
                    if (where.Residual is not null
                        && !ResidualHoldsAtPoint(where.Residual, ts, series, residualLookups))
                        continue;
                    slots[specIdx].UpdateCount(ts);
                }
            }
        }
    }

    private static AggSlot[] CreateAggSlots(IReadOnlyList<AggSpec> aggSpecs)
    {
        var slots = new AggSlot[aggSpecs.Count];
        for (int i = 0; i < slots.Length; i++)
            slots[i] = AggSlot.Create(aggSpecs[i]);
        return slots;
    }

    private static bool CanUseExtendedAggregateSketchPath(
        AggSpec spec,
        long bucketSizeMs,
        IReadOnlyList<GeoPointWhereFilter> geoFilters)
    {
        return spec.IsExtended
            && bucketSizeMs <= 0
            && spec.FieldName is not null
            && geoFilters.Count == 0;
    }

    private static double FieldValueToDouble(FieldValue v, MeasurementColumn? col)
    {
        if (v.TryGetNumeric(out var d)) return d;
        throw new InvalidOperationException(
            $"聚合仅支持数值字段（列 '{col?.Name ?? "?"}' 的类型为 {v.Type}）。");
    }

    private static object ComputeLegacyAggregateValue(Aggregator agg, BucketState st) => agg switch
    {
        Aggregator.Count => (object)st.Count,
        Aggregator.Sum => st.Sum,
        Aggregator.Min => st.Count == 0 ? 0.0 : st.Min,
        Aggregator.Max => st.Count == 0 ? 0.0 : st.Max,
        Aggregator.Avg => st.Count == 0 ? 0.0 : st.Sum / st.Count,
        Aggregator.First => st.Count == 0 ? 0.0 : st.FirstValue,
        Aggregator.Last => st.Count == 0 ? 0.0 : st.LastValue,
        _ => throw new InvalidOperationException($"不支持的聚合 {agg}。"),
    };

    private static AggSpec ResolveAggregateSpec(
        FunctionCallExpression fn,
        string columnName,
        MeasurementSchema schema)
    {
        if (!FunctionRegistry.TryGetAggregate(fn.Name, out var aggregate))
            throw new InvalidOperationException($"未知聚合函数 '{fn.Name}'。");

        var fieldName = aggregate.ResolveFieldName(fn, schema);

        if (aggregate.LegacyAggregator is { } legacy)
            return new AggSpec(columnName, legacy, fieldName,
                ExtendedFunction: null, ExtendedCall: null, Schema: null);

        // 扩展聚合：保留函数与 AST 引用以便每个桶按需创建独立累加器。
        return new AggSpec(columnName, default, fieldName,
            ExtendedFunction: aggregate, ExtendedCall: fn, Schema: schema);
    }

    private sealed record AggSpec(
        string ColumnName,
        Aggregator LegacyAggregator,
        string? FieldName,
        IAggregateFunction? ExtendedFunction,
        FunctionCallExpression? ExtendedCall,
        MeasurementSchema? Schema)
    {
        public bool IsExtended => ExtendedFunction is not null;
        public bool IsCountStar => !IsExtended && LegacyAggregator == Aggregator.Count && FieldName is null;
    }

    /// <summary>每个 (bucket × spec) 的累加槽：legacy 走 <see cref="BucketState"/>，扩展聚合走累加器。</summary>
    private sealed class AggSlot
    {
        private readonly AggSpec _spec;
        private BucketState _legacy = BucketState.Empty;
        private readonly IAggregateAccumulator? _extended;

        private AggSlot(AggSpec spec, IAggregateAccumulator? extended)
        {
            _spec = spec;
            _extended = extended;
        }

        public static AggSlot Create(AggSpec spec)
        {
            if (!spec.IsExtended)
                return new AggSlot(spec, extended: null);

            var accumulator = spec.ExtendedFunction!.CreateAccumulator(spec.ExtendedCall!, spec.Schema!)
                ?? throw new InvalidOperationException(
                    $"扩展聚合 '{spec.ExtendedFunction.Name}' 未返回累加器实例。");
            return new AggSlot(spec, accumulator);
        }

        public void UpdateCount(long timestamp)
        {
            if (_extended is not null)
                throw new InvalidOperationException("扩展聚合不支持 count-only 更新路径。");
            _legacy = _legacy.Update(timestamp, 0.0);
        }

        public void Update(long timestamp, FieldValue value, MeasurementColumn? col)
        {
            if (_extended is null)
            {
                _legacy = _legacy.Update(timestamp, FieldValueToDouble(value, col));
            }
            else if (value.Type == FieldType.Vector)
            {
                _extended.Add(timestamp, value.AsVector());
            }
            else if (value.Type == FieldType.GeoPoint)
            {
                _extended.Add(timestamp, value.AsGeoPoint());
            }
            else
            {
                _extended.Add(timestamp, FieldValueToDouble(value, col));
            }
        }

        public bool TryUpdateExtendedFromSidecar(
            Tsdb tsdb,
            ulong seriesId,
            string fieldName,
            TimeRange range,
            out long observedCount)
        {
            observedCount = 0;
            if (_extended is null)
                return false;

            return tsdb.Query.TryAddExtendedAggregateSketches(
                seriesId,
                fieldName,
                range,
                _extended,
                out observedCount);
        }

        public object? Finalize()
            => _extended is not null
                ? _extended.Finalize()
                : ComputeLegacyAggregateValue(_spec.LegacyAggregator, _legacy);
    }

    private readonly record struct BucketState(
        long Count,
        double Sum,
        double Min,
        double Max,
        long FirstTimestamp,
        double FirstValue,
        long LastTimestamp,
        double LastValue)
    {
        public static BucketState Empty => new(0, 0, double.PositiveInfinity, double.NegativeInfinity, long.MaxValue, 0, long.MinValue, 0);

        public BucketState Update(long timestamp, double value)
        {
            return new BucketState(
                Count + 1,
                Sum + value,
                value < Min ? value : Min,
                value > Max ? value : Max,
                timestamp < FirstTimestamp ? timestamp : FirstTimestamp,
                timestamp < FirstTimestamp ? value : FirstValue,
                timestamp > LastTimestamp ? timestamp : LastTimestamp,
                timestamp > LastTimestamp ? value : LastValue);
        }
    }
}
