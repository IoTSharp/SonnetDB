using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SonnetDB.Data.Remote;
using SonnetDB.Documents;
using SonnetDB.Engine;

namespace SonnetDB.Data.Documents;

/// <summary>
/// SonnetDB Document Store 客户端，统一支持嵌入式与远程 SonnetDB。
/// </summary>
public sealed class SndbDocumentClient : IDisposable
{
    private const int DefaultFindLimit = 100;
    private static readonly TimeSpan CursorTtl = TimeSpan.FromMinutes(15);
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>
    /// 使用 SonnetDB 连接字符串创建文档客户端。
    /// </summary>
    /// <param name="connectionString">SonnetDB 连接字符串。</param>
    public SndbDocumentClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        Open();
    }

    /// <summary>当前连接模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>远程数据库名或嵌入式数据库目录。</summary>
    public string Database => _database;

    /// <summary>
    /// 创建文档集合。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="ifNotExists">集合已存在时是否返回 <c>exists</c> 而不是报错。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>集合操作状态，例如 <c>created</c> 或 <c>exists</c>。</returns>
    public async Task<string> CreateCollectionAsync(
        string collection,
        bool ifNotExists = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);

        if (_embedded is not null)
        {
            if (_embedded.Documents.Catalog.TryGet(collection) is not null)
            {
                if (!ifNotExists)
                    throw new InvalidOperationException($"document collection '{collection}' 已存在。");
                return "exists";
            }

            _embedded.Documents.Create(DocumentCollectionSchema.Create(collection));
            return "created";
        }

        using var response = await PostJsonAsync(
            CollectionUrl(collection),
            new DocumentCollectionCreateRequest(ifNotExists),
            SndbDocumentClientJsonContext.Default.DocumentCollectionCreateRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentCollectionOperationResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Status;
    }

    /// <summary>
    /// 删除文档集合。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>集合存在并被删除时返回 true；集合不存在时返回 false。</returns>
    public async Task<bool> DropCollectionAsync(string collection, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);

        if (_embedded is not null)
            return _embedded.Documents.Drop(collection);

        using var request = new HttpRequestMessage(HttpMethod.Delete, CollectionUrl(collection));
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentCollectionOperationResponse, cancellationToken)
            .ConfigureAwait(false);
        return string.Equals(body.Status, "dropped", StringComparison.Ordinal);
    }

    /// <summary>
    /// 写入或覆盖单条文档。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">JSON 文档文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档写入结果。</returns>
    public async Task<SndbDocumentWriteResult> InsertOneAsync(
        string collection,
        string id,
        string json,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ValidateId(id);

        if (_embedded is not null)
        {
            _embedded.Documents.Open(collection).Upsert(id, json);
            return new SndbDocumentWriteResult(collection, Inserted: 1, Matched: 0, Modified: 0, Deleted: 0);
        }

        using var document = JsonDocument.Parse(json);
        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "insert-one"),
            new DocumentWriteItem(id, document.RootElement.Clone()),
            SndbDocumentClientJsonContext.Default.DocumentWriteItem,
            cancellationToken).ConfigureAwait(false);
        return ToWriteResult(await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 批量写入或覆盖文档。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="documents">文档 ID 与 JSON 文档文本列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档写入结果。</returns>
    public async Task<SndbDocumentWriteResult> InsertManyAsync(
        string collection,
        IEnumerable<KeyValuePair<string, string>> documents,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(documents);

        var items = MaterializeWriteItems(documents);
        if (_embedded is not null)
        {
            var store = _embedded.Documents.Open(collection);
            foreach (var item in items)
                store.Upsert(item.Id, item.Json);
            return new SndbDocumentWriteResult(collection, items.Count, 0, 0, 0);
        }

        using var payload = BuildInsertManyRequest(items);
        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "insert-many"),
            payload.Request,
            SndbDocumentClientJsonContext.Default.DocumentInsertManyRequest,
            cancellationToken).ConfigureAwait(false);
        return ToWriteResult(await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 查询文档。第一版支持按 ID / ID 列表或集合顺序扫描。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="options">查询选项；为空时按集合顺序扫描默认数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中的文档列表。</returns>
    public async Task<IReadOnlyList<SndbDocument>> FindAsync(
        string collection,
        SndbDocumentFindOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var page = await FindPageAsync(collection, options, cancellationToken).ConfigureAwait(false);
        return page.Documents;
    }

    /// <summary>
    /// 分页查询文档，并返回 continuation token。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="options">查询选项；ContinuationToken 不为空时继续读取下一页。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>当前页文档与下一页 token。</returns>
    public async Task<SndbDocumentPage> FindPageAsync(
        string collection,
        SndbDocumentFindOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        options ??= new SndbDocumentFindOptions();
        int limit = NormalizeFindLimit(options.Limit);

        if (_embedded is not null)
        {
            var store = _embedded.Documents.Open(collection);
            return FindEmbeddedPage(collection, store, options, limit);
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "find"),
            new DocumentFindRequest(
                options.Id,
                options.Ids,
                options.Limit,
                options.Skip,
                options.Filter,
                options.Projection,
                options.Sort,
                options.ContinuationToken),
            SndbDocumentClientJsonContext.Default.DocumentFindRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentFindResponse, cancellationToken)
            .ConfigureAwait(false);
        return ToPage(body, limit);
    }

    /// <summary>
    /// 按 ID 查询单条文档。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="id">文档 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到时返回文档；否则返回 null。</returns>
    public async Task<SndbDocument?> FindOneAsync(
        string collection,
        string id,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ValidateId(id);

        if (_embedded is not null)
        {
            var row = _embedded.Documents.Open(collection).Get(id);
            return row is null ? null : new SndbDocument(row.Id, row.Json, row.Version);
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "find-one"),
            new DocumentFindRequest(Id: id),
            SndbDocumentClientJsonContext.Default.DocumentFindRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentFindOneResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Found && body.Document is not null ? ToDocument(body.Document) : null;
    }

    /// <summary>
    /// 整体替换一条已存在文档；文档不存在时不执行 upsert。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">新的 JSON 文档文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档替换结果。</returns>
    public async Task<SndbDocumentWriteResult> UpdateOneAsync(
        string collection,
        string id,
        string json,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ValidateId(id);

        if (_embedded is not null)
        {
            var store = _embedded.Documents.Open(collection);
            if (store.Get(id) is null)
                return new SndbDocumentWriteResult(collection, 0, 0, 0, 0);
            store.Upsert(id, json);
            return new SndbDocumentWriteResult(collection, 0, 1, 1, 0);
        }

        using var document = JsonDocument.Parse(json);
        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "update-one"),
            new DocumentUpdateOneRequest(id, document.RootElement.Clone()),
            SndbDocumentClientJsonContext.Default.DocumentUpdateOneRequest,
            cancellationToken).ConfigureAwait(false);
        return ToWriteResult(await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 整体替换多条已存在文档；不存在的 ID 会被跳过。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="documents">文档 ID 与新的 JSON 文档文本列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档替换结果。</returns>
    public async Task<SndbDocumentWriteResult> UpdateManyAsync(
        string collection,
        IEnumerable<KeyValuePair<string, string>> documents,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(documents);

        var items = MaterializeWriteItems(documents);
        if (_embedded is not null)
        {
            var store = _embedded.Documents.Open(collection);
            int modified = 0;
            foreach (var item in items)
            {
                if (store.Get(item.Id) is null)
                    continue;
                store.Upsert(item.Id, item.Json);
                modified++;
            }

            return new SndbDocumentWriteResult(collection, 0, modified, modified, 0);
        }

        using var payload = BuildUpdateManyRequest(items);
        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "update-many"),
            payload.Request,
            SndbDocumentClientJsonContext.Default.DocumentUpdateManyRequest,
            cancellationToken).ConfigureAwait(false);
        return ToWriteResult(await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 删除单条文档。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="id">文档 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档删除结果。</returns>
    public async Task<SndbDocumentWriteResult> DeleteOneAsync(
        string collection,
        string id,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ValidateId(id);

        if (_embedded is not null)
        {
            int deleted = _embedded.Documents.Open(collection).Delete(id) ? 1 : 0;
            return new SndbDocumentWriteResult(collection, 0, 0, 0, deleted);
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "delete-one"),
            new DocumentDeleteOneRequest(id),
            SndbDocumentClientJsonContext.Default.DocumentDeleteOneRequest,
            cancellationToken).ConfigureAwait(false);
        return ToWriteResult(await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 批量删除文档。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="ids">要删除的文档 ID 列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档删除结果。</returns>
    public async Task<SndbDocumentWriteResult> DeleteManyAsync(
        string collection,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.ToArray();

        if (_embedded is not null)
        {
            int deleted = _embedded.Documents.Open(collection).DeleteMany(idList);
            return new SndbDocumentWriteResult(collection, 0, 0, 0, deleted);
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "delete-many"),
            new DocumentDeleteManyRequest(idList),
            SndbDocumentClientJsonContext.Default.DocumentDeleteManyRequest,
            cancellationToken).ConfigureAwait(false);
        return ToWriteResult(await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 统计文档数量。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="ids">可选文档 ID 列表；为空时统计整个集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档数量。</returns>
    public async Task<long> CountAsync(
        string collection,
        IReadOnlyList<string>? ids = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);

        if (_embedded is not null)
        {
            var store = _embedded.Documents.Open(collection);
            return ids is null ? store.Count() : store.GetMany(ids).Count;
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "count"),
            new DocumentCountRequest(ids),
            SndbDocumentClientJsonContext.Default.DocumentCountRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentCountResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Count;
    }

    /// <summary>
    /// 读取指定 JSON path 上的 distinct 标量值。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="path">JSON path 表达式。</param>
    /// <param name="ids">可选文档 ID 列表；为空时扫描整个集合。</param>
    /// <param name="limit">最多返回的 distinct 值数量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>distinct 查询结果。</returns>
    public async Task<SndbDocumentDistinctResult> DistinctAsync(
        string collection,
        string path,
        IReadOnlyList<string>? ids = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (_embedded is not null)
        {
            var values = _embedded.Documents.Open(collection).Distinct(path, limit, ids);
            return new SndbDocumentDistinctResult(collection, path, values);
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "distinct"),
            new DocumentDistinctRequest(path, ids, limit),
            SndbDocumentClientJsonContext.Default.DocumentDistinctRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentDistinctResponse, cancellationToken)
            .ConfigureAwait(false);
        return new SndbDocumentDistinctResult(collection, body.Path, body.Values.Select(ToObject).ToArray());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http?.Dispose();
        _embedded?.Dispose();
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            if (string.IsNullOrWhiteSpace(_builder.DataSource))
                throw new InvalidOperationException("文档客户端缺少 Data Source。");

            _database = _builder.DataSource;
            _embedded = Tsdb.Open(new TsdbOptions { RootDirectory = _builder.DataSource });
            return;
        }

        var (baseUrl, dbFromUrl) = ParseRemoteEndpoint(_builder.DataSource);
        _database = !string.IsNullOrWhiteSpace(_builder.Database) ? _builder.Database! : dbFromUrl;
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程文档客户端缺少数据库名。");

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(_builder.Timeout),
        };
        if (!string.IsNullOrWhiteSpace(_builder.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _builder.Token);
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(value, typeInfo);
        var response = await _http!.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SonnetDB document response body is empty.");
    }

    private static async Task<SndbServerException> BuildHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var error = await JsonSerializer.DeserializeAsync(stream, RemoteJsonContext.Default.ServerErrorBody, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
                return new SndbServerException(error.Error, error.Message, response.StatusCode);
        }
        catch
        {
        }

        return new SndbServerException("http_error", response.ReasonPhrase ?? "SonnetDB HTTP error.", response.StatusCode);
    }

    private string CollectionUrl(string collection) =>
        $"v1/db/{Uri.EscapeDataString(_database)}/documents/{Uri.EscapeDataString(collection)}";

    private string CollectionActionUrl(string collection, string action) => CollectionUrl(collection) + "/" + action;

    private static List<WriteItem> MaterializeWriteItems(IEnumerable<KeyValuePair<string, string>> documents)
    {
        var result = new List<WriteItem>();
        foreach (var pair in documents)
        {
            ValidateId(pair.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(pair.Value);
            result.Add(new WriteItem(pair.Key, JsonPathEvaluator.NormalizeJson(pair.Value)));
        }

        return result;
    }

    private static InsertManyPayload BuildInsertManyRequest(IReadOnlyList<WriteItem> items)
    {
        var documents = new JsonDocument[items.Count];
        var requestItems = new DocumentWriteItem[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            documents[i] = JsonDocument.Parse(items[i].Json);
            requestItems[i] = new DocumentWriteItem(items[i].Id, documents[i].RootElement.Clone());
        }

        return new InsertManyPayload(new DocumentInsertManyRequest(requestItems), documents);
    }

    private static UpdateManyPayload BuildUpdateManyRequest(IReadOnlyList<WriteItem> items)
    {
        var documents = new JsonDocument[items.Count];
        var requestItems = new DocumentWriteItem[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            documents[i] = JsonDocument.Parse(items[i].Json);
            requestItems[i] = new DocumentWriteItem(items[i].Id, documents[i].RootElement.Clone());
        }

        return new UpdateManyPayload(new DocumentUpdateManyRequest(requestItems), documents);
    }

    private static SndbDocumentWriteResult ToWriteResult(DocumentWriteResponse response) =>
        new(response.Collection, response.Inserted, response.Matched, response.Modified, response.Deleted);

    private static SndbDocument ToDocument(DocumentItemResponse response) =>
        new(response.Id, response.Document.GetRawText(), response.Version);

    private static SndbDocumentPage ToPage(DocumentFindResponse response, int requestedLimit) =>
        new(
            response.Collection,
            response.Documents.Select(ToDocument).ToArray(),
            response.ContinuationToken,
            response.HasMore,
            response.BatchSize ?? response.Limit ?? requestedLimit,
            response.SnapshotVersion,
            response.CursorExpiresAtUtc);

    private static SndbDocumentPage FindEmbeddedPage(
        string collection,
        DocumentCollectionStore store,
        SndbDocumentFindOptions options,
        int limit)
    {
        var query = new DocumentQuery(
            Filter: MergeClientFilters(options),
            Projection: ToCoreProjection(options.Projection),
            Sort: ToCoreSort(options.Sort),
            Limit: limit,
            Skip: options.Skip);
        string fingerprint = DocumentCursorToken.Fingerprint(collection, query);
        DocumentCursorState? cursor = DecodeCursor(options.ContinuationToken, collection, fingerprint, store.LastVersion);

        if (!HasAdvancedQuery(options) && !string.IsNullOrWhiteSpace(options.Id))
        {
            var row = store.Get(options.Id);
            var idRows = row is null ? Array.Empty<DocumentRow>() : new[] { row };
            int effectiveSkip = cursor?.Offset ?? options.Skip;
            var idPageRows = idRows.Skip(effectiveSkip).Take(limit + 1).ToArray();
            return BuildEmbeddedPage(
                collection,
                fingerprint,
                store.LastVersion,
                limit,
                idPageRows.Take(limit).Select(static item => new SndbDocument(item.Id, item.Json, item.Version)).ToArray(),
                idPageRows.Length > limit,
                checked(effectiveSkip + Math.Min(idPageRows.Length, limit)),
                nextLastId: null);
        }

        if (!HasAdvancedQuery(options) && options.Ids is { Count: > 0 })
        {
            int effectiveSkip = cursor?.Offset ?? options.Skip;
            var idsPageRows = store.GetMany(options.Ids).Skip(effectiveSkip).Take(limit + 1).ToArray();
            return BuildEmbeddedPage(
                collection,
                fingerprint,
                store.LastVersion,
                limit,
                idsPageRows.Take(limit).Select(static item => new SndbDocument(item.Id, item.Json, item.Version)).ToArray(),
                idsPageRows.Length > limit,
                checked(effectiveSkip + Math.Min(idsPageRows.Length, limit)),
                nextLastId: null);
        }

        if (HasAdvancedQuery(options) || !string.IsNullOrWhiteSpace(options.Id) || options.Ids is { Count: > 0 })
        {
            int effectiveSkip = cursor?.Offset ?? options.Skip;
            var result = DocumentQueryPlanner.Execute(
                store,
                store.Schema,
                query with { Limit = limit + 1, Skip = effectiveSkip });
            var pageItems = result.Items.Take(limit).ToArray();
            bool hasMore = result.Items.Count > limit;
            return BuildEmbeddedPage(
                collection,
                fingerprint,
                store.LastVersion,
                limit,
                pageItems.Select(static item => new SndbDocument(item.Id, item.Json, item.Version)).ToArray(),
                hasMore,
                checked(effectiveSkip + pageItems.Length),
                nextLastId: null);
        }

        IReadOnlyList<DocumentRow> scanRows = cursor is null
            ? store.Scan(limit + 1, options.Skip)
            : store.ScanAfter(cursor.LastId, limit + 1);
        var scanPageRows = scanRows.Take(limit).Select(static row => new SndbDocument(row.Id, row.Json, row.Version)).ToArray();
        return BuildEmbeddedPage(
            collection,
            fingerprint,
            store.LastVersion,
            limit,
            scanPageRows,
            scanRows.Count > limit,
            checked((cursor?.Offset ?? options.Skip) + scanPageRows.Length),
            scanPageRows.Length == 0 ? cursor?.LastId : scanPageRows[^1].Id);
    }

    private static DocumentCursorState? DecodeCursor(
        string? token,
        string collection,
        string fingerprint,
        long currentVersion)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var state = DocumentCursorToken.Decode(token);
        if (!string.Equals(state.Collection, collection, StringComparison.Ordinal)
            || !string.Equals(state.QueryFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("document cursor token does not match this find request.");
        }

        if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("document cursor token has expired.");
        if (state.SnapshotVersion != currentVersion)
            throw new InvalidOperationException("document cursor snapshot is stale; restart the find request.");

        return state;
    }

    private static SndbDocumentPage BuildEmbeddedPage(
        string collection,
        string fingerprint,
        long snapshotVersion,
        int limit,
        IReadOnlyList<SndbDocument> documents,
        bool hasMore,
        int nextOffset,
        string? nextLastId)
    {
        DateTimeOffset? expiresAt = hasMore ? DateTimeOffset.UtcNow.Add(CursorTtl) : null;
        string? token = hasMore
            ? DocumentCursorToken.Encode(new DocumentCursorState(
                collection,
                fingerprint,
                snapshotVersion,
                expiresAt!.Value,
                nextOffset,
                nextLastId))
            : null;

        return new SndbDocumentPage(collection, documents, token, hasMore, limit, snapshotVersion, expiresAt);
    }

    private static int NormalizeFindLimit(int? limit)
    {
        if (limit is null)
            return DefaultFindLimit;
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than 0.");
        return limit.Value;
    }

    private static bool HasAdvancedQuery(SndbDocumentFindOptions options)
        => options.Filter is not null
            || options.Projection is { Count: > 0 }
            || options.Sort is { Count: > 0 };

    private static DocumentFilter? MergeClientFilters(SndbDocumentFindOptions options)
    {
        var filters = new List<DocumentFilter>();
        if (!string.IsNullOrWhiteSpace(options.Id))
            filters.Add(new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, options.Id));
        if (options.Ids is { Count: > 0 })
            filters.Add(new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.In, options.Ids));
        if (ToCoreFilter(options.Filter) is { } filter)
            filters.Add(filter);

        return filters.Count switch
        {
            0 => null,
            1 => filters[0],
            _ => new DocumentAndFilter(filters),
        };
    }

    private static DocumentFilter? ToCoreFilter(SndbDocumentFilter? filter)
    {
        if (filter is null)
            return null;

        if (filter.And is { Count: > 0 })
            return new DocumentAndFilter(filter.And.Select(ToRequiredCoreFilter).ToArray());
        if (filter.Or is { Count: > 0 })
            return new DocumentOrFilter(filter.Or.Select(ToRequiredCoreFilter).ToArray());
        if (filter.Not is not null)
            return new DocumentNotFilter(ToRequiredCoreFilter(filter.Not));

        var op = ParseFilterOperator(filter.Op);
        return new DocumentFieldFilter(
            ToCoreField(filter.Path),
            op,
            op == DocumentFilterOperator.Exists
                ? ToBooleanOrDefault(filter.Value)
                : ToCoreValue(filter.Value));
    }

    private static DocumentFilter ToRequiredCoreFilter(SndbDocumentFilter filter)
        => ToCoreFilter(filter) ?? throw new InvalidOperationException("文档过滤表达式不能为空。");

    private static DocumentProjection? ToCoreProjection(IReadOnlyList<SndbDocumentProjection>? projection)
    {
        if (projection is not { Count: > 0 })
            return null;

        return new DocumentProjection(projection
            .Select(static item =>
            {
                var field = ToCoreField(item.Path);
                return new DocumentProjectionField(item.Name ?? DefaultProjectionName(field), field);
            })
            .ToArray());
    }

    private static IReadOnlyList<DocumentSort> ToCoreSort(IReadOnlyList<SndbDocumentSort>? sort)
        => sort is { Count: > 0 }
            ? sort.Select(static item => new DocumentSort(ToCoreField(item.Path), item.Descending)).ToArray()
            : Array.Empty<DocumentSort>();

    private static DocumentFieldRef ToCoreField(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, "_id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "id", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFieldRef.Id;
        }

        if (string.Equals(path, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "json", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFieldRef.Document;
        }

        return DocumentFieldRef.JsonPath(path);
    }

    private static string DefaultProjectionName(DocumentFieldRef field)
        => field.Kind switch
        {
            DocumentFieldKind.Id => "_id",
            DocumentFieldKind.Document => "document",
            DocumentFieldKind.JsonPath => field.Path!.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1]
                .TrimEnd(']')
                .Split('[', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[^1]
                .Trim('\''),
            _ => "value",
        };

    private static DocumentFilterOperator ParseFilterOperator(string? op)
        => (op ?? "eq").ToLowerInvariant() switch
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
            _ => throw new InvalidOperationException($"不支持的文档过滤操作符 '{op}'。"),
        };

    private static object? ToCoreValue(JsonElement? value)
    {
        if (value is null)
            return null;

        return ToCoreValue(value.Value);
    }

    private static object? ToCoreValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out long longValue) ? longValue : value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ToCoreValue).ToArray(),
            JsonValueKind.Object => value.GetRawText(),
            _ => null,
        };

    private static bool ToBooleanOrDefault(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.Value.ValueKind == JsonValueKind.True || value.Value.ValueKind == JsonValueKind.False)
            return value.Value.GetBoolean();
        return false;
    }

    private static object? ToObject(JsonElementValue value)
        => value.Kind switch
        {
            ScalarKind.Null => null,
            ScalarKind.Boolean => value.BooleanValue,
            ScalarKind.Integer => value.IntegerValue,
            ScalarKind.Double => value.DoubleValue,
            ScalarKind.String => value.StringValue,
            _ => null,
        };

    private static (string BaseUrl, string Database) ParseRemoteEndpoint(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("远程文档客户端缺少 Data Source。");

        var ds = dataSource.Trim();
        if (ds.StartsWith("sonnetdb+http://", StringComparison.OrdinalIgnoreCase))
            ds = "http://" + ds["sonnetdb+http://".Length..];
        else if (ds.StartsWith("sonnetdb+https://", StringComparison.OrdinalIgnoreCase))
            ds = "https://" + ds["sonnetdb+https://".Length..];

        if (!Uri.TryCreate(ds, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"远程 Data Source 不是合法 URL: {dataSource}");

        return ($"{uri.Scheme}://{uri.Authority}/", uri.AbsolutePath.Trim('/'));
    }

    private static void ValidateCollection(string collection)
        => ArgumentException.ThrowIfNullOrWhiteSpace(collection);

    private static void ValidateId(string id)
        => ArgumentException.ThrowIfNullOrWhiteSpace(id);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record WriteItem(string Id, string Json);

    private sealed class InsertManyPayload : IDisposable
    {
        private readonly IReadOnlyList<JsonDocument> _documents;

        public InsertManyPayload(DocumentInsertManyRequest request, IReadOnlyList<JsonDocument> documents)
        {
            Request = request;
            _documents = documents;
        }

        public DocumentInsertManyRequest Request { get; }

        public void Dispose()
        {
            foreach (var document in _documents)
                document.Dispose();
        }
    }

    private sealed class UpdateManyPayload : IDisposable
    {
        private readonly IReadOnlyList<JsonDocument> _documents;

        public UpdateManyPayload(DocumentUpdateManyRequest request, IReadOnlyList<JsonDocument> documents)
        {
            Request = request;
            _documents = documents;
        }

        public DocumentUpdateManyRequest Request { get; }

        public void Dispose()
        {
            foreach (var document in _documents)
                document.Dispose();
        }
    }
}
