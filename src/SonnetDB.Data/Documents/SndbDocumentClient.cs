using System.Buffers;
using System.Net.Http.Json;
using System.Text.Json;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Protocol;

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
    private FrameChannel? _frames;
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
        SndbDocumentValidator? validator = null,
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

            _embedded.Documents.Create(DocumentCollectionSchema.Create(
                collection,
                validator: ToCoreValidator(validator)));
            return "created";
        }

        using var response = await PostJsonAsync(
            CollectionUrl(collection),
            new DocumentCollectionCreateRequest(ifNotExists, validator),
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
    /// 设置或替换文档集合 validator。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="validator">validator 声明。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>validator 操作响应。</returns>
    public async Task<SndbDocumentValidatorResponse> SetValidatorAsync(
        string collection,
        SndbDocumentValidator validator,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(validator);

        if (_embedded is not null)
        {
            var updated = _embedded.Documents.SetValidator(collection, ToCoreValidator(validator)
                ?? throw new InvalidOperationException("validator 不可为空。"));
            return new SndbDocumentValidatorResponse(collection, "updated", ToClientValidator(updated));
        }

        using var content = JsonContent.Create(validator, SndbDocumentClientJsonContext.Default.SndbDocumentValidator);
        using var response = await _http!.PutAsync(CollectionActionUrl(collection, "validator"), content, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentValidatorResponse, cancellationToken)
            .ConfigureAwait(false);
        return new SndbDocumentValidatorResponse(body.Collection, body.Status, body.Validator);
    }

    /// <summary>
    /// 删除文档集合 validator。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>validator 存在并被删除时返回 true。</returns>
    public async Task<bool> DropValidatorAsync(string collection, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);

        if (_embedded is not null)
            return _embedded.Documents.DropValidator(collection);

        using var request = new HttpRequestMessage(HttpMethod.Delete, CollectionActionUrl(collection, "validator"));
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentValidatorResponse, cancellationToken)
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
            var result = _embedded.Documents.Open(collection).Insert(id, json);
            return ToClientWriteResult(collection, result);
        }

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var docs = new[] { new DocumentWriteRequest(id, JsonPathEvaluator.NormalizeJson(json)) };
            if (await TryFrameInsertAsync(fx, collection, docs, ordered: true, cancellationToken).ConfigureAwait(false) is { } framed)
                return framed;
        }

        using var document = JsonDocument.Parse(json);
        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "insert-one"),
            new DocumentWriteItem(id, document.RootElement.Clone()),
            SndbDocumentClientJsonContext.Default.DocumentWriteItem,
            cancellationToken).ConfigureAwait(false));
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
        bool ordered = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(documents);

        var items = MaterializeWriteItems(documents);
        if (_embedded is not null)
        {
            var result = _embedded.Documents.Open(collection).InsertMany(
                items.Select(static item => new DocumentWriteRequest(item.Id, item.Json)),
                ordered);
            return ToClientWriteResult(collection, result);
        }

        if (_frames is { } fx && fx.ShouldTryFrames() && items.Count > 0)
        {
            var docs = items.Select(static item => new DocumentWriteRequest(item.Id, item.Json)).ToArray();
            if (await TryFrameInsertAsync(fx, collection, docs, ordered, cancellationToken).ConfigureAwait(false) is { } framed)
                return framed;
        }

        using var payload = BuildInsertManyRequest(items, ordered);
        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "insert-many"),
            payload.Request,
            SndbDocumentClientJsonContext.Default.DocumentInsertManyRequest,
            cancellationToken).ConfigureAwait(false));
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

        // 帧仅承载「非 advanced 查询 + 无 continuation token + 无 skip」的单页 find（id / ids / 扫描）；
        // 帧 find 响应不携带 continuation token / snapshotVersion，故一旦命中 hasMore 或任何高级/分页
        // 语义即回落 REST，避免篡改服务端 continuation-token 契约。
        if (_frames is { } fx && fx.ShouldTryFrames()
            && !HasAdvancedQuery(options)
            && string.IsNullOrWhiteSpace(options.ContinuationToken)
            && options.Skip == 0)
        {
            if (await TryFrameFindPageAsync(fx, collection, options, limit, cancellationToken).ConfigureAwait(false) is { } framedPage)
                return framedPage;
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

        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            var w = new ArrayBufferWriter<byte>();
            DocFrameCodec.EncodeFindRequest(w, 1, _database, collection, ids: [id]);
            var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
            if (frame is { } f)
            {
                var rows = DocFrameCodec.DecodeFindResponse(f.Payload);
                return rows.Length == 0 ? null : new SndbDocument(rows[0].Id, rows[0].Json, rows[0].Version);
            }
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
            return ToClientWriteResult(collection, store.Replace(id, json));
        }

        using var document = JsonDocument.Parse(json);
        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "update-one"),
            new DocumentUpdateOneRequest(Id: id, Document: document.RootElement.Clone()),
            SndbDocumentClientJsonContext.Default.DocumentUpdateOneRequest,
            cancellationToken).ConfigureAwait(false));
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
        bool ordered = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(documents);

        var items = MaterializeWriteItems(documents);
        if (_embedded is not null)
        {
            var result = _embedded.Documents.Open(collection).ReplaceMany(
                items.Select(static item => new DocumentWriteRequest(item.Id, item.Json)),
                ordered);
            return ToClientWriteResult(collection, result);
        }

        using var payload = BuildUpdateManyRequest(items, ordered);
        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "update-many"),
            payload.Request,
            SndbDocumentClientJsonContext.Default.DocumentUpdateManyRequest,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 对匹配到的一条文档执行局部更新操作符，可选在未匹配时 upsert。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="filter">过滤条件；为空时按 <paramref name="id"/> 匹配。</param>
    /// <param name="update">局部更新操作符集合。</param>
    /// <param name="id">可选文档 ID；提供时会与 <paramref name="filter"/> 做 AND 合并。</param>
    /// <param name="upsert">未匹配时是否插入新文档。</param>
    /// <param name="upsertId">upsert 新文档 ID；为空时从 <paramref name="id"/> 或过滤条件推断。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档更新结果。</returns>
    public async Task<SndbDocumentWriteResult> UpdateOneAsync(
        string collection,
        SndbDocumentFilter? filter,
        SndbDocumentUpdate update,
        string? id = null,
        bool upsert = false,
        string? upsertId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(update);
        if (!string.IsNullOrWhiteSpace(id))
            ValidateId(id);

        if (_embedded is not null)
        {
            var result = _embedded.Documents.Open(collection).UpdateOneWrite(
                MergeClientFilters(id, filter),
                ToCoreUpdate(update),
                upsert,
                upsertId ?? id);
            return ToClientWriteResult(collection, result);
        }

        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "update-one"),
            new DocumentUpdateOneRequest(id, null, filter, update, upsert, upsertId),
            SndbDocumentClientJsonContext.Default.DocumentUpdateOneRequest,
            cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 对所有匹配文档执行局部更新操作符，可选在未匹配时 upsert。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="filter">过滤条件；为空时匹配全部文档。</param>
    /// <param name="update">局部更新操作符集合。</param>
    /// <param name="upsert">未匹配时是否插入新文档。</param>
    /// <param name="upsertId">upsert 新文档 ID；为空时从过滤条件推断。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>文档更新结果。</returns>
    public async Task<SndbDocumentWriteResult> UpdateManyAsync(
        string collection,
        SndbDocumentFilter? filter,
        SndbDocumentUpdate update,
        bool upsert = false,
        string? upsertId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(update);

        if (_embedded is not null)
        {
            var result = _embedded.Documents.Open(collection).UpdateManyWrite(
                ToCoreFilter(filter),
                ToCoreUpdate(update),
                upsert,
                upsertId);
            return ToClientWriteResult(collection, result);
        }

        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "update-many"),
            new DocumentUpdateManyRequest(null, filter, update, upsert, upsertId),
            SndbDocumentClientJsonContext.Default.DocumentUpdateManyRequest,
            cancellationToken).ConfigureAwait(false));
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

        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "delete-one"),
            new DocumentDeleteOneRequest(id),
            SndbDocumentClientJsonContext.Default.DocumentDeleteOneRequest,
            cancellationToken).ConfigureAwait(false));
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
        bool ordered = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.ToArray();

        if (_embedded is not null)
        {
            var result = _embedded.Documents.Open(collection).DeleteMany(idList, ordered);
            return ToClientWriteResult(collection, result);
        }

        return ToWriteResult(await PostWriteJsonAsync(
            CollectionActionUrl(collection, "delete-many"),
            new DocumentDeleteManyRequest(idList, ordered),
            SndbDocumentClientJsonContext.Default.DocumentDeleteManyRequest,
            cancellationToken).ConfigureAwait(false));
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

    /// <summary>
    /// 执行文档聚合管线。
    /// </summary>
    /// <param name="collection">文档集合名称。</param>
    /// <param name="pipeline">按顺序执行的聚合阶段。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>聚合输出文档列表。</returns>
    public async Task<SndbDocumentAggregateResult> AggregateAsync(
        string collection,
        IReadOnlyList<SndbDocumentAggregateStage> pipeline,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateCollection(collection);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (pipeline.Count == 0)
            throw new InvalidOperationException("document aggregate pipeline 不能为空。");

        if (_embedded is not null)
        {
            var result = _embedded.Documents.Open(collection).Aggregate(ToCoreAggregation(pipeline));
            return new SndbDocumentAggregateResult(collection, result.Documents, result.Documents.Count);
        }

        using var response = await PostJsonAsync(
            CollectionActionUrl(collection, "aggregate"),
            new DocumentAggregateRequest(pipeline),
            SndbDocumentClientJsonContext.Default.DocumentAggregateRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentAggregateResponse, cancellationToken)
            .ConfigureAwait(false);
        return new SndbDocumentAggregateResult(
            body.Collection,
            body.Documents.Select(static document => document.GetRawText()).ToArray(),
            body.Count);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http?.Dispose();
        var embedded = _embedded;
        _embedded = null;
        if (embedded is not null)
            SharedSndbRegistry.Release(embedded);
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            if (string.IsNullOrWhiteSpace(_builder.DataSource))
                throw new InvalidOperationException("文档客户端缺少 Data Source。");

            _database = _builder.DataSource;
            _embedded = SharedSndbRegistry.Acquire(new TsdbOptions { RootDirectory = _builder.DataSource });
            return;
        }

        var (baseUrl, dbFromUrl) = ParseRemoteEndpoint(_builder.DataSource);
        _database = !string.IsNullOrWhiteSpace(_builder.Database) ? _builder.Database! : dbFromUrl;
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程文档客户端缺少数据库名。");

        _http = RemoteHttpClientFactory.Create(
            new Uri(baseUrl, UriKind.Absolute),
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout));
        _frames = new FrameChannel(_http, _builder.ResolveProtocol());
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

    private async Task<SndbDocumentWriteResult?> TryFrameInsertAsync(
        FrameChannel fx,
        string collection,
        IReadOnlyList<DocumentWriteRequest> docs,
        bool ordered,
        CancellationToken cancellationToken)
    {
        var w = new ArrayBufferWriter<byte>();
        DocFrameCodec.EncodeInsertRequest(w, 1, _database, collection, docs, ordered);
        var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
        if (frame is not { } f)
            return null;
        return ToClientWriteResult(collection, DocFrameCodec.DecodeInsertResponse(f.Payload));
    }

    private async Task<SndbDocumentPage?> TryFrameFindPageAsync(
        FrameChannel fx,
        string collection,
        SndbDocumentFindOptions options,
        int limit,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? ids =
            !string.IsNullOrWhiteSpace(options.Id) ? [options.Id!]
            : options.Ids is { Count: > 0 } ? options.Ids
            : null;

        var w = new ArrayBufferWriter<byte>();
        // 多取一条判定 hasMore：一旦溢出即回落 REST（帧无 continuation token）。
        DocFrameCodec.EncodeFindRequest(w, 1, _database, collection, ids, afterId: null, limit: limit + 1);
        var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
        if (frame is not { } f)
            return null;

        DocumentRow[] rows = DocFrameCodec.DecodeFindResponse(f.Payload);
        if (rows.Length > limit)
            return null; // hasMore：交回 REST，由服务端签发 continuation token

        var documents = rows.Select(static row => new SndbDocument(row.Id, row.Json, row.Version)).ToArray();
        return new SndbDocumentPage(collection, documents, ContinuationToken: null, HasMore: false, limit, SnapshotVersion: null, CursorExpiresAtUtc: null);
    }

    private async Task<DocumentWriteResponse> PostWriteJsonAsync<T>(
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(value, typeInfo);
        using var response = await _http!.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return await ReadJsonAsync(response, SndbDocumentClientJsonContext.Default.DocumentWriteResponse, cancellationToken).ConfigureAwait(false);

        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Conflict)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("errors", out var errors)
                    && errors.ValueKind == JsonValueKind.Array)
                {
                    var writeResponse = JsonSerializer.Deserialize(
                        body,
                        SndbDocumentClientJsonContext.Default.DocumentWriteResponse);
                    if (writeResponse is not null)
                        return writeResponse;
                }
            }
            catch (JsonException)
            {
            }
        }

        throw BuildHttpError(response, body);
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

    private static SndbServerException BuildHttpError(HttpResponseMessage response, string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.ServerErrorBody);
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

    private static InsertManyPayload BuildInsertManyRequest(IReadOnlyList<WriteItem> items, bool ordered)
    {
        var documents = new JsonDocument[items.Count];
        var requestItems = new DocumentWriteItem[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            documents[i] = JsonDocument.Parse(items[i].Json);
            requestItems[i] = new DocumentWriteItem(items[i].Id, documents[i].RootElement.Clone());
        }

        return new InsertManyPayload(new DocumentInsertManyRequest(requestItems, ordered), documents);
    }

    private static UpdateManyPayload BuildUpdateManyRequest(IReadOnlyList<WriteItem> items, bool ordered)
    {
        var documents = new JsonDocument[items.Count];
        var requestItems = new DocumentWriteItem[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            documents[i] = JsonDocument.Parse(items[i].Json);
            requestItems[i] = new DocumentWriteItem(items[i].Id, documents[i].RootElement.Clone());
        }

        return new UpdateManyPayload(new DocumentUpdateManyRequest(requestItems, Ordered: ordered), documents);
    }

    private static SndbDocumentWriteResult ToWriteResult(DocumentWriteResponse response) =>
        new(
            response.Collection,
            response.Inserted,
            response.Matched,
            response.Modified,
            response.Deleted,
            response.Errors?.Select(static error => new SndbDocumentWriteError(
                error.Index,
                error.Id,
                error.Code,
                error.Message,
                error.Severity)).ToArray());

    private static SndbDocumentWriteResult ToClientWriteResult(string collection, DocumentWriteResult result) =>
        new(
            collection,
            result.Inserted,
            result.Matched,
            result.Modified,
            result.Deleted,
            result.Errors.Select(static error => new SndbDocumentWriteError(
                error.Index,
                error.Id,
                error.Code,
                error.Message,
                error.Severity)).ToArray());

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

    private static DocumentValidatorDefinition? ToCoreValidator(SndbDocumentValidator? validator)
    {
        if (validator is null)
            return null;

        return new DocumentValidatorDefinition(
            validator.Rules.Select(ToCoreValidatorRule).ToArray(),
            ParseValidationAction(validator.ValidationAction));
    }

    private static DocumentValidatorRuleDefinition ToCoreValidatorRule(SndbDocumentValidatorRule rule)
    {
        var types = new List<DocumentValidatorValueType>();
        if (!string.IsNullOrWhiteSpace(rule.Type))
            types.Add(ParseValidatorValueType(rule.Type));
        if (rule.Types is not null)
            types.AddRange(rule.Types.Select(ParseValidatorValueType));

        return new DocumentValidatorRuleDefinition(
            rule.Path,
            rule.Required,
            types,
            rule.Minimum,
            rule.Maximum,
            rule.Enum?.Select(static value => DocumentValidatorExecutor.ToComparableJson(value)).ToArray(),
            rule.Pattern);
    }

    private static SndbDocumentValidator ToClientValidator(DocumentValidator validator)
        => new(
            validator.Rules.Select(static rule => new SndbDocumentValidatorRule(
                rule.Path,
                rule.Required,
                rule.Types.Count == 1 ? FormatValidatorValueType(rule.Types[0]) : null,
                rule.Types.Count > 1 ? rule.Types.Select(FormatValidatorValueType).ToArray() : null,
                rule.Minimum,
                rule.Maximum,
                rule.EnumValues.Select(ParseJsonElementFromComparableValue).ToArray(),
                rule.Pattern)).ToArray(),
            validator.Action == DocumentValidationAction.Warn ? "warn" : "error");

    private static DocumentValidationAction ParseValidationAction(string? action)
        => (action ?? "error").ToLowerInvariant() switch
        {
            "error" => DocumentValidationAction.Error,
            "warn" => DocumentValidationAction.Warn,
            _ => throw new ArgumentException($"不支持的 validationAction '{action}'。"),
        };

    private static DocumentValidatorValueType ParseValidatorValueType(string type)
        => type.ToLowerInvariant() switch
        {
            "string" => DocumentValidatorValueType.String,
            "number" => DocumentValidatorValueType.Number,
            "integer" or "int" => DocumentValidatorValueType.Integer,
            "boolean" or "bool" => DocumentValidatorValueType.Boolean,
            "object" => DocumentValidatorValueType.Object,
            "array" => DocumentValidatorValueType.Array,
            "null" => DocumentValidatorValueType.Null,
            _ => throw new ArgumentException($"不支持的 validator type '{type}'。"),
        };

    private static string FormatValidatorValueType(DocumentValidatorValueType type)
        => type switch
        {
            DocumentValidatorValueType.String => "string",
            DocumentValidatorValueType.Number => "number",
            DocumentValidatorValueType.Integer => "integer",
            DocumentValidatorValueType.Boolean => "boolean",
            DocumentValidatorValueType.Object => "object",
            DocumentValidatorValueType.Array => "array",
            DocumentValidatorValueType.Null => "null",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "不支持的 validator type。"),
        };

    private static JsonElement ParseJsonElementFromComparableValue(string value)
    {
        if (string.Equals(value, "true", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.Ordinal)
            || string.Equals(value, "null", StringComparison.Ordinal)
            || double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            try
            {
                using var scalar = JsonDocument.Parse(value);
                return scalar.RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        using var text = JsonDocument.Parse(ToJsonStringLiteral(value));
        return text.RootElement.Clone();
    }

    private static string ToJsonStringLiteral(string value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            writer.WriteStringValue(value);
        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

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

    private static DocumentFilter? MergeClientFilters(string? id, SndbDocumentFilter? filter)
    {
        var filters = new List<DocumentFilter>();
        if (!string.IsNullOrWhiteSpace(id))
            filters.Add(new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, id));
        if (ToCoreFilter(filter) is { } coreFilter)
            filters.Add(coreFilter);

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

    private static DocumentUpdate ToCoreUpdate(SndbDocumentUpdate update)
        => new(
            update.Set,
            update.Unset,
            update.Inc,
            update.Min,
            update.Max,
            update.Rename,
            update.Push,
            update.Pull,
            update.AddToSet,
            update.CurrentDate);

    private static DocumentAggregationPipeline ToCoreAggregation(IReadOnlyList<SndbDocumentAggregateStage> pipeline)
        => new(pipeline.Select(ToCoreAggregationStage).ToArray());

    private static DocumentAggregationStage ToCoreAggregationStage(SndbDocumentAggregateStage stage)
    {
        int setCount = CountAggregationStageProperties(stage);
        if (setCount != 1)
            throw new InvalidOperationException("aggregate pipeline 的每个 stage 必须且只能包含一个 $xxx 属性。");

        if (stage.Match is not null)
            return new DocumentMatchStage(ToRequiredCoreFilter(stage.Match));
        if (stage.Project is not null)
            return new DocumentProjectStage(ToCoreProjection(stage.Project)
                ?? throw new InvalidOperationException("$project 需要至少一个投影字段。"));
        if (stage.Group is not null)
            return ToCoreGroupStage(stage.Group);
        if (stage.Sort is not null)
            return new DocumentSortStage(ToCoreSort(stage.Sort));
        if (stage.Limit is not null)
            return new DocumentLimitStage(stage.Limit.Value);
        if (stage.Skip is not null)
            return new DocumentSkipStage(stage.Skip.Value);
        if (stage.Unwind is not null)
            return new DocumentUnwindStage(
                ToCoreField(stage.Unwind.Path),
                stage.Unwind.Name,
                stage.Unwind.PreserveNullAndEmptyArrays);
        if (stage.Count is not null)
            return new DocumentCountStage(string.IsNullOrWhiteSpace(stage.Count) ? "count" : stage.Count);
        if (stage.Distinct is not null)
            return new DocumentDistinctStage(
                ToCoreField(stage.Distinct.Path),
                string.IsNullOrWhiteSpace(stage.Distinct.Name) ? "value" : stage.Distinct.Name,
                stage.Distinct.Limit);

        throw new InvalidOperationException("aggregate pipeline stage 为空。");
    }

    private static int CountAggregationStageProperties(SndbDocumentAggregateStage stage)
    {
        int count = 0;
        if (stage.Match is not null) count++;
        if (stage.Project is not null) count++;
        if (stage.Group is not null) count++;
        if (stage.Sort is not null) count++;
        if (stage.Limit is not null) count++;
        if (stage.Skip is not null) count++;
        if (stage.Unwind is not null) count++;
        if (stage.Count is not null) count++;
        if (stage.Distinct is not null) count++;
        return count;
    }

    private static DocumentGroupStage ToCoreGroupStage(SndbDocumentAggregateGroup group)
    {
        var keys = group.Keys is { Count: > 0 }
            ? group.Keys.Select(static key => new DocumentAggregationGroupKey(key.Name, ToCoreField(key.Path))).ToArray()
            : Array.Empty<DocumentAggregationGroupKey>();
        var accumulators = group.Accumulators is { Count: > 0 }
            ? group.Accumulators.Select(static accumulator => new DocumentAggregationAccumulator(
                accumulator.Name,
                ParseAccumulatorOperator(accumulator.Op),
                string.IsNullOrWhiteSpace(accumulator.Path) ? null : ToCoreField(accumulator.Path))).ToArray()
            : Array.Empty<DocumentAggregationAccumulator>();

        if (keys.Length == 0 && accumulators.Length == 0)
            throw new InvalidOperationException("$group 至少需要一个 key 或 accumulator。");

        return new DocumentGroupStage(keys, accumulators);
    }

    private static DocumentAggregationAccumulatorOperator ParseAccumulatorOperator(string op)
        => op.ToLowerInvariant() switch
        {
            "count" => DocumentAggregationAccumulatorOperator.Count,
            "sum" => DocumentAggregationAccumulatorOperator.Sum,
            "avg" or "average" => DocumentAggregationAccumulatorOperator.Average,
            "min" => DocumentAggregationAccumulatorOperator.Min,
            "max" => DocumentAggregationAccumulatorOperator.Max,
            "first" => DocumentAggregationAccumulatorOperator.First,
            "last" => DocumentAggregationAccumulatorOperator.Last,
            "distinct" => DocumentAggregationAccumulatorOperator.Distinct,
            _ => throw new InvalidOperationException($"不支持的 document aggregate accumulator '{op}'。"),
        };

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
