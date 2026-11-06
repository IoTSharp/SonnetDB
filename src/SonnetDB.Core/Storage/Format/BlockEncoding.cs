namespace SonnetDB.Storage.Format;

/// <summary>
/// Block 数据载荷的压缩/编码方式。
/// <para>
/// 这是一个位标志枚举：低位表示时间戳列编码，高位表示值列编码，
/// 同一 Block 可以同时启用两侧编码（例如 <c>DeltaTimestamp | DeltaValue</c>）。
/// </para>
/// </summary>
[Flags]
public enum BlockEncoding : byte
{
    /// <summary>原始无压缩（两侧 plain bytes）。</summary>
    None = 0,

    /// <summary>时间戳列采用 delta-of-delta + zigzag varint 编码（Milestone 7 PR #29 启用）。</summary>
    DeltaTimestamp = 1,

    /// <summary>值列采用 XOR / Gorilla 等编码（Milestone 7 PR #30 启用）。</summary>
    DeltaValue = 2,

    /// <summary>
    /// 值列为 <see cref="FieldType.Vector"/> 的原始定长编码（Milestone 13 PR #58 c 启用）：
    /// 每个数据点写入 <c>dim × 4</c> 字节小端 IEEE-754 float32，dim 由 schema 声明，
    /// 同一 Block 内所有点的 dim 必须一致。该 Block 不可同时启用 <see cref="DeltaValue"/>。
    /// </summary>
    VectorRaw = 4,

    /// <summary>
    /// 值列为 <see cref="FieldType.GeoPoint"/> 的原始定长编码（Milestone 15 PR #70 启用）：
    /// 每个数据点写入 <c>lat(8) + lon(8)</c> 字节小端 IEEE-754 float64。
    /// 该 Block 不可同时启用 <see cref="DeltaValue"/>。
    /// </summary>
    GeoPointRaw = 8,

    /// <summary>
    /// Int64 值列使用游程编码（v7）：每段为 8 字节小端原值和 UInt32 varint 正长度。
    /// 仅在比原始载荷更小时使用，不可与其它值编码标志组合；旧 DeltaValue Int64 仍为 raw。
    /// </summary>
    Int64Rle = 16,
}
