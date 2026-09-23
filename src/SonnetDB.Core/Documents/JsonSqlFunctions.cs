using System.Globalization;
using System.Text.Json;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Documents;

/// <summary>
/// SQL JSON 标量函数的共享实现。
/// </summary>
/// <remarks>
/// 这些函数只使用 <see cref="JsonDocument"/> 和已解析的 <see cref="JsonPath"/>，
/// 不依赖反射序列化。输入、路径、嵌套深度和集合大小均有固定上限，便于在嵌入式、
/// Server 和 Native AOT 路径上保持相同的失败边界。
/// </remarks>
internal static class JsonSqlFunctions
{
    private const int MaxJsonTextLength = 8 * 1024 * 1024;
    private const int MaxPathLength = 1024;
    private const int MaxPathSegments = 64;
    private const int MaxComparisonDepth = 64;
    private const int MaxCollectionElements = 100_000;
    private const int MaxComparisonOperations = 1_000_000;

    /// <summary>
    /// 判断 JSON path 是否存在；JSON null 仍然算作存在。
    /// </summary>
    /// <param name="args">JSON 文本和 path 参数。</param>
    /// <returns>SQL NULL、存在布尔值或缺失时的 <see langword="false"/>。</returns>
    public static object? Exists(IReadOnlyList<object?> args)
    {
        string? json = RequireNullableString(args[0], "json_exists", "json");
        string? pathText = RequireNullableString(args[1], "json_exists", "path");
        if (json is null || pathText is null)
            return null;

        using var document = ParseDocument(json, "json_exists");
        JsonPath path = ParsePath(pathText, "json_exists");
        return JsonPathEvaluator.TryResolve(document.RootElement, path, out _);
    }

    /// <summary>
    /// 返回 JSON path 指向数组的元素数量。
    /// </summary>
    /// <param name="args">JSON 文本和 path 参数。</param>
    /// <returns>数组长度；缺失路径或 JSON null 返回 SQL NULL。</returns>
    /// <exception cref="InvalidOperationException">path 指向的值不是数组或超出资源上限。</exception>
    public static object? ArrayLength(IReadOnlyList<object?> args)
    {
        string? json = RequireNullableString(args[0], "json_array_length", "json");
        string? pathText = RequireNullableString(args[1], "json_array_length", "path");
        if (json is null || pathText is null)
            return null;

