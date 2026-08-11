using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Protocol;

namespace SonnetDB.Data.Graphs;

/// <summary>
/// Native Graph Preview typed client，统一支持嵌入式和远程 HTTP/NDJSON。
/// </summary>
public sealed class SndbGraphClient : IDisposable
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private FrameChannel? _frames;
    private Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>使用 SonnetDB 连接字符串创建 Graph 客户端。</summary>
    /// <param name="connectionString">嵌入式或远程连接字符串。</param>
    public SndbGraphClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        Open();
    }

    /// <summary>当前连接模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>当前数据库名或嵌入式数据目录。</summary>
    public string Database => _database;

    /// <summary>列出当前数据库的图目录。</summary>
    public async Task<IReadOnlyList<GraphInfoDto>> ListGraphsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
        {
            return _embedded.Graphs.Catalog.Snapshot()
                .Select(static definition => new GraphInfoDto
                {
                    Name = definition.Name,
                    StorageId = definition.StorageId,
                    RecordFormatVersion = definition.RecordFormatVersion,
                })
                .ToArray();
        }

        using HttpResponseMessage response = await _http!.GetAsync(GraphsUrl(), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, RemoteJsonContext.Default.GraphInfoDtoArray, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>创建图；图已存在时由服务端返回冲突。</summary>
    /// <param name="name">图名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>创建后的图摘要。</returns>
    public async Task<GraphInfoDto> CreateGraphAsync(string name, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (_embedded is not null)
        {
            GraphStore store = _embedded.Graphs.Create(name);
            return new GraphInfoDto { Name = store.Name, StorageId = store.StorageId, RecordFormatVersion = store.RecordFormatVersion };
        }
        using HttpResponseMessage response = await PostJsonAsync(
            GraphsUrl(),
            new GraphCreateRequest { Name = name },
            RemoteJsonContext.Default.GraphCreateRequest,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, RemoteJsonContext.Default.GraphInfoDto, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除图目录和物理存储。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>图存在并被删除时返回 true。</returns>
    public async Task<bool> DropGraphAsync(string graph, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (_embedded is not null)
            return _embedded.Graphs.Drop(graph);
        using HttpResponseMessage response = await _http!.DeleteAsync(GraphUrl(graph), cancellationToken).ConfigureAwait(false);
        return response.StatusCode != HttpStatusCode.NotFound && await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取顶点。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="id">顶点标识符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>顶点不存在时返回 null。</returns>
    public async Task<GraphVertex?> GetVertexAsync(string graph, GraphElementId id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            return read.GetVertex(id);
        }
        using HttpResponseMessage response = await _http!.GetAsync(VertexUrl(graph, id), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        GraphVertexDto dto = await ReadJsonAsync(response, RemoteJsonContext.Default.GraphVertexDto, cancellationToken).ConfigureAwait(false);
        return ToVertex(dto);
    }

    /// <summary>读取边。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="id">边标识符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>边不存在时返回 null。</returns>
    public async Task<GraphEdge?> GetEdgeAsync(string graph, GraphElementId id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            return read.GetEdge(id);
        }
        using HttpResponseMessage response = await _http!.GetAsync(EdgeUrl(graph, id), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        GraphEdgeDto dto = await ReadJsonAsync(response, RemoteJsonContext.Default.GraphEdgeDto, cancellationToken).ConfigureAwait(false);
        return ToEdge(dto);
    }

    /// <summary>原子 upsert 顶点。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">带 request ID 和预期版本的写请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<GraphCommitResult> UpsertVertexAsync(string graph, GraphUpsertVertexRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateVertexUpsertRequest(request);
        if (_embedded is not null)
        {
            GraphTransaction transaction = _embedded.Graphs.Open(graph).BeginTransaction(request.RequestId);
            transaction.UpsertVertex(new GraphElementId(request.Id), request.ExpectedElementVersion, (request.Labels ?? []).Select(static id => new LabelId(id)), (request.Properties ?? []).Select(ToProperty), request.UniquePropertyIds ?? []);
            return transaction.Commit(cancellationToken);
        }
        using HttpResponseMessage response = await PutJsonAsync(VertexUrl(graph, new GraphElementId(request.Id)), request, RemoteJsonContext.Default.GraphUpsertVertexRequest, cancellationToken).ConfigureAwait(false);
        GraphMutationResponse result = await ReadJsonAsync(response, RemoteJsonContext.Default.GraphMutationResponse, cancellationToken).ConfigureAwait(false);
        return new GraphCommitResult(result.Sequence, result.IsDuplicate);
    }

    /// <summary>原子 upsert 边。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">带 request ID、端点和预期版本的写请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<GraphCommitResult> UpsertEdgeAsync(string graph, GraphUpsertEdgeRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateEdgeUpsertRequest(request);
        if (_embedded is not null)
        {
            GraphTransaction transaction = _embedded.Graphs.Open(graph).BeginTransaction(request.RequestId);
            transaction.UpsertEdge(new GraphElementId(request.Id), request.ExpectedElementVersion, new GraphElementId(request.SourceId), new GraphElementId(request.TargetId), new LabelId(request.LabelId), (request.Properties ?? []).Select(ToProperty), request.UniquePropertyIds ?? []);
            return transaction.Commit(cancellationToken);
        }
        using HttpResponseMessage response = await PutJsonAsync(EdgeUrl(graph, new GraphElementId(request.Id)), request, RemoteJsonContext.Default.GraphUpsertEdgeRequest, cancellationToken).ConfigureAwait(false);
        GraphMutationResponse result = await ReadJsonAsync(response, RemoteJsonContext.Default.GraphMutationResponse, cancellationToken).ConfigureAwait(false);
        return new GraphCommitResult(result.Sequence, result.IsDuplicate);
    }

    /// <summary>使用 RESTRICT 语义原子删除顶点。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="id">顶点标识符。</param>
    /// <param name="request">预期版本与幂等 request ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交序列号和幂等解析状态。</returns>
    public async Task<GraphCommitResult> DeleteVertexAsync(
        string graph,
        GraphElementId id,
        GraphDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateDeleteRequest(id, request);
        if (_embedded is not null)
        {
            GraphTransaction transaction = _embedded.Graphs.Open(graph).BeginTransaction(request.RequestId);
            transaction.DeleteVertex(id, request.ExpectedElementVersion);
            return transaction.Commit(cancellationToken);
        }
        using HttpResponseMessage response = await SendJsonAsync(
            HttpMethod.Delete,
            VertexUrl(graph, id),
            request,
            RemoteJsonContext.Default.GraphDeleteRequest,
            cancellationToken).ConfigureAwait(false);
        GraphMutationResponse result = await ReadJsonAsync(response, RemoteJsonContext.Default.GraphMutationResponse, cancellationToken).ConfigureAwait(false);
        return new GraphCommitResult(result.Sequence, result.IsDuplicate);
    }

    /// <summary>原子删除边及其双向邻接和索引投影。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="id">边标识符。</param>
    /// <param name="request">预期版本与幂等 request ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>提交序列号和幂等解析状态。</returns>
    public async Task<GraphCommitResult> DeleteEdgeAsync(
        string graph,
        GraphElementId id,
        GraphDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateDeleteRequest(id, request);
        if (_embedded is not null)
        {
            GraphTransaction transaction = _embedded.Graphs.Open(graph).BeginTransaction(request.RequestId);
            transaction.DeleteEdge(id, request.ExpectedElementVersion);
            return transaction.Commit(cancellationToken);
        }
        using HttpResponseMessage response = await SendJsonAsync(
            HttpMethod.Delete,
            EdgeUrl(graph, id),
            request,
            RemoteJsonContext.Default.GraphDeleteRequest,
            cancellationToken).ConfigureAwait(false);
        GraphMutationResponse result = await ReadJsonAsync(response, RemoteJsonContext.Default.GraphMutationResponse, cancellationToken).ConfigureAwait(false);
        return new GraphCommitResult(result.Sequence, result.IsDuplicate);
    }

    /// <summary>以单个幂等事务导入一批 vertex/edge。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">native vertices/edges 或 graph.json nodes/relationships 批次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>批次提交结果。</returns>
    public async Task<GraphImportResponse> ImportAsync(string graph, GraphImportRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        GraphImportVertexDto[] vertices = (request.Vertices ?? []).Concat(request.Nodes ?? []).ToArray();
        GraphImportEdgeDto[] edges = (request.Edges ?? []).Concat(request.Relationships ?? []).ToArray();
        ValidateImportRequest(vertices, edges, request.RequestId);

        if (_embedded is not null)
        {
            GraphTransaction transaction = _embedded.Graphs.Open(graph).BeginTransaction(request.RequestId);
            foreach (GraphImportVertexDto vertex in vertices)
                transaction.UpsertVertex(new GraphElementId(vertex.Id), vertex.ExpectedElementVersion, (vertex.Labels ?? []).Select(static id => new LabelId(id)), (vertex.Properties ?? []).Select(ToProperty), vertex.UniquePropertyIds ?? []);
            foreach (GraphImportEdgeDto edge in edges)
                transaction.UpsertEdge(new GraphElementId(edge.Id), edge.ExpectedElementVersion, new GraphElementId(edge.SourceId), new GraphElementId(edge.TargetId), new LabelId(edge.LabelId), (edge.Properties ?? []).Select(ToProperty), edge.UniquePropertyIds ?? []);
            GraphCommitResult result = transaction.Commit(cancellationToken);
            return new GraphImportResponse { Sequence = result.Sequence, IsDuplicate = result.IsDuplicate, VertexCount = vertices.Length, EdgeCount = edges.Length };
        }

        using HttpResponseMessage response = await PostJsonAsync(
            ImportUrl(graph),
            request,
            RemoteJsonContext.Default.GraphImportRequest,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, RemoteJsonContext.Default.GraphImportResponse, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>流式扩展邻接；远程使用 NDJSON，嵌入式使用同一分页 cursor。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">扩展方向与预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按稳定快照顺序返回的扩展结果。</returns>
    public async IAsyncEnumerable<GraphExpansion> ExpandAsync(
        string graph,
        GraphExpandRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateExpandRequest(request);
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            using GraphCursor<GraphExpansion> cursor = read.Expand(new GraphElementId(request.VertexId), request.Direction, request.EdgeLabelId is { } label ? new LabelId(label) : null, new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            while (true)
            {
                IReadOnlyList<GraphExpansion> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    yield break;
                foreach (GraphExpansion item in page)
                    yield return item;
            }
        }

        if (_frames is { } frames && frames.ShouldTryFrames())
        {
            var writer = new ArrayBufferWriter<byte>();
            const uint StreamId = 1;
            GraphFrameCodec.EncodeExpandRequest(
                writer,
                StreamId,
                _database,
                graph,
                new GraphElementId(request.VertexId),
                request.Direction,
                request.EdgeLabelId is { } frameLabel ? new LabelId(frameLabel) : null,
                request.PageSize,
                request.MaxResults);
            IReadOnlyList<FrameMessage>? responseFrames = await frames
                .TrySendAsync(writer.WrittenMemory, cancellationToken)
                .ConfigureAwait(false);
            if (responseFrames is not null)
            {
                bool sawMeta = false;
                bool sawEnd = false;
                long rowCount = 0;
                foreach (FrameMessage frame in responseFrames)
                {
                    FrameChannel.ThrowIfError(frame.Header, frame.Payload);
                    if (frame.Header.Service != (byte)FrameService.Graph
                        || frame.Header.Op != (byte)GraphFrameOp.Expand
                        || frame.Header.StreamId != StreamId
                        || !frame.Header.IsResponse)
                    {
                        throw new InvalidDataException("SonnetDB Graph Frame 响应信封无效。");
                    }
                    switch (GraphFrameCodec.PeekChunkKind(frame.Payload))
                    {
                        case GraphFrameChunkKind.Meta when !sawMeta && !sawEnd:
                            _ = GraphFrameCodec.DecodeExpandMetaFrame(frame.Payload);
                            sawMeta = true;
                            break;
                        case GraphFrameChunkKind.Row when sawMeta && !sawEnd:
                            rowCount++;
                            yield return GraphFrameCodec.DecodeExpandRowFrame(frame.Payload);
                            break;
                        case GraphFrameChunkKind.End when sawMeta && !sawEnd:
                            long expectedRows = GraphFrameCodec.DecodeExpandEndFrame(frame.Payload);
                            if (expectedRows != rowCount)
                                throw new InvalidDataException("SonnetDB Graph Frame end 行数不匹配。");
                            sawEnd = true;
                            break;
                        default:
                            throw new InvalidDataException("SonnetDB Graph Frame 响应块顺序无效。");
                    }
                }
                if (!sawMeta || !sawEnd)
                    throw new InvalidDataException("SonnetDB Graph Frame 响应未完整结束。");
                yield break;
            }
        }

        using HttpResponseMessage response = await PostJsonAsync(
            StreamExpandUrl(graph),
            request,
            RemoteJsonContext.Default.GraphExpandRequest,
            cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            GraphExpansionDto? dto = JsonSerializer.Deserialize(line, RemoteJsonContext.Default.GraphExpansionDto);
            if (dto is not null)
                yield return ToExpansion(dto);
        }
    }

    /// <summary>按 label 或 label/property 精确索引流式读取顶点。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">索引条件和读取预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>稳定索引顺序的顶点流。</returns>
    public async IAsyncEnumerable<GraphVertex> SeekVerticesAsync(
        string graph,
        GraphSeekRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateSeekRequest(request);
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            using GraphCursor<GraphVertex> cursor = request.PropertyId is { } propertyId
                ? read.SeekVertices(
                    new LabelId(request.LabelId),
                    propertyId,
                    ToValue(request.Value ?? throw new ArgumentException("property seek 需要 value。", nameof(request))),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults })
                : read.SeekVerticesByLabel(
                    new LabelId(request.LabelId),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            while (true)
            {
                IReadOnlyList<GraphVertex> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    yield break;
                foreach (GraphVertex vertex in page)
                    yield return vertex;
            }
        }
        await foreach (GraphVertexDto dto in ReadNdjsonAsync(
            VertexSeekUrl(graph),
            request,
            RemoteJsonContext.Default.GraphSeekRequest,
            RemoteJsonContext.Default.GraphVertexDto,
            cancellationToken).ConfigureAwait(false))
        {
            yield return ToVertex(dto);
        }
    }

    /// <summary>按 label 或 label/property 精确索引流式读取边。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">索引条件和读取预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>稳定索引顺序的边流。</returns>
    public async IAsyncEnumerable<GraphEdge> SeekEdgesAsync(
        string graph,
        GraphSeekRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateSeekRequest(request);
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            using GraphCursor<GraphEdge> cursor = request.PropertyId is { } propertyId
                ? read.SeekEdges(
                    new LabelId(request.LabelId),
                    propertyId,
                    ToValue(request.Value ?? throw new ArgumentException("property seek 需要 value。", nameof(request))),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults })
                : read.SeekEdgesByLabel(
                    new LabelId(request.LabelId),
                    new GraphCursorOptions { PageSize = request.PageSize, MaxResults = request.MaxResults });
            while (true)
            {
                IReadOnlyList<GraphEdge> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    yield break;
                foreach (GraphEdge edge in page)
                    yield return edge;
            }
        }
        await foreach (GraphEdgeDto dto in ReadNdjsonAsync(
            EdgeSeekUrl(graph),
            request,
            RemoteJsonContext.Default.GraphSeekRequest,
            RemoteJsonContext.Default.GraphEdgeDto,
            cancellationToken).ConfigureAwait(false))
        {
            yield return ToEdge(dto);
        }
    }

    /// <summary>流式执行 BFS、DFS 或显式深度范围路径枚举。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">遍历模式、方向和预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按请求模式生成的路径流。</returns>
    public async IAsyncEnumerable<GraphPath> TraverseAsync(
        string graph,
        GraphTraversalRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateTraversalRequest(request);
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            GraphTraversalOptions options = ToTraversalOptions(request);
            using GraphCursor<GraphPath> cursor = request.Kind switch
            {
                GraphTraversalKind.BreadthFirst => read.Bfs(
                    new GraphElementId(request.StartId),
                    request.Direction,
                    ToOptionalLabel(request.EdgeLabelId),
                    options),
                GraphTraversalKind.DepthFirst => read.Dfs(
                    new GraphElementId(request.StartId),
                    request.Direction,
                    ToOptionalLabel(request.EdgeLabelId),
                    options),
                GraphTraversalKind.Paths => read.Paths(
                    new GraphElementId(request.StartId),
                    request.MinDepth,
                    request.MaxDepth,
                    request.Direction,
                    ToOptionalLabel(request.EdgeLabelId),
                    options),
                _ => throw new ArgumentOutOfRangeException(nameof(request)),
            };
            while (true)
            {
                IReadOnlyList<GraphPath> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    yield break;
                foreach (GraphPath path in page)
                    yield return path;
            }
        }
        await foreach (GraphPathDto dto in ReadNdjsonAsync(
            TraverseUrl(graph),
            request,
            RemoteJsonContext.Default.GraphTraversalRequest,
            RemoteJsonContext.Default.GraphPathDto,
            cancellationToken).ConfigureAwait(false))
        {
            yield return ToPath(dto);
        }
    }

    /// <summary>查找第一条无权最短路径。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">端点、方向和预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在时返回路径，否则返回 null。</returns>
    public async Task<GraphPath?> ShortestPathAsync(
        string graph,
        GraphShortestPathRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateShortestPathRequest(request);
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            return read.ShortestPath(
                new GraphElementId(request.StartId),
                new GraphElementId(request.TargetId),
                request.Direction,
                ToOptionalLabel(request.EdgeLabelId),
                new GraphTraversalOptions
                {
                    MaxDepth = request.MaxDepth,
                    MaxFrontier = request.MaxFrontier,
                    MaxPaths = request.MaxFrontier,
                },
                cancellationToken);
        }
        using HttpResponseMessage response = await PostJsonAsync(
            ShortestPathUrl(graph),
            request,
            RemoteJsonContext.Default.GraphShortestPathRequest,
            cancellationToken).ConfigureAwait(false);
        GraphShortestPathResponse result = await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphShortestPathResponse,
            cancellationToken).ConfigureAwait(false);
        return result.Path is null ? null : ToPath(result.Path);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http?.Dispose();
        _http = null;
        _frames = null;
        Tsdb? embedded = _embedded;
        _embedded = null;
        if (embedded is not null)
            SharedSndbRegistry.Release(embedded);
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            string dataSource = _builder.ResolveEmbeddedDataSource();
            if (string.IsNullOrWhiteSpace(dataSource))
                throw new InvalidOperationException("Graph 客户端缺少 Data Source。");
            _database = dataSource;
            _embedded = SharedSndbRegistry.Acquire(_builder.CreateEmbeddedOptions(dataSource));
            return;
        }
        _database = _builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程 Graph 客户端缺少数据库名。");
        _http = RemoteHttpClientFactory.Create(new Uri(_builder.ResolveBaseUrl(), UriKind.Absolute), _builder.Username, _builder.Password, _builder.Token, TimeSpan.FromSeconds(_builder.Timeout));
        _frames = new FrameChannel(_http, _builder.ResolveProtocol());
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(string url, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(value, typeInfo);
        HttpResponseMessage response = await _http!.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<HttpResponseMessage> PutJsonAsync<T>(string url, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(value, typeInfo);
        HttpResponseMessage response = await _http!.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<HttpResponseMessage> SendJsonAsync<T>(
        HttpMethod method,
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(value, typeInfo),
        };
        HttpResponseMessage response = await _http!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SonnetDB Graph response body is empty.");
    }

    private async IAsyncEnumerable<TResponse> ReadNdjsonAsync<TRequest, TResponse>(
        string url,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> requestTypeInfo,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> responseTypeInfo,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        where TResponse : class
    {
        using HttpResponseMessage response = await PostJsonAsync(
            url,
            request,
            requestTypeInfo,
            cancellationToken).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            TResponse? item = JsonSerializer.Deserialize(line, responseTypeInfo);
            if (item is not null)
                yield return item;
        }
    }

    private static async Task<bool> EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return true;
        string message = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException($"SonnetDB Graph HTTP {(int)response.StatusCode}: {message}");
    }

    private static GraphProperty ToProperty(GraphPropertyDto dto) => new(dto.PropertyId, ToValue(dto.Value));

    private static GraphPropertyValue ToValue(GraphValueDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
    {
        GraphPropertyKind.Null => GraphPropertyValue.Null,
        GraphPropertyKind.Int64 when value.Int64 is { } number => GraphPropertyValue.FromInt64(number),
        GraphPropertyKind.Float64 when value.Float64 is { } number => GraphPropertyValue.FromFloat64(number),
        GraphPropertyKind.Boolean when value.Boolean is { } boolean => GraphPropertyValue.FromBoolean(boolean),
        GraphPropertyKind.String when value.String is not null => GraphPropertyValue.FromString(value.String),
        GraphPropertyKind.DateTime when value.DateTime is { } dateTime => GraphPropertyValue.FromDateTime(dateTime),
        GraphPropertyKind.Blob when value.BlobBase64 is not null => GraphPropertyValue.FromBlob(Convert.FromBase64String(value.BlobBase64)),
        GraphPropertyKind.Json when value.Json is not null => GraphPropertyValue.FromJson(value.Json),
        _ => throw new ArgumentException("graph property value 与 kind 不匹配。", nameof(value)),
    };
    }

    private static GraphVertex ToVertex(GraphVertexDto dto)
        => new GraphVertexFactory().CreateVertex(dto);

    private static GraphEdge ToEdge(GraphEdgeDto dto)
        => new GraphVertexFactory().CreateEdge(dto);

    private static GraphExpansion ToExpansion(GraphExpansionDto dto)
        => new GraphVertexFactory().CreateExpansion(dto);

    private string GraphsUrl() => $"v1/db/{Uri.EscapeDataString(_database)}/graphs";
    private string GraphUrl(string graph) => GraphsUrl() + "/" + Uri.EscapeDataString(graph);
    private string VertexUrl(string graph, GraphElementId id) => GraphUrl(graph) + "/vertices/" + id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string EdgeUrl(string graph, GraphElementId id) => GraphUrl(graph) + "/edges/" + id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string StreamExpandUrl(string graph) => GraphUrl(graph) + "/expand/stream";
    private string VertexSeekUrl(string graph) => GraphUrl(graph) + "/vertices/seek/stream";
    private string EdgeSeekUrl(string graph) => GraphUrl(graph) + "/edges/seek/stream";
    private string TraverseUrl(string graph) => GraphUrl(graph) + "/traverse/stream";
    private string ShortestPathUrl(string graph) => GraphUrl(graph) + "/shortest-path";
    private string ImportUrl(string graph) => GraphUrl(graph) + "/import";

    private static void ValidateName(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value is "." or ".." || value.Length > 128)
            throw new ArgumentException("graph 名称长度或值无效。", parameterName);
    }

    private static void ValidateVertexUpsertRequest(GraphUpsertVertexRequest request)
    {
        ValidateMutationRequest(request.Id, request.ExpectedElementVersion, request.RequestId, nameof(request));
        ValidateLabels(request.Labels);
        ValidateProperties(request.Properties);
        ValidateUniquePropertyIds(request.UniquePropertyIds);
    }

    private static void ValidateEdgeUpsertRequest(GraphUpsertEdgeRequest request)
    {
        ValidateMutationRequest(request.Id, request.ExpectedElementVersion, request.RequestId, nameof(request));
        if (request.SourceId <= 0 || request.TargetId <= 0 || request.LabelId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "edge endpoint 和 label ID 必须为正数。");
        ValidateProperties(request.Properties);
        ValidateUniquePropertyIds(request.UniquePropertyIds);
    }

    private static void ValidateMutationRequest(long id, long expectedVersion, Guid requestId, string parameterName)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(parameterName, "element ID 必须为正数。");
        if (expectedVersion < 0)
            throw new ArgumentOutOfRangeException(parameterName, "expected element version 不能为负数。");
        if (requestId == Guid.Empty)
            throw new ArgumentException("request ID 不能为空。", parameterName);
    }

    private static void ValidateDeleteRequest(GraphElementId id, GraphDeleteRequest request)
    {
        if (id.Value <= 0 || request.ExpectedElementVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "删除请求的 element ID 和 expected version 必须为正数。");
        if (request.RequestId == Guid.Empty)
            throw new ArgumentException("request ID 不能为空。", nameof(request));
    }

    private static void ValidateLabels(IReadOnlyList<int>? labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        int previous = 0;
        foreach (int label in labels.Order())
        {
            if (label <= 0 || label == previous)
                throw new ArgumentException("labels 必须为正数且不能重复。", nameof(labels));
            previous = label;
        }
    }

    private static void ValidateProperties(IReadOnlyList<GraphPropertyDto>? properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var propertyIds = new HashSet<int>();
        foreach (GraphPropertyDto property in properties)
        {
            if (property is null || property.PropertyId <= 0 || !propertyIds.Add(property.PropertyId))
                throw new ArgumentException("properties 必须包含不重复的正数 property ID。", nameof(properties));
            _ = ToValue(property.Value);
        }
    }

    private static void ValidateUniquePropertyIds(IReadOnlyList<int>? propertyIds)
    {
        ArgumentNullException.ThrowIfNull(propertyIds);
        var seen = new HashSet<int>();
        foreach (int propertyId in propertyIds)
        {
            if (propertyId <= 0 || !seen.Add(propertyId))
                throw new ArgumentException("unique property ID 必须为正数且不能重复。", nameof(propertyIds));
        }
    }

    private static void ValidateImportRequest(
        IReadOnlyList<GraphImportVertexDto> vertices,
        IReadOnlyList<GraphImportEdgeDto> edges,
        Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("导入 request ID 不能为空。", nameof(requestId));
        if (vertices.Count + edges.Count is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(vertices), "导入批次必须包含 1 到 10,000 个元素。");
        foreach (GraphImportVertexDto vertex in vertices)
        {
            if (vertex is null)
                throw new ArgumentException("导入批次不能包含 null vertex。", nameof(vertices));
            ValidateVertexUpsertRequest(new GraphUpsertVertexRequest
            {
                Id = vertex.Id,
                ExpectedElementVersion = vertex.ExpectedElementVersion,
                Labels = vertex.Labels,
                Properties = vertex.Properties ?? [],
                UniquePropertyIds = vertex.UniquePropertyIds ?? [],
                RequestId = requestId,
            });
        }
        foreach (GraphImportEdgeDto edge in edges)
        {
            if (edge is null)
                throw new ArgumentException("导入批次不能包含 null edge。", nameof(edges));
            ValidateEdgeUpsertRequest(new GraphUpsertEdgeRequest
            {
                Id = edge.Id,
                ExpectedElementVersion = edge.ExpectedElementVersion,
                SourceId = edge.SourceId,
                TargetId = edge.TargetId,
                LabelId = edge.LabelId,
                Properties = edge.Properties ?? [],
                UniquePropertyIds = edge.UniquePropertyIds ?? [],
                RequestId = requestId,
            });
        }
    }

    private static void ValidateExpandRequest(GraphExpandRequest request)
    {
        if (request.VertexId <= 0 || request.EdgeLabelId is <= 0 || !Enum.IsDefined(request.Direction)
            || request.PageSize is <= 0 or > 1_000 || request.MaxResults is <= 0 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(request), "expand 的顶点、方向或读取预算无效。");
    }

    private static void ValidateSeekRequest(GraphSeekRequest request)
    {
        if (request.LabelId <= 0 || request.PageSize is <= 0 or > 1_000 || request.MaxResults is <= 0 or > 10_000
            || request.PropertyId is <= 0 || (request.PropertyId is null) != (request.Value is null))
            throw new ArgumentException("seek 的 label/property/value 或读取预算无效。", nameof(request));
        if (request.Value is not null)
            _ = ToValue(request.Value);
    }

    private static void ValidateTraversalRequest(GraphTraversalRequest request)
    {
        if (request.StartId <= 0 || !Enum.IsDefined(request.Kind) || !Enum.IsDefined(request.Direction)
            || !Enum.IsDefined(request.PathUniqueness) || request.EdgeLabelId is <= 0 || request.MinDepth < 0
            || request.MaxDepth is < 0 or > 64 || request.MinDepth > request.MaxDepth
            || request.MaxFrontier is <= 0 or > 10_000 || request.MaxPaths is <= 0 or > 10_000
            || request.PageSize is <= 0 or > 1_000
            || (request.Kind != GraphTraversalKind.Paths && request.MinDepth != 0))
            throw new ArgumentException("Graph traversal 的起点、模式、方向、深度或执行预算无效。", nameof(request));
    }

    private static void ValidateShortestPathRequest(GraphShortestPathRequest request)
    {
        if (request.StartId <= 0 || request.TargetId <= 0 || !Enum.IsDefined(request.Direction)
            || request.EdgeLabelId is <= 0 || request.MaxDepth is < 0 or > 64 || request.MaxFrontier is <= 0 or > 10_000)
            throw new ArgumentException("Shortest path 的端点、方向、深度或 frontier 预算无效。", nameof(request));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static GraphTraversalOptions ToTraversalOptions(GraphTraversalRequest request)
        => new()
        {
            MaxDepth = request.MaxDepth,
            MaxFrontier = request.MaxFrontier,
            MaxPaths = request.MaxPaths,
            PathUniqueness = request.PathUniqueness,
            PageSize = request.PageSize,
        };

    private static LabelId? ToOptionalLabel(int? labelId)
        => labelId is { } value ? new LabelId(value) : null;

    private static GraphPath ToPath(GraphPathDto dto)
        => new(
            dto.VertexIds.Select(static value => new GraphElementId(value)).ToArray(),
            dto.EdgeIds.Select(static value => new GraphElementId(value)).ToArray());
}

