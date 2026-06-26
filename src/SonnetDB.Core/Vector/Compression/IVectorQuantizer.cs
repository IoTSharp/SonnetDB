namespace SonnetDB.Vector.Compression;

/// <summary>
/// 通用向量量化器接口。
/// </summary>
/// <remarks>
/// <para>
/// 所有 M13 量化器（SQ8 / PQ / OPQ / RQ）实现该接口，使索引层
/// （<c>FlatIndex</c> / <c>HnswIndex</c> / <c>VamanaIndex</c> / <c>IvfPqIndex</c>）
/// 能够按统一契约存取压缩编码与重建向量。
/// </para>
/// <para>
/// 实现必须满足以下约束：
/// <list type="bullet">
/// <item><c>Encode</c> 与 <c>Decode</c> 在已训练状态下线程安全（只读纯函数）。</item>
/// <item><c>Train</c> 不要求线程安全；调用方自行串行化。</item>
/// <item>所有数值字段使用 little-endian 字节序持久化。</item>
/// </list>
/// </para>
/// </remarks>
public interface IVectorQuantizer
{
    /// <summary>量化器类型。</summary>
    QuantizerKind Kind { get; }

    /// <summary>向量维度。</summary>
    int Dimensions { get; }

    /// <summary>每条编码占用字节数。</summary>
    int CodeBytes { get; }

    /// <summary>是否已经训练完成；未训练时调用 <see cref="Encode"/> / <see cref="Decode"/> 抛出异常。</summary>
    bool IsTrained { get; }

    /// <summary>
    /// 在训练数据上训练量化器参数。
    /// </summary>
    /// <param name="data">行优先训练数据，长度 = <paramref name="count"/> × <see cref="Dimensions"/>。</param>
    /// <param name="count">训练向量数。</param>
    void Train(ReadOnlySpan<float> data, int count);

    /// <summary>
    /// 将单个向量编码为压缩字节。
    /// </summary>
    /// <param name="vector">输入向量，长度 = <see cref="Dimensions"/>。</param>
    /// <param name="code">输出编码缓冲，长度 ≥ <see cref="CodeBytes"/>。</param>
    void Encode(ReadOnlySpan<float> vector, Span<byte> code);

    /// <summary>
    /// 将压缩编码解码回近似向量（精度有损，**仅供调试或低精度回退**）。
    /// </summary>
    /// <param name="code">编码字节，长度 ≥ <see cref="CodeBytes"/>。</param>
    /// <param name="vector">输出向量缓冲，长度 ≥ <see cref="Dimensions"/>。</param>
    void Decode(ReadOnlySpan<byte> code, Span<float> vector);

    /// <summary>
    /// 基于查询向量构造打分器（持有该查询的预计算状态：ADC LUT、归一化等）。
    /// 返回的 <see cref="IQuantizedScorer"/> 实例非线程安全，按查询独立创建。
    /// </summary>
    /// <param name="query">查询向量，长度 = <see cref="Dimensions"/>。</param>
    /// <returns>面向该查询的量化打分内核（L2² 语义）。</returns>
    IQuantizedScorer BuildScorer(ReadOnlySpan<float> query);
}
