using System.Text.Json.Serialization;

namespace SonnetDB.Cli;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(CliProfilesDocument))]
[JsonSerializable(typeof(List<CliRemoteProfile>))]
[JsonSerializable(typeof(CliLocalProfile))]
[JsonSerializable(typeof(List<CliLocalProfile>))]
[JsonSerializable(typeof(CliCopilotIngestRequest))]
[JsonSerializable(typeof(CliCopilotIngestResponse))]
[JsonSerializable(typeof(CliCopilotErrorResponse))]
[JsonSerializable(typeof(CliCopilotSkillsReloadRequest))]
[JsonSerializable(typeof(CliCopilotSkillsReloadResponse))]
[JsonSerializable(typeof(CliCopilotSkillsListResponse))]
[JsonSerializable(typeof(CliCopilotSkillsHit))]
[JsonSerializable(typeof(CliCopilotSkillLoadResponse))]
[JsonSerializable(typeof(List<CliCopilotSkillsHit>))]
[JsonSerializable(typeof(DocumentImportReport))]
[JsonSerializable(typeof(DocumentImportCheckpoint))]
[JsonSerializable(typeof(List<DocumentImportItemError>))]
[JsonSerializable(typeof(List<DocumentImportGap>))]
[JsonSerializable(typeof(List<DocumentImportIndexSuggestion>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
[JsonSerializable(typeof(SonnetDB.Graphs.GraphInfoDto))]
[JsonSerializable(typeof(SonnetDB.Graphs.GraphInfoDto[]))]
[JsonSerializable(typeof(SonnetDB.Graphs.GraphOperationsOverviewDto))]
[JsonSerializable(typeof(SonnetDB.Graphs.GraphVisualizationDto))]
[JsonSerializable(typeof(SonnetDB.Graphs.GraphMaintenanceApprovalDto))]
[JsonSerializable(typeof(SonnetDB.Graphs.GraphMaintenanceAuditListDto))]
[JsonSerializable(typeof(List<SonnetDB.Graphs.GraphMaintenanceApprovalDto>))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

/// <summary>
/// CLI 提交给 <c>POST /v1/copilot/docs/ingest</c> 的请求体（PR #64）。
/// </summary>
internal sealed record CliCopilotIngestRequest(
    IReadOnlyList<string>? Roots = null,
    bool Force = false,
    bool DryRun = false);

/// <summary>
/// CLI 解析 <c>POST /v1/copilot/docs/ingest</c> 的响应体（PR #64）。
/// </summary>
internal sealed record CliCopilotIngestResponse(
    int ScannedFiles,
    int IndexedFiles,
    int SkippedFiles,
    int DeletedFiles,
    int WrittenChunks,
    bool DryRun,
    double ElapsedMilliseconds);

/// <summary>
/// 服务端通用错误响应（与 <c>SonnetDB.Contracts.ErrorResponse</c> 保持兼容）。
/// </summary>
internal sealed record CliCopilotErrorResponse(string Error, string Message);

/// <summary>
/// CLI 提交给 <c>POST /v1/copilot/skills/reload</c> 的请求体（PR #65）。
/// </summary>
internal sealed record CliCopilotSkillsReloadRequest(
    string? Root = null,
    bool Force = false,
    bool DryRun = false);

/// <summary>
/// CLI 解析 <c>POST /v1/copilot/skills/reload</c> 的响应体（PR #65）。
/// </summary>
internal sealed record CliCopilotSkillsReloadResponse(
    int ScannedSkills,
    int IndexedSkills,
    int SkippedSkills,
    int DeletedSkills,
    bool DryRun,
    double ElapsedMilliseconds);

/// <summary>
/// CLI 解析 <c>GET /v1/copilot/skills/list</c> 的响应体（PR #65）。
/// </summary>
internal sealed record CliCopilotSkillsListResponse(IReadOnlyList<CliCopilotSkillsHit> Skills);

/// <summary>
/// 技能列表 / 检索的单条记录（PR #65）。
/// </summary>
internal sealed record CliCopilotSkillsHit(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    double Score);

/// <summary>
/// CLI 解析 <c>GET /v1/copilot/skills/{name}</c> 的响应体（PR #65）。
/// </summary>
internal sealed record CliCopilotSkillLoadResponse(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    string Body,
    string Source);
