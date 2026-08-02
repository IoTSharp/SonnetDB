using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SonnetDB.Sql.Execution;

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
            if (MatchesCore(query.Filter, row, query.Collation))
                matches.Add(row);
        }

        var ordered = ApplySort(matches, query.Sort, query.Collation);
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
        => Matches(filter, row, DocumentCollation.Ordinal);

    /// <summary>
    /// 按指定基础校对模式判断单个文档是否匹配过滤表达式。
    /// </summary>
    /// <param name="filter">过滤表达式；为 null 时匹配。</param>
    /// <param name="row">文档行。</param>
    /// <param name="collation">字符串比较使用的基础校对模式。</param>
    /// <returns>匹配返回 true。</returns>
    public static bool Matches(DocumentFilter? filter, DocumentRow row, DocumentCollation collation)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateCollation(collation);
        ValidateFilter(filter);
        return MatchesCore(filter, row, collation);
    }

    /// <summary>
    /// 匹配已经通过 <see cref="ValidateFilter"/> 校验的过滤表达式，供一次校验后的批量扫描复用。
    /// </summary>
    /// <param name="filter">已校验的过滤表达式。</param>
    /// <param name="row">文档行。</param>
    /// <param name="collation">字符串比较使用的基础校对模式。</param>
    /// <returns>匹配返回 true。</returns>
    internal static bool MatchesValidated(
        DocumentFilter? filter,
        DocumentRow row,
        DocumentCollation collation = DocumentCollation.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(row);
        ValidateCollation(collation);
        return MatchesCore(filter, row, collation);
    }

    private static bool MatchesCore(DocumentFilter? filter, DocumentRow row, DocumentCollation collation)
    {
        if (filter is null)
            return true;
        return filter switch
        {
            DocumentAndFilter and => and.Filters.All(child => MatchesCore(child, row, collation)),
            DocumentOrFilter or => or.Filters.Any(child => MatchesCore(child, row, collation)),
            DocumentNotFilter not => !MatchesCore(not.Filter, row, collation),
            DocumentFieldFilter field => MatchesFieldFilter(field, row, collation),
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
        ValidateCollation(query.Collation);
        ValidateFilter(query.Filter);
    }

    private static void ValidateCollation(DocumentCollation collation)
    {
        if (collation != DocumentCollation.Ordinal
            && collation != DocumentCollation.OrdinalIgnoreCase)
        {
            throw new InvalidOperationException($"不支持的 document collation '{collation}'。");
        }
    }

    /// <summary>
    /// 在读取任何文档前校验过滤表达式形状与操作数。
    /// </summary>
    /// <param name="filter">待校验的过滤表达式。</param>
    internal static void ValidateFilter(DocumentFilter? filter)
    {
        if (filter is null)
            return;

        var ancestors = new HashSet<DocumentFilter>(ReferenceEqualityComparer.Instance);
        ValidateFilterCore(filter, ancestors);
    }

    private static void ValidateFilterCore(
        DocumentFilter filter,
        HashSet<DocumentFilter> ancestors)
    {
        if (!ancestors.Add(filter))
            throw new InvalidOperationException("document filter 不能包含循环引用。");

        try
        {
            switch (filter)
            {
                case DocumentAndFilter and:
                    ValidateLogicalChildren("$and", and.Filters, ancestors);
                    return;

                case DocumentOrFilter or:
                    ValidateLogicalChildren("$or", or.Filters, ancestors);
                    return;

                case DocumentNotFilter not:
                    if (not.Filter is null)
                        throw new InvalidOperationException("$not 必须包含一个过滤表达式。");
                    ValidateFilterCore(not.Filter, ancestors);
                    return;

                case DocumentFieldFilter field:
                    ValidateFieldFilter(field, ancestors);
                    return;

                default:
                    throw new InvalidOperationException(
                        $"document filter 必须恰好使用一种受支持的表达式形态，实际为 '{filter.GetType().Name}'。");
            }
        }
        finally
        {
            ancestors.Remove(filter);
        }
    }

    private static void ValidateLogicalChildren(
        string op,
        IReadOnlyList<DocumentFilter>? filters,
        HashSet<DocumentFilter> ancestors)
    {
        if (filters is not { Count: > 0 })
            throw new InvalidOperationException($"{op} 必须包含至少一个过滤表达式。");

        for (int i = 0; i < filters.Count; i++)
        {
            DocumentFilter child = filters[i]
                ?? throw new InvalidOperationException($"{op} 的第 {i} 个过滤表达式不能为空。");
            ValidateFilterCore(child, ancestors);
        }
    }

    private static void ValidateFieldFilter(
        DocumentFieldFilter filter,
        HashSet<DocumentFilter> ancestors)
    {
        if (filter.Field is null)
            throw new InvalidOperationException("字段过滤表达式必须指定 field。");
        ValidateField(filter.Field);

        if (!Enum.IsDefined(filter.Operator))
            throw new InvalidOperationException($"不支持的文档过滤运算符 '{filter.Operator}'。");

        switch (filter.Operator)
        {
            case DocumentFilterOperator.In:
            case DocumentFilterOperator.NotIn:
            case DocumentFilterOperator.All:
                if (!IsFilterValueSequence(filter.Value))
                    throw new InvalidOperationException($"{FormatOperator(filter.Operator)} 的操作数必须是值列表。");
                break;

            case DocumentFilterOperator.ElementMatch:
                if (filter.Value is not DocumentFilter elementFilter)
                    throw new InvalidOperationException("$elemMatch 的操作数必须是过滤表达式。");
                ValidateFilterCore(elementFilter, ancestors);
                break;

            case DocumentFilterOperator.Regex:
                var regex = ResolveRegex(filter.Value);
                RegexPatternMatcher.ValidatePattern(regex.Pattern, regex.Options);
                break;

            case DocumentFilterOperator.Type:
                if (EnumerateRequestedTypes(filter.Value).ToArray().Length == 0)
                    throw new InvalidOperationException("$type 至少需要一个 JSON 类型。");
                break;

            case DocumentFilterOperator.Size:
                _ = ReadArraySize(filter.Value);
                break;

            case DocumentFilterOperator.Exists:
                _ = ReadExistsExpected(filter.Value);
                break;
        }
    }

    private static void ValidateField(DocumentFieldRef field)
    {
        switch (field.Kind)
        {
            case DocumentFieldKind.Id:
            case DocumentFieldKind.Document:
                if (field.Path is not null)
                    throw new InvalidOperationException($"字段类型 '{field.Kind}' 不能同时指定 JSON path。");
                return;

            case DocumentFieldKind.JsonPath:
                if (string.IsNullOrWhiteSpace(field.Path))
                    throw new InvalidOperationException("JSON path 字段引用缺少 path。");
                _ = JsonPath.Parse(field.Path);
                return;

            default:
                throw new InvalidOperationException($"不支持的文档字段类型 '{field.Kind}'。");
        }
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
            .ThenBy(static candidate => candidate.AccessPath == "document_wildcard_index" ? 1 : 0)
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
        int outputRows = CountMatches(query.Filter, rows, query.Collation);
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
            SortUsesIndex(selected.AccessPath, rows.Count, query.Sort, query.Collation),
            ProjectionCoveredByIndex(selected, query.Projection),
            planCandidates,
            BuildGapReason(schema, query, selected));
    }

    private static IEnumerable<AccessCandidate> BuildAccessCandidates(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        DocumentQuery query)
    {
        if (query.Collation == DocumentCollation.Ordinal
            && TryExtractIdEquals(query.Filter, out string id))
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
        else if (query.Collation == DocumentCollation.Ordinal
                 && TryExtractIdSet(query.Filter, out var ids))
        {
            var rows = store.GetMany(ids);
            yield return AccessCandidate.FromRows(
                rows,
                "document_id_set",
                "primary",
                rows.Count,
                [FormatField(DocumentFieldRef.Id)]);
        }

        if (query.Collation == DocumentCollation.Ordinal)
        {
            var leaves = FlattenAnd(query.Filter).ToArray();
            var equalityByPath = ExtractEqualityByPath(leaves);
            foreach (var index in schema.Indexes.Where(static index => index.Kind == DocumentIndexKind.Path))
            {
                if (!CanUsePartialIndex(index, leaves))
                    continue;

                var prefixValues = BuildIndexPrefixValues(index, equalityByPath);
                if (prefixValues.Count == 0)
                    continue;
                if (prefixValues.Any(static value => !CanUseMultikeyLookupValue(value)))
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

            foreach (var index in schema.Indexes.Where(static index => index.Kind == DocumentIndexKind.Wildcard))
            {
                if (!CanUsePartialIndex(index, leaves))
                    continue;

                foreach (var pair in equalityByPath)
                {
                    if (!CanUseWildcardIndexForPath(index.Path, pair.Key)
                        || !CanUseMultikeyLookupValue(pair.Value)
                        || (index.IsSparse && pair.Value is null))
                    {
                        continue;
                    }

                    int entryCount = store.CountByWildcardIndex(index, pair.Key, pair.Value);
                    int residualPenalty = Math.Max(0, CollectFilterFields(query.Filter).Count() - 1);
                    string boundPath = pair.Key;
                    object? boundValue = pair.Value;
                    var boundIndex = index;
                    yield return AccessCandidate.Lazy(
                        () => store.GetByWildcardIndex(boundIndex, boundPath, boundValue),
                        "document_wildcard_index",
                        index.Name,
                        entryCount,
                        entryCount + residualPenalty,
                        [pair.Key]);
                }
            }
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

    private static bool CanUseMultikeyLookupValue(object? value)
    {
        if (value is JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array })
            return false;
        if (value is not string text)
            return true;

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
            return true;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool IsPathWithinWildcardRoot(string rootPath, string candidatePath)
    {
        JsonPath root = JsonPath.Parse(rootPath);
        JsonPath candidate = JsonPath.Parse(candidatePath);
        if (root.Segments.Count > candidate.Segments.Count)
            return false;

        for (int i = 0; i < root.Segments.Count; i++)
        {
            if (root.Segments[i] != candidate.Segments[i])
                return false;
        }

        return true;
    }

    private static bool CanUseWildcardIndexForPath(string rootPath, string candidatePath)
    {
        JsonPath root = JsonPath.Parse(rootPath);
        JsonPath candidate = JsonPath.Parse(candidatePath);
        if (!IsPathWithinWildcardRoot(rootPath, candidatePath))
            return false;

        // Wildcard 条目会扁平化 subtree 内的数组且不编码元素下标；带显式下标的
        // 查询必须回退扫描，否则一个零命中索引候选会造成假阴性。
        return candidate.Segments
            .Skip(root.Segments.Count)
            .All(static segment => segment.Kind != JsonPathSegmentKind.ArrayIndex);
    }

    private static int CountMatches(
        DocumentFilter? filter,
        IReadOnlyList<DocumentRow> rows,
        DocumentCollation collation)
    {
        int count = 0;
        foreach (var row in rows)
        {
            if (MatchesCore(filter, row, collation))
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
        if (query.Collation != DocumentCollation.Ordinal && query.Filter is not null)
            return "collation_requires_residual_scan";

        if (selected.AccessPath == "document_scan"
            && HasUnsupportedWildcardPredicate(schema, query.Filter))
        {
            return "wildcard_index_predicate_not_supported";
        }

        if (HasIndexIntersectionOpportunity(schema, query.Filter, selected))
            return "index_intersection_not_supported";

        if (!SortUsesIndex(selected.AccessPath, selected.EstimatedRows, query.Sort, query.Collation)
            && query.Sort.Count > 0)
            return "sort_requires_in_memory_order_by";

        if (!ProjectionCoveredByIndex(selected, query.Projection) && query.Projection is { Fields.Count: > 0 })
            return "projection_not_covered_by_index";

        return null;
    }

    private static bool HasUnsupportedWildcardPredicate(
        DocumentCollectionSchema schema,
        DocumentFilter? filter)
    {
        DocumentPathIndex[] wildcardIndexes = schema.Indexes
            .Where(static index => index.Kind == DocumentIndexKind.Wildcard)
            .ToArray();
        if (wildcardIndexes.Length == 0)
            return false;

        foreach (var leaf in FlattenAnd(filter))
        {
            if (leaf is not DocumentFieldFilter
                {
                    Field.Kind: DocumentFieldKind.JsonPath,
                    Field.Path: not null,
                } field)
            {
                continue;
            }

            string path = JsonPath.Parse(field.Field.Path).Text;
            DocumentPathIndex[] coveringIndexes = wildcardIndexes
                .Where(index => IsPathWithinWildcardRoot(index.Path, path))
                .ToArray();
            if (coveringIndexes.Length == 0)
                continue;
            if (field.Operator != DocumentFilterOperator.Equal
                || !CanUseMultikeyLookupValue(field.Value)
                || !coveringIndexes.Any(index => CanUseWildcardIndexForPath(index.Path, path)))
            {
                return true;
            }
        }

        return false;
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

    private static bool SortUsesIndex(
        string accessPath,
        int candidateRows,
        IReadOnlyList<DocumentSort> sort,
        DocumentCollation collation)
    {
        if (candidateRows <= 1)
            return true;

        if (collation != DocumentCollation.Ordinal)
            return false;

        if (sort.Count == 0)
            return accessPath is "document_id" or "document_scan";

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

    private static bool MatchesFieldFilter(
        DocumentFieldFilter filter,
        DocumentRow row,
        DocumentCollation collation)
    {
        if (filter.Field.Kind == DocumentFieldKind.Id)
        {
            return MatchesResolvedField(
                filter,
                exists: true,
                row.Id,
                hasElement: false,
                default,
                collation);
        }

        if (!TryGetFieldElements(row, filter.Field, out var owner, out var elements))
        {
            return MatchesResolvedField(
                filter,
                exists: false,
                actual: null,
                hasElement: false,
                default,
                collation);
        }

        using (owner)
        {
            bool requireAll = filter.Operator is DocumentFilterOperator.NotEqual or DocumentFilterOperator.NotIn;
            foreach (var element in elements)
            {
                object? actual = filter.Field.Kind == DocumentFieldKind.Document
                    ? row.Json
                    : JsonPathEvaluator.ConvertElement(element);
                bool matches = MatchesResolvedField(
                    filter,
                    exists: true,
                    actual,
                    hasElement: true,
                    element,
                    collation);
                if (matches != requireAll)
                    return matches;
            }

            return requireAll;
        }
    }

    private static bool MatchesElementFilterCore(
        DocumentFilter filter,
        JsonElement element,
        DocumentCollation collation)
        => filter switch
        {
            DocumentAndFilter and => and.Filters.All(child => MatchesElementFilterCore(child, element, collation)),
            DocumentOrFilter or => or.Filters.Any(child => MatchesElementFilterCore(child, element, collation)),
            DocumentNotFilter not => !MatchesElementFilterCore(not.Filter, element, collation),
            DocumentFieldFilter field => MatchesElementFieldFilter(field, element, collation),
            _ => throw new InvalidOperationException($"不支持的文档过滤表达式类型 '{filter.GetType().Name}'。"),
        };

    private static bool MatchesElementFieldFilter(
        DocumentFieldFilter filter,
        JsonElement element,
        DocumentCollation collation)
    {
        if (filter.Field.Kind == DocumentFieldKind.Id)
        {
            return MatchesResolvedField(
                filter,
                exists: false,
                actual: null,
                hasElement: false,
                default,
                collation);
        }

        JsonElement resolved;
        if (filter.Field.Kind == DocumentFieldKind.Document)
        {
            resolved = element;
        }
        else if (!JsonPathEvaluator.TryResolve(element, JsonPath.Parse(RequirePath(filter.Field)), out resolved))
        {
            return MatchesResolvedField(
                filter,
                exists: false,
                actual: null,
                hasElement: false,
                default,
                collation);
        }

        return MatchesResolvedField(
            filter,
            exists: true,
            JsonPathEvaluator.ConvertElement(resolved),
            hasElement: true,
            resolved,
            collation);
    }

    private static bool MatchesResolvedField(
        DocumentFieldFilter filter,
        bool exists,
        object? actual,
        bool hasElement,
        JsonElement actualElement,
        DocumentCollation collation)
    {
        if (filter.Operator == DocumentFilterOperator.Exists)
        {
            bool expected = ReadExistsExpected(filter.Value);
            return expected ? exists : !exists;
        }

        if (!exists)
            return false;

        return filter.Operator switch
        {
            DocumentFilterOperator.Equal => FieldValueEquals(actual, hasElement, actualElement, filter.Value, collation),
            DocumentFilterOperator.NotEqual => !FieldValueEquals(actual, hasElement, actualElement, filter.Value, collation),
            DocumentFilterOperator.GreaterThan => CompareScalar(actual, filter.Value, collation) is > 0,
            DocumentFilterOperator.GreaterThanOrEqual => CompareScalar(actual, filter.Value, collation) is >= 0,
            DocumentFilterOperator.LessThan => CompareScalar(actual, filter.Value, collation) is < 0,
            DocumentFilterOperator.LessThanOrEqual => CompareScalar(actual, filter.Value, collation) is <= 0,
            DocumentFilterOperator.In => EnumerateFilterValues(filter.Value)
                .Any(value => FieldValueEquals(actual, hasElement, actualElement, value, collation)),
            DocumentFilterOperator.NotIn => !EnumerateFilterValues(filter.Value)
                .Any(value => FieldValueEquals(actual, hasElement, actualElement, value, collation)),
            DocumentFilterOperator.Contains => ContainsValue(actual, hasElement, actualElement, filter.Value, collation),
            DocumentFilterOperator.ElementMatch => hasElement
                && ElementMatches(actualElement, (DocumentFilter)filter.Value!, collation),
            DocumentFilterOperator.Regex => RegexMatches(actual, hasElement, actualElement, filter.Value),
            DocumentFilterOperator.Type => TypeMatches(actual, hasElement, actualElement, filter.Value),
            DocumentFilterOperator.Size => hasElement
                && actualElement.ValueKind == JsonValueKind.Array
                && actualElement.GetArrayLength() == ReadArraySize(filter.Value),
            DocumentFilterOperator.All => hasElement
                && AllValuesMatch(actualElement, filter.Value, collation),
            _ => throw new InvalidOperationException($"不支持的文档过滤运算符 '{filter.Operator}'。"),
        };
    }

    private static IReadOnlyList<DocumentRow> ApplySort(
        IReadOnlyList<DocumentRow> rows,
        IReadOnlyList<DocumentSort> sort,
        DocumentCollation collation)
    {
        if (rows.Count <= 1)
            return rows;

        IReadOnlyList<DocumentSort> effectiveSort = sort.Count == 0
            ? new[] { new DocumentSort(DocumentFieldRef.Id) }
            : sort;

        return rows
            .OrderBy(row => row, new DocumentRowComparer(effectiveSort, collation))
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

    private static bool ContainsValue(
        object? actual,
        bool hasElement,
        JsonElement actualElement,
        object? expected,
        DocumentCollation collation)
    {
        if (hasElement)
            return JsonContains(actualElement, expected, collation);

        if (actual is string text && expected is string expectedText)
            return text.Contains(expectedText, GetStringComparison(collation));

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

    private static bool TryGetFieldElements(
        DocumentRow row,
        DocumentFieldRef field,
        out JsonDocument owner,
        out IReadOnlyList<JsonElement> elements)
    {
        owner = null!;
        elements = [];
        try
        {
            owner = JsonDocument.Parse(row.Json);
            if (field.Kind == DocumentFieldKind.Document)
            {
                elements = [owner.RootElement];
                return true;
            }

            if (field.Kind != DocumentFieldKind.JsonPath)
            {
                owner.Dispose();
                owner = null!;
                return false;
            }

            var resolved = new List<JsonElement>();
            ResolveFieldElements(
                owner.RootElement,
                JsonPath.Parse(RequirePath(field)).Segments,
                segmentIndex: 0,
                resolved);
            if (resolved.Count == 0)
            {
                owner.Dispose();
                owner = null!;
                return false;
            }

            elements = resolved;
            return true;
        }
        catch (JsonException)
        {
            owner?.Dispose();
            owner = null!;
            elements = [];
            return false;
        }
    }

    private static void ResolveFieldElements(
        JsonElement current,
        IReadOnlyList<JsonPathSegment> segments,
        int segmentIndex,
        List<JsonElement> output)
    {
        if (segmentIndex == segments.Count)
        {
            output.Add(current);
            return;
        }

        JsonPathSegment segment = segments[segmentIndex];
        if (current.ValueKind == JsonValueKind.Array && segment.Kind == JsonPathSegmentKind.Property)
        {
            // 查询索引会隐式展开对象数组；复检必须使用相同的 path 语义。
            foreach (var item in current.EnumerateArray())
                ResolveFieldElements(item, segments, segmentIndex, output);
            return;
        }

        if (segment.Kind == JsonPathSegmentKind.Property)
        {
            if (current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty(segment.PropertyName!, out var property))
            {
                ResolveFieldElements(property, segments, segmentIndex + 1, output);
            }

            return;
        }

        if (current.ValueKind == JsonValueKind.Array && segment.ArrayIndex < current.GetArrayLength())
            ResolveFieldElements(current[segment.ArrayIndex], segments, segmentIndex + 1, output);
    }

    private static bool JsonContains(
        JsonElement element,
        object? expected,
        DocumentCollation collation)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (JsonElementMatches(item, expected, collation))
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
                if (JsonElementMatches(property.Value, expected, collation))
                    return true;
            }

            return false;
        }

        return element.ValueKind == JsonValueKind.String
            && expected is string expectedText
            && (element.GetString() ?? string.Empty).Contains(expectedText, GetStringComparison(collation));
    }

    private static bool FieldValueEquals(
        object? actual,
        bool hasElement,
        JsonElement actualElement,
        object? expected,
        DocumentCollation collation)
    {
        if (hasElement
            && (actualElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                || expected is JsonElement
                {
                    ValueKind: JsonValueKind.Object or JsonValueKind.Array,
                }))
        {
            return JsonElementMatches(actualElement, expected, collation);
        }

        return ValuesEqual(actual, expected, collation);
    }

    private static bool JsonElementMatches(
        JsonElement element,
        object? expected,
        DocumentCollation collation)
    {
        if (expected is JsonElement expectedElement
            && expectedElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
        {
            return JsonElementsEqual(element, expectedElement, collation);
        }

        if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            && expected is string expectedJson
            && TryParseStructuredJson(expectedJson, out var expectedDocument))
        {
            using (expectedDocument)
                return JsonElementsEqual(element, expectedDocument.RootElement, collation);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (JsonElementMatches(item, expected, collation))
                    return true;
            }

            return false;
        }

        if (expected is JsonElement scalarElement)
            return JsonElementsEqual(element, scalarElement, collation);

        return ValuesEqual(JsonPathEvaluator.ConvertElement(element), expected, collation);
    }

    private static bool JsonElementsEqual(
        JsonElement left,
        JsonElement right,
        DocumentCollation collation)
    {
        if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            return left.GetDouble().Equals(right.GetDouble());

        if (left.ValueKind != right.ValueKind)
            return false;

        return left.ValueKind switch
        {
            JsonValueKind.Null => true,
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.String => string.Equals(
                left.GetString(),
                right.GetString(),
                GetStringComparison(collation)),
            JsonValueKind.Object => JsonObjectsEqual(left, right, collation),
            JsonValueKind.Array => JsonArraysEqual(left, right, collation),
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal),
        };
    }

    private static bool JsonObjectsEqual(
        JsonElement left,
        JsonElement right,
        DocumentCollation collation)
    {
        var leftProperties = left.EnumerateObject().ToArray();
        var rightProperties = right.EnumerateObject().ToArray();
        if (leftProperties.Length != rightProperties.Length)
            return false;

        for (int i = 0; i < leftProperties.Length; i++)
        {
            if (!string.Equals(leftProperties[i].Name, rightProperties[i].Name, StringComparison.Ordinal)
                || !JsonElementsEqual(leftProperties[i].Value, rightProperties[i].Value, collation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool JsonArraysEqual(
        JsonElement left,
        JsonElement right,
        DocumentCollation collation)
    {
        if (left.GetArrayLength() != right.GetArrayLength())
            return false;

        var leftItems = left.EnumerateArray();
        var rightItems = right.EnumerateArray();
        while (leftItems.MoveNext() && rightItems.MoveNext())
        {
            if (!JsonElementsEqual(leftItems.Current, rightItems.Current, collation))
                return false;
        }

        return true;
    }

    private static bool TryParseStructuredJson(string text, out JsonDocument document)
    {
        document = null!;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        char first = text.TrimStart()[0];
        if (first is not ('{' or '['))
            return false;

        try
        {
            document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static bool ElementMatches(
        JsonElement element,
        DocumentFilter filter,
        DocumentCollation collation)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in element.EnumerateArray())
        {
            if (MatchesElementFilterCore(filter, item, collation))
                return true;
        }

        return false;
    }

    private static bool RegexMatches(
        object? actual,
        bool hasElement,
        JsonElement actualElement,
        object? operand)
    {
        string? input;
        if (hasElement)
        {
            if (actualElement.ValueKind != JsonValueKind.String)
                return false;
            input = actualElement.GetString();
        }
        else
        {
            input = actual as string;
        }

        if (input is null)
            return false;

        var regex = ResolveRegex(operand);
        return RegexPatternMatcher.IsMatch(input, regex.Pattern, regex.Options);
    }

    private static DocumentRegex ResolveRegex(object? value)
    {
        if (value is DocumentRegex regex)
        {
            if (regex.Pattern is null)
                throw new InvalidOperationException("$regex 必须指定 pattern。");
            return regex;
        }

        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
                return new DocumentRegex(element.GetString() ?? string.Empty);
            if (element.ValueKind == JsonValueKind.Object)
                return ResolveRegexObject(element);
            throw new InvalidOperationException("$regex 的操作数必须是字符串或正则对象。");
        }

        if (value is string text)
        {
            if (TryParseRegexObject(text, out var parsed))
                return parsed;
            return new DocumentRegex(text);
        }

        throw new InvalidOperationException("$regex 的操作数必须是字符串或正则对象。");
    }

    private static bool TryParseRegexObject(string text, out DocumentRegex regex)
    {
        regex = null!;
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{')
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !TryGetRegexPattern(document.RootElement, out _))
            {
                return false;
            }

            regex = ResolveRegexObject(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DocumentRegex ResolveRegexObject(JsonElement element)
    {
        if (!TryGetRegexPattern(element, out var patternElement)
            || patternElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("$regex 对象必须包含字符串 pattern 或 $regex。");
        }

        string? options = null;
        if (element.TryGetProperty("options", out var optionsElement)
            || element.TryGetProperty("$options", out optionsElement))
        {
            if (optionsElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("$regex options 必须是字符串。");
            options = optionsElement.GetString();
        }

        return new DocumentRegex(patternElement.GetString() ?? string.Empty, options);
    }

    private static bool TryGetRegexPattern(JsonElement element, out JsonElement pattern)
        => element.TryGetProperty("pattern", out pattern)
           || element.TryGetProperty("$regex", out pattern);

    private static bool TypeMatches(
        object? actual,
        bool hasElement,
        JsonElement actualElement,
        object? operand)
    {
        DocumentJsonType actualType = hasElement
            ? GetJsonType(actualElement)
            : GetJsonType(actual);
        return EnumerateRequestedTypes(operand).Contains(actualType);
    }

    private static IEnumerable<DocumentJsonType> EnumerateRequestedTypes(object? value)
    {
        IEnumerable<object?> values = IsFilterValueSequence(value)
            ? EnumerateFilterValues(value)
            : [value];
        foreach (object? item in values)
        {
            if (!TryParseJsonType(item, out var type))
                throw new InvalidOperationException($"$type 不支持 JSON 类型 '{FormatValue(item)}'。");
            yield return type;
        }
    }

    private static bool TryParseJsonType(object? value, out DocumentJsonType type)
    {
        value = NormalizeComparableValue(value);
        if (value is DocumentJsonType typed && Enum.IsDefined(typed))
        {
            type = typed;
            return true;
        }

        if (value is string text)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "null":
                    type = DocumentJsonType.Null;
                    return true;
                case "bool":
                case "boolean":
                    type = DocumentJsonType.Boolean;
                    return true;
                case "number":
                case "int":
                case "long":
                case "double":
                case "decimal":
                    type = DocumentJsonType.Number;
                    return true;
                case "string":
                    type = DocumentJsonType.String;
                    return true;
                case "object":
                    type = DocumentJsonType.Object;
                    return true;
                case "array":
                    type = DocumentJsonType.Array;
                    return true;
            }
        }

        type = default;
        return false;
    }

    private static DocumentJsonType GetJsonType(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Null => DocumentJsonType.Null,
            JsonValueKind.True or JsonValueKind.False => DocumentJsonType.Boolean,
            JsonValueKind.Number => DocumentJsonType.Number,
            JsonValueKind.String => DocumentJsonType.String,
            JsonValueKind.Object => DocumentJsonType.Object,
            JsonValueKind.Array => DocumentJsonType.Array,
            _ => throw new InvalidOperationException($"不支持的 JSON 类型 '{element.ValueKind}'。"),
        };

    private static DocumentJsonType GetJsonType(object? value)
    {
        value = NormalizeComparableValue(value);
        return value switch
        {
            null => DocumentJsonType.Null,
            bool => DocumentJsonType.Boolean,
            string => DocumentJsonType.String,
            _ when value is not null && IsNumeric(value) => DocumentJsonType.Number,
            _ => throw new InvalidOperationException($"无法把 '{value!.GetType().Name}' 映射为 JSON 类型。"),
        };
    }

    private static int ReadArraySize(object? value)
    {
        value = NormalizeComparableValue(value);
        if (value is null || !IsNumeric(value))
            throw new InvalidOperationException("$size 的操作数必须是非负整数。");

        decimal size;
        try
        {
            size = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is OverflowException or FormatException)
        {
            throw new InvalidOperationException("$size 的操作数必须是非负整数。", exception);
        }

        if (size < 0 || size > int.MaxValue || size != decimal.Truncate(size))
            throw new InvalidOperationException("$size 的操作数必须是非负整数。");
        return decimal.ToInt32(size);
    }

    private static bool AllValuesMatch(
        JsonElement element,
        object? operand,
        DocumentCollation collation)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return false;

        object?[] expectedValues = EnumerateFilterValues(operand).ToArray();
        if (expectedValues.Length == 0)
            return false;

        foreach (object? expected in expectedValues)
        {
            bool found = false;
            foreach (var item in element.EnumerateArray())
            {
                if (!JsonElementMatches(item, expected, collation))
                    continue;
                found = true;
                break;
            }

            if (!found)
                return false;
        }

        return true;
    }

    private static bool ReadExistsExpected(object? value)
    {
        value = NormalizeComparableValue(value);
        return value switch
        {
            null => true,
            bool boolean => boolean,
            _ => throw new InvalidOperationException("$exists 的操作数必须是布尔值或 null。"),
        };
    }

    private static bool IsFilterValueSequence(object? value)
        => value is JsonElement { ValueKind: JsonValueKind.Array }
           || value is System.Collections.IEnumerable and not string;

    private static IEnumerable<object?> EnumerateFilterValues(object? value)
    {
        if (value is null)
            yield break;

        if (value is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
                yield return item.Clone();
            yield break;
        }

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

    private static bool ValuesEqual(
        object? left,
        object? right,
        DocumentCollation collation)
    {
        left = NormalizeComparableValue(left);
        right = NormalizeComparableValue(right);
        if (left is null || right is null)
            return left is null && right is null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        if (left is string leftString && right is string rightString)
            return string.Equals(leftString, rightString, GetStringComparison(collation));

        return Equals(left, right);
    }

    private static int? CompareScalar(
        object? left,
        object? right,
        DocumentCollation collation)
    {
        left = NormalizeComparableValue(left);
        right = NormalizeComparableValue(right);
        if (left is null || right is null)
            return null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        if (left is string leftString && right is string rightString)
            return string.Compare(leftString, rightString, GetStringComparison(collation));

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        throw new InvalidOperationException($"无法比较 {left.GetType().Name} 与 {right.GetType().Name}。");
    }

    private static StringComparison GetStringComparison(DocumentCollation collation)
        => collation == DocumentCollation.OrdinalIgnoreCase
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string FormatOperator(DocumentFilterOperator op)
        => op switch
        {
            DocumentFilterOperator.In => "$in",
            DocumentFilterOperator.NotIn => "$nin",
            DocumentFilterOperator.All => "$all",
            _ => op.ToString(),
        };

    private static string FormatValue(object? value)
        => Convert.ToString(NormalizeComparableValue(value), CultureInfo.InvariantCulture) ?? "null";

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
        private readonly DocumentCollation _collation;

        public DocumentRowComparer(IReadOnlyList<DocumentSort> sort, DocumentCollation collation)
        {
            _sort = sort;
            _collation = collation;
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

        private int CompareSortValue(SortValue left, SortValue right)
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

            int? cmp = CompareScalar(left.Value, right.Value, _collation);
            if (cmp is not null)
                return cmp.Value;

            return string.Compare(
                Convert.ToString(left.Value, CultureInfo.InvariantCulture),
                Convert.ToString(right.Value, CultureInfo.InvariantCulture),
                GetStringComparison(_collation));
        }
    }
}
