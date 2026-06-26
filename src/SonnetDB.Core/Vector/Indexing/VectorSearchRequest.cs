using SonnetDB.Vector.Primitives;

namespace SonnetDB.Vector.Indexing;

/// <summary>
/// 向量索引搜索请求。
/// </summary>
/// <param name="Query">查询向量。</param>
/// <param name="TopK">返回结果数量上限。</param>
/// <param name="Metric">KNN 距离度量；必须与索引构建时的度量一致。</param>
public sealed record VectorSearchRequest(
    ReadOnlyMemory<float> Query,
    int TopK,
    KnnMetric Metric);
