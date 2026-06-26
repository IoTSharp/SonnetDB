namespace SonnetDB.Vector.Model;

/// <summary>
/// <see cref="Metric"/> 的辅助扩展方法。
/// </summary>
public static class MetricExtensions
{
    /// <summary>
    /// 判定该度量的"分数越大表示越相似"还是"越小越相似"。
    /// </summary>
    /// <param name="metric">度量类型。</param>
    /// <returns>
    /// <see langword="true"/> 表示分数越大越相似（如 <see cref="Metric.InnerProduct"/>），
    /// <see langword="false"/> 表示分数越小越相似（如 <see cref="Metric.L2"/> / <see cref="Metric.Cosine"/>）。
    /// </returns>
    public static bool IsLargerBetter(this Metric metric)
        => metric == Metric.InnerProduct;
}
