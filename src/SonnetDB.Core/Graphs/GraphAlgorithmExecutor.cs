namespace SonnetDB.Graphs;

/// <summary>一次加权图算法查询的端点。</summary>
/// <param name="StartId">查询起点。</param>
/// <param name="TargetId">查询目标。</param>
public readonly record struct GraphWeightedPathQuery(
    GraphElementId StartId,
    GraphElementId TargetId);

/// <summary>批量图算法执行预算。</summary>
public sealed record GraphAlgorithmBatchOptions
{
    /// <summary>一次批量最多接受的查询数。</summary>
    public int MaxQueries { get; init; } = 1_000;

    /// <summary>批量结果最多保留的条目数。</summary>
    public int MaxResults { get; init; } = 1_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxQueries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxResults);
    }
}

/// <summary>批量加权路径执行结果。</summary>
public sealed class GraphWeightedPathBatchResult
{
    internal GraphWeightedPathBatchResult(
        IReadOnlyList<GraphWeightedPath?> paths,
        int processedQueries,
        long expandedVertices,
        long expandedEdges)
    {
        Paths = paths.ToArray();
        ProcessedQueries = processedQueries;
        ExpandedVertices = expandedVertices;
        ExpandedEdges = expandedEdges;
    }

    /// <summary>按输入顺序排列的路径；不可达查询对应 null。</summary>
    public IReadOnlyList<GraphWeightedPath?> Paths { get; }

    /// <summary>已经完成的查询数。</summary>
    public int ProcessedQueries { get; }

    /// <summary>所有查询累计从 frontier 取出的顶点数。</summary>
    public long ExpandedVertices { get; }

    /// <summary>所有查询累计检查过的邻接边数。</summary>
    public long ExpandedEdges { get; }
}

/// <summary>共享 GraphReadSession 的批量算法执行入口。</summary>
public static class GraphAlgorithmExecutor
{
    /// <summary>
    /// 在同一个 statement snapshot 上按输入顺序执行多条加权最短路径查询。
    /// 每条查询独立遵守其路径预算，取消会停止后续工作并抛出取消异常。
    /// </summary>
    /// <param name="session">共享的稳定图读会话。</param>
    /// <param name="queries">加权路径查询端点。</param>
    /// <param name="pathOptions">每条查询使用的权重、算法和路径预算。</param>
    /// <param name="batchOptions">批量条目预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按输入顺序排列的批量结果和累计诊断计数。</returns>
    public static GraphWeightedPathBatchResult ExecuteShortestPaths(
        GraphReadSession session,
        IEnumerable<GraphWeightedPathQuery> queries,
        GraphWeightedShortestPathOptions pathOptions,
        GraphAlgorithmBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(pathOptions);
        pathOptions.Validate();
        batchOptions ??= new GraphAlgorithmBatchOptions();
        batchOptions.Validate();

        var paths = new List<GraphWeightedPath?>();
        int queryLimit = Math.Min(batchOptions.MaxQueries, batchOptions.MaxResults);
        long expandedVertices = 0;
        long expandedEdges = 0;
        foreach (GraphWeightedPathQuery query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (paths.Count >= queryLimit)
                throw new GraphWeightedPathLimitExceededException(
                    $"批量图算法查询数超过上限 {queryLimit}。");
            GraphWeightedPath? path = session.WeightedShortestPath(
                query.StartId,
                query.TargetId,
                pathOptions,
                cancellationToken);
            paths.Add(path);
            if (path is not null)
            {
                expandedVertices = checked(expandedVertices + path.ExpandedVertices);
                expandedEdges = checked(expandedEdges + path.ExpandedEdges);
            }
        }

        return new GraphWeightedPathBatchResult(paths, paths.Count, expandedVertices, expandedEdges);
    }

    /// <summary>ExecuteShortestPaths 的简短别名。</summary>
    /// <param name="session">共享的稳定图读会话。</param>
    /// <param name="queries">加权路径查询端点。</param>
    /// <param name="pathOptions">每条查询使用的权重、算法和路径预算。</param>
    /// <param name="batchOptions">批量条目预算。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按输入顺序排列的批量结果。</returns>
    public static GraphWeightedPathBatchResult RunShortestPaths(
        GraphReadSession session,
        IEnumerable<GraphWeightedPathQuery> queries,
        GraphWeightedShortestPathOptions pathOptions,
        GraphAlgorithmBatchOptions? batchOptions = null,
        CancellationToken cancellationToken = default)
        => ExecuteShortestPaths(session, queries, pathOptions, batchOptions, cancellationToken);
}
