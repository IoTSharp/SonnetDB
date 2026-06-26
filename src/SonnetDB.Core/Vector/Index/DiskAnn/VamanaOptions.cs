namespace SonnetDB.Vector.Index.DiskAnn;

/// <summary>
/// Vamana / DiskANN 索引的可调参数。
/// </summary>
/// <remarks>
/// <para>
/// 这些参数控制图的稠密度、构建质量与搜索宽度，参考 DiskANN 论文 (Subramanya et al., 2019) 与
/// 微软开源实现 (https://github.com/microsoft/DiskANN) 的默认值。
/// </para>
/// <para>
/// 经验：
/// <list type="bullet">
///   <item><see cref="MaxDegree"/>（R）越大，图越稠密，召回越高但内存与磁盘占用线性上升。常用 32 / 64 / 96。</item>
///   <item><see cref="SearchListSize"/>（L）越大，构建质量越高（也用于在线 Add）。常用 75 / 100 / 125。</item>
///   <item><see cref="Alpha"/> &gt; 1 时 RobustPrune 倾向于保留更多"长边"，提升远距连通性，常用 1.0 / 1.2。</item>
///   <item><see cref="BeamWidth"/>（W）影响磁盘版的 IO 并发，内存版当前未直接使用。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class VamanaOptions
{
    /// <summary>默认参数（R=32，L=75，alpha=1.2，beam=4）。</summary>
    public static VamanaOptions Default { get; } = new();

    /// <summary>每个节点最大邻居数 R。默认 32。</summary>
    public int MaxDegree { get; init; } = 32;

    /// <summary>构建 / 搜索时的候选列表大小 L（实际搜索 ef = max(L, topK)）。默认 75。</summary>
    public int SearchListSize { get; init; } = 75;

    /// <summary>RobustPrune 的 alpha 系数。默认 1.2。</summary>
    public float Alpha { get; init; } = 1.2f;

    /// <summary>磁盘 BeamSearch 的并发束宽（内存版未使用）。默认 4。</summary>
    public int BeamWidth { get; init; } = 4;

    /// <summary>随机数种子，用于初始随机邻居与候选 tie-break。</summary>
    public int? Seed { get; init; }

    /// <summary>校验参数合法性。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDegree);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SearchListSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(BeamWidth);
        if (MaxDegree > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDegree), MaxDegree, "MaxDegree 不能超过 65535。");
        }
        if (Alpha < 1.0f)
        {
            throw new ArgumentOutOfRangeException(nameof(Alpha), Alpha, "Alpha 必须 ≥ 1.0。");
        }
        if (SearchListSize < MaxDegree)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SearchListSize), SearchListSize,
                "SearchListSize 必须 ≥ MaxDegree，否则候选不足以支撑剪枝。");
        }
    }
}
