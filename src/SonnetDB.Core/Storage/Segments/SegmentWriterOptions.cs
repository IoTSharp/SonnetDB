namespace SonnetDB.Storage.Segments;

/// <summary>
/// <see cref="SegmentWriter"/> 的构建选项。
/// </summary>
public sealed record SegmentWriterOptions
{
    /// <summary>底层 <see cref="System.IO.BufferedStream"/> 缓冲区大小，默认 64 KiB。</summary>
    public int BufferSize { get; init; } = 64 * 1024;

    /// <summary>是否在 Commit 时执行 fsync（<c>Flush(true)</c>）。默认 <c>true</c>。</summary>
    public bool FsyncOnCommit { get; init; } = true;

    /// <summary>临时文件后缀，默认 <c>".tmp"</c>。</summary>
    public string TempFileSuffix { get; init; } = ".tmp";

    /// <summary>
    /// 时间戳载荷编码方式：
    /// <list type="bullet">
    ///   <item><description><c>None</c>：每个时间戳 8 字节 LE（V1 默认，向后兼容）。</description></item>
    ///   <item><description><c>DeltaTimestamp</c>：delta-of-delta + zigzag varint（V2，PR #29）。</description></item>
    /// </list>
    /// 默认 <c>None</c> 以保持已有段文件与测试行为不变；新场景可显式启用 <c>DeltaTimestamp</c>。
    /// </summary>
    public Format.BlockEncoding TimestampEncoding { get; init; } = Format.BlockEncoding.None;

    /// <summary>
    /// 值载荷编码方式：
    /// <list type="bullet">
    ///   <item><description><c>None</c>：原始 8B/1B/UTF-8 直存（V1 默认）。</description></item>
    ///   <item><description><c>DeltaValue</c>：Float64 走简化版 Gorilla XOR；Boolean 走 RLE；
    ///       String 走字典编码；Int64 在游程载荷更小时采用独立 v7 Int64Rle 标志，否则仍按 8B LE 直存。</description></item>
    /// </list>
    /// 默认 <c>None</c>；显式开启时会在 <c>BlockHeader.Encoding</c> 上记录实际值编码标志。
    /// </summary>
    public Format.BlockEncoding ValueEncoding { get; init; } = Format.BlockEncoding.None;

    /// <summary>
    /// 崩溃注入钩子（仅供测试）：在写入指定字节偏移时被调用，可抛出异常以模拟崩溃。
    /// </summary>
    internal Action<long>? FailAt { get; init; }

    /// <summary>
    /// 原子 rename 完成后的回调（仅供测试）：可抛出异常以模拟 rename 之后、Checkpoint 之前的崩溃。
    /// </summary>
    internal Action? PostRenameAction { get; init; }

    /// <summary>默认选项实例。</summary>
    public static SegmentWriterOptions Default { get; } = new();
}
