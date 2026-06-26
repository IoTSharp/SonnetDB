using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// <see cref="IvfFlatIndex{TKey}"/> 的全量内部状态快照，仅供持久化层 / 恢复路径使用。
/// </summary>
/// <typeparam name="TKey">主键类型。</typeparam>
/// <param name="Dimensions">向量维度。</param>
/// <param name="Metric">距离度量。</param>
/// <param name="Options">IVF 参数。</param>
/// <param name="IsTrained">是否已训练；为 <c>false</c> 时 <paramref name="Centroids"/> / <paramref name="InvertedLists"/> 为 <see langword="null"/>。</param>
/// <param name="Vectors">行优先连续向量缓冲（长度 = <paramref name="Keys"/>.Length × <paramref name="Dimensions"/>）。</param>
/// <param name="Keys">按行号排列的主键。</param>
/// <param name="RowToList">每行所属倒排列表 ID；未训练时为 -1。</param>
/// <param name="Centroids">已训练时存放 NList × Dimensions 个聚类中心；未训练时为 <see langword="null"/>。</param>
/// <param name="InvertedLists">已训练时存放每个 list 的行号集合；未训练时为 <see langword="null"/>。</param>
internal sealed record IvfFlatIndexSnapshot<TKey>(
    int Dimensions,
    Metric Metric,
    IvfOptions Options,
    bool IsTrained,
    float[] Vectors,
    TKey[] Keys,
    int[] RowToList,
    float[]? Centroids,
    int[][]? InvertedLists)
    where TKey : notnull;
