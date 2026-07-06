using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Documents;

/// <summary>
/// 文档查询规划与执行器，供 SQL SELECT 与 Document API 共享。
/// </summary>
public static class DocumentQueryPlanner
{
    /// <summary>
    /// 执行文档查询。
    /// </summary>
    /// <param name="store">文档集合存储。</param>
    /// <param name="schema">文档集合 schema。</param>
    /// <param name="query">查询计划。</param>
    /// <returns>查询结果。</returns>
    public static DocumentQueryResult Execute(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentQuery query)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        var selection = SelectAccessPath(store, schema, query);
        var matches = new List<DocumentRow>();
        foreach (var row in selection.Selected.LoadRows())
        {
            if (Matches(query.Filter, row))
                matches.Add(row);
        }

        var ordered = ApplySort(matches, query.Sort);
        int matchedCount = ordered.Count;
        var paged = ApplyPagination(ordered, query.Skip, query.Limit);
        var items = new List<DocumentQueryItem>(paged.Count);
        foreach (var row in paged)
            items.Add(new DocumentQueryItem(row.Id, ProjectJson(row, query.Projection), row.Version));

        return new DocumentQueryResult(items, matchedCount, selection.Selected.AccessPath, selection.Selected.IndexName);
    }

    /// <summary>
    /// 估算文档查询访问路径。
    /// </summary>
    /// <param name="store">文档集合存储。</param>
    /// <param name="schema">文档集合 schema。</param>
    /// <param name="filter">过滤表达式。</param>
    /// <returns>访问路径、索引名与候选行数量。</returns>
    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentFilter? filter)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);

        var plan = Explain(store, schema, new DocumentQuery(Filter: filter));
        return (plan.AccessPath, plan.IndexName, plan.EstimatedCandidateRows);
    }

    /// <summary>
    /// 解释文档查询的访问路径、代价与未实现优化缺口。
    /// </summary>
    /// <param name="store">文档集合存储。</param>
    /// <param name="schema">文档集合 schema。</param>
    /// <param name="query">查询计划。</param>
    /// <returns>文档查询规划结果。</returns>
    public static DocumentQueryPlan Explain(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentQuery query)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        return BuildPlan(store, schema, query);
    }

    /// <summary>
    /// 判断单个文档是否匹配过滤表达式。
    /// </summary>
    /// <param name="filter">过滤表达式；为 null 时匹配。</param>
    /// <param name="row">文档行。</param>
    /// <returns>匹配返回 true。</returns>
    public static bool Matches(DocumentFilter? filter, DocumentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (filter is null)
            return true;

        return filter switch
        {
            DocumentAndFilter and => and.Filters.All(child => Matches(child, row)),
            DocumentOrFilter or => or.Filters.Any(child => Matches(child, row)),
            DocumentNotFilter not => !Matches(not.Filter, row),
            DocumentFieldFilter field => MatchesFieldFilter(field, row),
            _ => throw new InvalidOperationException($"不支持的文档过滤表达式类型 '{filter.GetType().Name}'。"),
        };
    }

    /// <summary>
    /// 读取文档字段的值，并区分字段缺失与 JSON null。
    /// </summary>
    /// <param name="row">文档行。</param>
    /// <param name="field">字段引用。</param>
    /// <param name="value">字段存在时的值。</param>
    /// <returns>字段存在返回 true。</returns>
    public static bool TryGetFieldValue(DocumentRow row, DocumentFieldRef field, out object? value)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(field);
        value = null;
        switch (field.Kind)
        {
            case DocumentFieldKind.Id:
                value = row.Id;
                return true;

            case DocumentFieldKind.Document:
                value = row.Json;
                return true;

            case DocumentFieldKind.JsonPath:
                return JsonPathEvaluator.TryEvaluate(row.Json, RequirePath(field), out value);

            default:
                throw new InvalidOperationException($"不支持的文档字段类型 '{field.Kind}'。");
        }
    }

    private static void ValidateQuery(DocumentQuery query)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        if (query.Limit is < 0)
            throw new ArgumentOutOfRangeException(nameof(query), "limit 不能为负数。");
    }

    private static AccessSelection SelectAccessPath(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentQuery query)
    {
        var candidates = BuildAccessCandidates(store, schema, query).ToArray();
        var selected = candidates
            .OrderBy(static candidate => candidate.Cost)
            .ThenByDescending(static candidate => candidate.FilterPushdownFields.Count)
            .ThenBy(static candidate => candidate.IndexName, StringComparer.Ordinal)
            .First();

        return new AccessSelection(selected, candidates);
    }

    private static DocumentQueryPlan BuildPlan(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentQuery query)
    {
        var (selected, candidates) = SelectAccessPath(store, schema, query);
        var rows = selected.LoadRows();
        int outputRows = CountMatches(query.Filter, rows);
        var selectedCandidate = new DocumentQueryPlanCandidate(
            selected.AccessPath,
            selected.IndexName,
            rows.Count,
            selected.Cost,
            Selected: true,
            selected.FilterPushdownFields,
            RejectReason: null);
        var planCandidates = candidates
            .Select(candidate => ReferenceEquals(candidate, selected)
                ? selectedCandidate
                : new DocumentQueryPlanCandidate(
                    candidate.AccessPath,
                    candidate.IndexName,
                    candidate.EstimatedRows,
                    candidate.Cost,
                    Selected: false,
                    candidate.FilterPushdownFields,
                    BuildRejectReason(candidate, selected)))
            .OrderBy(static candidate => candidate.Cost)
            .ThenBy(static candidate => candidate.IndexName, StringComparer.Ordinal)
            .ToArray();
        var pushed = new HashSet<string>(selected.FilterPushdownFields, StringComparer.Ordinal);
        var residual = CollectFilterFields(query.Filter)
            .Where(field => !pushed.Contains(field))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new DocumentQueryPlan(
            selected.AccessPath,
            selected.IndexName,
            rows.Count,
            outputRows,
            selected.FilterPushdownFields.Count > 0,
            selected.FilterPushdownFields,
            residual,
            SortUsesIndex(selected.AccessPath, rows.Count, query.Sort),
            ProjectionCoveredByIndex(selected, query.Projection),
            planCandidates,
            BuildGapReason(schema, query, selected));
    }

    private static IEnumerable<AccessCandidate> BuildAccessCandidates(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentQuery query)
    {
        if (TryExtractIdEquals(query.Filter, out string id))
        {
            var row = store.Get(id);
            IReadOnlyList<DocumentRow> rows = row is null ? Array.Empty<DocumentRow>() : [row];
            yield return AccessCandidate.FromRows(
                rows,
                "document_id",
                "primary",
                Math.Max(0, rows.Count - 1),
                [FormatField(DocumentFieldRef.Id)]);
        }
        else if (TryExtractIdSet(query.Filter, out var ids))
        {
            var rows = store.GetMany(ids);
            yield return AccessCandidate.FromRows(
                rows,
                "document_id_set",
                "primary",
                rows.Count,
                [FormatField(DocumentFieldRef.Id)]);
        }

        var leaves = FlattenAnd(query.Filter).ToArray();
        var equalityByPath = ExtractEqualityByPath(leaves);
        foreach (var index in schema.Indexes)
        {
            if (!CanUsePartialIndex(index, leaves))
                continue;

            var prefixValues = BuildIndexPrefixValues(index, equalityByPath);
            if (prefixValues.Count == 0)
                continue;

            if (index.IsSparse && prefixValues.Any(static value => value is null))
                continue;

            bool fullMatch = prefixValues.Count == index.Paths.Count;
            int entryCount = fullMatch
                ? store.CountByIndex(index, prefixValues)
                : store.CountByIndexPrefix(index, prefixValues);
            var pushedFields = index.Paths.Take(prefixValues.Count).ToArray();
            int residualPenalty = Math.Max(0, CollectFilterFields(query.Filter).Count() - pushedFields.Length);
            var boundIndex = index;
            yield return AccessCandidate.Lazy(
                () => fullMatch
                    ? store.GetByIndex(boundIndex, prefixValues)
                    : store.GetByIndexPrefix(boundIndex, prefixValues),
                fullMatch ? "document_index" : "document_index_prefix",
                index.Name,
                entryCount,
                entryCount + residualPenalty,
                pushedFields);
        }

        int documentCount = store.Count();
        yield return AccessCandidate.Lazy(
            () => store.Scan(),
            "document_scan",
            null,
            documentCount,
            documentCount,
            Array.Empty<string>());
    }

    private static Dictionary<string, object?> ExtractEqualityByPath(IReadOnlyList<DocumentFilter> leaves)
    {
        var equalityByPath = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            if (leaf is not DocumentFieldFilter
                {
                    Field.Kind: DocumentFieldKind.JsonPath,
                    Field.Path: not null,
                    Operator: DocumentFilterOperator.Equal,
                    Value: var filterValue,
                } fieldFilter)
            {
                continue;
            }

            string normalized = JsonPath.Parse(fieldFilter.Field.Path).Text;
            equalityByPath[normalized] = filterValue;
        }

        return equalityByPath;
    }

    private static IReadOnlyList<object?> BuildIndexPrefixValues(
        DocumentPathIndex index,
        IReadOnlyDictionary<string, object?> equalityByPath)
    {
        var values = new List<object?>(index.Paths.Count);
        foreach (string path in index.Paths)
        {
            if (!equalityByPath.TryGetValue(path, out var value))
                break;

            values.Add(value);
        }

        return values;
    }

    private static int CountMatches(DocumentFilter? filter, IReadOnlyList<DocumentRow> rows)
    {
        int count = 0;
        foreach (var row in rows)
        {
            if (Matches(filter, row))
                count++;
        }

        return count;
    }

    private static IEnumerable<string> CollectFilterFields(DocumentFilter? filter)
    {
        if (filter is null)
            yield break;

        switch (filter)
        {
            case DocumentAndFilter and:
                foreach (var child in and.Filters)
                {
                    foreach (var field in CollectFilterFields(child))
                        yield return field;
                }
                yield break;

            case DocumentOrFilter or:
                foreach (var child in or.Filters)
                {
                    foreach (var field in CollectFilterFields(child))
                        yield return field;
                }
                yield break;

            case DocumentNotFilter not:
                foreach (var field in CollectFilterFields(not.Filter))
                    yield return field;
                yield break;

            case DocumentFieldFilter field:
                yield return FormatField(field.Field);
                yield break;
        }
    }

    private static string BuildRejectReason(AccessCandidate candidate, AccessCandidate selected)
        => candidate.EstimatedRows > selected.EstimatedRows
            ? "higher_candidate_rows"
            : "higher_or_equal_cost";

    private static string? BuildGapReason(
        DocumentCollectionSchema schema,
        DocumentQuery query,
        AccessCandidate selected)
    {
        if (HasIndexIntersectionOpportunity(schema, query.Filter, selected))
            return "index_intersection_not_supported";

        if (!SortUsesIndex(selected.AccessPath, selected.EstimatedRows, query.Sort) && query.Sort.Count > 0)
            return "sort_requires_in_memory_order_by";

        if (!ProjectionCoveredByIndex(selected, query.Projection) && query.Projection is { Fields.Count: > 0 })
            return "projection_not_covered_by_index";

        return null;
    }

    private static bool HasIndexIntersectionOpportunity(
        DocumentCollectionSchema schema,
        DocumentFilter? filter,
        AccessCandidate selected)
    {
        var equalityByPath = ExtractEqualityByPath(FlattenAnd(filter).ToArray());
        if (equalityByPath.Count < 2)
            return false;

        int usableSingleFieldIndexes = 0;
        foreach (var index in schema.Indexes)
        {
            if (index.Paths.Count != 1)
                continue;
            if (!equalityByPath.ContainsKey(index.Path))
                continue;
            if (string.Equals(index.Name, selected.IndexName, StringComparison.Ordinal))
                continue;

            usableSingleFieldIndexes++;
        }

        return usableSingleFieldIndexes >= 2
               || (usableSingleFieldIndexes >= 1 && selected.AccessPath is "document_index" or "document_index_prefix");
    }

    private static bool SortUsesIndex(string accessPath, int candidateRows, IReadOnlyList<DocumentSort> sort)
    {
        if (sort.Count == 0)
            return accessPath is "document_id" or "document_scan";

        if (candidateRows <= 1)
            return true;

        if (sort.Count != 1 || sort[0].Descending)
            return false;

        return sort[0].Field.Kind == DocumentFieldKind.Id
               && accessPath is "document_id" or "document_scan";
    }

    private static bool ProjectionCoveredByIndex(AccessCandidate selected, DocumentProjection? projection)
    {
        if (projection is null || projection.Fields.Count == 0)
            return false;

        var pushed = new HashSet<string>(selected.FilterPushdownFields, StringComparer.Ordinal);
        foreach (var field in projection.Fields)
        {
            string formatted = FormatField(field.Field);
            if (!string.Equals(formatted, "_id", StringComparison.Ordinal)
                && !pushed.Contains(formatted))
            {
                return false;
            }
        }

        return selected.AccessPath is "document_id" or "document_index" or "document_index_prefix";
    }

    private static string FormatField(DocumentFieldRef field)
        => field.Kind switch
        {
            DocumentFieldKind.Id => "_id",
            DocumentFieldKind.Document => "document",
            DocumentFieldKind.JsonPath => JsonPath.Parse(RequirePath(field)).Text,
            _ => field.Kind.ToString(),
        };

    private static bool TryExtractIdEquals(DocumentFilter? filter, out string id)
    {
        id = string.Empty;
        foreach (var leaf in FlattenAnd(filter))
        {
            if (leaf is DocumentFieldFilter
                {
                    Field.Kind: DocumentFieldKind.Id,
                    Operator: DocumentFilterOperator.Equal,
                    Value: string value,
                })
            {
                id = value;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractIdSet(DocumentFilter? filter, out IReadOnlyList<string> ids)
    {
        ids = Array.Empty<string>();
        foreach (var leaf in FlattenAnd(filter))
        {
            if (leaf is not DocumentFieldFilter
                {
                    Field.Kind: DocumentFieldKind.Id,
                    Operator: DocumentFilterOperator.In,
                    Value: var value,
                })
            {
                continue;
            }

            var values = EnumerateFilterValues(value)
                .OfType<string>()
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (values.Length == 0)
                continue;

            ids = values;
            return true;
        }

        return false;
    }

    private static bool CanUsePartialIndex(DocumentPathIndex index, IReadOnlyList<DocumentFilter> leaves)
        => index.PartialFilter is null
           || leaves.Any(leaf => LeafSatisfiesPartialFilter(index.PartialFilter, leaf));

    private static bool LeafSatisfiesPartialFilter(DocumentIndexPartialFilter filter, DocumentFilter leaf)
    {
        if (leaf is not DocumentFieldFilter
            {
                Field.Kind: DocumentFieldKind.JsonPath,
                Field.Path: not null,
            } field)
        {
            return false;
        }

        string normalizedPath = JsonPath.Parse(field.Field.Path).Text;
        if (!string.Equals(normalizedPath, filter.Path, StringComparison.Ordinal))
            return false;

        if (filter.Operator == DocumentIndexPartialFilterOperator.Exists)
        {
            bool expected = filter.ValueScalar is null or "true";
            if (field.Operator == DocumentFilterOperator.Exists)
            {
                bool actual = field.Value is not bool b || b;
                return actual == expected;
            }

            return expected;
        }

        return TryMapPartialFilterOperator(field.Operator, out var mapped)
               && mapped == filter.Operator
               && string.Equals(JsonPathEvaluator.ToIndexScalar(field.Value), filter.ValueScalar, StringComparison.Ordinal);
    }

    private static bool TryMapPartialFilterOperator(
        DocumentFilterOperator source,
        out DocumentIndexPartialFilterOperator target)
    {
        switch (source)
        {
            case DocumentFilterOperator.Equal:
                target = DocumentIndexPartialFilterOperator.Equal;
                return true;
            case DocumentFilterOperator.NotEqual:
                target = DocumentIndexPartialFilterOperator.NotEqual;
                return true;
            case DocumentFilterOperator.GreaterThan:
                target = DocumentIndexPartialFilterOperator.GreaterThan;
                return true;
            case DocumentFilterOperator.GreaterThanOrEqual:
                target = DocumentIndexPartialFilterOperator.GreaterThanOrEqual;
                return true;
            case DocumentFilterOperator.LessThan:
                target = DocumentIndexPartialFilterOperator.LessThan;
                return true;
            case DocumentFilterOperator.LessThanOrEqual:
                target = DocumentIndexPartialFilterOperator.LessThanOrEqual;
                return true;
            default:
                target = default;
                return false;
        }
    }

    private static IEnumerable<DocumentFilter> FlattenAnd(DocumentFilter? filter)
    {
        if (filter is null)
            yield break;

        if (filter is DocumentAndFilter and)
        {
            foreach (var child in and.Filters)
            {
                foreach (var leaf in FlattenAnd(child))
                    yield return leaf;
            }

            yield break;
        }

        yield return filter;
    }

    private static bool MatchesFieldFilter(DocumentFieldFilter filter, DocumentRow row)
    {
        bool exists = TryGetFieldValue(row, filter.Field, out object? actual);
        if (filter.Operator == DocumentFilterOperator.Exists)
        {
            bool expected = filter.Value is not bool b || b;
            return expected ? exists : !exists;
        }

        if (!exists)
            return false;

        return filter.Operator switch
        {
            DocumentFilterOperator.Equal => ValuesEqual(actual, filter.Value),
            DocumentFilterOperator.NotEqual => !ValuesEqual(actual, filter.Value),
            DocumentFilterOperator.GreaterThan => CompareScalar(actual, filter.Value) is > 0,
            DocumentFilterOperator.GreaterThanOrEqual => CompareScalar(actual, filter.Value) is >= 0,
            DocumentFilterOperator.LessThan => CompareScalar(actual, filter.Value) is < 0,
            DocumentFilterOperator.LessThanOrEqual => CompareScalar(actual, filter.Value) is <= 0,
            DocumentFilterOperator.In => EnumerateFilterValues(filter.Value).Any(value => ValuesEqual(actual, value)),
            DocumentFilterOperator.NotIn => !EnumerateFilterValues(filter.Value).Any(value => ValuesEqual(actual, value)),
            DocumentFilterOperator.Contains => ContainsValue(row, filter.Field, actual, filter.Value),
            _ => throw new InvalidOperationException($"不支持的文档过滤运算符 '{filter.Operator}'。"),
        };
    }

    private static IReadOnlyList<DocumentRow> ApplySort(IReadOnlyList<DocumentRow> rows, IReadOnlyList<DocumentSort> sort)
    {
        if (rows.Count <= 1)
            return rows;

        IReadOnlyList<DocumentSort> effectiveSort = sort.Count == 0
            ? new[] { new DocumentSort(DocumentFieldRef.Id) }
            : sort;

        return rows
            .OrderBy(row => row, new DocumentRowComparer(effectiveSort))
            .ToArray();
    }

    private static IReadOnlyList<DocumentRow> ApplyPagination(IReadOnlyList<DocumentRow> rows, int skip, int? limit)
    {
        if (skip >= rows.Count)
            return [];

        int take = limit ?? (rows.Count - skip);
        if (take <= 0)
            return [];

        return rows.Skip(skip).Take(Math.Min(take, rows.Count - skip)).ToArray();
    }

    private static string ProjectJson(DocumentRow row, DocumentProjection? projection)
    {
        if (projection is null || projection.Fields.Count == 0)
            return row.Json;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in projection.Fields)
            {
                if (field.Field.Kind == DocumentFieldKind.Id)
                {
                    writer.WritePropertyName(field.Name);
                    writer.WriteStringValue(row.Id);
                    continue;
                }

                if (TryGetFieldElement(row, field.Field, out var owner, out var element))
                {
                    writer.WritePropertyName(field.Name);
                    using (owner)
                        element.WriteTo(writer);
                    continue;
                }

                if (TryGetFieldValue(row, field.Field, out object? value))
                {
                    writer.WritePropertyName(field.Name);
                    WriteJsonValue(writer, value);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;

            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;

            case byte or sbyte or short or ushort or int or uint or long:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;

            case ulong ulongValue:
                writer.WriteNumberValue(ulongValue);
                break;

            case float or double or decimal:
                writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                break;

            case string text when TryWriteRawJsonValue(writer, text):
                break;

            case string text:
                writer.WriteStringValue(text);
                break;

            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }

    private static bool TryWriteRawJsonValue(Utf8JsonWriter writer, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        char first = text.TrimStart()[0];
        if (first != '{' && first != '[')
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            document.RootElement.WriteTo(writer);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsValue(DocumentRow row, DocumentFieldRef field, object? actual, object? expected)
    {
        if (TryGetFieldElement(row, field, out var owner, out var element))
        {
            using (owner)
                return JsonContains(element, expected);
        }

        if (actual is string text && expected is string expectedText)
            return text.Contains(expectedText, StringComparison.Ordinal);

        return false;
    }

    private static bool TryGetFieldElement(
        DocumentRow row,
        DocumentFieldRef field,
        out JsonDocument owner,
        out JsonElement element)
    {
        owner = null!;
        element = default;
        try
        {
            owner = JsonDocument.Parse(row.Json);
            if (field.Kind == DocumentFieldKind.Document)
            {
                element = owner.RootElement;
                return true;
            }

            if (field.Kind != DocumentFieldKind.JsonPath
                || !JsonPathEvaluator.TryResolve(owner.RootElement, JsonPath.Parse(RequirePath(field)), out element))
            {
                owner.Dispose();
                owner = null!;
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            owner?.Dispose();
            owner = null!;
            return false;
        }
    }

    private static bool JsonContains(JsonElement element, object? expected)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (JsonElementMatches(item, expected))
                    return true;
            }

            return false;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (expected is string propertyName && element.TryGetProperty(propertyName, out _))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (JsonElementMatches(property.Value, expected))
                    return true;
            }

            return false;
        }

        return element.ValueKind == JsonValueKind.String
            && expected is string expectedText
            && (element.GetString() ?? string.Empty).Contains(expectedText, StringComparison.Ordinal);
    }

    private static bool JsonElementMatches(JsonElement element, object? expected)
        => ValuesEqual(JsonPathEvaluator.ConvertElement(element), expected);

    private static IEnumerable<object?> EnumerateFilterValues(object? value)
    {
        if (value is null)
            yield break;

        if (value is IEnumerable<object?> objects)
        {
            foreach (var item in objects)
                yield return item;
            yield break;
        }

        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            foreach (var item in sequence)
                yield return item;
            yield break;
        }

        yield return value;
    }

    private static object? NormalizeComparableValue(object? value)
    {
        if (value is JsonElement element)
            return JsonPathEvaluator.ConvertElement(element);
        return value;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        left = NormalizeComparableValue(left);
        right = NormalizeComparableValue(right);
        if (left is null || right is null)
            return left is null && right is null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        return Equals(left, right);
    }

    private static int? CompareScalar(object? left, object? right)
    {
        left = NormalizeComparableValue(left);
        right = NormalizeComparableValue(right);
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

    private static bool IsNumeric(object value) => value is
        byte or sbyte or
        short or ushort or
        int or uint or
        long or ulong or
        float or double or decimal;

    private static string RequirePath(DocumentFieldRef field)
        => field.Path ?? throw new InvalidOperationException("JSON path 字段引用缺少 path。");

    private sealed record AccessSelection(
        AccessCandidate Selected,
        IReadOnlyList<AccessCandidate> Candidates);

    private sealed class AccessCandidate
    {
        private readonly Func<IReadOnlyList<DocumentRow>>? _loadRows;
        private IReadOnlyList<DocumentRow>? _rows;

        private AccessCandidate(
            IReadOnlyList<DocumentRow>? rows,
            Func<IReadOnlyList<DocumentRow>>? loadRows,
            string accessPath,
            string? indexName,
            int estimatedRows,
            int cost,
            IReadOnlyList<string> filterPushdownFields)
        {
            _rows = rows;
            _loadRows = loadRows;
            AccessPath = accessPath;
            IndexName = indexName;
            EstimatedRows = estimatedRows;
            Cost = cost;
            FilterPushdownFields = filterPushdownFields;
        }

        public string AccessPath { get; }

        public string? IndexName { get; }

        public int EstimatedRows { get; }

        public int Cost { get; }

        public IReadOnlyList<string> FilterPushdownFields { get; }

        public static AccessCandidate FromRows(
            IReadOnlyList<DocumentRow> rows,
            string accessPath,
            string? indexName,
            int cost,
            IReadOnlyList<string> filterPushdownFields)
            => new(rows, loadRows: null, accessPath, indexName, rows.Count, cost, filterPushdownFields);

        public static AccessCandidate Lazy(
            Func<IReadOnlyList<DocumentRow>> loadRows,
            string accessPath,
            string? indexName,
            int estimatedRows,
            int cost,
            IReadOnlyList<string> filterPushdownFields)
            => new(rows: null, loadRows, accessPath, indexName, estimatedRows, cost, filterPushdownFields);

        public IReadOnlyList<DocumentRow> LoadRows() => _rows ??= _loadRows!();
    }

    private readonly struct SortValue
    {
        public SortValue(bool exists, object? value)
        {
            Exists = exists;
            Value = NormalizeComparableValue(value);
        }

        public bool Exists { get; }

        public object? Value { get; }
    }

    private sealed class DocumentRowComparer : IComparer<DocumentRow>
    {
        private readonly IReadOnlyList<DocumentSort> _sort;

        public DocumentRowComparer(IReadOnlyList<DocumentSort> sort)
        {
            _sort = sort;
        }

        public int Compare(DocumentRow? x, DocumentRow? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            foreach (var sort in _sort)
            {
                var left = ReadSortValue(x, sort.Field);
                var right = ReadSortValue(y, sort.Field);
                int cmp = CompareSortValue(left, right);
                if (cmp != 0)
                    return sort.Descending ? -cmp : cmp;
            }

            return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
        }

        private static SortValue ReadSortValue(DocumentRow row, DocumentFieldRef field)
            => TryGetFieldValue(row, field, out object? value)
                ? new SortValue(exists: true, value)
                : new SortValue(exists: false, null);

        private static int CompareSortValue(SortValue left, SortValue right)
        {
            if (!left.Exists && !right.Exists)
                return 0;
            if (!left.Exists)
                return -1;
            if (!right.Exists)
                return 1;
            if (left.Value is null && right.Value is null)
                return 0;
            if (left.Value is null)
                return -1;
            if (right.Value is null)
                return 1;

            int? cmp = CompareScalar(left.Value, right.Value);
            if (cmp is not null)
                return cmp.Value;

            return string.Compare(
                Convert.ToString(left.Value, CultureInfo.InvariantCulture),
                Convert.ToString(right.Value, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }
}
