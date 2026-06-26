using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// <see cref="IvfPqIndex{TKey}"/> 的全量内部状态快照，仅供持久化层 / 恢复路径使用。
/// </summary>
/// <typeparam name="TKey">主键类型。</typeparam>
/// <param name="Dimensions">向量维度。</param>
/// <param name="Metric">距离度量。</param>
/// <param name="Options">IVF-PQ 参数。</param>
/// <param name="IsTrained">是否已训练。</param>
/// <param name="RowCount">编码字节阵中实际行数（不含未使用容量）。</param>
/// <param name="Keys">按行号排列的主键。</param>
/// <param name="RowToList">每行所属倒排列表 ID；未训练时为 -1。</param>
/// <param name="Centroids">已训练时存放 NList × Dimensions 个聚类中心；未训练时为 <see langword="null"/>。</param>
/// <param name="InvertedLists">已训练时存放每个 list 的行号集合；未训练时为 <see langword="null"/>。</param>
/// <param name="Codes">已训练时存放每行的 M 字节 PQ 编码（长度 = <paramref name="RowCount"/> × M）；未训练时为 <see langword="null"/>。</param>
/// <param name="CodebookCentroids">已训练时存放 PQ 码本子空间中心（M × Ksub × SubDim 行优先）；未训练时为 <see langword="null"/>。</param>
internal sealed record IvfPqIndexSnapshot<TKey>(
    int Dimensions,
    Metric Metric,
    IvfPqOptions Options,
    bool IsTrained,
    int RowCount,
    TKey[] Keys,
    int[] RowToList,
    float[]? Centroids,
    int[][]? InvertedLists,
    byte[]? Codes,
    float[]? CodebookCentroids)
    where TKey : notnull;
