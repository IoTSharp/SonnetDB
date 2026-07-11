namespace SonnetDB.Contracts;

/// <summary>AI 助手配置响应（不含 token 明文）。</summary>
public sealed record AiConfigResponse(
    bool Enabled,
    bool IsCloudBound,
    DateTimeOffset? CloudAccessTokenExpiresAtUtc,
    DateTimeOffset? CloudBoundAtUtc);

/// <summary>AI 助手配置写入请求。</summary>
public sealed record AiConfigRequest(
    bool Enabled);

/// <summary>创建 sonnetdb.com 设备码绑定请求。</summary>
public sealed record AiCloudDeviceCodeRequest(
    string? ClientName,
    string? ClientVersion,
    string? DeviceName);

/// <summary>创建 sonnetdb.com 设备码绑定响应。</summary>
public sealed record AiCloudDeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

/// <summary>轮询 sonnetdb.com 设备码绑定响应。</summary>
public sealed record AiCloudDeviceTokenResponse(
    bool Authorized,
    string? Error,
    string? Message,
    string? AccessTokenExpiresAtUtc);

/// <summary>轮询 sonnetdb.com 设备码绑定请求。</summary>
public sealed record AiCloudDeviceTokenRequest(string DeviceCode);

/// <summary>平台可用 AI 模型列表响应。</summary>
public sealed record AiCloudModelsResponse(
    string Default,
    IReadOnlyList<string> Candidates)
{
    /// <summary>按模型来源组织的 provider-neutral 分组。</summary>
    public IReadOnlyList<CopilotModelGroupResponse> Groups { get; init; } = [];
}

/// <summary>前端发送的 AI 聊天请求。</summary>
public sealed record AiChatRequest(
    List<AiMessage> Messages,
    string? Db = null,
    string Mode = "chat");

/// <summary>AI 消息（与 OpenAI 格式对齐）。</summary>
public sealed record AiMessage(string Role, string Content);

/// <summary>AI 启用状态（任何已认证用户可读）。</summary>
public sealed record AiStatusResponse(bool Enabled, bool IsCloudBound);

/// <summary>SSE 流式 token 事件（内部 SSE 数据格式）。</summary>
internal sealed record SseTokenEvent(string Token);

/// <summary>SSE 流式错误事件。</summary>
internal sealed record SseErrorEvent(string Error);
