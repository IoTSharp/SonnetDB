namespace SonnetDB.Vector.Compression;

/// <summary>
/// 量化打分内核：基于查询预计算状态对压缩编码批量评分。
/// </summary>
/// <remarks>
/// <para>
/// 打分语义遵循 L2 平方距离：返回值越小代表越接近查询。对于
/// 内积 / 余弦度量，调用方需在外层做归一化或自行翻转排序方向。
/// </para>
/// <para>
/// 通常由 <see cref="IVectorQuantizer"/> 的 <c>BuildScorer(query)</c>（具体实现各异）
/// 创建：内部已基于 query 计算好查找表（ADC LUT 等）。
/// 实例非线程安全，按查询独立创建。
/// </para>
/// </remarks>
public interface IQuantizedScorer
{
    /// <summary>每条编码占用字节数。</summary>
    int CodeBytes { get; }

    /// <summary>
    /// 对单条编码计算近似距离。
    /// </summary>
    /// <param name="code">压缩编码（长度 = <see cref="CodeBytes"/>）。</param>
    /// <returns>近似 L2 平方距离。</returns>
    float Score(ReadOnlySpan<byte> code);
}
