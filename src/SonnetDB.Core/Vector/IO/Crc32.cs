namespace SonnetDB.Vector.IO;

/// <summary>
/// CRC32（IEEE 802.3 多项式 <c>0xEDB88320</c>）实现，用于向量 WAL / 段文件完整性校验。
/// 委托给 .NET 运行时以按实际 x64/ARM64 指令能力选择加速路径，并保留标量回退。
/// </summary>
internal static class Crc32
{
    /// <summary>
    /// 计算指定字节序列的 CRC32 值。
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
        => global::System.IO.Hashing.Crc32.HashToUInt32(data);
}
