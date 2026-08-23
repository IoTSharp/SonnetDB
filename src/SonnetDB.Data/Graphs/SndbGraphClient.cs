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
    private readonly object _operationsSync = new();
    private readonly Dictionary<Guid, GraphMaintenanceApprovalDto> _embeddedApprovals = [];
    private readonly List<GraphMaintenanceApprovalDto> _embeddedAudit = [];
    private string? _embeddedAuditPath;
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
                    MaxPaths = request.MaxPaths,
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

    /// <summary>查找一条加权最短路径，嵌入式和远程模式使用同一执行合同。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">权重属性、算法、端点和执行预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>存在时返回加权路径，否则返回 null。</returns>
    public async Task<GraphWeightedPath?> WeightedShortestPathAsync(
        string graph,
        GraphWeightedShortestPathRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ValidateName(graph, nameof(graph));
        ValidateWeightedShortestPathRequest(request);
        if (_embedded is not null)
        {
            using GraphReadSession read = _embedded.Graphs.Open(graph).BeginRead();
            return read.WeightedShortestPath(
                new GraphElementId(request.StartId),
                new GraphElementId(request.TargetId),
                GraphWeightedShortestPathOptions.ForProperty(request.WeightPropertyId) with
                {
                    Algorithm = request.Algorithm,
                    Direction = request.Direction,
                    EdgeLabelId = ToOptionalLabel(request.EdgeLabelId),
                    MaxDepth = request.MaxDepth,
                    MaxFrontier = request.MaxFrontier,
                    MaxVisitedVertices = request.MaxVisitedVertices,
                    MaxExpandedEdges = request.MaxExpandedEdges,
                    MaxTotalWeight = request.MaxTotalWeight ?? double.PositiveInfinity,
                    PageSize = request.PageSize,
                    MaxPageBytes = request.MaxPageBytes,
                },
                cancellationToken);
        }
        using HttpResponseMessage response = await PostJsonAsync(
            WeightedShortestPathUrl(graph),
            request,
            RemoteJsonContext.Default.GraphWeightedShortestPathRequest,
            cancellationToken).ConfigureAwait(false);
        GraphWeightedShortestPathResponse result = await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphWeightedShortestPathResponse,
            cancellationToken).ConfigureAwait(false);
        if (result.Path is null || result.TotalWeight is null)
            return null;
        return new GraphWeightedPath(
            ToPath(result.Path),
            result.TotalWeight.Value,
            result.Algorithm,
            result.ExpandedVertices,
            result.ExpandedEdges,
            result.SnapshotSequence);
    }

    /// <summary>读取 schema、索引、degree 和慢遍历组成的 Graph 运维概览。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>共享的 Graph 运维能力与当前统计。</returns>
    public async Task<GraphOperationsOverviewDto> GetOperationsOverviewAsync(
        string graph,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (_embedded is not null)
        {
            GraphStore store = _embedded.Graphs.Open(graph);
            using GraphReadSession read = store.BeginRead();
            GraphStatistics statistics = read.RefreshStatistics(cancellationToken);
            return new GraphOperationsOverviewDto(
                ToGraphInfo(store),
                statistics.Sequence,
                statistics.VertexCount,
                statistics.EdgeCount,
                statistics.LabelCardinality
                    .OrderBy(static item => item.Key.Value)
                    .Select(static item => new GraphLabelStatisticDto(item.Key.Value, item.Value))
                    .ToArray(),
                statistics.PropertyIndexCardinality
                    .OrderBy(static item => item.Key.ElementKind)
                    .ThenBy(static item => item.Key.LabelId.Value)
                    .ThenBy(static item => item.Key.PropertyId)
                    .ThenBy(static item => item.Key.ValueKind)
                    .Select(static item => new GraphIndexStatisticDto(
                        item.Key.ElementKind.ToString().ToLowerInvariant(),
                        item.Key.LabelId.Value,
                        item.Key.PropertyId,
                        item.Key.ValueKind.ToString().ToLowerInvariant(),
                        item.Value))
                    .ToArray(),
                statistics.DegreeHistogram
                    .OrderBy(static item => item.Key)
                    .Select(static item => new GraphDegreeBucketDto(item.Key, item.Value))
                    .ToArray(),
                [],
                "not_available_embedded",
                CreateOperationsCapabilities(slowTraversalDiagnostics: false, audit: true));
        }

        using HttpResponseMessage response = await _http!.GetAsync(
            OperationsUrl(graph) + "/overview",
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphOperationsOverviewDto,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取适合运维界面渲染的有界 Graph 快照。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="limit">最多返回的顶点数，范围 1 到 1,000。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>同一 statement snapshot 上的顶点、内部边和截断状态。</returns>
    public async Task<GraphVisualizationDto> GetVisualizationAsync(
        string graph,
        int limit = 250,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (limit is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (_embedded is not null)
            return BuildEmbeddedVisualization(_embedded.Graphs.Open(graph), limit, cancellationToken);

        using HttpResponseMessage response = await _http!.GetAsync(
            OperationsUrl(graph) + "/visualization?limit="
                + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphVisualizationDto,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>把兼容 <see cref="SndbGraphImporter.ImportJsonAsync"/> 的有界 Graph JSON 写入目标流。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="destination">导出目标流；调用完成后保持打开。</param>
    /// <param name="maxElements">顶点与边合计上限，范围 1 到 1,000,000。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ExportJsonAsync(
        string graph,
        Stream destination,
        int maxElements = 100_000,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("Graph 导出目标流必须可写。", nameof(destination));
        if (maxElements is < 1 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(maxElements));

        if (_embedded is not null)
        {
            await WriteEmbeddedExportAsync(
                destination,
                _embedded.Graphs.Open(graph),
                maxElements,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        string url = OperationsUrl(graph) + "/export?maxElements="
            + maxElements.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using HttpResponseMessage response = await _http!.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>暂存一项 Graph 危险维护；本方法不会执行维护。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="request">动作和显式执行预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>十分钟有效的审批记录。</returns>
    public async Task<GraphMaintenanceApprovalDto> StageMaintenanceAsync(
        string graph,
        GraphMaintenanceStageRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        ValidateMaintenanceRequest(request);
        if (_embedded is not null)
        {
            _ = _embedded.Graphs.Open(graph);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var staged = new GraphMaintenanceApprovalDto
            {
                ApprovalId = Guid.NewGuid(),
                OccurredAtUtc = now,
                Database = _database,
                Graph = graph,
                Action = request.Action,
                State = "staged",
                Principal = "embedded-sdk",
                ExpiresAtUtc = now.AddMinutes(10),
                CompactOnCompletion = request.CompactOnCompletion,
                MaxWorkUnits = request.MaxWorkUnits,
            };
            lock (_operationsSync)
                AppendEmbeddedAudit(staged);
            return staged;
        }

        using HttpResponseMessage response = await PostJsonAsync(
            MaintenanceUrl(graph) + "/stage",
            request,
            RemoteJsonContext.Default.GraphMaintenanceStageRequest,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphMaintenanceApprovalDto,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>批准并执行一项已暂存的 Graph 维护。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="approvalId">暂存返回的审批标识符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成、暂停或失败前的最终可见审批状态。</returns>
    public async Task<GraphMaintenanceApprovalDto> ApproveMaintenanceAsync(
        string graph,
        Guid approvalId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (approvalId == Guid.Empty)
            throw new ArgumentException("Graph maintenance approval ID 不能为空。", nameof(approvalId));
        if (_embedded is not null)
            return ApproveEmbeddedMaintenance(_embedded.Graphs.Open(graph), graph, approvalId, cancellationToken);

        using HttpResponseMessage response = await _http!.PostAsync(
            MaintenanceUrl(graph) + "/" + approvalId.ToString("D") + "/approve",
            content: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphMaintenanceApprovalDto,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拒绝一项已暂存的 Graph 维护。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="approvalId">审批标识符。</param>
    /// <param name="reason">可选拒绝原因，最多 512 个字符。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>拒绝审计事件。</returns>
    public async Task<GraphMaintenanceApprovalDto> RejectMaintenanceAsync(
        string graph,
        Guid approvalId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (approvalId == Guid.Empty)
            throw new ArgumentException("Graph maintenance approval ID 不能为空。", nameof(approvalId));
        if (reason?.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(reason));
        if (_embedded is not null)
            return RejectEmbeddedMaintenance(graph, approvalId, reason);

        using HttpResponseMessage response = await PostJsonAsync(
            MaintenanceUrl(graph) + "/" + approvalId.ToString("D") + "/reject",
            new GraphMaintenanceDecisionRequest(reason),
            RemoteJsonContext.Default.GraphMaintenanceDecisionRequest,
            cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphMaintenanceApprovalDto,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取 Graph 维护审批与执行审计。</summary>
    /// <param name="graph">图名称。</param>
    /// <param name="limit">最近事件上限，范围 1 到 2,000。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按时间倒序排列的审计事件。</returns>
    public async Task<IReadOnlyList<GraphMaintenanceApprovalDto>> ListMaintenanceAuditAsync(
        string graph,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateName(graph, nameof(graph));
        if (limit is < 1 or > 2_000)
            throw new ArgumentOutOfRangeException(nameof(limit));
        if (_embedded is not null)
        {
            _ = _embedded.Graphs.Open(graph);
            lock (_operationsSync)
            {
                LoadEmbeddedAudit();
                return _embeddedAudit
                    .Where(entry => string.Equals(entry.Graph, graph, StringComparison.Ordinal))
                    .TakeLast(limit)
                    .Reverse()
                    .ToArray();
            }
        }

        using HttpResponseMessage response = await _http!.GetAsync(
            MaintenanceUrl(graph) + "/audit?limit="
                + limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        GraphMaintenanceAuditListDto result = await ReadJsonAsync(
            response,
            RemoteJsonContext.Default.GraphMaintenanceAuditListDto,
            cancellationToken).ConfigureAwait(false);
        return result.Items;
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
            string systemDirectory = Path.Combine(_embedded.RootDirectory, ".system");
            Directory.CreateDirectory(systemDirectory);
            _embeddedAuditPath = Path.Combine(systemDirectory, "graph-maintenance-audit.ndjson");
            LoadEmbeddedAudit();
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
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ServerErrorBody? error = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.ServerErrorBody);
            if (error is not null && !string.IsNullOrWhiteSpace(error.Error))
                throw new SndbServerException(error.Error, error.Message, response.StatusCode);
        }
        catch (JsonException)
        {
        }
        throw new SndbServerException(
            "http_error",
            string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase ?? "SonnetDB Graph HTTP error." : body,
            response.StatusCode);
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

    private static GraphInfoDto ToGraphInfo(GraphStore store)
        => new()
        {
            Name = store.Name,
            StorageId = store.StorageId,
            RecordFormatVersion = store.RecordFormatVersion,
        };

    private static GraphVisualizationDto BuildEmbeddedVisualization(
        GraphStore store,
        int limit,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = store.BeginRead();
        var vertices = new List<GraphVertexDto>(limit + 1);
        using (GraphCursor<GraphVertex> cursor = read.ScanVertices(new GraphCursorOptions
        {
            PageSize = 256,
            MaxResults = limit + 1,
        }))
        {
            while (vertices.Count <= limit)
            {
                IReadOnlyList<GraphVertex> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                vertices.AddRange(page.Select(ToDto));
            }
        }
        bool truncated = vertices.Count > limit;
        if (truncated)
            vertices.RemoveAt(vertices.Count - 1);

        var ids = vertices.Select(static vertex => vertex.Id).ToHashSet();
        int edgeResultLimit = checked((limit * 2) + 1);
        int edgeScanLimit = Math.Min(100_000, Math.Max(edgeResultLimit, limit * 100));
        var edges = new List<GraphEdgeDto>(edgeResultLimit);
        int scanned = 0;
        using (GraphCursor<GraphEdge> cursor = read.ScanEdges(new GraphCursorOptions
        {
            PageSize = 256,
            MaxResults = edgeScanLimit,
        }))
        {
            while (edges.Count < edgeResultLimit)
            {
                IReadOnlyList<GraphEdge> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                scanned += page.Count;
                edges.AddRange(page
                    .Where(edge => ids.Contains(edge.SourceId.Value) && ids.Contains(edge.TargetId.Value))
                    .Select(ToDto));
            }
        }
        if (edges.Count >= edgeResultLimit)
        {
            edges.RemoveRange(edgeResultLimit - 1, edges.Count - (edgeResultLimit - 1));
            truncated = true;
        }
        if (scanned >= edgeScanLimit)
            truncated = true;
        return new GraphVisualizationDto(read.Sequence, truncated, vertices, edges);
    }

    private static async Task WriteEmbeddedExportAsync(
        Stream destination,
        GraphStore store,
        int maxElements,
        CancellationToken cancellationToken)
    {
        using GraphReadSession read = store.BeginRead();
        await using var writer = new Utf8JsonWriter(destination);
        writer.WriteStartObject();
        writer.WriteNumber("snapshotSequence", read.Sequence);
        writer.WriteStartArray("vertices");
        int written = 0;
        bool truncated = false;
        using (GraphCursor<GraphVertex> cursor = read.ScanVertices(new GraphCursorOptions
        {
            PageSize = 256,
            MaxResults = maxElements + 1,
        }))
        {
            while (written <= maxElements)
            {
                IReadOnlyList<GraphVertex> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (GraphVertex vertex in page)
                {
                    if (written >= maxElements)
                    {
                        truncated = true;
                        break;
                    }
                    JsonSerializer.Serialize(writer, ToDto(vertex), RemoteJsonContext.Default.GraphVertexDto);
                    written++;
                }
                if (truncated)
                    break;
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        writer.WriteEndArray();
        writer.WriteStartArray("edges");
        if (!truncated && written < maxElements)
        {
            int remaining = maxElements - written;
            using GraphCursor<GraphEdge> cursor = read.ScanEdges(new GraphCursorOptions
            {
                PageSize = 256,
                MaxResults = remaining + 1,
            });
            while (written <= maxElements)
            {
                IReadOnlyList<GraphEdge> page = cursor.ReadNextPage(cancellationToken);
                if (page.Count == 0)
                    break;
                foreach (GraphEdge edge in page)
                {
                    if (written >= maxElements)
                    {
                        truncated = true;
                        break;
                    }
                    JsonSerializer.Serialize(writer, ToDto(edge), RemoteJsonContext.Default.GraphEdgeDto);
                    written++;
                }
                if (truncated)
                    break;
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        writer.WriteEndArray();
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("elementCount", written);
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private GraphMaintenanceApprovalDto ApproveEmbeddedMaintenance(
        GraphStore store,
        string graph,
        Guid approvalId,
        CancellationToken cancellationToken)
    {
        lock (_operationsSync)
        {
            GraphMaintenanceApprovalDto staged = ResolveEmbeddedApproval(graph, approvalId);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (now > staged.ExpiresAtUtc)
            {
                var expired = staged with
                {
                    OccurredAtUtc = now,
                    State = "expired",
                    ErrorCode = "graph_maintenance_approval_expired",
                    Reason = "Graph 维护审批已过期，请重新暂存。",
                };
                AppendEmbeddedAudit(expired);
                throw new InvalidOperationException(expired.Reason);
            }

            GraphMaintenanceExecutionDto result = ExecuteEmbeddedMaintenance(store, staged, cancellationToken);
            var completed = staged with
            {
                OccurredAtUtc = DateTimeOffset.UtcNow,
                State = result.IsComplete ? "completed" : "paused",
                Result = result,
            };
            AppendEmbeddedAudit(completed);
            return completed;
        }
    }

    private GraphMaintenanceApprovalDto RejectEmbeddedMaintenance(
        string graph,
        Guid approvalId,
        string? reason)
    {
        lock (_operationsSync)
        {
            GraphMaintenanceApprovalDto staged = ResolveEmbeddedApproval(graph, approvalId);
            var rejected = staged with
            {
                OccurredAtUtc = DateTimeOffset.UtcNow,
                State = "rejected",
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            };
            AppendEmbeddedAudit(rejected);
            return rejected;
        }
    }

    private GraphMaintenanceApprovalDto ResolveEmbeddedApproval(string graph, Guid approvalId)
    {
        LoadEmbeddedAudit();
        if (!_embeddedApprovals.TryGetValue(approvalId, out GraphMaintenanceApprovalDto? approval)
            || !string.Equals(approval.Graph, graph, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("未找到当前 Graph 的维护审批。");
        }
        if (!string.Equals(approval.State, "staged", StringComparison.Ordinal))
            throw new InvalidOperationException($"Graph 维护审批当前状态为 '{approval.State}'，不能重复决策。");
        return approval;
    }

    private void LoadEmbeddedAudit()
    {
        if (_embeddedAuditPath is null)
            return;
        _embeddedApprovals.Clear();
        _embeddedAudit.Clear();
        if (!File.Exists(_embeddedAuditPath))
            return;

        int lineNumber = 0;
        foreach (string line in File.ReadLines(_embeddedAuditPath))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                GraphMaintenanceApprovalDto entry = JsonSerializer.Deserialize(
                        line,
                        RemoteJsonContext.Default.GraphMaintenanceApprovalDto)
                    ?? throw new InvalidDataException("Graph embedded 维护审计记录不能为 null。");
                ValidateEmbeddedAuditEntry(entry, lineNumber);
                _embeddedAudit.Add(entry);
                _embeddedApprovals[entry.ApprovalId] = entry;
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Graph embedded 维护审计文件第 {lineNumber} 行损坏。", exception);
            }
        }
    }

    private void AppendEmbeddedAudit(GraphMaintenanceApprovalDto entry)
    {
        if (_embeddedAuditPath is null)
            throw new InvalidOperationException("Graph embedded 维护审计路径尚未初始化。");
        ValidateEmbeddedAuditEntry(entry, lineNumber: null);
        using var buffer = new MemoryStream();
        JsonSerializer.Serialize(buffer, entry, RemoteJsonContext.Default.GraphMaintenanceApprovalDto);
        buffer.WriteByte((byte)'\n');
        using var stream = new FileStream(
            _embeddedAuditPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        long originalLength = stream.Length;
        stream.Position = originalLength;
        try
        {
            buffer.Position = 0;
            buffer.CopyTo(stream);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            try
            {
                stream.SetLength(originalLength);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                // 保留原始写异常；下次打开会拒绝损坏的审计文件。
            }
            throw;
        }
        _embeddedAudit.Add(entry);
        _embeddedApprovals[entry.ApprovalId] = entry;
    }

    private static void ValidateEmbeddedAuditEntry(GraphMaintenanceApprovalDto entry, int? lineNumber)
    {
        bool valid = entry.ApprovalId != Guid.Empty
            && entry.OccurredAtUtc != default
            && !string.IsNullOrWhiteSpace(entry.Database)
            && !string.IsNullOrWhiteSpace(entry.Graph)
            && Enum.IsDefined(entry.Action)
            && !string.IsNullOrWhiteSpace(entry.State)
            && !string.IsNullOrWhiteSpace(entry.Principal)
            && entry.ExpiresAtUtc != default
            && entry.MaxWorkUnits is >= 1 and <= 4_096;
        if (valid)
            return;
        string location = lineNumber is null ? string.Empty : $"第 {lineNumber.Value} 行";
        throw new InvalidDataException($"Graph embedded 维护审计记录{location}字段无效。");
    }

    private static GraphMaintenanceExecutionDto ExecuteEmbeddedMaintenance(
        GraphStore store,
        GraphMaintenanceApprovalDto approval,
        CancellationToken cancellationToken)
    {
        if (approval.Action == GraphMaintenanceAction.RepairRebuild)
        {
            GraphMaintenanceResult result = store.RunMaintenance(
                new GraphMaintenanceOptions
                {
                    MaxWorkUnits = approval.MaxWorkUnits,
                    CompactOnCompletion = approval.CompactOnCompletion,
                },
                cancellationToken);
            return new GraphMaintenanceExecutionDto
            {
                Action = approval.Action,
                IsComplete = result.IsComplete,
                OperationId = result.OperationId,
                Phase = result.Phase.ToString(),
                Sequence = result.Sequence,
                ScannedRecords = result.ScannedRecords,
                RepairedEntries = result.RepairedEntries,
                RemovedEntries = result.RemovedEntries,
                WorkUnits = result.WorkUnits,
            };
        }
        long sequence = approval.Action switch
        {
            GraphMaintenanceAction.Checkpoint => store.Checkpoint(),
            GraphMaintenanceAction.Compact => store.Compact(),
            _ => throw new ArgumentOutOfRangeException(nameof(approval)),
        };
        return new GraphMaintenanceExecutionDto
        {
            Action = approval.Action,
            IsComplete = true,
            Sequence = sequence,
        };
    }

    private static void ValidateMaintenanceRequest(GraphMaintenanceStageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Action))
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.MaxWorkUnits is < 1 or > 4_096)
            throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Action != GraphMaintenanceAction.RepairRebuild && request.CompactOnCompletion)
            throw new ArgumentException("CompactOnCompletion 只适用于 repair/rebuild。", nameof(request));
    }

    private static GraphOperationsCapabilitiesDto CreateOperationsCapabilities(
        bool slowTraversalDiagnostics,
        bool audit)
        => new(
            SchemaAndIndexes: true,
            DegreeHistogram: true,
            SlowTraversalDiagnostics: slowTraversalDiagnostics,
            BoundedVisualization: true,
            RestrictedEditing: true,
            JsonImportExport: true,
            StagedMaintenance: true,
            Audit: audit);

    private static GraphVertexDto ToDto(GraphVertex vertex)
        => new()
        {
            Id = vertex.Id.Value,
            ElementVersion = vertex.ElementVersion,
            Labels = vertex.Labels.Select(static label => label.Value).ToArray(),
            Properties = vertex.Properties.Select(ToDto).ToArray(),
        };

    private static GraphEdgeDto ToDto(GraphEdge edge)
        => new()
        {
            Id = edge.Id.Value,
            ElementVersion = edge.ElementVersion,
            SourceId = edge.SourceId.Value,
            TargetId = edge.TargetId.Value,
            LabelId = edge.LabelId.Value,
            Properties = edge.Properties.Select(ToDto).ToArray(),
        };

    private static GraphPropertyDto ToDto(GraphProperty property)
        => new() { PropertyId = property.PropertyId, Value = ToDto(property.Value) };

    private static GraphValueDto ToDto(GraphPropertyValue value)
        => value.Kind switch
        {
            GraphPropertyKind.Null => new GraphValueDto { Kind = value.Kind },
            GraphPropertyKind.Int64 => new GraphValueDto { Kind = value.Kind, Int64 = value.AsInt64() },
            GraphPropertyKind.Float64 => new GraphValueDto { Kind = value.Kind, Float64 = value.AsFloat64() },
            GraphPropertyKind.Boolean => new GraphValueDto { Kind = value.Kind, Boolean = value.AsBoolean() },
            GraphPropertyKind.String => new GraphValueDto { Kind = value.Kind, String = value.AsString() },
            GraphPropertyKind.DateTime => new GraphValueDto { Kind = value.Kind, DateTime = value.AsDateTime() },
            GraphPropertyKind.Blob => new GraphValueDto { Kind = value.Kind, BlobBase64 = Convert.ToBase64String(value.AsBlob()) },
            GraphPropertyKind.Json => new GraphValueDto { Kind = value.Kind, Json = value.AsJson() },
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    private string GraphsUrl() => $"v1/db/{Uri.EscapeDataString(_database)}/graphs";
    private string GraphUrl(string graph) => GraphsUrl() + "/" + Uri.EscapeDataString(graph);
    private string VertexUrl(string graph, GraphElementId id) => GraphUrl(graph) + "/vertices/" + id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string EdgeUrl(string graph, GraphElementId id) => GraphUrl(graph) + "/edges/" + id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private string StreamExpandUrl(string graph) => GraphUrl(graph) + "/expand/stream";
    private string VertexSeekUrl(string graph) => GraphUrl(graph) + "/vertices/seek/stream";
    private string EdgeSeekUrl(string graph) => GraphUrl(graph) + "/edges/seek/stream";
    private string TraverseUrl(string graph) => GraphUrl(graph) + "/traverse/stream";
    private string ShortestPathUrl(string graph) => GraphUrl(graph) + "/shortest-path";
    private string WeightedShortestPathUrl(string graph) => GraphUrl(graph) + "/weighted-shortest-path";
    private string ImportUrl(string graph) => GraphUrl(graph) + "/import";
    private string OperationsUrl(string graph) => GraphUrl(graph) + "/operations";
    private string MaintenanceUrl(string graph) => GraphUrl(graph) + "/maintenance";

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
            || request.EdgeLabelId is <= 0 || request.MaxDepth is < 0 or > 64
            || request.MaxFrontier is <= 0 or > 10_000 || request.MaxPaths is <= 0 or > 10_000)
            throw new ArgumentException("Shortest path 的端点、方向、深度或执行预算无效。", nameof(request));
    }

    private static void ValidateWeightedShortestPathRequest(GraphWeightedShortestPathRequest request)
    {
        if (request.StartId <= 0 || request.TargetId <= 0 || request.WeightPropertyId <= 0
            || !Enum.IsDefined(request.Algorithm) || !Enum.IsDefined(request.Direction)
            || request.EdgeLabelId is <= 0
            || request.MaxDepth is < 0 or > 64
            || request.MaxFrontier is <= 0 or > 100_000
            || request.MaxVisitedVertices is <= 0 or > 1_000_000
            || request.MaxExpandedEdges is <= 0 or > 10_000_000
            || request.PageSize is <= 0 or > 1_000
            || request.MaxPageBytes is <= 0 or > 128 * 1024 * 1024)
            throw new ArgumentException("加权 shortest path 的权重属性、端点、算法或执行预算无效。", nameof(request));
        if (request.MaxTotalWeight is { } maxWeight
            && (!double.IsFinite(maxWeight) || maxWeight < 0))
            throw new ArgumentException("加权 shortest path 的 MaxTotalWeight 必须为空或非负有限数。", nameof(request));
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
