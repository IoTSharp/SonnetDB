namespace SonnetDB.Vector.Core;

/// <summary>
/// 向量索引的统一抽象接口。
/// 所有具体索引实现（Flat / HNSW / IVF 等）必须实现此接口。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// TODO(M2): 在 FlatIndex 中实现此接口。
/// TODO(M3): 在 HnswIndex 中实现此接口。
/// TODO(M4): 在 IvfFlatIndex / IvfPqIndex 中实现此接口。
/// </remarks>
public interface IIndex<TKey>
    where TKey : notnull
{
    /// <summary>索引中存储的向量维度。</summary>
    int Dimensions { get; }

    /// <summary>索引中的向量条数。</summary>
    long Count { get; }

    /// <summary>
    /// 向索引中添加一条向量记录。
    /// </summary>
    /// <param name="key">记录主键（需在索引内唯一）。</param>
    /// <param name="vector">向量数据（长度须等于 <see cref="Dimensions"/>）。</param>
    void Add(TKey key, ReadOnlySpan<float> vector);

    /// <summary>
    /// 从索引中删除指定主键的记录。
    /// </summary>
    /// <param name="key">要删除的主键。</param>
    /// <returns>删除成功返回 <see langword="true"/>，未找到返回 <see langword="false"/>。</returns>
    bool Remove(TKey key);

    /// <summary>
    /// 执行 K 近邻搜索，返回与查询向量最相似的 <paramref name="topK"/> 条记录。
    /// </summary>
    /// <param name="query">查询向量（长度须等于 <see cref="Dimensions"/>）。</param>
    /// <param name="topK">返回结果数量。</param>
    /// <param name="results">调用方提供的结果缓冲区（长度 ≥ topK），避免分配。</param>
    /// <returns>实际写入 <paramref name="results"/> 的结果数量。</returns>
    int Search(ReadOnlySpan<float> query, int topK, Span<(TKey Key, float Score)> results);
}
