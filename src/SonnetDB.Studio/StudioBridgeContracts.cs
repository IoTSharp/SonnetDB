using System.Text.Json.Serialization;

namespace SonnetDB.Studio;

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
    string ActiveDatabase);

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
    long UpdatedAt);

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
    string? Error);

/// <summary>
/// Studio bridge 使用 source-generated JSON，避免反射序列化入口。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(StudioBridgeManifest))]
[JsonSerializable(typeof(StudioMenuItem))]
[JsonSerializable(typeof(StudioDesktopActionMessage))]
[JsonSerializable(typeof(StudioConnectionLibrarySnapshot))]
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
[JsonSerializable(typeof(StudioManagedServerStatus))]
internal sealed partial class StudioBridgeJsonContext : JsonSerializerContext;
