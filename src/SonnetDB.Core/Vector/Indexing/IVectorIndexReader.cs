using SonnetDB.Vector.Primitives;

namespace SonnetDB.Vector.Indexing;

/// <summary>
/// 向量索引读取器。
/// </summary>
public interface IVectorIndexReader : IDisposable
{
    /// <summary>索引算法。</summary>
    VectorIndexAlgorithm Algorithm { get; }

    /// <summary>构建索引时使用的 KNN 度量。</summary>
    KnnMetric Metric { get; }

    /// <summary>向量维度。</summary>
    int Dimension { get; }

    /// <summary>索引内向量数量。</summary>
    int Count { get; }

    /// <summary>
    /// 搜索最近邻。
    /// </summary>
    /// <param name="request">搜索请求。</param>
    /// <returns>按距离升序排列的结果。</returns>
    IReadOnlyList<VectorSearchResult> Search(VectorSearchRequest request);
}
