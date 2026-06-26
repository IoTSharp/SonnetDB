namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// IVF-Flat（倒排文件 + 原始 float 存储）索引的可调参数。
/// </summary>
/// <remarks>
/// <para>
/// 参考 FAISS <c>IndexIVFFlat</c> / Milvus <c>IVF_FLAT</c> 的命名习惯：
/// <list type="bullet">
///   <item><see cref="NList"/>：聚类中心（倒排列表）数量。常用经验值约为 <c>√N</c>。</item>
///   <item><see cref="NProbe"/>：搜索时探测的列表数。越大召回越高，延迟越高。</item>
///   <item><see cref="MaxIterations"/>：K-Means 训练的最大迭代次数。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class IvfOptions
{
    /// <summary>默认参数（NList=64，NProbe=8，MaxIterations=25）。</summary>
    public static IvfOptions Default { get; } = new();

    /// <summary>聚类中心（倒排列表）数量。默认 64。</summary>
    public int NList { get; init; } = 64;

    /// <summary>搜索时探测的倒排列表数。默认 8。</summary>
    public int NProbe { get; init; } = 8;

    /// <summary>K-Means 训练的最大迭代次数。默认 25。</summary>
    public int MaxIterations { get; init; } = 25;

    /// <summary>K-Means 的随机种子；<see langword="null"/> 表示非确定性。</summary>
    public int? Seed { get; init; }

    /// <summary>校验参数合法性。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NList);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NProbe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxIterations);
        if (NProbe > NList)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NProbe), NProbe, $"NProbe（{NProbe}）不能超过 NList（{NList}）。");
        }
    }
}

/// <summary>
/// IVF-PQ（倒排文件 + 乘积量化）索引的可调参数。
/// </summary>
/// <remarks>
/// <para>
/// 在 <see cref="IvfOptions"/> 基础上额外配置 PQ 子量化器：
/// <list type="bullet">
///   <item><see cref="M"/>：子空间数量。<c>Dimensions</c> 必须能被 <c>M</c> 整除。</item>
///   <item><see cref="NBits"/>：每个子量化器的码本位数（K_sub = 2^NBits）。当前版本固定为 8。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class IvfPqOptions
{
    /// <summary>默认参数（NList=64，NProbe=8，M=8，NBits=8，MaxIterations=25）。</summary>
    public static IvfPqOptions Default { get; } = new();

    /// <summary>聚类中心（倒排列表）数量。默认 64。</summary>
    public int NList { get; init; } = 64;

    /// <summary>搜索时探测的倒排列表数。默认 8。</summary>
    public int NProbe { get; init; } = 8;

    /// <summary>子空间数量（PQ 中的 M）。默认 8。</summary>
    public int M { get; init; } = 8;

    /// <summary>每个子量化器的码本位数；当前仅支持 8（K_sub = 256）。</summary>
    public int NBits { get; init; } = 8;

    /// <summary>K-Means 训练的最大迭代次数。默认 25。</summary>
    public int MaxIterations { get; init; } = 25;

    /// <summary>K-Means 的随机种子；<see langword="null"/> 表示非确定性。</summary>
    public int? Seed { get; init; }

    /// <summary>校验参数合法性。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NList);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(NProbe);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(M);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxIterations);
        if (NBits != 8)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NBits), NBits, "当前版本 NBits 仅支持 8（K_sub = 256）。");
        }
        if (NProbe > NList)
        {
            throw new ArgumentOutOfRangeException(
                nameof(NProbe), NProbe, $"NProbe（{NProbe}）不能超过 NList（{NList}）。");
        }
    }
}
