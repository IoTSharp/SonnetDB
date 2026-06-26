namespace SonnetDB.Query;

/// <summary>
/// 向量距离计算工具。
/// </summary>
internal static class VectorDistance
{
    /// <summary>
    /// 按指定度量计算两个向量的距离。
    /// </summary>
    /// <param name="metric">距离度量方式。</param>
    /// <param name="a">左侧向量。</param>
    /// <param name="b">右侧向量。</param>
    /// <returns>距离值，越小表示越相似。</returns>
    public static double Compute(KnnMetric metric, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => SonnetDB.Vector.Primitives.VectorDistance.Compute(ToVectorMetric(metric), a, b);

    /// <summary>余弦距离：1 − (a·b) / (‖a‖ · ‖b‖)，值域 [0, 2]。</summary>
    public static double ComputeCosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => SonnetDB.Vector.Primitives.VectorDistance.ComputeCosine(a, b);

    /// <summary>L2（欧几里得）距离：√(Σ(aᵢ − bᵢ)²)。</summary>
    public static double ComputeL2(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => SonnetDB.Vector.Primitives.VectorDistance.ComputeL2(a, b);

    /// <summary>负内积：−(a·b)，值越小表示点积越大（越相似）。</summary>
    public static double ComputeNegativeInnerProduct(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => SonnetDB.Vector.Primitives.VectorDistance.ComputeNegativeInnerProduct(a, b);

    private static SonnetDB.Vector.Primitives.KnnMetric ToVectorMetric(KnnMetric metric)
        => metric switch
        {
            KnnMetric.L2 => SonnetDB.Vector.Primitives.KnnMetric.L2,
            KnnMetric.InnerProduct => SonnetDB.Vector.Primitives.KnnMetric.InnerProduct,
            _ => SonnetDB.Vector.Primitives.KnnMetric.Cosine,
        };
}
