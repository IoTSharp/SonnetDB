namespace SonnetDB.Vector.Primitives;

/// <summary>
/// KNN 检索使用的距离度量。
/// 所有度量均遵循“值越小表示越相似”的排序语义，便于数据库执行层统一按升序合并 Top-K。
/// </summary>
public enum KnnMetric : byte
{
    /// <summary>
    /// 余弦距离：1 - cosine similarity。
    /// </summary>
    Cosine = 0,

    /// <summary>
    /// L2 欧氏距离：sqrt(sum((a_i - b_i)^2))。
    /// </summary>
    L2 = 1,

    /// <summary>
    /// 负内积：-(a dot b)。内积越大，返回值越小。
    /// </summary>
    InnerProduct = 2,
}
