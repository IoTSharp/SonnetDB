namespace SonnetDB.FullText.Scoring;

/// <summary>
/// BM25 评分参数。
/// </summary>
/// <remarks>
/// 默认值 <see cref="K1"/>=1.2、<see cref="B"/>=0.75，与业界经典实现保持一致。
/// </remarks>
/// <param name="K1">词频饱和参数。</param>
/// <param name="B">长度归一化参数。</param>
public readonly record struct Bm25Parameters(double K1 = 1.2, double B = 0.75)
{
    /// <summary>
    /// 默认 BM25 参数。
    /// </summary>
    public static Bm25Parameters Default { get; } = new(1.2, 0.75);
}
