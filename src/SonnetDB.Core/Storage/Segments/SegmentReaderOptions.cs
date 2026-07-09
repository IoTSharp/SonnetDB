namespace SonnetDB.Storage.Segments;

/// <summary>
/// <see cref="SegmentReader"/> 的读取选项。
/// </summary>
public sealed record SegmentReaderOptions
{
    /// <summary>
    /// 是否在 Open 时校验 <see cref="SonnetDB.Storage.Format.SegmentFooter"/> 的 IndexCrc32（默认 true）。
    /// </summary>
    public bool VerifyIndexCrc { get; init; } = true;

    /// <summary>
    /// 读取 Block 时是否校验 <see cref="SonnetDB.Storage.Format.BlockHeader"/> 的 Crc32（默认 true）。
    /// </summary>
    public bool VerifyBlockCrc { get; init; } = true;

    /// <summary>
    /// 单个 <see cref="SegmentReader"/> 可用于缓存已解码 Block 的最大字节数；小于等于 0 表示禁用。
    /// 默认 16 MB，缓存以 LRU 策略淘汰，且仅驻留内存。
    /// </summary>
    public long DecodeBlockCacheMaxBytes { get; init; } = 16L * 1024L * 1024L;

    /// <summary>
    /// 进程内 HNSW vector 索引缓存的最大字节数；小于等于 0 表示不缓存。
    /// SegmentReader.Open 不会加载 v6 内嵌 section 或旧 sidecar，首次 <c>TryGetVectorIndex</c> 时按需读取，缓存以 LRU 策略淘汰。
    /// </summary>
    public long VectorIndexCacheMaxBytes { get; init; } = 16L * 1024L * 1024L;

    /// <summary>
    /// 是否允许对达到阈值的大段文件使用安全的 memory-mapped 读取路径；默认 true，避免大段在 Open 时整段进入托管堆。
    /// mmap 路径不会把整个段文件读入托管堆，读取 block payload 时按需复制所需字节。
    /// </summary>
    public bool UseMemoryMappedFileForLargeSegments { get; init; } = true;

    /// <summary>
    /// 启用 memory-mapped 读取路径的文件大小阈值；小于等于 0 表示只要启用选项就尝试 mmap。
    /// 若 mmap 打开失败，会自动回退到默认 <c>byte[]</c> reader。
    /// </summary>
    public long MemoryMappedFileThresholdBytes { get; init; } = 64L * 1024L * 1024L;

    /// <summary>使用默认选项（两项校验均启用）的共享实例。</summary>
    public static SegmentReaderOptions Default { get; } = new();
}
