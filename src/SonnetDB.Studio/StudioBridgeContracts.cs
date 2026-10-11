using System.Text.Json.Serialization;

namespace SonnetDB.Studio;

/// <summary>
/// NativeWebHost 启动握手返回的当前 WebView bridge 配置。
/// </summary>
internal sealed record StudioBridgeBootstrap(string EndpointUrl, string Token);

/// <summary>
/// Studio 桌面桥暴露给 Web Admin 的能力清单。
/// </summary>
internal sealed record StudioBridgeManifest(
    string Mode,
    string Version,
    string ServerUrl,
    string ManagedServerUrl,
    string DataRoot,
    string[] Capabilities,
    StudioMenuItem[] Menu,
    StudioManagedServerStatus ManagedServer);

/// <summary>
/// Studio 桌面菜单项，由 bridge manifest 与 Win32 宿主菜单共同消费。
/// </summary>
internal sealed record StudioMenuItem(
    string Id,
    string Label,
    string Command,
    string Group,
    string? Shortcut);

/// <summary>
/// Studio 宿主发送给 Web 工作台的桌面动作。
/// </summary>
internal sealed record StudioDesktopActionMessage(string Id);

/// <summary>
/// Studio 连接库快照。
/// </summary>
internal sealed record StudioConnectionLibrarySnapshot(
    StudioConnectionProfile[] Profiles,
    string ActiveProfileId,
    string ActiveDatabase)
{
    /// <summary>
    /// 当前选中连接及数据库的宿主身份；不代表连接健康或数据访问授权。
    /// </summary>
    public StudioConnectionIdentity? ActiveIdentity
        => Profiles?.FirstOrDefault(profile => profile is not null
            && string.Equals(profile.Id, ActiveProfileId, StringComparison.Ordinal)) is { } profile
            ? profile.Identity with { Database = ActiveDatabase }
            : null;
}

/// <summary>
/// Studio 宿主内的连接和数据库身份，保留数据库原始拼写与部署子路径。
/// </summary>
internal sealed record StudioConnectionIdentity(
    string Host,
    string ProfileId,
    string BaseUrl,
    string Database);

/// <summary>
/// Studio 连接库中的单个连接配置；鉴权 token 不落盘。
/// </summary>
internal sealed record StudioConnectionProfile(
    string Id,
    string Name,
    string Kind,
    string BaseUrl,
    string DefaultDatabase,
    string TokenMode,
    long CreatedAt,
    long UpdatedAt)
{
    /// <summary>
    /// 该连接配置及默认数据库的派生身份；客户端不能覆盖宿主类型。
    /// </summary>
    public StudioConnectionIdentity Identity => new("studio-desktop", Id, BaseUrl, DefaultDatabase);
}

/// <summary>
/// 文件对话框过滤器。
/// </summary>
internal sealed record StudioFileDialogFilter(string Name, string[] Extensions);

/// <summary>
/// 打开文本文件请求。
/// </summary>
internal sealed record StudioOpenFileRequest(
    string? Title,
    StudioFileDialogFilter[]? Filters,
    long? MaxBytes);

/// <summary>
/// 打开文本文件结果。
/// </summary>
internal sealed record StudioOpenFileResult(
    bool Canceled,
    string? FileName,
    string? Content,
    string? Error);

/// <summary>
/// 保存文本文件请求。
/// </summary>
internal sealed record StudioSaveFileRequest(
    string? Title,
    string? SuggestedName,
    string? Content,
    string? ContentType,
    StudioFileDialogFilter[]? Filters);

/// <summary>
/// 保存文本文件结果。
/// </summary>
internal sealed record StudioSaveFileResult(
    bool Canceled,
    string? FileName,
    string? Error);

/// <summary>
/// 打开二进制文件请求。
/// </summary>
internal sealed record StudioOpenBinaryFileRequest(
    string? Title,
    StudioFileDialogFilter[]? Filters);

/// <summary>
/// 选择目录请求。
/// </summary>
internal sealed record StudioSelectDirectoryRequest(string? Title, string? InitialPath);

/// <summary>
/// 选择目录结果。
/// </summary>
internal sealed record StudioSelectDirectoryResult(bool Canceled, string? Path, string? Error);

/// <summary>
/// 托管本地 SonnetDB Server 请求。
/// </summary>
internal sealed record StudioManagedServerRequest(string? DataRoot, string? Url);

/// <summary>打开已有嵌入式数据库目录的请求。</summary>
internal sealed record StudioOpenEmbeddedDatabaseRequest(string? Path);

/// <summary>
/// 托管本地 SonnetDB Server 运行状态。
/// </summary>
internal sealed record StudioManagedServerStatus(
    bool IsRunning,
    bool StartedByStudio,
    bool Healthy,
    int? ProcessId,
    string Url,
    string DataRoot,
    string? Error,
    string? MountedDatabasePath = null,
    string? MountedDatabaseName = null)
{
    /// <summary>
    /// 当前目标的进程归属，区分 Studio 子进程、外部实例与未运行目标。
    /// </summary>
    public string ProcessOwner => IsRunning ? StartedByStudio ? "studio" : "external" : "none";

    /// <summary>
    /// 已完成状态查询的生命周期快照；错误信息仍独立保留。
    /// </summary>
    public string LifecycleState => (IsRunning, StartedByStudio, Healthy) switch
    {
        (false, _, _) => string.IsNullOrEmpty(Error) ? "stopped" : "failed",
        (true, _, false) => "unhealthy",
        (true, true, true) => "running",
        _ => "external-running",
    };

    /// <summary>
    /// 仅 Studio 归属且有真实进程 ID 的目标可由该宿主停止。
    /// </summary>
    public bool CanStop => IsRunning && StartedByStudio && ProcessId is > 0;
}

/// <summary>
/// Studio bridge 使用 source-generated JSON，避免反射序列化入口。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StudioBridgeBootstrap))]
[JsonSerializable(typeof(StudioBridgeManifest))]
[JsonSerializable(typeof(StudioMenuItem))]
[JsonSerializable(typeof(StudioDesktopActionMessage))]
[JsonSerializable(typeof(StudioConnectionLibrarySnapshot))]
[JsonSerializable(typeof(StudioConnectionIdentity))]
[JsonSerializable(typeof(StudioConnectionProfile))]
[JsonSerializable(typeof(StudioFileDialogFilter))]
[JsonSerializable(typeof(StudioOpenFileRequest))]
[JsonSerializable(typeof(StudioOpenFileResult))]
[JsonSerializable(typeof(StudioSaveFileRequest))]
[JsonSerializable(typeof(StudioSaveFileResult))]
[JsonSerializable(typeof(StudioOpenBinaryFileRequest))]
[JsonSerializable(typeof(StudioSelectDirectoryRequest))]
[JsonSerializable(typeof(StudioSelectDirectoryResult))]
[JsonSerializable(typeof(StudioManagedServerRequest))]
[JsonSerializable(typeof(StudioOpenEmbeddedDatabaseRequest))]
[JsonSerializable(typeof(StudioManagedServerStatus))]
[JsonSerializable(typeof(StudioCopilotCredential))]
[JsonSerializable(typeof(StudioCopilotStatus))]
[JsonSerializable(typeof(StudioCopilotError))]
[JsonSerializable(typeof(StudioCopilotReadiness))]
[JsonSerializable(typeof(StudioCopilotContinuation))]
[JsonSerializable(typeof(StudioCopilotStreamRequest))]
internal sealed partial class StudioBridgeJsonContext : JsonSerializerContext;
