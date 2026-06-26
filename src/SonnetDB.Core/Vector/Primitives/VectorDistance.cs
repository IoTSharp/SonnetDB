using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Primitives;

/// <summary>
/// 面向数据库 KNN 执行层的向量距离 facade。
/// <para>
/// 与 <see cref="Distance"/> 不同，本类型固定采用“值越小表示越相似”的语义。
/// </para>
/// </summary>
public static class VectorDistance
{
    /// <summary>
    /// 根据指定度量计算两个向量之间的距离。
    /// </summary>
    /// <param name="metric">KNN 距离度量。</param>
    /// <param name="left">左侧向量。</param>
    /// <param name="right">右侧向量。</param>
    /// <returns>距离值，越小表示越相似。</returns>
    public static float Compute(KnnMetric metric, ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        => metric switch
        {
            KnnMetric.L2 => ComputeL2(left, right),
            KnnMetric.InnerProduct => ComputeNegativeInnerProduct(left, right),
            _ => ComputeCosine(left, right),
        };

    /// <summary>
    /// 计算余弦距离：1 - cosine similarity。
    /// </summary>
    /// <param name="left">左侧向量。</param>
    /// <param name="right">右侧向量。</param>
    /// <returns>余弦距离；任一零向量返回 1。</returns>
    public static float ComputeCosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        => Distance.Cosine(left, right);

    /// <summary>
    /// 计算 L2 欧氏距离。
    /// </summary>
    /// <param name="left">左侧向量。</param>
    /// <param name="right">右侧向量。</param>
    /// <returns>L2 欧氏距离。</returns>
    public static float ComputeL2(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        => Distance.L2(left, right);

    /// <summary>
    /// 计算负内积：-(a dot b)。
    /// </summary>
    /// <param name="left">左侧向量。</param>
    /// <param name="right">右侧向量。</param>
    /// <returns>负内积，越小表示内积越大。</returns>
    public static float ComputeNegativeInnerProduct(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        => -Distance.InnerProduct(left, right);

    internal static Metric ToVectorMetric(KnnMetric metric)
        => metric switch
        {
            KnnMetric.L2 => Metric.L2,
            KnnMetric.InnerProduct => Metric.InnerProduct,
            _ => Metric.Cosine,
        };

    internal static float ToLowerIsBetterScore(KnnMetric metric, float dotVectorScore)
        => metric switch
        {
            KnnMetric.InnerProduct => -dotVectorScore,
            KnnMetric.L2 => MathF.Sqrt(MathF.Max(0f, dotVectorScore)),
            _ => dotVectorScore,
        };
}
