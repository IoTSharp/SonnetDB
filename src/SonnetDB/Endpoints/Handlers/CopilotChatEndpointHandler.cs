using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
/// Copilot chat endpoint. A bound sonnetdb.com account uses the cloud runtime;
/// when no cloud token is bound, a ready configured <see cref="IChatProvider"/>
/// runs the same local agent and permission boundary.
/// </summary>
internal static class CopilotChatEndpointHandler
{
    private const int MaxCloudToolRounds = 4;
    private const int DefaultToolMaxRows = 100;
    private const string CloudOutcomeMissingMessage =
        "云端 Copilot 未返回 final/error 终态或本地工具调用，无法确认运行结果。";
    private const string CloudDoneMissingMessage =
        "云端 Copilot 响应缺少 done 事件，无法确认响应完整性。";
    private const string CloudEventAfterOutcomeMessage =
        "云端 Copilot 在 final/error 终态后返回了额外事件，已拒绝该响应。";
    private const string CloudOutcomeConflictMessage =
        "云端 Copilot 返回了多个 final/error 终态，已拒绝该响应。";
    private const string CloudEventAfterDoneMessage =
        "云端 Copilot 在 done 事件后返回了额外事件，已拒绝该响应。";
    private const string CloudToolOutcomeConflictMessage =
        "云端 Copilot 在同一轮同时返回工具调用和 final/error 终态，已拒绝该响应。";
    private const string CloudFinalEmptyMessage =
        "云端 Copilot 返回了空 final answer，已拒绝该响应。";
    private const string CloudToolCallIdMissingMessage =
        "云端 Copilot 工具调用缺少稳定的 toolCallId，已拒绝该响应。";
    private const string CloudToolCallConflictMessage =
        "云端 Copilot 对同一 toolCallId 返回了冲突的工具名称或参数，已拒绝该响应。";
    private const string RelayInterruptedMessage =
        "ServerRelay 连接已中断；该请求内运行已停止，可使用最后确认的 cursor 重放已记录事件。";

    public static void Map(
        WebApplication app,
        AiConfigStore configStore,
        ICopilotCloudGatewayClient cloudClient,
        CopilotLocalToolExecutor toolExecutor,
        CopilotStateStore stateStore,
        CopilotAgent localAgent,
        CopilotReadiness copilotReadiness,
        CopilotInFlightTracker inFlightTracker,
        GrantsStore grantsStore,
        TsdbRegistry registry)
    {
        var relayRuns = app.Services.GetService<CopilotServerRelayRunStore>()
            ?? new CopilotServerRelayRunStore();
        app.MapMethods("/v1/copilot/chat", ["POST"], (RequestDelegate)(ctx =>
            HandleAsync(ctx, configStore, cloudClient, toolExecutor, stateStore, localAgent, copilotReadiness, inFlightTracker, grantsStore, registry, relayRuns, sse: false)));

        app.MapMethods("/v1/copilot/chat/stream", ["POST"], (RequestDelegate)(ctx =>
            HandleAsync(ctx, configStore, cloudClient, toolExecutor, stateStore, localAgent, copilotReadiness, inFlightTracker, grantsStore, registry, relayRuns, sse: true)));
    }

