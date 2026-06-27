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
        ThrowIfDisposed();
        ValidateCollection(collection);
        options ??= new SndbDocumentFindOptions();

        if (_embedded is not null)
        {
            var store = _embedded.Documents.Open(collection);
            IReadOnlyList<DocumentRow> rows;
            if (!string.IsNullOrWhiteSpace(options.Id))
            {
                var row = store.Get(options.Id);
                rows = row is null ? [] : [row];
            }
            else if (options.Ids is { Count: > 0 })
            {
                rows = store.GetMany(options.Ids);
            }
            else
            {
                rows = store.Scan(options.Limit ?? DefaultFindLimit, options.Skip);
            }

            return rows.Select(static row => new SndbDocument(row.Id, row.Json, row.Version)).ToArray();
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "find"),
            new DocumentFindRequest(options.Id, options.Ids, options.Limit, options.Skip),
            SndbDocumentClientJsonContext.Default.DocumentFindRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentFindResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Documents.Select(ToDocument).ToArray();
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
