using SonnetDB.Query;

namespace SonnetDB.Catalog;

/// <summary>
/// 向量索引类型。
/// </summary>
public enum VectorIndexKind : byte
{
    /// <summary>
    /// HNSW（Hierarchical Navigable Small World）图索引。
    /// </summary>
    Hnsw = 1,

    /// <summary>
    /// IVF-Flat 倒排文件索引。
    /// </summary>
    IvfFlat = 2,

    /// <summary>
    /// IVF-PQ 倒排文件 + 乘积量化索引。
    /// </summary>
    IvfPq = 3,

    /// <summary>
    /// Vamana / DiskANN 单层图索引。
    /// </summary>
    Vamana = 4,
}

/// <summary>
/// HNSW 索引参数。
/// </summary>
/// <param name="M">每个节点在每层保留的最大邻接数。</param>
/// <param name="Ef">查询时（efSearch）使用的候选规模。</param>
/// <param name="EfConstruction">
/// 建图时（efConstruction）使用的候选规模，与 <paramref name="Ef"/> 解耦。
/// 越大图质量越高、构建越慢；一旦建图完成便不再参与检索。默认取 <c>max(Ef, 200)</c>，
/// 保证建图质量不低于检索精度，避免小 <paramref name="Ef"/> 把低质量图永久烤进持久化 blob（缺陷 I9）。
/// </param>
public sealed record HnswVectorIndexOptions(int M, int Ef, int EfConstruction);

/// <summary>
/// IVF-Flat 索引参数。
/// </summary>
/// <param name="NList">倒排列表数量。</param>
/// <param name="NProbe">搜索时探测的倒排列表数量。</param>
/// <param name="MaxIterations">K-Means 最大迭代次数。</param>
public sealed record IvfVectorIndexOptions(int NList, int NProbe, int MaxIterations);

/// <summary>
/// IVF-PQ 索引参数。
/// </summary>
/// <param name="NList">倒排列表数量。</param>
/// <param name="NProbe">搜索时探测的倒排列表数量。</param>
/// <param name="MaxIterations">K-Means 最大迭代次数。</param>
/// <param name="M">PQ 子空间数量。</param>
/// <param name="NBits">每个子量化器码本位数。</param>
public sealed record IvfPqVectorIndexOptions(int NList, int NProbe, int MaxIterations, int M, int NBits);

/// <summary>
/// Vamana / DiskANN 索引参数。
/// </summary>
/// <param name="MaxDegree">每个节点最大邻居数。</param>
/// <param name="SearchListSize">构建和搜索候选列表大小。</param>
/// <param name="Alpha">RobustPrune alpha。</param>
/// <param name="BeamWidth">BeamSearch 束宽。</param>
public sealed record VamanaVectorIndexOptions(int MaxDegree, int SearchListSize, float Alpha, int BeamWidth);

/// <summary>
/// Measurement 中某个 VECTOR 列的索引定义。
/// </summary>
/// <param name="Kind">索引类型。</param>
/// <param name="Metric">
/// 建图与检索使用的距离度量。建图时按此度量组织图 / 倒排结构，检索时只有查询度量与本度量一致才走 ANN 加速，
/// 否则回退暴力扫描（缺陷 I7：此前一律按 cosine 建图且 ANN gate 仅 cosine，非 cosine 索引白占空间仍暴力扫）。
/// </param>
/// <param name="Hnsw">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.Hnsw"/> 时的参数。</param>
/// <param name="Ivf">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.IvfFlat"/> 时的参数。</param>
/// <param name="IvfPq">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.IvfPq"/> 时的参数。</param>
/// <param name="Vamana">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.Vamana"/> 时的参数。</param>
public sealed record VectorIndexDefinition(
    VectorIndexKind Kind,
    KnnMetric Metric = KnnMetric.Cosine,
    HnswVectorIndexOptions? Hnsw = null,
    IvfVectorIndexOptions? Ivf = null,
    IvfPqVectorIndexOptions? IvfPq = null,
    VamanaVectorIndexOptions? Vamana = null)
{
    /// <summary>
    /// 创建 HNSW 索引定义。
    /// </summary>
    /// <param name="m">每个节点在每层保留的最大邻接数。</param>
    /// <param name="ef">检索时（efSearch）使用的候选规模。</param>
    /// <param name="metric">距离度量，默认 cosine。</param>
    /// <param name="efConstruction">
    /// 建图时（efConstruction）使用的候选规模；<c>null</c> 时取 <c>max(ef, 200)</c>，与 <paramref name="ef"/> 解耦（I9）。
    /// </param>
    /// <returns>HNSW 索引定义。</returns>
    public static VectorIndexDefinition CreateHnsw(int m, int ef, KnnMetric metric = KnnMetric.Cosine, int? efConstruction = null)
        => new(VectorIndexKind.Hnsw, metric, Hnsw: new HnswVectorIndexOptions(m, ef, efConstruction ?? Math.Max(ef, 200)));

    /// <summary>
    /// 创建 IVF-Flat 索引定义。
    /// </summary>
    public static VectorIndexDefinition CreateIvfFlat(int nList, int nProbe, int maxIterations, KnnMetric metric = KnnMetric.Cosine)
        => new(VectorIndexKind.IvfFlat, metric, Ivf: new IvfVectorIndexOptions(nList, nProbe, maxIterations));

    /// <summary>
    /// 创建 IVF-PQ 索引定义。
    /// </summary>
    public static VectorIndexDefinition CreateIvfPq(int nList, int nProbe, int maxIterations, int m, int nBits, KnnMetric metric = KnnMetric.Cosine)
        => new(VectorIndexKind.IvfPq, metric, IvfPq: new IvfPqVectorIndexOptions(nList, nProbe, maxIterations, m, nBits));

    /// <summary>
    /// 创建 Vamana 索引定义。
    /// </summary>
    public static VectorIndexDefinition CreateVamana(int maxDegree, int searchListSize, float alpha, int beamWidth, KnnMetric metric = KnnMetric.Cosine)
        => new(VectorIndexKind.Vamana, metric, Vamana: new VamanaVectorIndexOptions(maxDegree, searchListSize, alpha, beamWidth));
}
