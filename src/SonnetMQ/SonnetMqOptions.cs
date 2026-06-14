namespace SonnetMQ;

/// <summary>
/// SonnetMQ 本地队列选项。
/// </summary>
public sealed record SonnetMqOptions
{
    /// <summary>
    /// 队列目录或单文件路径。
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// 存储打开模式。默认使用单目录模式。
    /// </summary>
    public SonnetMqOpenMode OpenMode { get; init; } = SonnetMqOpenMode.Directory;

    /// <summary>
    /// 发布消息后是否立即 flush 到操作系统页缓存。
    /// </summary>
    public bool FlushOnPublish { get; init; }

    /// <summary>
    /// 发布消息后是否调用 durable flush。吞吐优先场景建议关闭，由宿主批量刷盘。
    /// </summary>
    public bool SyncOnPublish { get; init; }

    /// <summary>
    /// Topic 内 offset 稀疏索引步长。值越小 pull 定位越快，但内存占用越高。
    /// </summary>
    public int OffsetIndexStride { get; init; } = 1024;
}