    private static async Task HandleAsync(
        HttpContext ctx,
        AiConfigStore configStore,
        ICopilotCloudGatewayClient cloudClient,
        CopilotLocalToolExecutor toolExecutor,
        CopilotStateStore stateStore,
        CopilotAgent localAgent,
        CopilotReadiness copilotReadiness,
        CopilotInFlightTracker inFlightTracker,
        GrantsStore grantsStore,
        TsdbRegistry registry,
        CopilotServerRelayRunStore relayRuns,
        bool sse)
    {
        var cfg = configStore.Get();
        if (!IsCloudBound(cfg))
        {
            if (copilotReadiness.Evaluate().ChatReady)
            {
                await HandleLocalAsync(
                    ctx,
                    localAgent,
                    stateStore,
                    inFlightTracker,
                    grantsStore,
                    registry,
                    relayRuns,
                    sse).ConfigureAwait(false);
                return;
            }

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

        if (!messages.Any(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含至少一条 user message。")
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
        var conversationId = cloudRequest.ConversationId
            ?? throw new InvalidOperationException("Copilot 会话 ID 未生成。");
        var owner = CopilotStateStore.ResolveOwner(ctx);
        if (!TryResolveRunId(req.RunId, req.Cursor, out var runId))
        {
            await WriteErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "relay_run_id_invalid",
                "ServerRelay runId 必须为 1-128 个 ASCII 字母、数字、下划线或连字符；携带 cursor 时必须同时携带 runId。")
                .ConfigureAwait(false);
            return;
        }

        var binding = new CopilotServerRelayRunBinding(
            owner,
            databaseName,
            CreateRelayRequestFingerprint("cloud", req, messages, cloudRequest.Mode));
        var attach = relayRuns.Attach(runId, req.Cursor, binding);
        if (attach.Status != CopilotServerRelayAttachStatus.Created)
        {
            if (attach.Status == CopilotServerRelayAttachStatus.Attached)
            {
                await WriteRelayReplayAsync(ctx, attach.Run!, attach.AfterSequence, sse).ConfigureAwait(false);
            }
            else
            {
                await WriteRelayAttachErrorAsync(ctx, attach.Status).ConfigureAwait(false);
            }
            return;
        }

        var relayRun = attach.Run!;
        var latestUserMessage = messages.Last(static message =>
            string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));
        try
        {
            stateStore.UpsertConversation(owner, conversationId, latestUserMessage.Content, databaseName);
            stateStore.AppendMessage(owner, conversationId, "user", latestUserMessage.Content);
        }
        catch (UnauthorizedAccessException exception)
        {
            relayRun.Fail(exception.Message);
            relayRun.Complete();
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", exception.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            relayRun.Fail(exception.Message);
            relayRun.Complete();
            throw;
        }

        ConfigureRelayResponse(ctx, sse);

        var startedAt = Stopwatch.GetTimestamp();
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ctx.RequestAborted,
            relayRun.DeadlineToken);
        localContext = localContext with { CancellationToken = relayCancellation.Token };
        var responseProgress = new CopilotRelayResponseProgress();
        using var inFlightLease = inFlightTracker.Enter();
        using var activity = CopilotDiagnostics.ActivitySource.StartActivity("copilot.chat", ActivityKind.Server);
        activity?.SetTag("copilot.mode", cloudRequest.Mode);
        activity?.SetTag("copilot.conversation_id", cloudRequest.ConversationId);
        var run = new CopilotRunSummary();
        try
        {
            run = await RunCloudConversationAsync(
                ctx,
                cfg,
                cloudClient,
                toolExecutor,
                localContext,
                cloudRequest,
                relayRun,
                relayCancellation.Token,
                sse,
                responseProgress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            relayRun.Fail(RelayInterruptedMessage);
            throw;
        }
        catch (OperationCanceledException) when (relayRun.DeadlineToken.IsCancellationRequested)
        {
            relayRun.Expire();
            run.ErrorMessage = "ServerRelay run 已超过绝对 TTL，运行已停止并封闭为错误终态。";
        }
        catch (Exception ex)
        {
            run.ErrorMessage = ex.Message;
            relayRun.Fail(ex.Message);
            relayRun.Complete();
        }
        finally
        {
            await CompleteRelayResponseAsync(
                ctx,
                relayRun,
                responseProgress,
                sse).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var inputTokens = run.InputTokens ?? EstimateTokens(messages.Select(static message => message.Content));
            var outputTokens = run.OutputTokens ?? EstimateTokens([run.FinalAnswer ?? string.Empty]);
            var totalTokens = run.TotalTokens ?? checked(inputTokens + outputTokens);
            var estimated = !run.InputTokens.HasValue || !run.OutputTokens.HasValue || !run.TotalTokens.HasValue;

            stateStore.RecordUsage(owner, new CopilotUsageRecord(
                conversationId,
                run.Model,
                cloudRequest.Mode,
                inputTokens,
                outputTokens,
                totalTokens,
                estimated,
                run.ToolCalls,
                duration,
                run.Succeeded,
                DateTimeOffset.UtcNow));
            if (!string.IsNullOrWhiteSpace(run.FinalAnswer))
            {
                stateStore.AppendMessage(
                    owner,
                    conversationId,
                    "assistant",
                    run.FinalAnswer,
                    run.Citations,
                    run.Model,
                    inputTokens,
                    outputTokens);
            }

            var tags = new TagList
            {
                { "model", run.Model ?? "unknown" },
                { "mode", cloudRequest.Mode },
                { "succeeded", run.Succeeded },
            };
            CopilotDiagnostics.ChatRequests.Add(1, tags);
            CopilotDiagnostics.ChatDuration.Record(duration, tags);
            CopilotDiagnostics.ChatTokens.Add(inputTokens, new TagList { { "direction", "input" }, { "model", run.Model ?? "unknown" } });
            CopilotDiagnostics.ChatTokens.Add(outputTokens, new TagList { { "direction", "output" }, { "model", run.Model ?? "unknown" } });
            activity?.SetTag("copilot.model", run.Model);
            activity?.SetTag("copilot.tokens.input", inputTokens);
            activity?.SetTag("copilot.tokens.output", outputTokens);
            activity?.SetTag("copilot.tool_calls", run.ToolCalls);
            activity?.SetStatus(run.Succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error, run.ErrorMessage);
        }

        if (sse)
            await WriteSseDoneAsync(ctx).ConfigureAwait(false);
    }

    private static async Task HandleLocalAsync(
        HttpContext ctx,
        CopilotAgent agent,
        CopilotStateStore stateStore,
        CopilotInFlightTracker inFlightTracker,
        GrantsStore grantsStore,
        TsdbRegistry registry,
        CopilotServerRelayRunStore relayRuns,
        bool sse)
    {
        var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.CopilotChatRequest).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体格式无效。").ConfigureAwait(false);
            return;
        }

