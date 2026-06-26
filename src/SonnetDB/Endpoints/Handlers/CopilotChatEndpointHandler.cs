using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Catalog;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Storage.Format;

namespace SonnetDB.Endpoints;

/// <summary>
/// Cloud-only Copilot chat endpoint. The local server only supplies database context,
/// executes approved local tools, and relays tool results back to ai.sonnetdb.com.
/// </summary>
internal static class CopilotChatEndpointHandler
{
    private const int MaxCloudToolRounds = 4;
    private const int DefaultToolMaxRows = 100;

    public static void Map(
        WebApplication app,
        AiConfigStore configStore,
        ICopilotCloudGatewayClient cloudClient,
        CopilotLocalToolExecutor toolExecutor,
        GrantsStore grantsStore,
        TsdbRegistry registry)
    {
        app.MapMethods("/v1/copilot/chat", ["POST"], (RequestDelegate)(ctx =>
            HandleAsync(ctx, configStore, cloudClient, toolExecutor, grantsStore, registry, sse: false)));

        app.MapMethods("/v1/copilot/chat/stream", ["POST"], (RequestDelegate)(ctx =>
            HandleAsync(ctx, configStore, cloudClient, toolExecutor, grantsStore, registry, sse: true)));
    }

    private static async Task HandleAsync(
        HttpContext ctx,
        AiConfigStore configStore,
        ICopilotCloudGatewayClient cloudClient,
        CopilotLocalToolExecutor toolExecutor,
        GrantsStore grantsStore,
        TsdbRegistry registry,
        bool sse)
    {
        var cfg = configStore.Get();
        if (!IsCloudBound(cfg))
        {
            await WriteErrorAsync(
                ctx,
                StatusCodes.Status503ServiceUnavailable,
                "cloud_not_bound",
                "Copilot 尚未绑定 sonnetdb.com 账号，请先在「Copilot 设置」完成绑定。").ConfigureAwait(false);
            return;
        }

        if (cfg.CloudAccessTokenExpiresAtUtc is not null &&
            cfg.CloudAccessTokenExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            await WriteErrorAsync(
                ctx,
                StatusCodes.Status503ServiceUnavailable,
                "cloud_token_expired",
                "sonnetdb.com Cloud Access Token 已过期，请在「Copilot 设置」重新绑定账号。").ConfigureAwait(false);
            return;
        }

        var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotChatRequest).ConfigureAwait(false);
        if (req is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体格式无效。")
                .ConfigureAwait(false);
            return;
        }

