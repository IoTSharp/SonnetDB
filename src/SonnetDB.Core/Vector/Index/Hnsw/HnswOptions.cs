namespace SonnetDB.Vector.Index.Hnsw;

/// <summary>
/// HNSW（Hierarchical Navigable Small World）索引的可调参数。
/// </summary>
/// <remarks>
/// <para>
/// 这些参数主要影响图的构建质量、搜索召回率与时间 / 空间开销之间的折衷，
/// 取值参考 hnswlib / FAISS / Qdrant 的默认配置。
/// </para>
/// <para>
/// 一般经验：
/// <list type="bullet">
///   <item><see cref="M"/> 越大，图越稠密，召回率越高，但内存与构建时间都上升。</item>
///   <item><see cref="EfConstruction"/> 影响图的构建质量；越大召回越高，但构建越慢。</item>
///   <item><see cref="EfSearch"/> 仅影响查询；越大召回越高，但延迟越高。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class HnswOptions
{
    /// <summary>
    /// 默认参数（M=16，EfConstruction=200，EfSearch=50）。
    /// </summary>
    public static HnswOptions Default { get; } = new();

    /// <summary>
    /// 每个节点在每一层（除底层 L0）保留的最大邻居数。默认 16。
    /// </summary>
    public int M { get; init; } = 16;

    /// <summary>
    /// 构建索引时每层维护的候选集大小（efConstruction）。默认 200。
    /// 影响图的构建质量与构建时间。
    /// </summary>
    public int EfConstruction { get; init; } = 200;

    /// <summary>
    /// 查询时每层维护的候选集大小（ef）。默认 50。
    /// 实际生效的搜索 ef = max(<see cref="EfSearch"/>, topK)。
    /// </summary>
    public int EfSearch { get; init; } = 50;

    /// <summary>
    /// 随机数种子，用于层级抽样的确定性。<see langword="null"/> 表示使用线程安全的默认种子。
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>
    /// 校验参数合法性，参数非法时抛出 <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(M);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(EfConstruction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(EfSearch);
        if (M > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(M), M, "M 不能超过 1024。");
        }
    }
}
