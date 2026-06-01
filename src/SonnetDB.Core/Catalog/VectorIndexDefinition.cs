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
/// <param name="Ef">建图与查询默认使用的候选规模。</param>
public sealed record HnswVectorIndexOptions(int M, int Ef);

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
/// <param name="Hnsw">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.Hnsw"/> 时的参数。</param>
/// <param name="Ivf">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.IvfFlat"/> 时的参数。</param>
/// <param name="IvfPq">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.IvfPq"/> 时的参数。</param>
/// <param name="Vamana">当 <see cref="Kind"/> 为 <see cref="VectorIndexKind.Vamana"/> 时的参数。</param>
public sealed record VectorIndexDefinition(
    VectorIndexKind Kind,
    HnswVectorIndexOptions? Hnsw = null,
    IvfVectorIndexOptions? Ivf = null,
    IvfPqVectorIndexOptions? IvfPq = null,
    VamanaVectorIndexOptions? Vamana = null)
{
    /// <summary>
    /// 创建 HNSW 索引定义。
    /// </summary>
    /// <param name="m">每个节点在每层保留的最大邻接数。</param>
    /// <param name="ef">建图与查询默认使用的候选规模。</param>
    /// <returns>HNSW 索引定义。</returns>
    public static VectorIndexDefinition CreateHnsw(int m, int ef)
        => new(VectorIndexKind.Hnsw, Hnsw: new HnswVectorIndexOptions(m, ef));

    /// <summary>
    /// 创建 IVF-Flat 索引定义。
    /// </summary>
    public static VectorIndexDefinition CreateIvfFlat(int nList, int nProbe, int maxIterations)
        => new(VectorIndexKind.IvfFlat, Ivf: new IvfVectorIndexOptions(nList, nProbe, maxIterations));

    /// <summary>
    /// 创建 IVF-PQ 索引定义。
    /// </summary>
    public static VectorIndexDefinition CreateIvfPq(int nList, int nProbe, int maxIterations, int m, int nBits)
        => new(VectorIndexKind.IvfPq, IvfPq: new IvfPqVectorIndexOptions(nList, nProbe, maxIterations, m, nBits));

    /// <summary>
    /// 创建 Vamana 索引定义。
    /// </summary>
    public static VectorIndexDefinition CreateVamana(int maxDegree, int searchListSize, float alpha, int beamWidth)
        => new(VectorIndexKind.Vamana, Vamana: new VamanaVectorIndexOptions(maxDegree, searchListSize, alpha, beamWidth));
}