        using var document = ParseDocument(json, "json_array_length");
        JsonPath path = ParsePath(pathText, "json_array_length");
        if (!JsonPathEvaluator.TryResolve(document.RootElement, path, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        EnsureCollectionBudget(value, "json_array_length");
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("json_array_length 的 path 结果必须是 JSON 数组。");

        return (long)value.GetArrayLength();
    }

    /// <summary>
    /// 判断 JSON path 结果是否包含给定标量、对象、数组或对象字段名。
    /// </summary>
    /// <param name="args">JSON 文本、path 和候选值参数。</param>
    /// <returns>SQL NULL、包含结果或缺失 path 时的 <see langword="false"/>。</returns>
    /// <remarks>
    /// 数组候选值按无序子集处理；对象候选值按字段子集处理。对顶层对象传入字符串时，
    /// 字符串也可作为字段名查询。字符串数组成员保持 SQL 字符串语义，不会被强制解析为 JSON。
    /// </remarks>
    public static object? Contains(IReadOnlyList<object?> args)
    {
        string? json = RequireNullableString(args[0], "json_contains", "json");
        string? pathText = RequireNullableString(args[1], "json_contains", "path");
        object? candidate = args[2];
        if (json is null || pathText is null || candidate is null)
            return null;

        using var document = ParseDocument(json, "json_contains");
        JsonPath path = ParsePath(pathText, "json_contains");
        if (!JsonPathEvaluator.TryResolve(document.RootElement, path, out var target)
            || target.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        EnsureCollectionBudget(target, "json_contains");
        var comparisonBudget = new ComparisonBudget(MaxComparisonOperations);
        if (candidate is string candidateText && IsJsonContainerText(candidateText))
        {
            using var candidateDocument = ParseDocument(candidateText, "json_contains");
            EnsureCollectionBudget(candidateDocument.RootElement, "json_contains");
            return ContainsElement(
                target,
                candidateDocument.RootElement,
                0,
                allowObjectFieldName: false,
                "json_contains",
                comparisonBudget);
        }

        return ContainsScalar(
            target,
            candidate,
            0,
            allowObjectFieldName: true,
            "json_contains",
            comparisonBudget);
    }

    private static JsonDocument ParseDocument(string json, string functionName)
    {
        if (json.Length > MaxJsonTextLength)
        {
            throw new InvalidOperationException(
                $"{functionName} 的 JSON 文本超过 {MaxJsonTextLength} 个字符上限。");
        }

        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaxComparisonDepth });
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"{functionName} 的 JSON 无效或嵌套深度超过 {MaxComparisonDepth}。", exception);
        }
    }

    private static JsonPath ParsePath(string pathText, string functionName)
    {
        if (pathText.Length > MaxPathLength)
            throw new InvalidOperationException($"{functionName} 的 JSON path 超过 {MaxPathLength} 个字符上限。");

        try
        {
            JsonPath path = JsonPath.Parse(pathText);
            if (path.Segments.Count > MaxPathSegments)
            {
                throw new InvalidOperationException(
                    $"{functionName} 的 JSON path 片段数超过 {MaxPathSegments} 个上限。");
            }

            return path;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException($"{functionName} 的 JSON path 无效。", exception);
        }
    }

    private static string? RequireNullableString(object? value, string functionName, string argumentName)
    {
        if (value is null)
            return null;
        if (value is string text)
            return text;

        throw new InvalidOperationException(
            $"{functionName} 的 {argumentName} 参数必须是 STRING 或 NULL。");
    }

    private static bool IsJsonContainerText(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        return span.Length > 0 && span[0] is '{' or '[';
    }

    private static void EnsureCollectionBudget(JsonElement value, string functionName)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > MaxCollectionElements)
        {
            throw new InvalidOperationException(
                $"{functionName} 的 JSON 数组元素数超过 {MaxCollectionElements} 上限。");
        }

        if (value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() > MaxCollectionElements)
        {
            throw new InvalidOperationException(
                $"{functionName} 的 JSON 对象字段数超过 {MaxCollectionElements} 上限。");
        }
    }

    private static bool TryGetObjectProperty(
        JsonElement target,
        string propertyName,
        out JsonElement value,
        string functionName,
        ComparisonBudget comparisonBudget)
    {
        value = default;
        bool found = false;
        foreach (JsonProperty property in target.EnumerateObject())
        {
            comparisonBudget.Consume(functionName);
            if (string.Equals(property.Name, propertyName, StringComparison.Ordinal))
            {
                // JsonElement.TryGetProperty returns the last duplicate property.
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    private static int CountObjectProperties(
        JsonElement value,
        string functionName,
        ComparisonBudget comparisonBudget)
    {
        int count = 0;
        foreach (JsonProperty _ in value.EnumerateObject())
        {
            comparisonBudget.Consume(functionName);
            count++;
        }

        return count;
    }

    private static bool ContainsScalar(
        JsonElement target,
        object candidate,
        int depth,
        bool allowObjectFieldName,
        string functionName,
        ComparisonBudget comparisonBudget)
    {
        comparisonBudget.Consume(functionName);
        EnsureDepth(depth, functionName);
        switch (target.ValueKind)
        {
            case JsonValueKind.Array:
                EnsureCollectionBudget(target, functionName);
                foreach (JsonElement item in target.EnumerateArray())
                {
                    if (ContainsScalar(
                        item,
                        candidate,
                        depth + 1,
                        allowObjectFieldName: false,
                        functionName,
                        comparisonBudget))
                        return true;
                }

                return false;
            case JsonValueKind.Object when allowObjectFieldName && candidate is string fieldName:
                return TryGetObjectProperty(
                    target,
                    fieldName,
                    out _,
                    functionName,
                    comparisonBudget);
            case JsonValueKind.Object:
                return false;
            default:
                return ScalarEquals(target, candidate);
        }
    }

    private static bool ContainsElement(
        JsonElement target,
        JsonElement candidate,
        int depth,
        bool allowObjectFieldName,
        string functionName,
        ComparisonBudget comparisonBudget)
    {
        comparisonBudget.Consume(functionName);
        EnsureDepth(depth, functionName);
        switch (target.ValueKind)
        {
            case JsonValueKind.Array:
                EnsureCollectionBudget(target, functionName);
                if (candidate.ValueKind == JsonValueKind.Array)
                {
                    EnsureCollectionBudget(candidate, functionName);
                    return ContainsArraySubset(target, candidate, depth, functionName, comparisonBudget);
                }

                return target.EnumerateArray().Any(item =>
                    ContainsElement(
                        item,
                        candidate,
                        depth + 1,
                        allowObjectFieldName: false,
                        functionName,
                        comparisonBudget));
            case JsonValueKind.Object:
                EnsureCollectionBudget(target, functionName);
                if (candidate.ValueKind == JsonValueKind.Object)
                {
                    EnsureCollectionBudget(candidate, functionName);
                    foreach (JsonProperty property in candidate.EnumerateObject())
                    {
                        comparisonBudget.Consume(functionName);
                        if (!TryGetObjectProperty(
                                target,
                                property.Name,
                                out var actual,
                                functionName,
                                comparisonBudget)
                            || !ContainsElement(
                                actual,
                                property.Value,
                                depth + 1,
                                allowObjectFieldName: false,
                                functionName,
                                comparisonBudget))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                return allowObjectFieldName
                    && candidate.ValueKind == JsonValueKind.String
                    && TryGetObjectProperty(
                        target,
                        candidate.GetString()!,
                        out _,
                        functionName,
                        comparisonBudget);
            default:
                return JsonElementsEqual(target, candidate, depth, functionName, comparisonBudget);
        }
    }

    // 对象子集等匹配可能重叠，贪心选择首个目标会错误拒绝存在一一映射的候选数组。
    // 按需广度优先寻找增广路径；不保存 O(N*M) 比较图，也不按数组长度递归调用。
    private static bool ContainsArraySubset(
        JsonElement target,
        JsonElement candidate,
        int depth,
        string functionName,
        ComparisonBudget comparisonBudget)
    {
        int candidateCount = candidate.GetArrayLength();
        int targetCount = target.GetArrayLength();
        if (candidateCount == 0)
            return true;
        if (candidateCount > targetCount)
            return false;

        JsonElement[] targetItems = target.EnumerateArray().ToArray();
        JsonElement[] candidateItems = candidate.EnumerateArray().ToArray();
        int[] targetOwners = new int[targetCount];
        int[] candidateTargets = new int[candidateCount];
        int[] visitedTargets = new int[targetCount];
        int[] parentCandidates = new int[targetCount];
        int[] queue = new int[candidateCount];
        Array.Fill(targetOwners, -1);
        Array.Fill(candidateTargets, -1);

        for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
        {
            int visit = candidateIndex + 1;
            int queued = 1;
            int availableTarget = -1;
            queue[0] = candidateIndex;
            for (int cursor = 0; cursor < queued && availableTarget < 0; cursor++)
            {
                int currentCandidate = queue[cursor];
                for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
                {
                    // 已访问槽位的扫描也消耗预算，避免密集匹配留下无计数的二次循环。
                    comparisonBudget.Consume(functionName);
                    if (visitedTargets[targetIndex] == visit
                        || !ContainsElement(
                            targetItems[targetIndex],
                            candidateItems[currentCandidate],
                            depth + 1,
                            allowObjectFieldName: false,
                            functionName,
                            comparisonBudget))
                    {
                        continue;
                    }

                    visitedTargets[targetIndex] = visit;
                    parentCandidates[targetIndex] = currentCandidate;
                    int owner = targetOwners[targetIndex];
                    if (owner < 0)
                    {
                        availableTarget = targetIndex;
                        break;
                    }

                    // 每个已匹配候选恰好拥有一个目标，每个目标本轮最多访问一次。
                    queue[queued++] = owner;
                }
            }

            if (availableTarget < 0)
                return false;

            // 增广链至多经过当前已处理的候选数；只有 JSON 嵌套使用原有深度限制。
            for (int hop = 0; hop <= candidateIndex; hop++)
            {
                comparisonBudget.Consume(functionName);
                int owner = parentCandidates[availableTarget];
                int previousTarget = candidateTargets[owner];
                targetOwners[availableTarget] = owner;
                candidateTargets[owner] = availableTarget;
                if (previousTarget < 0)
                    break;
                availableTarget = previousTarget;
            }
        }

        return true;
    }

    private static bool JsonElementsEqual(
        JsonElement left,
        JsonElement right,
        int depth,
        string functionName,
        ComparisonBudget comparisonBudget)
    {
        comparisonBudget.Consume(functionName);
        EnsureDepth(depth, functionName);
        if (left.ValueKind != right.ValueKind)
        {
            return left.ValueKind == JsonValueKind.Number
                && right.ValueKind == JsonValueKind.Number
                && NumericEquals(left, right);
        }

        switch (left.ValueKind)
        {
            case JsonValueKind.Object:
                EnsureCollectionBudget(left, functionName);
                EnsureCollectionBudget(right, functionName);
                int rightPropertyCount = CountObjectProperties(right, functionName, comparisonBudget);
                int leftPropertyCount = 0;
                foreach (JsonProperty property in left.EnumerateObject())
                {
                    leftPropertyCount++;
                    comparisonBudget.Consume(functionName);
                    if (!TryGetObjectProperty(
                            right,
                            property.Name,
                            out var other,
                            functionName,
                            comparisonBudget)
                        || !JsonElementsEqual(
                            property.Value,
                            other,
                            depth + 1,
                            functionName,
                            comparisonBudget))
                    {
                        return false;
                    }
                }

                return rightPropertyCount == leftPropertyCount;
            case JsonValueKind.Array:
                EnsureCollectionBudget(left, functionName);
                EnsureCollectionBudget(right, functionName);
                if (left.GetArrayLength() != right.GetArrayLength())
                    return false;
                using (var leftEnumerator = left.EnumerateArray().GetEnumerator())
                using (var rightEnumerator = right.EnumerateArray().GetEnumerator())
                {
                    while (leftEnumerator.MoveNext() && rightEnumerator.MoveNext())
                    {
                        if (!JsonElementsEqual(
                            leftEnumerator.Current,
                            rightEnumerator.Current,
                            depth + 1,
                            functionName,
                            comparisonBudget))
                            return false;
                    }
                }

                return true;
            case JsonValueKind.Number:
                return NumericEquals(left, right);
            case JsonValueKind.String:
                return string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal);
            case JsonValueKind.True or JsonValueKind.False:
                return left.GetBoolean() == right.GetBoolean();
            case JsonValueKind.Null:
                return true;
            default:
                return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static bool ScalarEquals(JsonElement target, object candidate)
    {
        return target.ValueKind switch
        {
            JsonValueKind.String => candidate is string text
                && string.Equals(target.GetString(), text, StringComparison.Ordinal),
            JsonValueKind.True => candidate is bool boolean && boolean,
            JsonValueKind.False => candidate is bool boolean && !boolean,
            JsonValueKind.Number => NumericEquals(target, candidate),
            JsonValueKind.Null => candidate is null,
            _ => false,
        };
    }

    private static bool NumericEquals(JsonElement left, JsonElement right)
    {
        if (left.TryGetDecimal(out decimal leftDecimal) && right.TryGetDecimal(out decimal rightDecimal))
            return leftDecimal == rightDecimal;
        return left.TryGetDouble(out double leftDouble)
            && right.TryGetDouble(out double rightDouble)
            && leftDouble == rightDouble;
    }

    private static bool NumericEquals(JsonElement target, object candidate)
    {
        if (!TryGetDecimal(candidate, out decimal candidateDecimal))
            return false;
        if (target.TryGetDecimal(out decimal targetDecimal))
            return targetDecimal == candidateDecimal;
        return target.TryGetDouble(out double targetDouble)
            && double.TryParse(candidateDecimal.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double candidateDouble)
            && targetDouble == candidateDouble;
    }

    private static bool TryGetDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case byte number: result = number; return true;
            case sbyte number: result = number; return true;
            case short number: result = number; return true;
            case ushort number: result = number; return true;
            case int number: result = number; return true;
            case uint number: result = number; return true;
            case long number: result = number; return true;
            case ulong number: result = number; return true;
            case float number when !float.IsNaN(number) && !float.IsInfinity(number): result = (decimal)number; return true;
            case double number when !double.IsNaN(number) && !double.IsInfinity(number) && number is >= (double)decimal.MinValue and <= (double)decimal.MaxValue:
                result = (decimal)number;
                return true;
            case decimal number: result = number; return true;
            default:
                result = default;
                return false;
        }
    }

    private static void EnsureDepth(int depth, string functionName)
    {
        if (depth > MaxComparisonDepth)
        {
            throw new InvalidOperationException(
                $"{functionName} 的 JSON 比较深度超过 {MaxComparisonDepth} 上限。");
        }
    }

    private sealed class ComparisonBudget
    {
        private readonly int _limit;
        private int _used;

        internal ComparisonBudget(int limit)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
            _limit = limit;
        }

        internal void Consume(string functionName)
        {
            if ((_used & 255) == 0)
                SqlExecutor.ThrowIfCancellationRequested();
            if (_used >= _limit)
            {
                throw new InvalidOperationException(
                    $"{functionName} 的 JSON 比较次数超过 {_limit} 上限。");
            }

            _used++;
        }
    }
}