        var messages = NormalizeMessages(req);
        if (messages.Count == 0)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 message 或 messages。")
                .ConfigureAwait(false);
            return;
        }

        var visibleDatabases = DatabaseAccessEvaluator.GetVisibleDatabases(ctx, grantsStore, registry.ListDatabases());
        var isServerAdmin = DatabaseAccessEvaluator.IsServerAdmin(ctx);
        var provisioningIntent = CopilotProvisioning.TryExtractIntent(messages[^1].Content);
        var selectedDb = string.IsNullOrWhiteSpace(req.Db) ? null : req.Db.Trim();
        Tsdb? database = null;
        string databaseName = selectedDb ?? provisioningIntent?.DatabaseName ?? string.Empty;
        var databasePermission = DatabasePermission.None;

        if (!string.IsNullOrWhiteSpace(selectedDb))
        {
            if (!TsdbRegistry.IsValidName(selectedDb))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"非法数据库名 '{selectedDb}'。")
                    .ConfigureAwait(false);
                return;
            }

            if (DatabaseAccessEvaluator.IsSystemDatabase(selectedDb))
            {
                await WriteErrorAsync(
                    ctx,
                    StatusCodes.Status403Forbidden,
                    "system_database",
                    $"数据库 '{selectedDb}' 是系统内置库，不可在 Copilot 对话中直接使用，请选择一个业务数据库。")
                    .ConfigureAwait(false);
                return;
            }

            if (!registry.TryGet(selectedDb, out database))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found", $"数据库 '{selectedDb}' 不存在。")
                    .ConfigureAwait(false);
                return;
            }

            databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grantsStore, selectedDb);
            if (!DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Read))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", $"当前凭据对数据库 '{selectedDb}' 没有 read 权限。")
                    .ConfigureAwait(false);
                return;
            }
        }
        else if (provisioningIntent is not null &&
            visibleDatabases.Any(databaseItem => string.Equals(databaseItem, provisioningIntent.DatabaseName, StringComparison.OrdinalIgnoreCase)) &&
            registry.TryGet(provisioningIntent.DatabaseName, out var existingDatabase))
        {
            database = existingDatabase;
            databaseName = provisioningIntent.DatabaseName;
            databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grantsStore, databaseName);
        }

        var permissionMode = req.Mode?.Trim();
        var allowWrite = string.Equals(permissionMode, "read-write", StringComparison.OrdinalIgnoreCase);
        var canUseControlPlane = allowWrite && isServerAdmin;
        var localContext = new CopilotLocalToolContext(
            ctx,
            grantsStore,
            databaseName,
            database,
            visibleDatabases,
            allowWrite,
            canUseControlPlane);
        var cloudRequest = BuildCloudRequest(req, messages, databaseName, database, databasePermission, allowWrite, canUseControlPlane);

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = sse
            ? "text/event-stream; charset=utf-8"
            : "application/x-ndjson; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            await RunCloudConversationAsync(
                ctx,
                cfg,
                cloudClient,
                toolExecutor,
                localContext,
                cloudRequest,
                sse).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await WriteMappedEventAsync(
                ctx,
                new CopilotChatEvent("error", Message: ex.Message),
                sse).ConfigureAwait(false);
            await WriteMappedEventAsync(
                ctx,
                new CopilotChatEvent("done", Message: "completed"),
                sse).ConfigureAwait(false);
        }

        if (sse)
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
        }
    }

    private static async Task RunCloudConversationAsync(
        HttpContext ctx,
        SonnetDB.Configuration.AiOptions options,
        ICopilotCloudGatewayClient cloudClient,
        CopilotLocalToolExecutor toolExecutor,
        CopilotLocalToolContext localContext,
        CopilotCloudChatRequest cloudRequest,
        bool sse)
    {
        var finalSeen = false;
        var doneSeen = false;

        for (var round = 0; round < MaxCloudToolRounds; round++)
        {
            var response = await cloudClient.ChatAsync(options, cloudRequest, ctx.RequestAborted)
                .ConfigureAwait(false);
            var handledToolCall = false;

            foreach (var cloudEvent in response.Events)
            {
                var isToolResultRequired = string.Equals(
                    cloudEvent.Type,
                    "tool_result_required",
                    StringComparison.OrdinalIgnoreCase);
                if (string.Equals(cloudEvent.Type, "done", StringComparison.OrdinalIgnoreCase) &&
                    handledToolCall &&
                    !finalSeen)
                {
                    continue;
                }

                if (!isToolResultRequired)
                {
                    var mapped = MapCloudEvent(cloudEvent);
                    await WriteMappedEventAsync(ctx, mapped, sse).ConfigureAwait(false);
                    if (string.Equals(mapped.Type, "final", StringComparison.OrdinalIgnoreCase))
                    {
                        finalSeen = true;
                    }

                    if (string.Equals(mapped.Type, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        doneSeen = true;
                    }
                }

                if ((string.Equals(cloudEvent.Type, "tool_call", StringComparison.OrdinalIgnoreCase) ||
                     isToolResultRequired) &&
                    cloudEvent.Tool is not null)
                {
                    if (isToolResultRequired)
                    {
                        await WriteMappedEventAsync(ctx, MapCloudEvent(cloudEvent), sse).ConfigureAwait(false);
                    }

                    handledToolCall = true;
                    var localResult = cloudEvent.Tool.RequiresConfirmation
                        ? CreateConfirmationRequiredResult(cloudEvent.Tool)
                        : toolExecutor.Execute(localContext, cloudEvent.Tool);
                    var toolResultEvent = CreateLocalToolResultEvent(cloudEvent, localResult);
                    await WriteMappedEventAsync(ctx, toolResultEvent, sse).ConfigureAwait(false);

                    await SubmitToolResultAsync(
                        cloudClient,
                        options,
                        cloudRequest.ConversationId,
                        cloudEvent,
                        localResult,
                        ctx.RequestAborted).ConfigureAwait(false);
                }
            }

            if (finalSeen)
            {
                if (!doneSeen)
                {
                    await WriteMappedEventAsync(ctx, new CopilotChatEvent("done", Message: "completed"), sse)
                        .ConfigureAwait(false);
                }

                return;
            }

            if (!handledToolCall)
            {
                await WriteMappedEventAsync(
                    ctx,
                    new CopilotChatEvent("done", Message: "completed"),
                    sse).ConfigureAwait(false);
                return;
            }
        }

        await WriteMappedEventAsync(
            ctx,
            new CopilotChatEvent("error", Message: "云端 Copilot 工具循环超过最大轮次，请缩小问题范围后重试。"),
            sse).ConfigureAwait(false);
        await WriteMappedEventAsync(ctx, new CopilotChatEvent("done", Message: "completed"), sse)
            .ConfigureAwait(false);
    }

    private static async Task SubmitToolResultAsync(
        ICopilotCloudGatewayClient cloudClient,
        SonnetDB.Configuration.AiOptions options,
        string? conversationId,
        CopilotCloudRuntimeEvent cloudEvent,
        CopilotLocalToolResult localResult,
        CancellationToken cancellationToken)
    {
        var tool = cloudEvent.Tool
            ?? throw new InvalidOperationException("Cloud tool event does not contain a tool payload.");
        var request = new CopilotCloudToolResultRequest(
            cloudEvent.ConversationId ?? conversationId,
            cloudEvent.RequestId,
            tool.ToolCallId,
            new CopilotCloudToolResultPayload(
                localResult.Ok,
                localResult.Content,
                localResult.ErrorCode,
                localResult.ErrorMessage,
                localResult.ErrorCode == "client_confirmation_required"));
        await cloudClient.SubmitToolResultAsync(options, request, cancellationToken).ConfigureAwait(false);
    }

    private static CopilotCloudChatRequest BuildCloudRequest(
        CopilotChatRequest request,
        IReadOnlyList<AiMessage> messages,
        string databaseName,
        Tsdb? database,
        DatabasePermission databasePermission,
        bool allowWrite,
        bool canUseControlPlane)
    {
        var effectiveAllowWrite = allowWrite &&
            (canUseControlPlane || DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write));
        var conversationId = NormalizeConversationId(request.ConversationId)
            ?? $"sndb_{Guid.NewGuid():N}";
        var measurements = database is null
            ? null
            : BuildMeasurementSummaries(database);
        var capabilities = BuildCapabilities(database is not null, databasePermission, allowWrite, canUseControlPlane);

        return new CopilotCloudChatRequest(
            ConversationId: conversationId,
            Mode: ResolveCloudMode(request, messages),
            Database: string.IsNullOrWhiteSpace(databaseName)
                ? null
                : new CopilotCloudDatabaseContext(databaseName, Selected: true),
            Client: new CopilotCloudClientContext(
                "SonnetDB OSS Web Admin",
                GetClientVersion(),
                capabilities),
            Context: new CopilotCloudContextSummary(
                measurements,
                new CopilotCloudContextLimits(DefaultToolMaxRows, effectiveAllowWrite)),
            Messages: messages,
            Stream: false,
            MaxTokens: null,
            Model: null);
    }

    private static IReadOnlyCollection<string> BuildCapabilities(
        bool hasDatabase,
        DatabasePermission databasePermission,
        bool allowWrite,
        bool canUseControlPlane)
    {
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tool:draft_sql"
        };

        if (hasDatabase && DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Read))
        {
            capabilities.Add("tool:list_measurements");
            capabilities.Add("tool:describe_measurement");
            capabilities.Add("tool:sample_rows");
            capabilities.Add("tool:explain_sql");
            capabilities.Add("tool:query_sql");
        }

        if (allowWrite &&
            (canUseControlPlane || DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write)))
        {
            capabilities.Add("tool:execute_sql:requires_confirmation");
        }

        return capabilities.ToArray();
    }

    private static IReadOnlyCollection<CopilotCloudMeasurementSummary> BuildMeasurementSummaries(Tsdb database)
    {
        var measurements = database.Measurements.Snapshot();
        var summaries = new List<CopilotCloudMeasurementSummary>(Math.Min(measurements.Count, 50));
        foreach (var measurement in measurements.Take(50))
        {
            summaries.Add(new CopilotCloudMeasurementSummary(
                measurement.Name,
                measurement.TagColumns.Select(static column => column.Name).Take(40).ToArray(),
                measurement.FieldColumns
                    .Take(80)
                    .Select(static column => new CopilotCloudFieldSummary(column.Name, FormatColumnDataType(column)))
                    .ToArray()));
        }

        return summaries;
    }

    private static string ResolveCloudMode(CopilotChatRequest request, IReadOnlyList<AiMessage> messages)
    {
        var explicitMode = NormalizeCloudMode(request.CloudMode) ?? NormalizeCloudMode(request.Mode);
        if (explicitMode is not null)
        {
            return explicitMode;
        }

        var text = string.Join(
            '\n',
            messages.Select(static message => message.Content));
        if (ContainsAny(
            text,
            "retention",
            "compaction",
            "wal",
            "recover",
            "recovery",
            "delete",
            "drop",
            "grant",
            "revoke",
            "create user",
            "alter user",
            "permission",
            "slow query",
            "bulk",
            "ingest",
            "导入",
            "回填",
            "恢复",
            "权限",
            "授权",
            "撤权",
            "慢查询",
            "清理",
            "压缩",
            "保留"))
        {
            return "db_maintenance";
        }

        if (ContainsAny(text, "fix", "repair", "explain", "analyze", "optimize", "修复", "解释", "分析", "优化", "报错", "错误"))
        {
            return "sql_analyze";
        }

        if (ContainsAny(text, "文档", "知识", "帮助", "是什么", "为什么", "介绍", "docs", "help", "guide"))
        {
            return "knowledge_qa";
        }

        return "sql_assist";
    }

    private static string? NormalizeCloudMode(string? mode)
        => mode?.Trim().ToLowerInvariant() switch
        {
            "sql_assist" => "sql_assist",
            "sql_analyze" => "sql_analyze",
            "db_maintenance" => "db_maintenance",
            "knowledge_qa" => "knowledge_qa",
            _ => null
        };

    private static CopilotLocalToolResult CreateConfirmationRequiredResult(CopilotCloudToolCallEvent tool)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", false);
            writer.WriteBoolean("rejected", true);
            writer.WriteString("errorCode", "client_confirmation_required");
            writer.WriteString(
                "errorMessage",
                "该工具调用需要在 SonnetDB 本地确认后才能执行，客户端已阻止自动执行。");
            writer.WriteString("toolName", tool.Name);
            writer.WritePropertyName("arguments");
            tool.Arguments.WriteTo(writer);
            writer.WriteEndObject();
        }

        var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        using var document = JsonDocument.Parse(json);
        return new CopilotLocalToolResult(
            Ok: false,
            Content: document.RootElement.Clone(),
            ErrorCode: "client_confirmation_required",
            ErrorMessage: "该工具调用需要在 SonnetDB 本地确认后才能执行，客户端已阻止自动执行。",
            ResultJson: json);
    }

    private static CopilotChatEvent CreateLocalToolResultEvent(
        CopilotCloudRuntimeEvent cloudEvent,
        CopilotLocalToolResult result)
    {
        var tool = cloudEvent.Tool
            ?? throw new InvalidOperationException("Cloud tool event does not contain a tool payload.");
        return new CopilotChatEvent(
            Type: "tool_result",
            Message: result.Ok
                ? $"本地工具 {tool.Name} 已返回结果。"
                : $"本地工具 {tool.Name} 未执行：{result.ErrorMessage}",
            ToolName: tool.Name,
            ToolArguments: tool.Arguments.GetRawText(),
            ToolResult: result.ResultJson);
    }

    private static CopilotChatEvent MapCloudEvent(CopilotCloudRuntimeEvent cloudEvent)
    {
        if (string.Equals(cloudEvent.Type, "final", StringComparison.OrdinalIgnoreCase))
        {
            return new CopilotChatEvent(
                Type: "final",
                Message: "已生成最终回答。",
                Answer: cloudEvent.Answer ?? cloudEvent.Message ?? string.Empty);
        }

        if ((string.Equals(cloudEvent.Type, "tool_call", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(cloudEvent.Type, "tool_result_required", StringComparison.OrdinalIgnoreCase)) &&
            cloudEvent.Tool is not null)
        {
            return new CopilotChatEvent(
                Type: "tool_call",
                Message: $"云端请求本地工具 {cloudEvent.Tool.Name}。",
                ToolName: cloudEvent.Tool.Name,
                ToolArguments: cloudEvent.Tool.Arguments.GetRawText());
        }

        if (string.Equals(cloudEvent.Type, "risk_review", StringComparison.OrdinalIgnoreCase) &&
            cloudEvent.RiskReview is not null)
        {
            return new CopilotChatEvent(
                Type: "risk_review",
                Message: $"云端风险审查：{cloudEvent.RiskReview.Action}（{cloudEvent.RiskReview.RiskLevel}）。",
                ToolName: cloudEvent.Tool?.Name,
                ToolArguments: cloudEvent.Tool?.Arguments.GetRawText());
        }

        if (string.Equals(cloudEvent.Type, "retrieval", StringComparison.OrdinalIgnoreCase))
        {
            return new CopilotChatEvent(
                Type: "retrieval",
                Message: cloudEvent.Message ?? "云端已选择提示词、技能和官方知识库片段。",
                SkillNames: cloudEvent.Skills?.ToArray());
        }

        return new CopilotChatEvent(
            Type: cloudEvent.Type,
            Message: cloudEvent.Message);
    }

    private static async Task WriteMappedEventAsync(HttpContext ctx, CopilotChatEvent evt, bool sse)
    {
        if (sse)
        {
            await WriteSseEventAsync(ctx, evt).ConfigureAwait(false);
        }
        else
        {
            await WriteNdjsonEventAsync(ctx, evt).ConfigureAwait(false);
        }
    }

    private static async Task WriteNdjsonEventAsync(HttpContext ctx, CopilotChatEvent evt)
    {
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            evt,
            ServerJsonContext.Default.CopilotChatEvent,
            ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.WriteAsync("\n", ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteSseEventAsync(HttpContext ctx, CopilotChatEvent evt)
    {
        var json = JsonSerializer.Serialize(evt, ServerJsonContext.Default.CopilotChatEvent);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ctx.Request.ContentLength == 0)
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<AiMessage> NormalizeMessages(CopilotChatRequest request)
    {
        if (request.Messages is { Count: > 0 })
        {
            return request.Messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(static message => new AiMessage(NormalizeMessageRole(message.Role), message.Content.Trim()))
                .ToList();
        }

        return string.IsNullOrWhiteSpace(request.Message)
            ? []
            : [new AiMessage("user", request.Message.Trim())];
    }

    private static string NormalizeMessageRole(string? role)
        => role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => "assistant",
            "system" => "system",
            _ => "user"
        };

    private static bool IsCloudBound(SonnetDB.Configuration.AiOptions cfg)
        => !string.IsNullOrWhiteSpace(cfg.CloudAccessToken);

    private static string? NormalizeConversationId(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= 128
                ? normalized
                : normalized[..128];
    }

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string GetClientVersion()
        => typeof(CopilotChatEndpointHandler).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private static string FormatColumnDataType(MeasurementColumn column)
    {
        if (column.DataType == FieldType.Vector && column.VectorDimension is int dimension)
        {
            return $"vector({dimension})";
        }

        return column.DataType switch
        {
            FieldType.Float64 => "float64",
            FieldType.Int64 => "int64",
            FieldType.Boolean => "boolean",
            FieldType.String => "string",
            FieldType.Vector => "vector",
            _ => column.DataType.ToString().ToLowerInvariant()
        };
    }
}
