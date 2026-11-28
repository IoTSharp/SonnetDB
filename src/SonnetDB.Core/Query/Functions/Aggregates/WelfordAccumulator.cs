namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>
/// Welford 在线方差累加器：单遍计算样本均值与样本方差，
/// 数值稳定，支持两两合并（Chan-Golub-LeVeque parallel algorithm）。
/// <para>
/// 状态共三个：计数 <see cref="Count"/>、均值 <see cref="Mean"/>、累积平方差 <see cref="M2"/>。
/// 样本方差 = M2 / (n-1)；总体方差 = M2 / n。
/// </para>
/// </summary>
internal sealed class WelfordAccumulator
{
    /// <summary>已累加点数。</summary>
    public long Count { get; private set; }

    /// <summary>当前均值。</summary>
    public double Mean { get; private set; }

    /// <summary>累积平方差（Σ(xᵢ − μ)²）。</summary>
    public double M2 { get; private set; }

    /// <summary>累加单个数值。</summary>
    public void Add(double value)
    {
        if (double.IsNaN(value))
            return;

        Count++;
        double delta = value - Mean;
        Mean += delta / Count;
        double delta2 = value - Mean;
        M2 += delta * delta2;
    }

    /// <summary>将所有已累加样本视为乘以同一个二的整数次幂，计数保持不变。</summary>
    internal void RescalePowerOfTwo(int exponent)
    {
        Mean = Math.ScaleB(Mean, exponent);
        M2 = Math.ScaleB(M2, 2 * exponent);
    }

    /// <summary>合并另一个 Welford 累加器（并行算法）。</summary>
    public void Merge(WelfordAccumulator other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Count == 0)
            return;
        if (Count == 0)
        {
            Count = other.Count;
            Mean = other.Mean;
            M2 = other.M2;
            return;
        }

        long combinedCount = Count + other.Count;
        double delta = other.Mean - Mean;
        double newMean = Mean + delta * other.Count / combinedCount;
        double newM2 = M2 + other.M2 + delta * delta * Count * other.Count / combinedCount;
        Count = combinedCount;
        Mean = newMean;
        M2 = newM2;
    }

    /// <summary>样本方差（n-1 分母）；不足两点返回 NaN。</summary>
    public double SampleVariance => Count >= 2 ? M2 / (Count - 1) : double.NaN;

    /// <summary>样本标准差。</summary>
    public double SampleStdDev => Math.Sqrt(SampleVariance);
}
