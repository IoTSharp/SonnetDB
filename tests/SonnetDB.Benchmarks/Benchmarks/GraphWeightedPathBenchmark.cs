using BenchmarkDotNet.Attributes;
using SonnetDB.Graphs;
using SonnetDB.Kv;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M40 #362 设备拓扑加权路径基准，对拍 Dijkstra、A* 和双向 Dijkstra。
/// </summary>
/// <remarks>
/// fixture 使用双向网格表示带权设备链路。查询沿中间横向链路路由，网格其余行作为真实拓扑中的
/// 可达支路；A* 使用可采纳且一致的 Manhattan 启发式，双向搜索不使用额外索引或旁路数据。
/// </remarks>
[MemoryDiagnoser]
[BenchmarkCategory("M40", "Graph", "WeightedPath")]
public class GraphWeightedPathBenchmark
{
    private GraphWeightedPathBenchmarkFixture? _fixture;

    /// <summary>拓扑网格边长。</summary>
    [Params(32, 64)]
    public int Side { get; set; } = 32;

    /// <summary>创建持久化 topology fixture 并预验三种算法的结果。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _fixture = new GraphWeightedPathBenchmarkFixture(Side);
        _fixture.ValidateAlgorithms();
    }

    /// <summary>测量单向 Dijkstra 基线。</summary>
    /// <returns>路径及扩展计数校验和。</returns>
    [Benchmark(Baseline = true)]
    public long Dijkstra()
        => RequireFixture().ExecuteChecksum(GraphWeightedShortestPathAlgorithm.Dijkstra);

    /// <summary>测量带 Manhattan 启发式的 A*。</summary>
    /// <returns>路径及扩展计数校验和。</returns>
    [Benchmark]
    public long AStar()
        => RequireFixture().ExecuteChecksum(GraphWeightedShortestPathAlgorithm.AStar);

    /// <summary>测量双向 Dijkstra。</summary>
    /// <returns>路径及扩展计数校验和。</returns>
    [Benchmark]
    public long BidirectionalDijkstra()
        => RequireFixture().ExecuteChecksum(GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra);

    /// <summary>释放 snapshot、graph manager 和临时数据集。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _fixture?.Dispose();
        _fixture = null;
    }

    internal GraphWeightedPathBenchmarkFixture RequireFixture()
        => _fixture ?? throw new InvalidOperationException("请先调用 Setup 创建 Graph benchmark fixture。");
}

internal sealed class GraphWeightedPathBenchmarkFixture : IDisposable
{
    internal const int WeightPropertyId = 1;
    internal const string DatasetName = "gj-topology-weighted-route-v1";
    internal const string Seed = "0x534F4E4E45544442";
    private const int MutationBatchSize = 256;
    private static readonly LabelId VertexLabelId = new(1);
    private static readonly LabelId LinkLabelId = new(2);
    private readonly string _rootDirectory;
    private readonly GraphManager _manager;
    private readonly GraphReadSession _readSession;
    private bool _disposed;

    internal GraphWeightedPathBenchmarkFixture(int side)
    {
        if (side < 8)
            throw new ArgumentOutOfRangeException(nameof(side), "加权路径 topology fixture 的边长至少为 8。");

        Side = side;
        VertexCount = checked(side * side);
        EdgeCount = checked(4 * side * (side - 1));
        TargetRow = side / 2;
        StartId = GetVertexId(TargetRow, 0);
        TargetId = GetVertexId(TargetRow, side - 1);
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "sndb-m40-weighted-path-" + Guid.NewGuid().ToString("N"));
        _manager = new GraphManager(
            _rootDirectory,
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
        GraphStore store = _manager.Create("weighted_path");
        SeedVertices(store);
        SeedEdges(store);
        _readSession = store.BeginRead();
    }

    internal int Side { get; }

    internal int VertexCount { get; }

    internal int EdgeCount { get; }

    internal int TargetRow { get; }

    internal GraphElementId StartId { get; }

    internal GraphElementId TargetId { get; }

    internal long SnapshotSequence => _readSession.Sequence;

