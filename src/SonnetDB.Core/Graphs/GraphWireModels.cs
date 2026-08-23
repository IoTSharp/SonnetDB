namespace SonnetDB.Graphs;

/// <summary>HTTP/SDK 图属性值的显式类型编码。</summary>
public sealed record GraphValueDto
{
    /// <summary>属性类型。</summary>
    public required GraphPropertyKind Kind { get; init; }

    /// <summary>Int64 值。</summary>
    public long? Int64 { get; init; }

    /// <summary>Float64 值。</summary>
    public double? Float64 { get; init; }

    /// <summary>Boolean 值。</summary>
    public bool? Boolean { get; init; }

    /// <summary>String 值。</summary>
    public string? String { get; init; }

    /// <summary>DateTime 值。</summary>
    public DateTimeOffset? DateTime { get; init; }

    /// <summary>Blob 的 base64 文本。</summary>
    public string? BlobBase64 { get; init; }

    /// <summary>原始 JSON 文本。</summary>
    public string? Json { get; init; }
}

/// <summary>HTTP/SDK 图属性 DTO。</summary>
public sealed record GraphPropertyDto
{
    /// <summary>属性标识符。</summary>
    public int PropertyId { get; init; }

    /// <summary>属性值。</summary>
    public required GraphValueDto Value { get; init; }
}

/// <summary>HTTP/SDK 顶点 DTO。</summary>
public sealed record GraphVertexDto
{
    /// <summary>顶点标识符。</summary>
    public long Id { get; init; }

    /// <summary>元素版本。</summary>
    public long ElementVersion { get; init; }

    /// <summary>标签标识符。</summary>
    public IReadOnlyList<int> Labels { get; init; } = [];

    /// <summary>属性列表。</summary>
    public IReadOnlyList<GraphPropertyDto> Properties { get; init; } = [];
}

/// <summary>HTTP/SDK 边 DTO。</summary>
public sealed record GraphEdgeDto
{
    /// <summary>边标识符。</summary>
    public long Id { get; init; }

    /// <summary>元素版本。</summary>
    public long ElementVersion { get; init; }

    /// <summary>源顶点标识符。</summary>
    public long SourceId { get; init; }

    /// <summary>目标顶点标识符。</summary>
    public long TargetId { get; init; }

    /// <summary>边标签标识符。</summary>
    public int LabelId { get; init; }

    /// <summary>属性列表。</summary>
    public IReadOnlyList<GraphPropertyDto> Properties { get; init; } = [];
}

/// <summary>HTTP/SDK 邻接扩展结果 DTO。</summary>
public sealed record GraphExpansionDto
{
    /// <summary>扩展锚点。</summary>
    public long AnchorId { get; init; }

    /// <summary>相邻顶点。</summary>
    public long NeighborId { get; init; }

    /// <summary>扩展方向。</summary>
    public GraphDirection Direction { get; init; }

    /// <summary>命中的边。</summary>
    public required GraphEdgeDto Edge { get; init; }
}

/// <summary>Graph vertex upsert 请求。</summary>
public sealed record GraphUpsertVertexRequest
{
    /// <summary>顶点标识符。</summary>
    public long Id { get; init; }

    /// <summary>预期元素版本。</summary>
    public long ExpectedElementVersion { get; init; }

    /// <summary>标签标识符。</summary>
    public IReadOnlyList<int> Labels { get; init; } = [];

    /// <summary>属性列表。</summary>
    public IReadOnlyList<GraphPropertyDto> Properties { get; init; } = [];

    /// <summary>需要保持唯一的属性标识符。</summary>
    public IReadOnlyList<int> UniquePropertyIds { get; init; } = [];

    /// <summary>幂等 request ID。</summary>
    public Guid RequestId { get; init; }
}

/// <summary>Graph edge upsert 请求。</summary>
public sealed record GraphUpsertEdgeRequest
{
    /// <summary>边标识符。</summary>
    public long Id { get; init; }

