namespace SonnetDB.Contracts;

/// <summary>
/// Copilot 文档摄入请求体（PR #64）。
/// </summary>
/// <param name="Roots">可选的覆盖根目录列表。为空时使用配置中的 <c>Copilot:Docs:Roots</c>。</param>
/// <param name="Force">是否忽略 mtime/fingerprint 强制重新嵌入。</param>
/// <param name="DryRun">仅扫描与切片，不实际写入向量库。</param>
public sealed record CopilotIngestRequest(
    IReadOnlyList<string>? Roots = null,
    bool Force = false,
    bool DryRun = false);

/// <summary>
/// Copilot 文档摄入响应体（PR #64）。
/// </summary>
public sealed record CopilotIngestResponse(
    int ScannedFiles,
    int IndexedFiles,
    int SkippedFiles,
    int DeletedFiles,
    int WrittenChunks,
    bool DryRun,
    double ElapsedMilliseconds);

/// <summary>
/// Copilot 文档检索请求体（PR #64）。
/// </summary>
public sealed record CopilotSearchRequest(string Query, int? K = null);

/// <summary>
/// Copilot 文档检索单条命中（PR #64）。
/// </summary>
public sealed record CopilotSearchHit(
    string Source,
    string Title,
    string Section,
    string Content,
    double Score);

/// <summary>
/// Copilot 文档检索响应体（PR #64）。
/// </summary>
public sealed record CopilotSearchResponse(
    string Query,
    int Requested,
    IReadOnlyList<CopilotSearchHit> Hits,
    double ElapsedMilliseconds);

/// <summary>
/// Copilot 技能库摄入请求体（PR #65）。
/// </summary>
/// <param name="Root">可选根目录，覆盖配置中的 <c>Copilot:Skills:Root</c>。</param>
/// <param name="Force">是否忽略 mtime/fingerprint 强制重新嵌入。</param>
/// <param name="DryRun">仅扫描，不写入向量库。</param>
public sealed record CopilotSkillsIngestRequest(
    string? Root = null,
    bool Force = false,
    bool DryRun = false);

/// <summary>
/// Copilot 技能库摄入响应体（PR #65）。
/// </summary>
public sealed record CopilotSkillsIngestResponse(
    int ScannedSkills,
    int IndexedSkills,
    int SkippedSkills,
    int DeletedSkills,
    bool DryRun,
    double ElapsedMilliseconds);

/// <summary>
/// Copilot 技能库检索请求体（PR #65）。
/// </summary>
public sealed record CopilotSkillsSearchRequest(string Query, int? K = null);

/// <summary>
/// Copilot 技能库检索单条命中（PR #65）。
/// </summary>
public sealed record CopilotSkillsSearchHit(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    double Score);

/// <summary>
/// Copilot 技能库检索响应体（PR #65）。
/// </summary>
public sealed record CopilotSkillsSearchResponse(
    string Query,
    int Requested,
    IReadOnlyList<CopilotSkillsSearchHit> Hits,
    double ElapsedMilliseconds);

/// <summary>
/// Copilot 技能 load 响应体（PR #65）。
/// </summary>
public sealed record CopilotSkillLoadResponse(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    string Body,
    string Source);

/// <summary>
/// Copilot 技能 list 响应体（PR #65）。
/// </summary>
public sealed record CopilotSkillsListResponse(IReadOnlyList<CopilotSkillsSearchHit> Skills);

/// <summary>
/// Copilot 知识库可视化状态（M1.5）。供 Web Admin "知识库" 卡片只读展示。
/// </summary>
/// <param name="Enabled">Copilot 子系统是否启用。</param>
/// <param name="EmbeddingProvider">当前 embedding provider 名（builtin / local / openai）。</param>
/// <param name="EmbeddingFallback">是否处于降级状态（用户配 local，但因模型缺失退化为 builtin）。</param>
/// <param name="VectorDimension">向量维度，固定 384。</param>
/// <param name="DocsRoots">实际扫描的文档根目录（绝对路径）。</param>
/// <param name="IndexedFiles">已建索引的文档源数。</param>
/// <param name="IndexedChunks">已写入向量库的块数。</param>
/// <param name="LastIngestedUtc">最近一次摄入完成时间（UTC ISO-8601）；从未摄入则为 null。</param>
/// <param name="SkillCount">已加载的 Copilot 技能数。</param>
public sealed record CopilotKnowledgeStatusResponse(
    bool Enabled,
    string EmbeddingProvider,
    bool EmbeddingFallback,
    int VectorDimension,
    IReadOnlyList<string> DocsRoots,
    int IndexedFiles,
    int IndexedChunks,
    string? LastIngestedUtc,
    int SkillCount);

/// <summary>
/// Copilot 模型目录中的单个模型。
/// </summary>
/// <param name="Id">发送给模型运行时的稳定模型标识。</param>
/// <param name="DisplayName">面向用户的显示名称。</param>
/// <param name="IsDefault">是否为当前平台默认模型。</param>
public sealed record CopilotModelResponse(
    string Id,
    string DisplayName,
    bool IsDefault);

/// <summary>
/// Provider-neutral 模型分组。
/// </summary>
/// <param name="Key">稳定分组键：<c>platform-default</c>、<c>custom</c> 或 <c>local</c>。</param>
/// <param name="Label">面向用户的分组名称。</param>
/// <param name="Models">分组内可选模型。</param>
public sealed record CopilotModelGroupResponse(
    string Key,
    string Label,
    IReadOnlyList<CopilotModelResponse> Models);