        var messages = NormalizeMessages(request);
        if (messages.Count == 0)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 message 或 messages。").ConfigureAwait(false);
            return;
        }

        if (!messages.Any(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含至少一条 user message。").ConfigureAwait(false);
            return;
        }

        var visibleDatabases = DatabaseAccessEvaluator.GetVisibleDatabases(ctx, grantsStore, registry.ListDatabases());
        var isServerAdmin = DatabaseAccessEvaluator.IsServerAdmin(ctx);
        var provisioningIntent = CopilotProvisioning.TryExtractIntent(messages[^1].Content);
        var selectedDb = string.IsNullOrWhiteSpace(request.Db) ? null : request.Db.Trim();
        Tsdb? database = null;
        var databaseName = selectedDb ?? provisioningIntent?.DatabaseName ?? string.Empty;
        var databasePermission = DatabasePermission.None;

        if (!string.IsNullOrWhiteSpace(selectedDb))
        {
            if (!TsdbRegistry.IsValidName(selectedDb))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"非法数据库名 '{selectedDb}'。").ConfigureAwait(false);
                return;
            }

            if (DatabaseAccessEvaluator.IsSystemDatabase(selectedDb))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "system_database", $"数据库 '{selectedDb}' 是系统内置库，不可在 Copilot 对话中直接使用，请选择一个业务数据库。").ConfigureAwait(false);
                return;
            }

            if (!registry.TryGet(selectedDb, out database))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found", $"数据库 '{selectedDb}' 不存在。").ConfigureAwait(false);
                return;
            }

            databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grantsStore, selectedDb);
            if (!DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Read))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", $"当前凭据对数据库 '{selectedDb}' 没有 read 权限。").ConfigureAwait(false);
                return;
            }
        }
        else if (provisioningIntent is not null &&
            visibleDatabases.Any(item => string.Equals(item, provisioningIntent.DatabaseName, StringComparison.OrdinalIgnoreCase)) &&
            registry.TryGet(provisioningIntent.DatabaseName, out var existingDatabase))
        {
            database = existingDatabase;
            databaseName = provisioningIntent.DatabaseName;
            databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grantsStore, databaseName);
        }

        var allowWrite = string.Equals(request.Mode?.Trim(), "read-write", StringComparison.OrdinalIgnoreCase);
        if (allowWrite)
        {
            await WriteErrorAsync(
                ctx,
                StatusCodes.Status409Conflict,
                "local_write_confirmation_required",
                "本地 IChatProvider 目前只开放只读 Agent；写入请求必须通过云端风险审查或显式确认流程。")
                .ConfigureAwait(false);
            return;
        }

        var canUseControlPlane = allowWrite && isServerAdmin;
        var canWrite = allowWrite && (canUseControlPlane || DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Write));
        var agentContext = new CopilotAgentContext(
            databaseName,
            database,
            visibleDatabases,
            CanWrite: canWrite,
            ModelOverride: string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim(),
            CanUseControlPlane: canUseControlPlane,
            CanAdministerDatabase: DatabaseAccessEvaluator.HasPermission(databasePermission, DatabasePermission.Admin));
        var conversationId = NormalizeConversationId(request.ConversationId) ?? $"sndb_{Guid.NewGuid():N}";
        var owner = CopilotStateStore.ResolveOwner(ctx);
        if (!TryResolveRunId(request.RunId, request.Cursor, out var runId))
        {
            await WriteErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "relay_run_id_invalid",
                "ServerRelay runId 必须为 1-128 个 ASCII 字母、数字、下划线或连字符；携带 cursor 时必须同时携带 runId。")
                .ConfigureAwait(false);
            return;
        }

        var binding = new CopilotServerRelayRunBinding(
            owner,
            databaseName,
            CreateRelayRequestFingerprint("local", request, messages, request.Mode ?? "read-only"));
        var attach = relayRuns.Attach(runId, request.Cursor, binding);
        if (attach.Status != CopilotServerRelayAttachStatus.Created)
        {
            if (attach.Status == CopilotServerRelayAttachStatus.Attached)
            {
                await WriteRelayReplayAsync(ctx, attach.Run!, attach.AfterSequence, sse).ConfigureAwait(false);
            }
            else
            {
                await WriteRelayAttachErrorAsync(ctx, attach.Status).ConfigureAwait(false);
            }
            return;
        }

        var relayRun = attach.Run!;
        var latestUserMessage = messages.Last(static message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase));

        try
        {
            stateStore.UpsertConversation(owner, conversationId, latestUserMessage.Content, databaseName);
            stateStore.AppendMessage(owner, conversationId, "user", latestUserMessage.Content);
        }
        catch (UnauthorizedAccessException exception)
        {
            relayRun.Fail(exception.Message);
            relayRun.Complete();
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", exception.Message).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            relayRun.Fail(exception.Message);
            relayRun.Complete();
            throw;
        }

        ConfigureRelayResponse(ctx, sse);

        var startedAt = Stopwatch.GetTimestamp();
        using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ctx.RequestAborted,
            relayRun.DeadlineToken);
        var responseProgress = new CopilotRelayResponseProgress();
        var finalAnswer = (string?)null;
        IReadOnlyList<CopilotCitation>? citations = null;
        var toolCalls = 0L;
        var succeeded = false;
        string? errorMessage = null;
        using var inFlightLease = inFlightTracker.Enter();
        using var activity = CopilotDiagnostics.ActivitySource.StartActivity("copilot.chat", ActivityKind.Server);
        activity?.SetTag("copilot.mode", request.Mode ?? "read-only");
        activity?.SetTag("copilot.provider", "configured");
        activity?.SetTag("copilot.conversation_id", conversationId);

        try
        {
            await foreach (var evt in agent.RunAsync(agentContext, messages, request.DocsK, request.SkillsK, relayCancellation.Token).ConfigureAwait(false))
            {
                await WriteRelayEventAsync(
                    ctx,
                    relayRun,
                    evt,
                    sse,
                    responseProgress).ConfigureAwait(false);
                if (string.Equals(evt.Type, "tool_call", StringComparison.OrdinalIgnoreCase))
                    toolCalls++;
                if (string.Equals(evt.Type, "final", StringComparison.OrdinalIgnoreCase))
                {
                    succeeded = true;
                    finalAnswer = evt.Answer;
                    citations = evt.Citations;
                }
            }
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            relayRun.Fail(RelayInterruptedMessage);
            throw;
        }
        catch (OperationCanceledException) when (relayRun.DeadlineToken.IsCancellationRequested)
        {
            relayRun.Expire();
            errorMessage = "ServerRelay run 已超过绝对 TTL，运行已停止并封闭为错误终态。";
        }
        catch (Exception exception)
        {
            errorMessage = exception.Message;
            relayRun.Fail(exception.Message);
            relayRun.Complete();
        }
        finally
        {
            await CompleteRelayResponseAsync(
                ctx,
                relayRun,
                responseProgress,
                sse).ConfigureAwait(false);
            var duration = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var inputTokens = EstimateTokens(messages.Select(static message => message.Content));
            var outputTokens = EstimateTokens([finalAnswer ?? string.Empty]);
            stateStore.RecordUsage(owner, new CopilotUsageRecord(
                conversationId,
                request.Model,
                request.Mode ?? "read-only",
                inputTokens,
                outputTokens,
                checked(inputTokens + outputTokens),
                EstimatedTokens: true,
                ToolCalls: toolCalls,
                DurationMilliseconds: duration,
                Succeeded: succeeded,
                CreatedAtUtc: DateTimeOffset.UtcNow));
            if (!string.IsNullOrWhiteSpace(finalAnswer))
            {
                stateStore.AppendMessage(owner, conversationId, "assistant", finalAnswer, citations, request.Model, inputTokens, outputTokens);
            }

            activity?.SetTag("copilot.model", request.Model ?? "configured-default");
            activity?.SetTag("copilot.tool_calls", toolCalls);
            activity?.SetStatus(succeeded ? ActivityStatusCode.Ok : ActivityStatusCode.Error, errorMessage);
        }

        if (sse)
            await WriteSseDoneAsync(ctx).ConfigureAwait(false);
    }

    private static async Task<CopilotRunSummary> RunCloudConversationAsync(
        HttpContext ctx,
        SonnetDB.Configuration.AiOptions options,
        ICopilotCloudGatewayClient cloudClient,
        CopilotLocalToolExecutor toolExecutor,
        CopilotLocalToolContext localContext,
        CopilotCloudChatRequest cloudRequest,
        CopilotServerRelayRun relayRun,
        CancellationToken cancellationToken,
        bool sse,
        CopilotRelayResponseProgress responseProgress)
    {
        var summary = new CopilotRunSummary();
        var outcomeSeen = false;
        var doneSeen = false;
        var toolCallCache = new Dictionary<string, CachedCloudToolCall>(StringComparer.Ordinal);

        for (var round = 0; round < MaxCloudToolRounds; round++)
        {
            var response = await cloudClient.ChatAsync(options, cloudRequest, cancellationToken)
                .ConfigureAwait(false);
            summary.CaptureUsage(response.Events);
            ValidateCloudRoundEvents(response.Events, toolCallCache);
            var handledToolCall = false;

            foreach (var cloudEvent in response.Events)
            {
                summary.Capture(cloudEvent);
                var isToolResultRequired = string.Equals(
                    cloudEvent.Type,
                    "tool_result_required",
                    StringComparison.OrdinalIgnoreCase);
                var isToolRequest =
                    (string.Equals(cloudEvent.Type, "tool_call", StringComparison.OrdinalIgnoreCase) ||
                     isToolResultRequired) &&
                    cloudEvent.Tool is not null;
                if (string.Equals(cloudEvent.Type, "done", StringComparison.OrdinalIgnoreCase) &&
                    !outcomeSeen)
                {
                    continue;
                }

                if (!isToolResultRequired)
                {
                    var mapped = MapCloudEvent(cloudEvent);
                    await WriteRelayEventAsync(
                        ctx,
                        relayRun,
                        mapped,
                        sse,
                        responseProgress).ConfigureAwait(false);
                    if (string.Equals(mapped.Type, "final", StringComparison.OrdinalIgnoreCase))
                    {
                        outcomeSeen = true;
                        summary.Succeeded = true;
                        summary.FinalAnswer = mapped.Answer;
                        summary.Citations = mapped.Citations;
                    }

                    if (string.Equals(mapped.Type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        outcomeSeen = true;
                        summary.ErrorMessage = mapped.Message;
                    }

                    if (string.Equals(mapped.Type, "done", StringComparison.OrdinalIgnoreCase))
                    {
                        doneSeen = true;
                    }
                }

                if (isToolRequest)
                {
                    if (isToolResultRequired)
                    {
                        await WriteRelayEventAsync(
                            ctx,
                            relayRun,
                            MapCloudEvent(cloudEvent),
                            sse,
                            responseProgress).ConfigureAwait(false);
                    }

                    handledToolCall = true;
                    var tool = cloudEvent.Tool!;
                    var toolCallId = GetRequiredToolCallId(tool);
                    CopilotLocalToolResult localResult;
                    if (toolCallCache.TryGetValue(toolCallId, out var cachedToolCall))
                    {
                        localResult = cachedToolCall.Result;
                    }
                    else
                    {
                        localResult = tool.RequiresConfirmation
                            ? CreateConfirmationRequiredResult(tool)
                            : ExecuteLocalTool(toolExecutor, localContext, tool);
                        toolCallCache.Add(
                            toolCallId,
                            new CachedCloudToolCall(
                                tool.Name,
                                tool.Arguments.Clone(),
                                localResult));
                        summary.ToolCalls++;
                        CopilotDiagnostics.ToolCalls.Add(1, new TagList { { "tool.name", tool.Name } });
                    }

                    var toolResultEvent = CreateLocalToolResultEvent(cloudEvent, localResult);
                    await WriteRelayEventAsync(
                        ctx,
                        relayRun,
                        toolResultEvent,
                        sse,
                        responseProgress).ConfigureAwait(false);

                    await SubmitToolResultAsync(
                        cloudClient,
                        options,
                        cloudRequest.ConversationId,
                        cloudEvent,
                        localResult,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (outcomeSeen)
            {
                if (!doneSeen)
                    throw new InvalidOperationException(CloudDoneMissingMessage);
                return summary;
            }

            if (!handledToolCall)
            {
                summary.ErrorMessage = CloudOutcomeMissingMessage;
                await WriteRelayEventAsync(
                    ctx,
                    relayRun,
                    new CopilotChatEvent("error", Message: CloudOutcomeMissingMessage),
                    sse,
                    responseProgress).ConfigureAwait(false);
                await WriteRelayEventAsync(
                    ctx,
                    relayRun,
                    new CopilotChatEvent("done", Message: "completed"),
                    sse,
                    responseProgress).ConfigureAwait(false);
                return summary;
            }
        }

        await WriteRelayEventAsync(
            ctx,
            relayRun,
            new CopilotChatEvent("error", Message: "云端 Copilot 工具循环超过最大轮次，请缩小问题范围后重试。"),
            sse,
            responseProgress).ConfigureAwait(false);
        await WriteRelayEventAsync(
            ctx,
            relayRun,
            new CopilotChatEvent("done", Message: "completed"),
            sse,
            responseProgress)
            .ConfigureAwait(false);
        summary.ErrorMessage = "云端 Copilot 工具循环超过最大轮次。";
        return summary;
    }

    private static void ValidateCloudRoundEvents(
        IReadOnlyList<CopilotCloudRuntimeEvent> events,
        IReadOnlyDictionary<string, CachedCloudToolCall> cachedToolCalls)
    {
        var outcomeSeen = false;
        var doneSeen = false;
        var toolRequestSeen = false;
        var roundToolCalls = new Dictionary<string, CloudToolCallIdentity>(StringComparer.Ordinal);

        foreach (var cloudEvent in events)
        {
            var isDone = string.Equals(cloudEvent.Type, "done", StringComparison.OrdinalIgnoreCase);
            var isOutcome = string.Equals(cloudEvent.Type, "final", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cloudEvent.Type, "error", StringComparison.OrdinalIgnoreCase);
            var isToolRequest = (string.Equals(cloudEvent.Type, "tool_call", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(cloudEvent.Type, "tool_result_required", StringComparison.OrdinalIgnoreCase)) &&
                cloudEvent.Tool is not null;

            if (doneSeen)
                throw new InvalidOperationException(CloudEventAfterDoneMessage);

            if (outcomeSeen)
            {
                if (isDone)
                {
                    doneSeen = true;
                    continue;
                }

                throw new InvalidOperationException(
                    isOutcome ? CloudOutcomeConflictMessage : CloudEventAfterOutcomeMessage);
            }

            if (isDone)
            {
                doneSeen = true;
                continue;
            }

            if (isOutcome)
            {
                if (toolRequestSeen)
                    throw new InvalidOperationException(CloudToolOutcomeConflictMessage);
                if (string.Equals(cloudEvent.Type, "final", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(cloudEvent.Answer))
                {
                    throw new InvalidOperationException(CloudFinalEmptyMessage);
                }
                outcomeSeen = true;
                continue;
            }

            if (isToolRequest)
            {
                ValidateCloudToolCall(cloudEvent.Tool!, cachedToolCalls, roundToolCalls);
                toolRequestSeen = true;
            }
        }

        if (events.Count > 0 && !doneSeen)
            throw new InvalidOperationException(CloudDoneMissingMessage);
    }

    private static void ValidateCloudToolCall(
        CopilotCloudToolCallEvent tool,
        IReadOnlyDictionary<string, CachedCloudToolCall> cachedToolCalls,
        IDictionary<string, CloudToolCallIdentity> roundToolCalls)
    {
        var toolCallId = GetRequiredToolCallId(tool);
        if (cachedToolCalls.TryGetValue(toolCallId, out var cachedToolCall))
        {
            if (!HasSameToolIdentity(cachedToolCall.Name, cachedToolCall.Arguments, tool))
                throw new InvalidOperationException(CloudToolCallConflictMessage);
            return;
        }

        if (roundToolCalls.TryGetValue(toolCallId, out var roundToolCall))
        {
            if (!HasSameToolIdentity(roundToolCall.Name, roundToolCall.Arguments, tool))
                throw new InvalidOperationException(CloudToolCallConflictMessage);
            return;
        }

        roundToolCalls.Add(
            toolCallId,
            new CloudToolCallIdentity(tool.Name, tool.Arguments.Clone()));
    }

    private static string GetRequiredToolCallId(CopilotCloudToolCallEvent tool)
    {
        if (string.IsNullOrWhiteSpace(tool.ToolCallId))
            throw new InvalidOperationException(CloudToolCallIdMissingMessage);
        return tool.ToolCallId;
    }

    private static bool HasSameToolIdentity(
        string toolName,
        JsonElement arguments,
        CopilotCloudToolCallEvent tool)
        => string.Equals(toolName, tool.Name, StringComparison.Ordinal) &&
            JsonElement.DeepEquals(arguments, tool.Arguments);

    /// <summary>
    /// 执行云端请求的本地工具，并生成与内置 Agent 一致的工具子 span。
    /// </summary>
    private static CopilotLocalToolResult ExecuteLocalTool(
        CopilotLocalToolExecutor toolExecutor,
        CopilotLocalToolContext localContext,
        CopilotCloudToolCallEvent tool)
    {
        using var activity = CopilotDiagnostics.ActivitySource.StartActivity(
            CopilotDiagnostics.RunToolActivityName,
            ActivityKind.Internal);
        activity?.SetTag("tool.name", tool.Name);
        activity?.SetTag("tool.arguments.length", tool.Arguments.GetRawText().Length);

        try
        {
            var result = toolExecutor.Execute(localContext, tool);
            activity?.SetTag("tool.success", result.Ok);
            if (!result.Ok)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                activity?.SetTag("error.type", result.ErrorCode ?? "tool_failed");
            }

            return result;
        }
        catch (Exception ex)
        {
            CopilotDiagnostics.RecordFailure(activity, ex);
            throw;
        }
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
            Model: string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim());
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
            ToolResult: result.ResultJson)
        {
            ToolCallId = tool.ToolCallId,
        };
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
                ToolArguments: cloudEvent.Tool.Arguments.GetRawText())
            {
                ToolCallId = cloudEvent.Tool.ToolCallId,
            };
        }

        if (string.Equals(cloudEvent.Type, "risk_review", StringComparison.OrdinalIgnoreCase) &&
            cloudEvent.RiskReview is not null)
        {
            return new CopilotChatEvent(
                Type: "risk_review",
                Message: $"云端风险审查：{cloudEvent.RiskReview.Action}（{cloudEvent.RiskReview.RiskLevel}）。",
                ToolName: cloudEvent.Tool?.Name,
                ToolArguments: cloudEvent.Tool?.Arguments.GetRawText())
            {
                ToolCallId = cloudEvent.Tool?.ToolCallId ?? cloudEvent.ToolCallId,
            };
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
            Message: cloudEvent.Message)
        {
            ToolCallId = cloudEvent.ToolCallId ?? cloudEvent.Result?.ToolCallId,
        };
    }

    private static async Task WriteRelayEventAsync(
        HttpContext ctx,
        CopilotServerRelayRun relayRun,
        CopilotChatEvent evt,
        bool sse,
        CopilotRelayResponseProgress? responseProgress = null)
    {
        var mapped = relayRun.Publish(evt);
        await WriteMappedEventAsync(ctx, mapped, sse).ConfigureAwait(false);
        if (responseProgress is not null)
            responseProgress.LastWrittenSequence = mapped.Sequence ?? responseProgress.LastWrittenSequence;
    }

    private static async Task WriteRelayTailAsync(
        HttpContext ctx,
        CopilotServerRelayRun relayRun,
        CopilotRelayResponseProgress responseProgress,
        bool sse)
    {
        await foreach (var evt in relayRun.ReadAfterAsync(
                           responseProgress.LastWrittenSequence,
                           ctx.RequestAborted).ConfigureAwait(false))
        {
            await WriteMappedEventAsync(ctx, evt, sse).ConfigureAwait(false);
            responseProgress.LastWrittenSequence = evt.Sequence ?? responseProgress.LastWrittenSequence;
        }
    }

    internal static async Task CompleteRelayResponseAsync(
        HttpContext ctx,
        CopilotServerRelayRun relayRun,
        CopilotRelayResponseProgress responseProgress,
        bool sse)
    {
        relayRun.Complete();
        if (!ctx.RequestAborted.IsCancellationRequested)
        {
            await WriteRelayTailAsync(
                ctx,
                relayRun,
                responseProgress,
                sse).ConfigureAwait(false);
        }
    }

    private static async Task WriteRelayReplayAsync(
        HttpContext ctx,
        CopilotServerRelayRun relayRun,
        long afterSequence,
        bool sse)
    {
        ConfigureRelayResponse(ctx, sse);
        await foreach (var evt in relayRun.ReadAfterAsync(afterSequence, ctx.RequestAborted).ConfigureAwait(false))
            await WriteMappedEventAsync(ctx, evt, sse).ConfigureAwait(false);

        if (sse)
            await WriteSseDoneAsync(ctx).ConfigureAwait(false);
    }

    internal sealed class CopilotRelayResponseProgress
    {
        public long LastWrittenSequence { get; set; }
    }

    private static async Task WriteRelayAttachErrorAsync(
        HttpContext ctx,
        CopilotServerRelayAttachStatus status)
    {
        var (statusCode, code, message) = status switch
        {
            CopilotServerRelayAttachStatus.Unknown => (
                StatusCodes.Status410Gone,
                "relay_run_unknown",
                "ServerRelay run 不存在或已退出当前服务器进程，拒绝按 cursor 重新执行。"),
            CopilotServerRelayAttachStatus.Expired => (
                StatusCodes.Status410Gone,
                "relay_run_expired",
                "ServerRelay run 已超过绝对 TTL，拒绝重放或重新执行。"),
            CopilotServerRelayAttachStatus.Conflict => (
                StatusCodes.Status409Conflict,
                "relay_run_conflict",
                "ServerRelay runId 已绑定到不同的认证主体、数据库或请求内容。"),
            CopilotServerRelayAttachStatus.CursorInvalid => (
                StatusCodes.Status409Conflict,
                "relay_cursor_invalid",
                "ServerRelay cursor 不属于该 run，或引用了尚未产生的 sequence。"),
            CopilotServerRelayAttachStatus.CapacityExceeded => (
                StatusCodes.Status429TooManyRequests,
                "relay_capacity_exceeded",
                "ServerRelay 单进程续流容量已满，请等待现有 run 的 TTL 到期。"),
            _ => (
                StatusCodes.Status409Conflict,
                "relay_attach_invalid",
                "ServerRelay run 无法附加。"),
        };
        await WriteErrorAsync(ctx, statusCode, code, message).ConfigureAwait(false);
    }

    private static void ConfigureRelayResponse(HttpContext ctx, bool sse)
    {
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = sse
            ? "text/event-stream; charset=utf-8"
            : "application/x-ndjson; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers.Append("X-Accel-Buffering", "no");
    }

    private static async Task WriteSseDoneAsync(HttpContext ctx)
    {
        await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
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

    private static bool TryResolveRunId(string? requestedRunId, string? cursor, out string runId)
    {
        if (requestedRunId is null)
        {
            if (!string.IsNullOrWhiteSpace(cursor))
            {
                runId = string.Empty;
                return false;
            }

            runId = $"sndb_run_{Guid.NewGuid():N}";
            return true;
        }

        runId = requestedRunId.Trim();
        if (runId.Length is 0 or > 128)
            return false;

        foreach (var character in runId)
        {
            var valid = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-';
            if (!valid)
                return false;
        }

        return true;
    }

    private static string CreateRelayRequestFingerprint(
        string provider,
        CopilotChatRequest request,
        IReadOnlyList<AiMessage> messages,
        string effectiveMode)
    {
        var builder = new StringBuilder();
        AppendFingerprintValue(builder, provider);
        AppendFingerprintValue(builder, request.Db?.Trim());
        AppendFingerprintValue(builder, request.ConversationId?.Trim());
        AppendFingerprintValue(builder, request.Mode?.Trim().ToLowerInvariant());
        AppendFingerprintValue(builder, request.CloudMode?.Trim().ToLowerInvariant());
        AppendFingerprintValue(builder, effectiveMode.Trim().ToLowerInvariant());
        AppendFingerprintValue(builder, request.Model?.Trim());
        AppendFingerprintValue(builder, request.DocsK?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintValue(builder, request.SkillsK?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendFingerprintValue(builder, messages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var message in messages)
        {
            AppendFingerprintValue(builder, message.Role);
            AppendFingerprintValue(builder, message.Content);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendFingerprintValue(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(value.Length)
            .Append(':')
            .Append(value);
    }

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

    private static long EstimateTokens(IEnumerable<string> values)
    {
        long asciiCharacters = 0;
        long nonAsciiCharacters = 0;
        foreach (var value in values)
        {
            foreach (var character in value)
            {
                if (character <= 0x7f)
                    asciiCharacters++;
                else
                    nonAsciiCharacters++;
            }
        }

        return (asciiCharacters + 3) / 4 + nonAsciiCharacters;
    }

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

    private readonly record struct CloudToolCallIdentity(string Name, JsonElement Arguments);

    private sealed record CachedCloudToolCall(
        string Name,
        JsonElement Arguments,
        CopilotLocalToolResult Result);

    private sealed class CopilotRunSummary
    {
        public bool Succeeded { get; set; }

        public string? ErrorMessage { get; set; }

        public string? FinalAnswer { get; set; }

        public IReadOnlyList<CopilotCitation>? Citations { get; set; }

        public string? Model { get; private set; }

        public long? InputTokens { get; private set; }

        public long? OutputTokens { get; private set; }

        public long? TotalTokens { get; private set; }

        public long ToolCalls { get; set; }

        public void Capture(CopilotCloudRuntimeEvent cloudEvent)
        {
            if (!string.IsNullOrWhiteSpace(cloudEvent.Model))
                Model = cloudEvent.Model.Trim();
        }

        public void CaptureUsage(IReadOnlyList<CopilotCloudRuntimeEvent> events)
        {
            var input = MaxUsage(events, static item => item.Usage?.InputTokens ?? item.InputTokens);
            var output = MaxUsage(events, static item => item.Usage?.OutputTokens ?? item.OutputTokens);
            var total = MaxUsage(events, static item => item.Usage?.TotalTokens ?? item.TotalTokens);
            if (input.HasValue)
                InputTokens = checked((InputTokens ?? 0) + Math.Max(0, input.Value));
            if (output.HasValue)
                OutputTokens = checked((OutputTokens ?? 0) + Math.Max(0, output.Value));
            if (total.HasValue)
                TotalTokens = checked((TotalTokens ?? 0) + Math.Max(0, total.Value));
        }

        private static long? MaxUsage(
            IReadOnlyList<CopilotCloudRuntimeEvent> events,
            Func<CopilotCloudRuntimeEvent, long?> selector)
        {
            long? maximum = null;
            foreach (var cloudEvent in events)
            {
                var value = selector(cloudEvent);
                if (value.HasValue && (!maximum.HasValue || value.Value > maximum.Value))
                    maximum = value.Value;
            }
            return maximum;
        }
    }
}