    /// <summary>预期元素版本。</summary>
    public long ExpectedElementVersion { get; init; }

    /// <summary>源顶点标识符。</summary>
    public long SourceId { get; init; }

    /// <summary>目标顶点标识符。</summary>
    public long TargetId { get; init; }

    /// <summary>边标签标识符。</summary>
    public int LabelId { get; init; }

    /// <summary>属性列表。</summary>
    public IReadOnlyList<GraphPropertyDto> Properties { get; init; } = [];

    /// <summary>需要保持唯一的属性标识符。</summary>
    public IReadOnlyList<int> UniquePropertyIds { get; init; } = [];

    /// <summary>幂等 request ID。</summary>
    public Guid RequestId { get; init; }
}

/// <summary>Graph adjacency expand 请求。</summary>
public sealed record GraphExpandRequest
{
    /// <summary>扩展锚点。</summary>
    public long VertexId { get; init; }

    /// <summary>扩展方向。</summary>
    public GraphDirection Direction { get; init; } = GraphDirection.Outgoing;

    /// <summary>可选边标签。</summary>
    public int? EdgeLabelId { get; init; }

    /// <summary>目标顶点必须包含的可选标签。</summary>
    public int? TargetLabelId { get; init; }

    /// <summary>目标顶点必须包含的可选属性标识符。</summary>
    public int? TargetPropertyId { get; init; }

    /// <summary>目标顶点属性必须精确匹配的可选值。</summary>
    public GraphValueDto? TargetPropertyValue { get; init; }

    /// <summary>分页大小。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>结果上限。</summary>
    public int MaxResults { get; init; } = 10_000;
}

/// <summary>Graph mutation 响应。</summary>
public sealed record GraphMutationResponse
{
    /// <summary>事务序列号。</summary>
    public long Sequence { get; init; }

    /// <summary>是否为重复 request ID 的幂等重试。</summary>
    public bool IsDuplicate { get; init; }
}

/// <summary>创建图请求。</summary>
public sealed record GraphCreateRequest
{
    /// <summary>图名称。</summary>
    public required string Name { get; init; }
}

/// <summary>图目录摘要。</summary>
public sealed record GraphInfoDto
{
    /// <summary>图名称。</summary>
    public required string Name { get; init; }

    /// <summary>物理存储标识。</summary>
    public Guid StorageId { get; init; }

    /// <summary>记录格式版本。</summary>
    public int RecordFormatVersion { get; init; }
}

/// <summary>图删除请求。</summary>
public sealed record GraphDeleteRequest
{
    /// <summary>预期元素版本。</summary>
    public long ExpectedElementVersion { get; init; }

    /// <summary>幂等 request ID。</summary>
    public Guid RequestId { get; init; }
}

/// <summary>Graph expand 分页响应。</summary>
public sealed record GraphExpandResponse
{
    /// <summary>快照序列号。</summary>
    public long SnapshotSequence { get; init; }

    /// <summary>本页结果。</summary>
    public IReadOnlyList<GraphExpansionDto> Items { get; init; } = [];

    /// <summary>是否已读完。</summary>
    public bool IsExhausted { get; init; }
}

/// <summary>Graph label/property seek 请求。</summary>
public sealed record GraphSeekRequest
{
    /// <summary>标签标识符。</summary>
    public int LabelId { get; init; }

    /// <summary>可选属性标识符；省略时只按 label seek。</summary>
    public int? PropertyId { get; init; }

    /// <summary>与属性标识符同时提供的精确匹配值。</summary>
    public GraphValueDto? Value { get; init; }

    /// <summary>底层读取页大小。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>结果上限。</summary>
    public int MaxResults { get; init; } = 10_000;
}

/// <summary>远程 Graph 遍历模式。</summary>
public enum GraphTraversalKind : byte
{
    /// <summary>广度优先遍历。</summary>
    BreadthFirst = 1,

