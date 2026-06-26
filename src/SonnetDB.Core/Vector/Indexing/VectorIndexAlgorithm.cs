namespace SonnetDB.Vector.Indexing;

/// <summary>
/// 库级向量索引算法。
/// </summary>
public enum VectorIndexAlgorithm : byte
{
    /// <summary>
    /// 精确线性扫描索引。
    /// </summary>
    Flat = 0,

    /// <summary>
    /// HNSW 图索引。
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
