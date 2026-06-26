namespace SonnetDB.Vector.Model;

/// <summary>
/// 向量距离 / 相似度度量类型。
/// </summary>
public enum Metric : byte
{
    /// <summary>L2（欧氏）距离，值越小越相似。</summary>
    L2 = 0,

    /// <summary>余弦距离（1 - CosineSimilarity），值越小越相似。</summary>
    Cosine = 1,

    /// <summary>内积（点积），值越大越相似（需归一化向量）。</summary>
    InnerProduct = 2,

    /// <summary>汉明距离，适用于二值向量，值越小越相似。</summary>
    Hamming = 3,

    /// <summary>归一化点积，等价于余弦相似度。</summary>
    DotProduct = 4,
}

/// <summary>
/// 一条向量记录，包含 Key、向量数据与可选 payload。
/// </summary>
/// <typeparam name="TKey">记录主键类型，必须是非托管类型以支持 AOT。</typeparam>
/// <remarks>
/// TODO(M2): 在 FlatIndex 实现中使用此记录类型。
/// </remarks>
public sealed class VectorRecord<TKey>
    where TKey : notnull
{
    /// <summary>
    /// 初始化 <see cref="VectorRecord{TKey}"/> 的新实例。
    /// </summary>
    /// <param name="key">记录的唯一标识键。</param>
    /// <param name="vector">向量数据（float32 数组）。</param>
    public VectorRecord(TKey key, float[] vector)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(vector);
        Key = key;
        Vector = vector;
    }

    /// <summary>记录的唯一标识键。</summary>
    public TKey Key { get; }

    /// <summary>float32 向量数据。</summary>
    public float[] Vector { get; }

    /// <summary>
    /// 可选的标量 payload，用于标量过滤（M6）。
    /// </summary>
    /// <remarks>
    /// TODO(M6): 实现 payload 索引与过滤逻辑。
    /// </remarks>
    public Dictionary<string, object>? Payload { get; init; }
}