    /// <summary>深度优先遍历。</summary>
    DepthFirst = 2,

    /// <summary>按显式最小和最大深度枚举路径。</summary>
    Paths = 3,
}

/// <summary>Graph BFS、DFS 或受限路径请求。</summary>
public sealed record GraphTraversalRequest
{
    /// <summary>起点标识符。</summary>
    public long StartId { get; init; }

    /// <summary>遍历模式。</summary>
    public GraphTraversalKind Kind { get; init; } = GraphTraversalKind.BreadthFirst;

    /// <summary>Paths 模式的最小深度。</summary>
    public int MinDepth { get; init; }

    /// <summary>最大 hop 深度。</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>扩展方向。</summary>
    public GraphDirection Direction { get; init; } = GraphDirection.Outgoing;

    /// <summary>可选边标签。</summary>
    public int? EdgeLabelId { get; init; }

    /// <summary>frontier 条目上限。</summary>
    public int MaxFrontier { get; init; } = 10_000;

    /// <summary>路径结果上限。</summary>
    public int MaxPaths { get; init; } = 10_000;

    /// <summary>路径去重策略。</summary>
    public GraphPathUniqueness PathUniqueness { get; init; } = GraphPathUniqueness.Vertex;

    /// <summary>结果页大小。</summary>
    public int PageSize { get; init; } = 128;
}

/// <summary>Graph shortest path 请求。</summary>
public sealed record GraphShortestPathRequest
{
    /// <summary>起点标识符。</summary>
    public long StartId { get; init; }

    /// <summary>目标标识符。</summary>
    public long TargetId { get; init; }

    /// <summary>最大 hop 深度。</summary>
    public int MaxDepth { get; init; } = 6;

    /// <summary>扩展方向。</summary>
    public GraphDirection Direction { get; init; } = GraphDirection.Outgoing;

    /// <summary>可选边标签。</summary>
    public int? EdgeLabelId { get; init; }

    /// <summary>frontier 条目上限。</summary>
    public int MaxFrontier { get; init; } = 10_000;

    /// <summary>确认可达性前允许检查的路径上限。</summary>
    public int MaxPaths { get; init; } = 10_000;
}

/// <summary>HTTP/SDK 图路径 DTO。</summary>
public sealed record GraphPathDto
{
    /// <summary>按路径顺序排列的顶点标识符。</summary>
    public IReadOnlyList<long> VertexIds { get; init; } = [];

    /// <summary>按路径顺序排列的边标识符。</summary>
    public IReadOnlyList<long> EdgeIds { get; init; } = [];
}

/// <summary>Shortest path 响应。</summary>
public sealed record GraphShortestPathResponse
{
    /// <summary>查询使用的稳定快照序列号。</summary>
    public long SnapshotSequence { get; init; }

    /// <summary>找到的最短路径；不存在时为 null。</summary>
    public GraphPathDto? Path { get; init; }
}

/// <summary>Graph 加权最短路径请求。</summary>
public sealed record GraphWeightedShortestPathRequest
{
    /// <summary>起点标识符。</summary>
    public long StartId { get; init; }

    /// <summary>目标标识符。</summary>
    public long TargetId { get; init; }

    /// <summary>边权重属性标识符。</summary>
    public int WeightPropertyId { get; init; }

    /// <summary>使用的加权路径算法。</summary>
    public GraphWeightedShortestPathAlgorithm Algorithm { get; init; } = GraphWeightedShortestPathAlgorithm.Dijkstra;

    /// <summary>扩展方向。</summary>
    public GraphDirection Direction { get; init; } = GraphDirection.Outgoing;

    /// <summary>可选边标签过滤。</summary>
    public int? EdgeLabelId { get; init; }

    /// <summary>最大 hop 数。</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>frontier 条目上限。</summary>
    public int MaxFrontier { get; init; } = 10_000;

    /// <summary>访问顶点上限。</summary>
    public int MaxVisitedVertices { get; init; } = 1_000_000;

