using SonnetDB.Vector.Index.Hnsw;

namespace SonnetDB.Vector.Indexing;

/// <summary>
/// HNSW 向量索引参数。
/// </summary>
/// <param name="M">每个节点在每层保留的最大邻接数。</param>
/// <param name="EfConstruction">构建索引时的候选集大小。</param>
/// <param name="EfSearch">查询时的候选集大小。</param>
/// <param name="Seed">可选随机种子，用于确定性建图。</param>
public sealed record VectorIndexHnswOptions(
    int M = 16,
    int EfConstruction = 200,
    int EfSearch = 50,
    int? Seed = null)
{
    /// <summary>
    /// 默认 HNSW 参数。
    /// </summary>
    public static VectorIndexHnswOptions Default { get; } = new();

    internal HnswOptions ToHnswOptions()
        => new()
        {
            M = M,
            EfConstruction = EfConstruction,
            EfSearch = EfSearch,
            Seed = Seed,
        };
}