    internal GraphWeightedPath Execute(GraphWeightedShortestPathAlgorithm algorithm)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        GraphWeightedShortestPathOptions options = GraphWeightedShortestPathOptions.ForProperty(WeightPropertyId) with
        {
            Algorithm = algorithm,
            EdgeLabelId = LinkLabelId,
            Heuristic = algorithm == GraphWeightedShortestPathAlgorithm.AStar ? EstimateRemainingCost : null,
            MaxDepth = checked(Side * 2),
            MaxFrontier = checked(VertexCount * 4),
            MaxVisitedVertices = VertexCount,
            MaxExpandedEdges = checked((long)EdgeCount * 4),
            PageSize = 256,
        };
        return _readSession.WeightedShortestPath(StartId, TargetId, options)
            ?? throw new InvalidDataException($"{algorithm} 未找到固定 topology fixture 的可达路径。");
    }

    internal long ExecuteChecksum(GraphWeightedShortestPathAlgorithm algorithm)
    {
        GraphWeightedPath path = Execute(algorithm);
        return checked(
            path.ExpandedEdges
            + path.ExpandedVertices
            + path.Depth
            + (long)path.TotalWeight
            + path.VertexIds.Count
            + path.EdgeIds.Count);
    }

    internal void ValidateAlgorithms()
    {
        GraphWeightedPath[] paths =
        [
            Execute(GraphWeightedShortestPathAlgorithm.Dijkstra),
            Execute(GraphWeightedShortestPathAlgorithm.AStar),
            Execute(GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra),
        ];
        double expectedWeight = Side - 1;
        int expectedDepth = Side - 1;
        foreach (GraphWeightedPath path in paths)
        {
            if (path.TotalWeight != expectedWeight
                || path.Depth != expectedDepth
                || path.VertexIds[0] != StartId
                || path.VertexIds[^1] != TargetId)
            {
                throw new InvalidDataException(
                    $"{path.Algorithm} 结果不符合固定 topology oracle："
                    + $"weight={path.TotalWeight}, depth={path.Depth}。");
            }
        }
    }

    internal double EstimateRemainingCost(GraphElementId vertexId)
    {
        long zeroBased = vertexId.Value - 1;
        int row = checked((int)(zeroBased / Side));
        int column = checked((int)(zeroBased % Side));
        return Math.Abs(TargetRow - row) + (Side - 1 - column);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _readSession.Dispose();
        _manager.Dispose();
        try
        {
            if (Directory.Exists(_rootDirectory))
                Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 基准清理不能覆盖已经取得的测量结果。
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 句柄短暂存活时由临时目录后续清理。
        }
    }

    private void SeedVertices(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        int buffered = 0;
        for (int id = 1; id <= VertexCount; id++)
        {
            transaction.UpsertVertex(new GraphElementId(id), 0, [VertexLabelId], []);
            buffered++;
            if (buffered != MutationBatchSize && id != VertexCount)
                continue;
            transaction.Commit();
            if (id != VertexCount)
                transaction = store.BeginTransaction(Guid.NewGuid());
            buffered = 0;
        }
    }

    private void SeedEdges(GraphStore store)
    {
        long edgeId = VertexCount + 1L;
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        int buffered = 0;
        for (int row = 0; row < Side; row++)
            for (int column = 0; column < Side; column++)
            {
                GraphElementId current = GetVertexId(row, column);
                if (column + 1 < Side)
                {
                    AddDirectedEdge(transaction, edgeId++, current, GetVertexId(row, column + 1));
                    AddDirectedEdge(transaction, edgeId++, GetVertexId(row, column + 1), current);
                    buffered += 2;
                }
                if (row + 1 < Side)
                {
                    AddDirectedEdge(transaction, edgeId++, current, GetVertexId(row + 1, column));
                    AddDirectedEdge(transaction, edgeId++, GetVertexId(row + 1, column), current);
                    buffered += 2;
                }

                if (buffered < MutationBatchSize)
                    continue;
                transaction.Commit();
                transaction = store.BeginTransaction(Guid.NewGuid());
                buffered = 0;
            }

        if (buffered > 0)
            transaction.Commit();

        if (edgeId != VertexCount + EdgeCount + 1L)
            throw new InvalidDataException("固定 topology fixture 的 edge 数量与生成器合同不一致。");
    }

    private static void AddDirectedEdge(
        GraphTransaction transaction,
        long edgeId,
        GraphElementId sourceId,
        GraphElementId targetId)
        => transaction.UpsertEdge(
            new GraphElementId(edgeId),
            0,
            sourceId,
            targetId,
            LinkLabelId,
            [new GraphProperty(WeightPropertyId, GraphPropertyValue.FromInt64(1))]);

    private GraphElementId GetVertexId(int row, int column)
        => new(checked((long)row * Side + column + 1));
}