    /// <summary>检查邻接边上限。</summary>
    public long MaxExpandedEdges { get; init; } = 10_000_000;

    /// <summary>可选的路径总权重上限。</summary>
    public double? MaxTotalWeight { get; init; }

    /// <summary>邻接页大小。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>邻接页 payload 字节上限。</summary>
    public int MaxPageBytes { get; init; } = 32 * 1024 * 1024;
}

/// <summary>Graph 加权最短路径响应。</summary>
public sealed record GraphWeightedShortestPathResponse
{
    /// <summary>查询使用的稳定快照序列号。</summary>
    public long SnapshotSequence { get; init; }

    /// <summary>实际使用的算法。</summary>
    public GraphWeightedShortestPathAlgorithm Algorithm { get; init; }

    /// <summary>路径总权重；没有路径时为空。</summary>
    public double? TotalWeight { get; init; }

    /// <summary>从 frontier 取出的顶点数。</summary>
    public int ExpandedVertices { get; init; }

    /// <summary>检查过的邻接边数。</summary>
    public long ExpandedEdges { get; init; }

    /// <summary>找到的路径；不可达时为 null。</summary>
    public GraphPathDto? Path { get; init; }
}

/// <summary>批量导入顶点。</summary>
public sealed record GraphImportVertexDto
{
    /// <summary>顶点标识符。</summary>
    public long Id { get; init; }

    /// <summary>标签标识符。</summary>
    public IReadOnlyList<int> Labels { get; init; } = [];

    /// <summary>属性列表。</summary>
    public IReadOnlyList<GraphPropertyDto> Properties { get; init; } = [];

    /// <summary>唯一属性标识符。</summary>
    public IReadOnlyList<int> UniquePropertyIds { get; init; } = [];

    /// <summary>更新时的预期版本。</summary>
    public long ExpectedElementVersion { get; init; }
}

/// <summary>批量导入边。</summary>
public sealed record GraphImportEdgeDto
{
    /// <summary>边标识符。</summary>
    public long Id { get; init; }

    /// <summary>源顶点标识符。</summary>
    public long SourceId { get; init; }

    /// <summary>目标顶点标识符。</summary>
    public long TargetId { get; init; }

    /// <summary>边标签标识符。</summary>
    public int LabelId { get; init; }

    /// <summary>属性列表。</summary>
    public IReadOnlyList<GraphPropertyDto> Properties { get; init; } = [];

    /// <summary>唯一属性标识符。</summary>
    public IReadOnlyList<int> UniquePropertyIds { get; init; } = [];

    /// <summary>更新时的预期版本。</summary>
    public long ExpectedElementVersion { get; init; }
}

/// <summary>有界、幂等 Graph 批量导入请求。</summary>
public sealed record GraphImportRequest
{
    /// <summary>批次幂等 request ID。</summary>
    public Guid RequestId { get; init; }

    /// <summary>native 格式顶点列表。</summary>
    public IReadOnlyList<GraphImportVertexDto> Vertices { get; init; } = [];

    /// <summary>native 格式边列表。</summary>
    public IReadOnlyList<GraphImportEdgeDto> Edges { get; init; } = [];

    /// <summary>Graphify graph.json 的 nodes 别名。</summary>
    public IReadOnlyList<GraphImportVertexDto> Nodes { get; init; } = [];

    /// <summary>Graphify graph.json 的 relationships 别名。</summary>
    public IReadOnlyList<GraphImportEdgeDto> Relationships { get; init; } = [];
}

/// <summary>批量导入结果。</summary>
public sealed record GraphImportResponse
{
    /// <summary>提交序列号。</summary>
    public long Sequence { get; init; }

    /// <summary>是否解析为重复批次。</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>导入的顶点数。</summary>
    public int VertexCount { get; init; }

    /// <summary>导入的边数。</summary>
    public int EdgeCount { get; init; }
}
