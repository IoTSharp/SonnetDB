namespace SonnetDB.Contracts;

/// <summary>
/// 管理后台“关于”页面使用的服务器与运行环境快照。
/// </summary>
public sealed record SystemAboutResponse(
    string ServerVersion,
    string HostName,
    string OsDescription,
    string OsVersion,
    string OsArchitecture,
    string ProcessArchitecture,
    string RuntimeDescription,
    CpuAboutInfo Cpu,
    MemoryAboutInfo Memory,
    IReadOnlyList<DiskAboutInfo> Disks,
    IReadOnlyList<GpuAboutInfo> Gpus,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// CPU 信息。
/// </summary>
public sealed record CpuAboutInfo(
    string Name,
    int LogicalProcessors,
    double? SpeedMHz);

/// <summary>
/// 主机内存信息。
/// </summary>
public sealed record MemoryAboutInfo(
    long TotalBytes,
    long AvailableBytes,
    string Source);

/// <summary>
/// 单个磁盘或挂载点信息。
/// </summary>
public sealed record DiskAboutInfo(
    string Name,
    string Format,
    long TotalBytes,
    long AvailableBytes);

/// <summary>
/// 单个图形处理器信息。
/// </summary>
public sealed record GpuAboutInfo(
    string Name,
    string? Driver,
    string? PciId);
