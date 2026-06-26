using SonnetDB.Vector.Primitives;

namespace SonnetDB.Vector.Indexing;

/// <summary>
/// IVF-Flat 向量索引参数。
/// </summary>
/// <param name="NList">倒排列表数量。</param>
/// <param name="NProbe">搜索时探测的倒排列表数量。</param>
/// <param name="MaxIterations">K-Means 最大迭代次数。</param>
/// <param name="Seed">可选随机种子。</param>
public sealed record VectorIndexIvfOptions(
    int NList = 64,
    int NProbe = 8,
    int MaxIterations = 25,
    int? Seed = null);

/// <summary>
/// IVF-PQ 向量索引参数。
/// </summary>
/// <param name="NList">倒排列表数量。</param>
/// <param name="NProbe">搜索时探测的倒排列表数量。</param>
/// <param name="MaxIterations">K-Means 最大迭代次数。</param>
/// <param name="M">PQ 子空间数量。</param>
/// <param name="NBits">每个子量化器码本位数。</param>
/// <param name="Seed">可选随机种子。</param>
public sealed record VectorIndexIvfPqOptions(
    int NList = 64,
    int NProbe = 8,
    int MaxIterations = 25,
    int M = 8,
    int NBits = 8,
    int? Seed = null);

/// <summary>
/// Vamana / DiskANN 向量索引参数。
/// </summary>
/// <param name="MaxDegree">每个节点最大邻居数。</param>
/// <param name="SearchListSize">构建和搜索候选列表大小。</param>
/// <param name="Alpha">RobustPrune alpha。</param>
/// <param name="BeamWidth">BeamSearch 束宽。</param>
/// <param name="Seed">可选随机种子。</param>
public sealed record VectorIndexVamanaOptions(
    int MaxDegree = 32,
    int SearchListSize = 75,
    float Alpha = 1.2f,
    int BeamWidth = 4,
    int? Seed = null);

/// <summary>
/// 向量索引构建输入。
/// </summary>
/// <param name="Algorithm">索引算法。</param>
/// <param name="Metric">KNN 距离度量。</param>
/// <param name="Vectors">行优先连续 float32 向量载荷，长度必须为 <c>Count * Dimension</c>。</param>
/// <param name="Count">向量数量。</param>
/// <param name="Dimension">向量维度。</param>
/// <param name="Hnsw">HNSW 参数；当 <paramref name="Algorithm"/> 为 <see cref="VectorIndexAlgorithm.Hnsw"/> 时使用。</param>
/// <param name="Ivf">IVF-Flat 参数；当 <paramref name="Algorithm"/> 为 <see cref="VectorIndexAlgorithm.IvfFlat"/> 时使用。</param>
/// <param name="IvfPq">IVF-PQ 参数；当 <paramref name="Algorithm"/> 为 <see cref="VectorIndexAlgorithm.IvfPq"/> 时使用。</param>
/// <param name="Vamana">Vamana 参数；当 <paramref name="Algorithm"/> 为 <see cref="VectorIndexAlgorithm.Vamana"/> 时使用。</param>
public sealed record VectorIndexBuildInput(
    VectorIndexAlgorithm Algorithm,
    KnnMetric Metric,
    ReadOnlyMemory<float> Vectors,
    int Count,
    int Dimension,
    VectorIndexHnswOptions? Hnsw = null,
    VectorIndexIvfOptions? Ivf = null,
    VectorIndexIvfPqOptions? IvfPq = null,
    VectorIndexVamanaOptions? Vamana = null);
