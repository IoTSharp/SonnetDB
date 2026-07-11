using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Contracts;

namespace SonnetDB.Copilot;

internal sealed record CopilotCloudChatRequest(
    string? ConversationId,
    string Mode,
    CopilotCloudDatabaseContext? Database,
    CopilotCloudClientContext Client,
    CopilotCloudContextSummary Context,
    IReadOnlyCollection<AiMessage> Messages,
    bool Stream,
    int? MaxTokens,
    string? Model);

internal sealed record CopilotCloudDatabaseContext(
    string? Name,
    bool? Selected);

internal sealed record CopilotCloudClientContext(
    string Name,
    string Version,
    IReadOnlyCollection<string> Capabilities);

internal sealed record CopilotCloudContextSummary(
    IReadOnlyCollection<CopilotCloudMeasurementSummary>? Measurements,
    CopilotCloudContextLimits Limits);

internal sealed record CopilotCloudMeasurementSummary(
    string? Name,
    IReadOnlyCollection<string>? Tags,
    IReadOnlyCollection<CopilotCloudFieldSummary>? Fields);

internal sealed record CopilotCloudFieldSummary(
    string? Name,
    string? Type);

internal sealed record CopilotCloudContextLimits(
    int? MaxRowsPerToolCall,
    bool? AllowWrite);

internal sealed record CopilotCloudRuntimeEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string? RequestId = null,
    [property: JsonPropertyName("conversationId")] string? ConversationId = null,
    [property: JsonPropertyName("workflow")] string? Workflow = null,
    [property: JsonPropertyName("toolCallId")] string? ToolCallId = null,
    [property: JsonPropertyName("tool")] CopilotCloudToolCallEvent? Tool = null,
    [property: JsonPropertyName("result")] CopilotCloudToolResultEvent? Result = null,
    [property: JsonPropertyName("riskReview")] CopilotCloudRiskReview? RiskReview = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("answer")] string? Answer = null,
    [property: JsonPropertyName("mode")] string? Mode = null,
    [property: JsonPropertyName("skills")] IReadOnlyCollection<string>? Skills = null,
    [property: JsonPropertyName("knowledge")] IReadOnlyCollection<CopilotCloudKnowledgeReference>? Knowledge = null,
    [property: JsonPropertyName("prompt")] CopilotCloudPromptReference? Prompt = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("provider")] string? Provider = null,
    [property: JsonPropertyName("requiresClientAction")] bool? RequiresClientAction = null,
    [property: JsonPropertyName("usage")] CopilotCloudUsage? Usage = null,
    [property: JsonPropertyName("inputTokens")] long? InputTokens = null,
    [property: JsonPropertyName("outputTokens")] long? OutputTokens = null,
    [property: JsonPropertyName("totalTokens")] long? TotalTokens = null);

internal sealed record CopilotCloudUsage(
    [property: JsonPropertyName("inputTokens")] long? InputTokens = null,
    [property: JsonPropertyName("outputTokens")] long? OutputTokens = null,
    [property: JsonPropertyName("totalTokens")] long? TotalTokens = null);

internal sealed record CopilotCloudToolCallEvent(
    string ToolCallId,
    string Name,
    JsonElement Arguments,
    bool RequiresConfirmation,
    int TimeoutSeconds,
    int? MaxRows,
    DateTimeOffset ExpiresAt);

internal sealed record CopilotCloudToolResultEvent(
    string ToolCallId,
    string Name,
    bool Ok,
    JsonElement Summary);

internal sealed record CopilotCloudRiskReview(
    string Scenario,
    string RiskLevel,
    string Action,
    string ImpactScope,
    IReadOnlyCollection<string> Checklist,
    string? EstimateSql,
    bool RequiresConfirmation,
    bool BlocksAutoExecution,
    Guid? AuditId);

internal sealed record CopilotCloudPromptReference(
    string Name,
    string Version);

internal sealed record CopilotCloudKnowledgeReference(
    string Source,
    string Heading,
    string Version);

internal sealed record CopilotCloudToolResultRequest(
    string? ConversationId,
    string? RequestId,
    string? ToolCallId,
    CopilotCloudToolResultPayload? Result);

internal sealed record CopilotCloudToolResultPayload(
    bool Ok,
    JsonElement? Content,
    string? ErrorCode,
    string? ErrorMessage,
    bool? Rejected);

internal sealed record CopilotCloudToolResultResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("toolCallId")] string ToolCallId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("result")] CopilotCloudToolResultEvent Result);

internal sealed record CopilotCloudChatResponse(
    int StatusCode,
    string? RequestId,
    IReadOnlyList<CopilotCloudRuntimeEvent> Events);