internal sealed class GraphVertexFactory
{
    internal GraphVertex CreateVertex(GraphVertexDto dto)
        => new(
            new GraphElementId(dto.Id),
            dto.ElementVersion,
            dto.Labels.Select(static value => new LabelId(value)).ToArray(),
            dto.Properties.Select(static value => new GraphProperty(value.PropertyId, ToValue(value.Value))).ToArray());

    internal GraphEdge CreateEdge(GraphEdgeDto dto)
        => new(
            new GraphElementId(dto.Id),
            dto.ElementVersion,
            new GraphElementId(dto.SourceId),
            new GraphElementId(dto.TargetId),
            new LabelId(dto.LabelId),
            dto.Properties.Select(static value => new GraphProperty(value.PropertyId, ToValue(value.Value))).ToArray());

    internal GraphExpansion CreateExpansion(GraphExpansionDto dto)
        => new(
            new GraphElementId(dto.AnchorId),
            new GraphElementId(dto.NeighborId),
            dto.Direction,
            CreateEdge(dto.Edge));

    private static GraphPropertyValue ToValue(GraphValueDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind switch
    {
        GraphPropertyKind.Null => GraphPropertyValue.Null,
        GraphPropertyKind.Int64 when value.Int64 is { } number => GraphPropertyValue.FromInt64(number),
        GraphPropertyKind.Float64 when value.Float64 is { } number => GraphPropertyValue.FromFloat64(number),
        GraphPropertyKind.Boolean when value.Boolean is { } boolean => GraphPropertyValue.FromBoolean(boolean),
        GraphPropertyKind.String when value.String is not null => GraphPropertyValue.FromString(value.String),
        GraphPropertyKind.DateTime when value.DateTime is { } dateTime => GraphPropertyValue.FromDateTime(dateTime),
        GraphPropertyKind.Blob when value.BlobBase64 is not null => GraphPropertyValue.FromBlob(Convert.FromBase64String(value.BlobBase64)),
        GraphPropertyKind.Json when value.Json is not null => GraphPropertyValue.FromJson(value.Json),
        _ => throw new ArgumentException("graph property value 与 kind 不匹配。", nameof(value)),
    };
    }
}