/// <summary>
/// Copilot 可用模型目录。
/// </summary>
/// <param name="Default">平台当前默认模型；没有可用模型时为空字符串。</param>
/// <param name="Candidates">兼容旧客户端的扁平候选模型列表。</param>
public sealed record CopilotModelsResponse(
    string Default,
    IReadOnlyList<string> Candidates)
{
    /// <summary>按模型来源组织的 provider-neutral 分组。</summary>
    public IReadOnlyList<CopilotModelGroupResponse> Groups { get; init; } = [];
}

/// <summary>
/// Copilot 聊天请求体（PR #67 / #68）。
/// </summary>
/// <param name="Db">目标数据库名。建库型 provisioning 请求可为空，此时由服务端根据用户消息推断目标库名。</param>
/// <param name="Message">兼容旧客户端的单条用户问题；若同时提供 <paramref name="Messages"/>，以后者为准。</param>
/// <param name="Messages">多轮对话历史，通常按 <c>user/assistant</c> 交替排列，最后一条应为当前用户问题。</param>
/// <param name="DocsK">文档召回条数；为空时使用服务端默认值。</param>
/// <param name="SkillsK">技能召回条数；为空时使用服务端默认值。</param>
/// <param name="Mode">权限模式（M7）：<c>read-only</c>（默认，禁止写入工具调用）或 <c>read-write</c>（允许，但仍受凭据本身权限上限约束）。</param>
/// <param name="CloudMode">云端 Copilot Runtime 模式；为空时服务端按用户意图推断。</param>
/// <param name="ConversationId">前端会话 ID，用于云端关联同一轮工具结果和审计。</param>
public sealed record CopilotChatRequest(
    string? Db,
    string? Message = null,
    List<AiMessage>? Messages = null,
    int? DocsK = null,
    int? SkillsK = null,
    string? Mode = null,
    string? CloudMode = null,
    string? ConversationId = null)
{
    /// <summary>本次调用的可选模型覆盖；为空时继续跟随平台默认模型。</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Copilot 回答中附带的一条 citation（PR #67）。
/// </summary>
/// <param name="Id">引用编号，例如 <c>C1</c>。</param>
/// <param name="Kind">引用类别：<c>doc</c> / <c>skill</c> / <c>tool</c>。</param>
/// <param name="Title">引用标题。</param>
/// <param name="Source">引用来源。</param>
/// <param name="Snippet">引用摘要片段。</param>
public sealed record CopilotCitation(
    string Id,
    string Kind,
    string Title,
    string Source,
    string Snippet);

/// <summary>
/// Copilot 聊天流中的单条事件（PR #67 / #68）。
/// </summary>
/// <param name="Type">事件类型：<c>start</c> / <c>retrieval</c> / <c>tool_call</c> / <c>tool_retry</c> / <c>tool_result</c> / <c>final</c> / <c>error</c> / <c>done</c>。</param>
/// <param name="Message">阶段性说明或错误消息。</param>
/// <param name="Answer">最终回答文本，仅 <c>final</c> 事件使用。</param>
/// <param name="ToolName">工具名，仅工具相关事件使用。</param>
/// <param name="ToolArguments">工具参数的 JSON 文本。</param>
/// <param name="ToolResult">工具结果的 JSON 文本。</param>
/// <param name="SkillNames">召回到的技能名列表。</param>
/// <param name="ToolNames">召回技能建议使用的工具名列表。</param>
/// <param name="Citations">当前事件附带的 citations。</param>
/// <param name="Attempt">当前重试或执行轮次；仅工具重试相关事件使用。</param>
public sealed record CopilotChatEvent(
    string Type,
    string? Message = null,
    string? Answer = null,
    string? ToolName = null,
    string? ToolArguments = null,
    string? ToolResult = null,
    IReadOnlyList<string>? SkillNames = null,
    IReadOnlyList<string>? ToolNames = null,
    IReadOnlyList<CopilotCitation>? Citations = null,
    int? Attempt = null);

/// <summary>
/// 创建或更新 Copilot 会话的请求。
/// </summary>
public sealed record CopilotConversationUpsertRequest(
    string? Id = null,
    string? Title = null,
    string? Database = null);

/// <summary>
/// Copilot 服务端会话摘要。
/// </summary>
public sealed record CopilotConversationResponse(
    string Id,
    string Title,
    string? Database,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int MessageCount);

/// <summary>
/// 当前认证主体的 Copilot 会话列表。
/// </summary>
public sealed record CopilotConversationListResponse(IReadOnlyList<CopilotConversationResponse> Conversations);

/// <summary>
/// Copilot 服务端持久化消息。
/// </summary>
public sealed record CopilotMessageResponse(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    IReadOnlyList<CopilotCitation>? Citations,
    string? Model,
    long InputTokens,
    long OutputTokens,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// 单个 Copilot 会话的消息列表。
/// </summary>
public sealed record CopilotMessageListResponse(IReadOnlyList<CopilotMessageResponse> Messages);

/// <summary>
/// Copilot 用量的模型维度摘要。
/// </summary>
public sealed record CopilotModelMetricsResponse(string Model, long RequestCount, long TotalTokens);

/// <summary>
/// 当前认证主体在指定窗口内的 Copilot 调用量与 token 摘要。
/// </summary>
public sealed record CopilotMetricsResponse(
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    long RequestCount,
    long SucceededCount,
    long FailedCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    long ToolCalls,
    double AverageDurationMilliseconds,
    bool IncludesEstimatedTokens,
    IReadOnlyList<CopilotModelMetricsResponse> Models);
