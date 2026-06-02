using System.Globalization;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// MM8 文档集合 Hybrid Search 执行器。
/// </summary>
internal static class HybridSearchExecutor
{
    private const string FunctionName = "hybrid_search";
    private const int DefaultK = 20;
    private const int DefaultTextCandidateMultiplier = 8;

    public static bool IsHybridSearch(SelectStatement statement)
        => statement.TableValuedFunction is { Name: var name }
            && string.Equals(name, FunctionName, StringComparison.OrdinalIgnoreCase);

    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("hybrid_search 只能出现在 FROM 表值函数中。");
        var schema = tsdb.Documents.Catalog.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"hybrid_search(...) 的 source '{statement.Measurement}' 必须是 document collection。");
        if (statement.GroupBy.Count != 0)
            throw new InvalidOperationException("hybrid_search(...) 暂不支持 GROUP BY。");

        var options = BindOptions(schema, call, statement.Pagination);
        var store = tsdb.Documents.Open(schema.Name);
        var projections = BuildProjections(statement.Projections);
        var rows = ScoreRows(store, options);
        rows = ApplyWhere(rows, statement.Where);
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

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(
        Tsdb tsdb,
        SelectStatement statement,
        DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("hybrid_search 只能出现在 FROM 表值函数中。");
        var options = BindOptions(schema, call, statement.Pagination);
        var store = tsdb.Documents.Open(schema.Name);
        int documentCount = store.Scan().Count;
        return ("hybrid_search", options.FullTextIndex.Name, documentCount);
    }

    private static IReadOnlyList<HybridRow> ScoreRows(
        DocumentCollectionStore store,
        HybridSearchOptions options)
    {
        var fullTextHits = store.SearchFullText(
            options.FullTextIndex,
            options.TextField,
            options.TextQuery,
            options.TextCandidateLimit);
        var bm25ById = fullTextHits.ToDictionary(static h => h.DocumentId, static h => h.Score, StringComparer.Ordinal);
        double maxBm25 = bm25ById.Count == 0 ? 0d : bm25ById.Values.Max();

        var rows = new List<HybridRow>();
        foreach (var documentRow in store.Scan())
        {
            if (!TryReadVector(documentRow, options.VectorPath, out var vector))
                continue;
            if (vector.Length != options.QueryVector.Length)
            {
                throw new InvalidOperationException(
                    $"hybrid_search 文档 '{documentRow.Id}' 的向量维度 {vector.Length} 与查询向量维度 {options.QueryVector.Length} 不一致。");
            }

            double distance = VectorDistance.Compute(options.Metric, options.QueryVector, vector);
            double vectorScore = DistanceToScore(options.Metric, distance);
            bm25ById.TryGetValue(documentRow.Id, out double bm25);
            double textScore = maxBm25 <= 0d ? 0d : bm25 / maxBm25;
            double hybridScore = (options.TextWeight * textScore) + (options.VectorWeight * vectorScore);

            rows.Add(new HybridRow(
                documentRow,
                Bm25Score: bm25,
                VectorDistance: distance,
                VectorScore: vectorScore,
                HybridScore: hybridScore));
        }

        return rows;
    }

    private static List<HybridRow> ApplyWhere(
        IReadOnlyList<HybridRow> rows,
        SqlExpression? where)
    {
        if (where is null)
            return rows.ToList();

        var filtered = new List<HybridRow>(rows.Count);
        foreach (var row in rows)
        {
            if (EvaluateBoolean(where, row))
                filtered.Add(row);
        }

        return filtered;
    }

    private static List<HybridRow> ApplyOrderBy(
        IReadOnlyList<HybridRow> rows,
        OrderBySpec? orderBy,
        IReadOnlyList<Projection> projections)
    {
        if (orderBy is null)
        {
            return rows
                .OrderByDescending(static r => r.HybridScore)
                .ThenByDescending(static r => r.Bm25Score)
                .ThenBy(static r => r.VectorDistance)
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

    private static List<HybridRow> ApplyPagination(
        IReadOnlyList<HybridRow> rows,
        PaginationSpec? pagination)
    {
        if (pagination is null)
            return rows.ToList();

        if (pagination.Offset >= rows.Count)
            return [];

        int take = pagination.Fetch ?? (rows.Count - pagination.Offset);
        if (take <= 0)
            return [];

        return rows
            .Skip(pagination.Offset)
            .Take(Math.Min(take, rows.Count - pagination.Offset))
            .ToList();
    }

    private static HybridSearchOptions BindOptions(
        DocumentCollectionSchema schema,
        FunctionCallExpression call,
        PaginationSpec? pagination)
    {
        if (call.IsStar)
            throw new InvalidOperationException("hybrid_search(*) 非法。");

        var args = BindArguments(call);
        var source = RequireIdentifierArgument(args, "source");
        if (!string.Equals(source, schema.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"hybrid_search source '{source}' 与解析出的 document collection '{schema.Name}' 不一致。");
        }

        string textQuery = RequireStringArgument(args, "text", "text_query");
        float[] queryVector = RequireVectorArgument(args, "vector", "query_vector");
        int k = GetPositiveIntArgument(args, DefaultK, "k", "top_k");
        string textField = GetFieldArgument(args, defaultValue: "*", "text_field", "field");
        string vectorField = NormalizeJsonPath(GetFieldArgument(args, defaultValue: "$.embedding", "vector_field", "embedding_field"));
        var fullTextIndex = ResolveFullTextIndex(schema, args);
        var metric = GetMetricArgument(args);
        double textWeight = GetNonNegativeDoubleArgument(args, 0.5d, "text_weight");
        double vectorWeight = GetNonNegativeDoubleArgument(args, 0.5d, "vector_weight");
        if (textWeight == 0d && vectorWeight == 0d)
            throw new InvalidOperationException("hybrid_search 的 text_weight 与 vector_weight 不能同时为 0。");

        int textCandidateLimit = Math.Max(k * DefaultTextCandidateMultiplier, DefaultFullTextTopK(pagination));
        return new HybridSearchOptions(
            fullTextIndex,
            textField,
            textQuery,
            JsonPath.Parse(vectorField),
            queryVector,
            k,
            textCandidateLimit,
            metric,
            textWeight,
            vectorWeight);
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
                    throw new InvalidOperationException("hybrid_search(...) 使用命名参数时，所有参数都必须写成 name => value。");
                if (!result.TryAdd(named.Name, named.Value))
                    throw new InvalidOperationException($"hybrid_search(...) 参数 '{named.Name}' 重复。");
            }

            return result;
        }

        if (call.Arguments.Count is < 3 or > 4)
        {
            throw new InvalidOperationException(
                "hybrid_search(source, text, vector[, k]) 或 hybrid_search(source => ..., text => ..., vector => ..., k => ...) 需要 source/text/vector 参数。");
        }

        result["source"] = call.Arguments[0];
        result["text"] = call.Arguments[1];
        result["vector"] = call.Arguments[2];
        if (call.Arguments.Count == 4)
            result["k"] = call.Arguments[3];
        return result;
    }

    private static DocumentFullTextIndex ResolveFullTextIndex(
        DocumentCollectionSchema schema,
        IReadOnlyDictionary<string, SqlExpression> args)
    {
        string? indexName = TryGetArgument(args, out var indexExpression, "text_index", "fulltext_index", "index")
            ? ExpressionToName(indexExpression, "全文索引名")
            : null;

        if (indexName is not null)
        {
            return schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{schema.Name}' 中不存在全文索引 '{indexName}'。");
        }

        if (schema.FullTextIndexes.Count == 1)
            return schema.FullTextIndexes[0];

        throw new InvalidOperationException(
            $"hybrid_search 需要 text_index 参数；document collection '{schema.Name}' 当前有 {schema.FullTextIndexes.Count} 个全文索引。");
    }

    private static bool TryReadVector(DocumentRow row, JsonPath path, out float[] vector)
    {
        vector = [];
        using var document = JsonDocument.Parse(row.Json);
        if (!JsonPathEvaluator.TryResolve(document.RootElement, path, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Null)
            return false;
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"hybrid_search 文档 '{row.Id}' 的向量字段 '{path.Text}' 必须是 JSON number array。");

        int length = element.GetArrayLength();
        if (length == 0)
            throw new InvalidOperationException(
                $"hybrid_search 文档 '{row.Id}' 的向量字段 '{path.Text}' 不能为空数组。");

        var result = new float[length];
        int index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out float value))
            {
                throw new InvalidOperationException(
                    $"hybrid_search 文档 '{row.Id}' 的向量字段 '{path.Text}' 只能包含 number。");
            }

            result[index++] = value;
        }

        vector = result;
        return true;
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
                    projections.Add(new Projection("bm25_score", new IdentifierExpression("bm25_score")));
                    projections.Add(new Projection("vector_distance", new IdentifierExpression("vector_distance")));
                    projections.Add(new Projection("vector_score", new IdentifierExpression("vector_score")));
                    projections.Add(new Projection("hybrid_score", new IdentifierExpression("hybrid_score")));
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
                    throw new InvalidOperationException(
                        $"hybrid_search SELECT 暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return [.. projections];
    }

    private static bool EvaluateBoolean(SqlExpression expression, HybridRow row)
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
        }

        var value = EvaluateScalar(expression, row);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("hybrid_search WHERE 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(BinaryExpression binary, HybridRow row)
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
            _ => throw new InvalidOperationException($"不支持的比较运算符 {binary.Operator}。"),
        };
    }

    private static object? EvaluateScalar(SqlExpression expression, HybridRow row)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(unary.Operand, row), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, row),
            _ => throw new InvalidOperationException(
                $"hybrid_search 表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateFunction(FunctionCallExpression function, HybridRow row)
    {
        if (function.IsStar)
            throw new InvalidOperationException($"hybrid_search 不支持函数 {function.Name}(*)。");

        if (string.Equals(function.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.Bm25Score);
        if (string.Equals(function.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.VectorDistance);
        if (string.Equals(function.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.VectorScore);
        if (string.Equals(function.Name, "hybrid_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.HybridScore);

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(function.Arguments[0], row) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        throw new InvalidOperationException(
            "hybrid_search 当前仅支持 json_value(document, '$.path')、bm25_score()、vector_distance()、vector_score() 与 hybrid_score() 函数。");
    }

    private static object RequireNoArguments(FunctionCallExpression function, object value)
    {
        if (function.Arguments.Count != 0)
            throw new InvalidOperationException($"函数 {function.Name}() 不接受参数。");
        return value;
    }

    private static object EvaluateArithmetic(BinaryExpression binary, HybridRow row)
    {
        var left = RequireDouble(EvaluateScalar(binary.Left, row), binary.Operator.ToString());
        var right = RequireDouble(EvaluateScalar(binary.Right, row), binary.Operator.ToString());
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

    private static object? GetIdentifierValue(IdentifierExpression identifier, HybridRow row)
    {
        if (identifier.Qualifier is not null)
            throw new InvalidOperationException("hybrid_search 当前不支持限定列名。");

        if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
            return row.Document.Id;
        if (string.Equals(identifier.Name, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "json", StringComparison.OrdinalIgnoreCase))
            return row.Document.Json;
        if (string.Equals(identifier.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
            return row.Bm25Score;
        if (string.Equals(identifier.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return row.VectorDistance;
        if (string.Equals(identifier.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return row.VectorScore;
        if (string.Equals(identifier.Name, "hybrid_score", StringComparison.OrdinalIgnoreCase))
            return row.HybridScore;

        return JsonPathEvaluator.Evaluate(row.Document.Json, "$." + identifier.Name);
    }

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

    private static int DefaultFullTextTopK(PaginationSpec? pagination)
    {
        if (pagination is null)
            return 100;

        long topK = pagination.Fetch is int fetch
            ? (long)pagination.Offset + fetch
            : (long)pagination.Offset + 100;
        return topK <= 0 ? 0 : topK > int.MaxValue ? int.MaxValue : (int)topK;
    }

    private static string RequireIdentifierArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        string name)
    {
        if (!TryGetArgument(args, out var expression, name))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{name}'。");
        return ExpressionToName(expression, name);
    }

    private static string RequireStringArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{names[0]}'。");
        if (expression is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var value })
            return value!;
        throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是字符串字面量。");
    }

    private static float[] RequireVectorArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{names[0]}'。");
        if (expression is not VectorLiteralExpression vector)
            throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是向量字面量。");

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
        throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是正整数字面量。");
    }

    private static double GetNonNegativeDoubleArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        double defaultValue,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            return defaultValue;
        double value = expression switch
        {
            LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var integer } => integer,
            LiteralExpression { Kind: SqlLiteralKind.Float, FloatValue: var number } => number,
            _ => throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是非负数字字面量。"),
        };
        if (value < 0d || double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是非负数字字面量。");
        return value;
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
            throw new InvalidOperationException("hybrid_search 参数 'metric' 必须是字符串字面量。");
        return metric!.ToLowerInvariant() switch
        {
            "cosine" or "cosine_distance" => KnnMetric.Cosine,
            "l2" or "l2_distance" or "euclidean" => KnnMetric.L2,
            "inner_product" or "dot" or "ip" => KnnMetric.InnerProduct,
            _ => throw new InvalidOperationException(
                $"hybrid_search 不支持 metric '{metric}'，仅支持 'cosine' / 'l2' / 'inner_product'。"),
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
            _ => throw new InvalidOperationException($"hybrid_search 参数 '{argumentName}' 必须是标识符或字符串字面量。"),
        };

    private static string NormalizeJsonPath(string path)
    {
        if (path == "*")
            throw new InvalidOperationException("hybrid_search 的 vector_field 不能是 '*'。");
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
        SqlBinaryOperator.GreaterThanOrEqual;

    private static bool IsArithmeticOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Add or
        SqlBinaryOperator.Subtract or
        SqlBinaryOperator.Multiply or
        SqlBinaryOperator.Divide or
        SqlBinaryOperator.Modulo;

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

    private sealed record HybridSearchOptions(
        DocumentFullTextIndex FullTextIndex,
        string TextField,
        string TextQuery,
        JsonPath VectorPath,
        float[] QueryVector,
        int K,
        int TextCandidateLimit,
        KnnMetric Metric,
        double TextWeight,
        double VectorWeight);

    private sealed record HybridRow(
        DocumentRow Document,
        double Bm25Score,
        double VectorDistance,
        double VectorScore,
        double HybridScore);

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
