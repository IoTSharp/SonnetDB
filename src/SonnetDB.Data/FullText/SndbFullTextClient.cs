using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SonnetDB.Data.Documents;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.FullText;
using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Cjk;
using SonnetDB.FullText.Tokenizers.Jieba;
using SonnetDB.FullText.Tokenizers.Unicode;

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

        using var budget = new FullTextSearchBudget(options.Budget, cancellationToken);
        ValidateRequestBudget(collection, index, field, query, options, budget);

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
            options.ContinuationToken,
            options.Budget);

        if (_embedded is not null)
            return SearchEmbedded(request, budget, cancellationToken);

        using var content = JsonContent.Create(
            request,
            SndbFullTextClientJsonContext.Default.SndbFullTextSearchRequest);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"v1/db/{Uri.EscapeDataString(_database)}/fulltext/search")
        { Content = content };
        try
        {
            using HttpResponseMessage response = await _http!.SendAsync(message,
                HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadErrorBodyBoundedAsync(response, budget).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity
                    && detail.Contains("fulltext_search_budget_exceeded", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FullTextSearchBudgetExceededException(detail);
                }
                throw new HttpRequestException(
                    $"全文检索失败（HTTP {(int)response.StatusCode} {response.StatusCode}）：{detail}",
                    inner: null,
                    response.StatusCode);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[16 * 1024];
            int maxReads = checked((int)budget.MaxResponseBytes + 1);
            bool complete = false;
            for (int readIndex = 0; readIndex < maxReads; readIndex++)
            {
                budget.Check();
                int read = await stream.ReadAsync(chunk, budget.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    complete = true;
                    break;
                }
                budget.ChargeWork(Math.Max(1, read / 64));
                budget.ChargeResponseBytes(read);
                buffer.Write(chunk, 0, read);
            }
            if (!complete)
                throw new FullTextSearchBudgetExceededException("全文响应超过有界读取预算。");
            var result = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)),
                SndbFullTextClientJsonContext.Default.SndbFullTextSearchResult)
                ?? throw new InvalidDataException("全文检索响应为空。");
            budget.Check();
            return result;
        }
        catch (OperationCanceledException)
        {
            budget.Check();
            throw;
        }
    }

    /// <summary>读取全文索引字段与分析器设置。</summary>
    public async Task<SndbFullTextIndexSettings> GetSettingsAsync(
        string collection,
        string index,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        cancellationToken.ThrowIfCancellationRequested();
        if (_embedded is not null)
            return MapSettings(_embedded.Documents.GetFullTextIndexSettings(collection, index));

        return await PostJsonAsync(
            "fulltext/settings",
            new SndbFullTextSettingsRequest(collection, index),
            SndbFullTextClientJsonContext.Default.SndbFullTextSettingsRequest,
            SndbFullTextClientJsonContext.Default.SndbFullTextIndexSettings,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>比较当前全文分析器与候选分词器的词元差异。</summary>
    public async Task<SndbFullTextAnalyzerDiff> AnalyzeDiffAsync(
        string collection,
        string index,
        string text,
        string? tokenizer = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (_embedded is not null)
        {
            var schema = _embedded.Documents.Catalog.TryGet(collection)
                ?? throw new InvalidOperationException($"文档集合 '{collection}' 不存在。");
            var definition = schema.TryGetFullTextIndex(index)
                ?? throw new InvalidOperationException($"全文索引 '{index}' 不存在。");
            var current = _embedded.Documents.AnalyzeFullText(collection, index, text);
            ITokenizer candidateTokenizer = CreateTokenizer(tokenizer ?? definition.Tokenizer);
            var sink = new CollectingTokenSink();
            candidateTokenizer.Tokenize(text.AsSpan(), sink);
            return BuildAnalyzerDiff(current, sink.Tokens);
        }

        return await PostJsonAsync(
            "fulltext/analyzer-diff",
            new SndbFullTextAnalyzerDiffRequest(collection, index, text, tokenizer),
            SndbFullTextClientJsonContext.Default.SndbFullTextAnalyzerDiffRequest,
            SndbFullTextClientJsonContext.Default.SndbFullTextAnalyzerDiff,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取指定文档对全文查询的相关性解释。</summary>
    public async Task<SndbFullTextRelevanceExplanation> ExplainRelevanceAsync(
        string collection,
        string index,
        string field,
        string query,
        string documentId,
        SndbFullTextSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        options ??= new SndbFullTextSearchOptions();
        cancellationToken.ThrowIfCancellationRequested();
        FullTextSearchMode mode = string.Equals(options.Mode, "fuzzy", StringComparison.OrdinalIgnoreCase)
            ? FullTextSearchMode.Fuzzy : FullTextSearchMode.Exact;
        FullTextQueryKind queryKind = options.QueryKind?.Trim().ToLowerInvariant() switch
        {
            "any" or "or" or "should" => FullTextQueryKind.Any,
            "phrase" => FullTextQueryKind.Phrase,
            _ => FullTextQueryKind.All,
        };
        if (_embedded is not null)
        {
            var value = _embedded.Documents.ExplainFullText(collection, index, field, query, documentId, mode, queryKind);
            return new SndbFullTextRelevanceExplanation(
                value.DocumentId,
                value.Score,
                value.Tokenizer,
                value.QueryTerms,
                value.Contributions.Select(static item => new SndbFullTextTermContribution(item.Term, item.Matched, item.ScoreContribution)).ToArray());
        }

        return await PostJsonAsync(
            "fulltext/relevance-explain",
            new SndbFullTextRelevanceExplainRequest(collection, index, field, query, documentId, options.Mode, options.QueryKind),
            SndbFullTextClientJsonContext.Default.SndbFullTextRelevanceExplainRequest,
            SndbFullTextClientJsonContext.Default.SndbFullTextRelevanceExplanation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同步重建全文索引并返回可观察任务状态。</summary>
    public async Task<SndbFullTextRebuildStatus> RebuildAsync(
        string collection,
        string index,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        cancellationToken.ThrowIfCancellationRequested();
        if (_embedded is not null)
        {
            _ = _embedded.Documents.RebuildFullTextIndex(collection, index);
            return MapRebuildStatus(_embedded.Documents.GetFullTextRebuildProgress(collection, index));
        }

        return await PostJsonAsync(
            "fulltext/rebuild",
            new SndbFullTextRebuildRequest(collection, index),
            SndbFullTextClientJsonContext.Default.SndbFullTextRebuildRequest,
            SndbFullTextClientJsonContext.Default.SndbFullTextRebuildStatus,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取全文索引最近一次重建任务状态。</summary>
    public async Task<SndbFullTextRebuildStatus> GetRebuildStatusAsync(
        string collection,
        string index,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        cancellationToken.ThrowIfCancellationRequested();
        if (_embedded is not null)
            return MapRebuildStatus(_embedded.Documents.GetFullTextRebuildProgress(collection, index));

        return await PostJsonAsync(
            "fulltext/rebuild/status",
            new SndbFullTextRebuildRequest(collection, index),
            SndbFullTextClientJsonContext.Default.SndbFullTextRebuildRequest,
            SndbFullTextClientJsonContext.Default.SndbFullTextRebuildStatus,
            cancellationToken).ConfigureAwait(false);
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

    private async Task<TResponse> PostJsonAsync<TRequest, TResponse>(
        string relativePath,
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(request, requestTypeInfo);
        using HttpResponseMessage response = await _http!.PostAsync(
            $"v1/db/{Uri.EscapeDataString(_database)}/{relativePath}",
            content,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"全文请求失败（HTTP {(int)response.StatusCode} {response.StatusCode}）：{detail}",
                inner: null,
                response.StatusCode);
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, responseTypeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("全文响应为空。");
    }

    private static async Task<string> ReadErrorBodyBoundedAsync(
        HttpResponseMessage response,
        FullTextSearchBudget budget)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int maxReads = checked((int)budget.MaxResponseBytes + 1);
        for (int readIndex = 0; readIndex < maxReads; readIndex++)
        {
            budget.Check();
            int read = await stream.ReadAsync(chunk, budget.Token).ConfigureAwait(false);
            if (read == 0)
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            budget.ChargeWork(Math.Max(1, read / 64));
            budget.ChargeResponseBytes(read);
            buffer.Write(chunk, 0, read);
        }
        throw new FullTextSearchBudgetExceededException("全文错误响应超过有界读取预算。");
    }

    private static void ValidateRequestBudget(
        string collection,
        string index,
        string field,
        string query,
        SndbFullTextSearchOptions options,
        FullTextSearchBudget budget)
    {
        if (query.Length > budget.MaxQueryLength)
            throw new FullTextSearchBudgetExceededException($"全文查询超过 {budget.MaxQueryLength} 字符预算。");
        budget.ChargeInputString(collection);
        budget.ChargeInputString(index);
        budget.ChargeInputString(field);
        budget.ChargeInputString(query);
        if (options.Sort is { Count: > 100_000 } || options.Facets is { Count: > 100_000 })
            throw new FullTextSearchBudgetExceededException("全文排序或 facet 请求数量超过预算。");
        long shapeUnits = (options.Sort?.Count ?? 0) + (options.Facets?.Count ?? 0);
        if (shapeUnits > 0)
            budget.ChargeWork(shapeUnits);
        if (options.Sort is { } sorts)
            foreach (SndbDocumentSort sort in sorts)
                budget.ChargeInputString(sort.Path);
        if (options.Facets is { } facets)
            foreach (SndbFullTextFacetRequest facet in facets)
                budget.ChargeInputString(facet.Field);
        if (options.ContinuationToken is { } continuation)
            budget.ChargeInputString(continuation);
        var pending = new Stack<(SndbDocumentFilter Filter, int Depth)>();
        if (options.Filter is { } filter)
            pending.Push((filter, 0));
        for (int visited = 0; pending.Count > 0 && visited < 100_000; visited++)
        {
            budget.ChargeWork();
            var (current, depth) = pending.Pop();
            if (depth > 64)
                throw new FullTextSearchBudgetExceededException("全文过滤结构超过 64 层预算。");
            budget.ChargeInputString(current.Path);
            budget.ChargeInputString(current.Op);
            budget.ChargeInputString(current.RegexOptions);
            if (current.Value is { } value)
                budget.ChargeInputString(value.GetRawText());
            if (current.Not is { } not)
                pending.Push((not, depth + 1));
            if (current.ElemMatch is { } element)
                pending.Push((element, depth + 1));
            foreach (IReadOnlyList<SndbDocumentFilter>? children in new[] { current.And, current.Or })
            {
                if (children is null)
                    continue;
                if (children.Count > 100_000 - visited - pending.Count)
                    throw new FullTextSearchBudgetExceededException("全文过滤节点超过 100000 个预算。");
                foreach (SndbDocumentFilter child in children)
                    pending.Push((child, depth + 1));
            }
        }
        if (pending.Count != 0)
            throw new FullTextSearchBudgetExceededException("全文过滤节点超过 100000 个预算。");
    }

    private static SndbFullTextIndexSettings MapSettings(DocumentFullTextIndexSettings settings)
        => new(
            settings.SearchableFields ?? [],
            settings.FilterableFields ?? [],
            settings.SortableFields ?? [],
            settings.Synonyms ?? new Dictionary<string, string>(StringComparer.Ordinal),
            settings.StopWords ?? [],
            MapTypoPolicy(settings.TypoPolicy ?? DocumentFullTextTypoPolicy.Default));

    private static SndbFullTextTypoPolicy MapTypoPolicy(DocumentFullTextTypoPolicy policy)
        => new(policy.Enabled, policy.ShortTokenMaxEdits, policy.MediumTokenMaxEdits, policy.LongTokenMaxEdits);

    private static SndbFullTextRebuildStatus MapRebuildStatus(DocumentFullTextRebuildProgress progress)
        => new(progress.State, progress.ProcessedDocuments, progress.TotalDocuments, progress.Error, progress.StartedUtc, progress.CompletedUtc);

    private static SndbFullTextAnalyzerDiff BuildAnalyzerDiff(
        IReadOnlyList<Token> current,
        IReadOnlyList<Token> candidate)
    {
        var currentTokens = current.Select(static token => new SndbFullTextAnalyzerToken(token.Text, token.StartOffset, token.EndOffset, token.PositionIncrement)).ToArray();
        var candidateTokens = candidate.Select(static token => new SndbFullTextAnalyzerToken(token.Text, token.StartOffset, token.EndOffset, token.PositionIncrement)).ToArray();
        var currentTerms = currentTokens.Select(static token => token.Text).ToHashSet(StringComparer.Ordinal);
        var candidateTerms = candidateTokens.Select(static token => token.Text).ToHashSet(StringComparer.Ordinal);
        return new SndbFullTextAnalyzerDiff(
            currentTokens,
            candidateTokens,
            candidateTerms.Except(currentTerms, StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray(),
            currentTerms.Except(candidateTerms, StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal).ToArray());
    }

    private static ITokenizer CreateTokenizer(string name)
        => name.Trim().ToLowerInvariant() switch
        {
            "unicode" => new UnicodeTokenizer(),
            "cjk" or "cjk_bigram" or "bigram" => new CjkBigramTokenizer(),
            "jieba" or "chinese" => new ChineseTokenizer(),
            _ => throw new ArgumentException($"未知全文分词器 '{name}'。", nameof(name)),
        };

    private SndbFullTextSearchResult SearchEmbedded(
        SndbFullTextSearchRequest request,
        FullTextSearchBudget budget,
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
        var store = _embedded.Documents.Open(request.Collection);
        DocumentFilter? filter = ToCoreFilter(request.Filter);
        DocumentFullTextCompleteSearchResult complete = store.SearchFullTextComplete(
            budget, definition, request.Field, request.Query, filter, mode, queryKind, cancellationToken);
        IReadOnlyDictionary<string, DocumentRow> rows = complete.Rows;
        var ordered = ApplySort(complete.Hits, request.Sort, budget);
        var page = ordered.Skip(offset).Take(pageSize).ToArray();
        bool hasMore = ordered.Count > offset + page.Length;
        var hits = page.Select(hit => BuildHit(hit, request, rows, budget)).ToArray();
        var facets = BuildFacets(request.Facets, ordered, rows, budget);
        var result = new SndbFullTextSearchResult(
            request.Collection,
            hits,
            facets,
            hasMore ? (offset + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            hasMore,
            pageSize,
            ordered.Count);
        budget.CheckOutputString(result.Collection);
        if (result.NextContinuationToken is { } nextToken)
            budget.CheckOutputString(nextToken);
        _ = budget.Serialize(result, SndbFullTextClientJsonContext.Default.SndbFullTextSearchResult);
        return result;
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
        IReadOnlyList<SndbDocumentSort>? sort,
        FullTextSearchBudget budget)
    {
        string path = sort is { Count: > 0 } ? sort[0].Path : "score";
        bool descending = sort is not { Count: > 0 } || sort[0].Descending;
        if (string.Equals(path, "id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "_id", StringComparison.OrdinalIgnoreCase))
            return budget.Sort(hits, (left, right) =>
            {
                int byId = descending ? string.CompareOrdinal(right.DocumentId, left.DocumentId) : string.CompareOrdinal(left.DocumentId, right.DocumentId);
                return byId != 0 ? byId : right.Score.CompareTo(left.Score);
            });
        return budget.Sort(hits, (left, right) =>
        {
            int byScore = descending ? right.Score.CompareTo(left.Score) : left.Score.CompareTo(right.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.DocumentId, right.DocumentId);
        });
    }

    private static SndbFullTextHit BuildHit(
        DocumentFullTextSearchHit hit,
        SndbFullTextSearchRequest request,
        IReadOnlyDictionary<string, DocumentRow> rows,
        FullTextSearchBudget budget)
    {
        budget.ChargeWork();
        budget.CheckOutputString(hit.DocumentId);
        string text = rows.TryGetValue(hit.DocumentId, out DocumentRow? row)
            ? (request.Field == "*" || request.Field.Equals("document", StringComparison.OrdinalIgnoreCase) || request.Field.Equals("json", StringComparison.OrdinalIgnoreCase)
                ? row.Json : JsonPathEvaluator.TryEvaluate(row.Json, request.Field, out object? value) ? value?.ToString() ?? string.Empty : row.Json)
            : string.Empty;
        budget.ChargeWork(Math.Max(1, text.Length / 64));
        string[] terms = request.Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(32).ToArray();
        budget.CheckOutputString("bm25");
        foreach (string term in terms)
            budget.CheckOutputString(term);
        var offsets = new List<SndbFullTextMatchedOffset>();
        foreach (string term in terms)
        {
            int start = 0;
            while (start < text.Length)
            {
                budget.ChargeWork(Math.Max(1, (text.Length - start) / 64));
                int found = text.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                budget.CheckOutputString(request.Field);
                budget.CheckOutputString(term);
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
                budget.ChargeWork();
                int start = Math.Max(0, match.Start - fragmentSize / 3);
                string fragment = text.Substring(start, Math.Min(fragmentSize, text.Length - start));
                budget.CheckOutputString(fragment);
                highlights.Add(fragment);
            }
        }
        string[] matchedTerms = offsets.Select(static item => item.Term).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string term in matchedTerms)
            budget.CheckOutputString(term);
        return new SndbFullTextHit(hit.DocumentId, hit.Score,
            new SndbFullTextScoreMetadata("bm25", 1, terms),
            matchedTerms, offsets, highlights);
    }

    internal static IReadOnlyList<SndbFullTextFacetResult> BuildFacets(
        IReadOnlyList<SndbFullTextFacetRequest>? requests,
        IReadOnlyList<DocumentFullTextSearchHit> hits,
        IReadOnlyDictionary<string, DocumentRow> rows,
        FullTextSearchBudget budget)
    {
        if (requests is not { Count: > 0 }) return [];
        var results = new List<SndbFullTextFacetResult>();
        foreach (var request in requests.Take(16))
        {
            budget.ChargeWork();
            if (string.IsNullOrWhiteSpace(request.Field))
                continue;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var hit in hits)
            {
                budget.ChargeWork();
                if (!rows.TryGetValue(hit.DocumentId, out var row) || !JsonPathEvaluator.TryEvaluate(row.Json, request.Field, out object? value)) continue;
                budget.ChargeWork(Math.Max(1, row.Json.Length / 64));
                string? key = JsonPathEvaluator.ToIndexScalar(value);
                if (key is not null) counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
            }
            var buckets = budget.Sort(counts.ToArray(), static (left, right) =>
                {
                    int byCount = right.Value.CompareTo(left.Value);
                    return byCount != 0 ? byCount : string.CompareOrdinal(left.Key, right.Key);
                })
                .Take(Math.Clamp(request.Limit, 1, 100)).Select(static pair => new SndbFullTextFacetBucket(pair.Key, pair.Value)).ToArray();
            budget.CheckOutputString(request.Field);
            foreach (SndbFullTextFacetBucket bucket in buckets)
                budget.CheckOutputString(bucket.Value);
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
            "eq" => DocumentFilterOperator.Equal,
            "ne" => DocumentFilterOperator.NotEqual,
            "gt" => DocumentFilterOperator.GreaterThan,
            "gte" => DocumentFilterOperator.GreaterThanOrEqual,
            "lt" => DocumentFilterOperator.LessThan,
            "lte" => DocumentFilterOperator.LessThanOrEqual,
            "in" => DocumentFilterOperator.In,
            "nin" => DocumentFilterOperator.NotIn,
            "exists" => DocumentFilterOperator.Exists,
            "contains" => DocumentFilterOperator.Contains,
            "regex" => DocumentFilterOperator.Regex,
            "type" => DocumentFilterOperator.Type,
            "size" => DocumentFilterOperator.Size,
            "all" => DocumentFilterOperator.All,
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
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out long number) ? number : value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(ToCoreValue).ToArray(),
        JsonValueKind.Object => value.GetRawText(),
        _ => null,
    };

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SndbFullTextClient)); }
}
