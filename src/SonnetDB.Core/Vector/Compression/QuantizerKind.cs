namespace SonnetDB.Vector.Compression;

/// <summary>
/// 量化器类型标识，用于持久化时区分不同的 <see cref="IVectorQuantizer"/> 实现。
/// </summary>
/// <remarks>
/// 数值固定，**不得**重新编号，并且会被写入 <c>quantizer.bin</c> 的首字节。
/// </remarks>
public enum QuantizerKind : byte
{
    /// <summary>未量化（直接存储 fp32）。</summary>
    None = 0,

    /// <summary>逐维 min/max 标量量化到 uint8（M13.1）。</summary>
    Sq8 = 1,

    /// <summary>乘积量化（M13.2，由 <c>PqCodebook</c> 抽象升级而来）。</summary>
    Pq = 2,

    /// <summary>优化乘积量化（M13.3，PQ + 旋转矩阵）。</summary>
    Opq = 3,

    /// <summary>残差量化（M13.4）。</summary>
    Rq = 4,
}
