using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonnetDB.Data.Documents;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;

namespace SonnetDB.Data.FullText;

/// <summary>
/// SonnetDB 类型化全文检索客户端，统一支持嵌入式和远程服务端。
/// </summary>
public sealed class SndbFullTextClient : IDisposable
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>使用 SonnetDB 连接字符串创建全文客户端。</summary>
    /// <param name="connectionString">嵌入式或远程连接字符串。</param>
    public SndbFullTextClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        Open();
    }

    /// <summary>使用调用方 HTTP 客户端创建远程客户端，供合同测试注入传输。</summary>
    /// <param name="connectionString">远程连接字符串。</param>
    /// <param name="httpClient">HTTP 客户端；客户端释放时一并释放。</param>
    internal SndbFullTextClient(string connectionString, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _builder = new SndbConnectionStringBuilder(connectionString);
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
            throw new ArgumentException("测试 HTTP 客户端仅适用于远程连接。", nameof(connectionString));
        _database = _builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程全文客户端缺少数据库名。");
        httpClient.BaseAddress ??= new Uri(_builder.ResolveBaseUrl(), UriKind.Absolute);
        _http = httpClient;
    }

    /// <summary>当前连接模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>当前数据库名或嵌入式目录。</summary>
    public string Database => _database;

    /// <summary>
    /// 执行类型化全文检索。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="index">全文索引名称。</param>
    /// <param name="field">索引字段或 <c>*</c>。</param>
    /// <param name="query">查询文本。</param>
    /// <param name="options">过滤、排序、facet、高亮和分页选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>带稳定评分元数据和 continuation token 的分页结果。</returns>
    public async Task<SndbFullTextSearchResult> SearchAsync(
        string collection,
        string index,
        string field,
        string query,
        SndbFullTextSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        options ??= new SndbFullTextSearchOptions();
        if (options.Skip < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "skip 不能为负数。");
        cancellationToken.ThrowIfCancellationRequested();

        var request = new SndbFullTextSearchRequest(
            collection,
            index,
            field,
            query,
            options.PageSize,
            options.Skip,
            options.Mode,
            options.QueryKind,
            options.Filter,
            options.Sort,
            options.Facets,
            options.Highlight,
            options.ContinuationToken);

        if (_embedded is not null)
            return SearchEmbedded(request, cancellationToken);

        using var content = JsonContent.Create(
            request,
            SndbFullTextClientJsonContext.Default.SndbFullTextSearchRequest);
        using HttpResponseMessage response = await _http!.PostAsync(
            $"v1/db/{Uri.EscapeDataString(_database)}/fulltext/search",
            content,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"全文检索失败（HTTP {(int)response.StatusCode} {response.StatusCode}）：{detail}",
                inner: null,
                response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(
            stream,
            SndbFullTextClientJsonContext.Default.SndbFullTextSearchResult,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("全文检索响应为空。");
    }

    /// <summary>释放嵌入式数据库或远程 HTTP 资源。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http?.Dispose();
        _http = null;
        Tsdb? embedded = _embedded;
        _embedded = null;
        if (embedded is not null)
            SharedSndbRegistry.Release(embedded);
    }

    private SndbFullTextSearchResult SearchEmbedded(
        SndbFullTextSearchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schema = _embedded!.Documents.Catalog.TryGet(request.Collection)
            ?? throw new InvalidOperationException($"文档集合 '{request.Collection}' 不存在。");
        var definition = schema.TryGetFullTextIndex(request.Index)
            ?? throw new InvalidOperationException($"全文索引 '{request.Index}' 不存在。");
        FullTextSearchMode mode = string.Equals(request.Mode, "fuzzy", StringComparison.OrdinalIgnoreCase)
            ? FullTextSearchMode.Fuzzy
            : FullTextSearchMode.Exact;
        FullTextQueryKind queryKind = request.QueryKind?.Trim().ToLowerInvariant() switch
        {
            "any" or "or" or "should" => FullTextQueryKind.Any,
            "phrase" => FullTextQueryKind.Phrase,
            _ => FullTextQueryKind.All,
        };
        int pageSize = request.PageSize is null or <= 0 ? 20 : Math.Min(request.PageSize.Value, 100);
        int offset = request.Skip;
        if (!string.IsNullOrWhiteSpace(request.ContinuationToken)
            && !int.TryParse(request.ContinuationToken, out offset))
            throw new ArgumentException("嵌入式全文 continuation token 无效。", nameof(request));
        int take = Math.Min(10_000, checked(offset + pageSize + 1));
        var store = _embedded.Documents.Open(request.Collection);
        var candidates = store.SearchFullText(definition, request.Field, request.Query, take, mode, queryKind);
        var rows = new Dictionary<string, DocumentRow>(StringComparer.Ordinal);
        foreach (DocumentFullTextSearchHit hit in candidates)
        {
            if (store.Get(hit.DocumentId) is { } row)
                rows[hit.DocumentId] = row;
        }
        DocumentFilter? filter = ToCoreFilter(request.Filter);
        if (filter is not null)
        {
            rows = store.Scan(10_000).ToDictionary(static row => row.Id, StringComparer.Ordinal);
            candidates = candidates.Where(hit => rows.TryGetValue(hit.DocumentId, out var row) && DocumentQueryPlanner.Matches(filter, row)).ToArray();
        }
        var ordered = ApplySort(candidates, request.Sort);
        var page = ordered.Skip(offset).Take(pageSize).ToArray();
        bool hasMore = ordered.Count > offset + page.Length || candidates.Count == take;
        var hits = page.Select(hit => BuildHit(hit, request, rows)).ToArray();
        var facets = BuildFacets(request.Facets, ordered, rows);
        return new SndbFullTextSearchResult(
            request.Collection,
            hits,
            facets,
            hasMore ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            hasMore,
            pageSize,
            ordered.Count);
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            string dataSource = _builder.ResolveEmbeddedDataSource();
            if (string.IsNullOrWhiteSpace(dataSource))
                throw new InvalidOperationException("全文客户端缺少 Data Source。");
            _database = dataSource;
            _embedded = SharedSndbRegistry.Acquire(_builder.CreateEmbeddedOptions(dataSource));
            return;
        }

        _database = _builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程全文客户端缺少数据库名。");
        _http = RemoteHttpClientFactory.Create(
            new Uri(_builder.ResolveBaseUrl(), UriKind.Absolute),
            _builder.Username,
            _builder.Password,
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout),
            allowAutoRedirect: false);
    }

    private static IReadOnlyList<DocumentFullTextSearchHit> ApplySort(
        IReadOnlyList<DocumentFullTextSearchHit> hits,
        IReadOnlyList<SndbDocumentSort>? sort)
    {
        string path = sort is { Count: > 0 } ? sort[0].Path : "score";
        bool descending = sort is not { Count: > 0 } || sort[0].Descending;
        if (string.Equals(path, "id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "_id", StringComparison.OrdinalIgnoreCase))
            return (descending ? hits.OrderByDescending(static hit => hit.DocumentId, StringComparer.Ordinal) : hits.OrderBy(static hit => hit.DocumentId, StringComparer.Ordinal))
                .ThenByDescending(static hit => hit.Score).ToArray();
        return (descending ? hits.OrderByDescending(static hit => hit.Score) : hits.OrderBy(static hit => hit.Score))
            .ThenBy(static hit => hit.DocumentId, StringComparer.Ordinal).ToArray();
    }

    private static SndbFullTextHit BuildHit(
        DocumentFullTextSearchHit hit,
        SndbFullTextSearchRequest request,
        IReadOnlyDictionary<string, DocumentRow> rows)
    {
        string text = rows.TryGetValue(hit.DocumentId, out DocumentRow? row)
            ? (request.Field == "*" || request.Field.Equals("document", StringComparison.OrdinalIgnoreCase) || request.Field.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? row.Json : JsonPathEvaluator.TryEvaluate(row.Json, request.Field, out object? value) ? value?.ToString() ?? string.Empty : row.Json)
            : string.Empty;
        string[] terms = request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(32).ToArray();
        var offsets = new List<SndbFullTextMatchedOffset>();
        foreach (string term in terms)
        {
            int start = 0;
            while (start < text.Length)
            {
                int found = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                offsets.Add(new SndbFullTextMatchedOffset(request.Field, term, found, found + term.Length));
                start = found + term.Length;
                if (offsets.Count >= 64) break;
            }
            if (offsets.Count >= 64) break;
        }
        var highlights = new List<string>();
        if (request.Highlight is { } highlight)
        {
            int fragmentSize = Math.Clamp(highlight.FragmentSize, 32, 1_024);
            foreach (var match in offsets.Take(Math.Clamp(highlight.MaxFragments, 1, 8)))
            {
                int start = Math.Max(0, match.Start - fragmentSize / 3);
                highlights.Add(text.Substring(start, Math.Min(fragmentSize, text.Length - start)));
            }
        }
        return new SndbFullTextHit(hit.DocumentId, hit.Score,
            new SndbFullTextScoreMetadata("bm25", 1, terms),
            offsets.Select(static item => item.Term).Distinct(StringComparer.Ordinal).ToArray(), offsets, highlights);
    }

    private static IReadOnlyList<SndbFullTextFacetResult> BuildFacets(
        IReadOnlyList<SndbFullTextFacetRequest>? requests,
        IReadOnlyList<DocumentFullTextSearchHit> hits,
        IReadOnlyDictionary<string, DocumentRow> rows)
    {
        if (requests is not { Count: > 0 }) return [];
        var results = new List<SndbFullTextFacetResult>();
        foreach (var request in requests.Take(16))
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var hit in hits)
            {
                if (!rows.TryGetValue(hit.DocumentId, out var row) || !JsonPathEvaluator.TryEvaluate(row.Json, request.Field, out object? value)) continue;
                string? key = JsonPathEvaluator.ToIndexScalar(value);
                if (key is not null) counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
            }
            var buckets = counts.OrderByDescending(static pair => pair.Value).ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Take(Math.Clamp(request.Limit, 1, 100)).Select(static pair => new SndbFullTextFacetBucket(pair.Key, pair.Value)).ToArray();
            results.Add(new SndbFullTextFacetResult(request.Field, buckets));
        }
        return results;
    }

    private static DocumentFilter? ToCoreFilter(SndbDocumentFilter? filter)
    {
        if (filter is null) return null;
        if (filter.And is { Count: > 0 }) return new DocumentAndFilter(filter.And.Select(ToRequiredCoreFilter).ToArray());
        if (filter.Or is { Count: > 0 }) return new DocumentOrFilter(filter.Or.Select(ToRequiredCoreFilter).ToArray());
        if (filter.Not is not null) return new DocumentNotFilter(ToRequiredCoreFilter(filter.Not));
        DocumentFilterOperator op = (filter.Op ?? "eq").ToLowerInvariant() switch
        {
            "eq" => DocumentFilterOperator.Equal, "ne" => DocumentFilterOperator.NotEqual, "gt" => DocumentFilterOperator.GreaterThan,
            "gte" => DocumentFilterOperator.GreaterThanOrEqual, "lt" => DocumentFilterOperator.LessThan, "lte" => DocumentFilterOperator.LessThanOrEqual,
            "in" => DocumentFilterOperator.In, "nin" => DocumentFilterOperator.NotIn, "exists" => DocumentFilterOperator.Exists,
            "contains" => DocumentFilterOperator.Contains, "regex" => DocumentFilterOperator.Regex, "type" => DocumentFilterOperator.Type,
            "size" => DocumentFilterOperator.Size, "all" => DocumentFilterOperator.All,
            _ => throw new InvalidOperationException($"不支持的 document filter op '{filter.Op}'。"),
        };
        object? value = op switch
        {
            DocumentFilterOperator.Regex => new DocumentRegex(filter.Value?.GetString() ?? string.Empty, filter.RegexOptions),
            DocumentFilterOperator.Exists => filter.Value is { } valueElement && valueElement.ValueKind is JsonValueKind.False ? false : true,
            _ => ToCoreValue(filter.Value),
        };
        return new DocumentFieldFilter(ToCoreField(filter.Path), op, value);
    }

    private static DocumentFilter ToRequiredCoreFilter(SndbDocumentFilter filter) => ToCoreFilter(filter) ?? throw new InvalidOperationException("document filter 不能为空。");
    private static DocumentFieldRef ToCoreField(string? path)
        => string.IsNullOrWhiteSpace(path) || path.Equals("id", StringComparison.OrdinalIgnoreCase) || path.Equals("_id", StringComparison.OrdinalIgnoreCase)
            ? DocumentFieldRef.Id : path.Equals("document", StringComparison.OrdinalIgnoreCase) || path.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? DocumentFieldRef.Document : DocumentFieldRef.JsonPath(path);
    private static object? ToCoreValue(JsonElement? value) => value is null ? null : ToCoreValue(value.Value);
    private static object? ToCoreValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null, JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.TryGetInt64(out long number) ? number : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(ToCoreValue).ToArray(), JsonValueKind.Object => value.GetRawText(), _ => null,
    };

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SndbFullTextClient)); }
}
