using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SonnetDB.Catalog;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Mcp;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Copilot;

/// <summary>
/// PR #67 / #68：Copilot 问答编排器。
/// </summary>
internal sealed class CopilotAgent
{
    private const int DefaultDocsK = 5;
    private const int MaxDocsK = 10;
    private const int DefaultSkillsK = 3;
    private const int MaxSkillsK = 8;
    private const int MaxLoadedSkills = 3;
    private const int MaxReActRounds = 6;      // ReAct 最多循环轮次（每轮执行 1 个工具）
    private const int HistoryTokenBudget = 1200;
    private const int MaxSqlRepairAttempts = 3;

    private readonly DocsSearchService _docsSearchService;
    private readonly SkillSearchService _skillSearchService;
    private readonly SkillRegistry _skillRegistry;
    private readonly IChatProvider _chatProvider;
    private readonly SonnetDbMcpSchemaCache _schemaCache;
    private readonly SonnetDbMcpExplainSqlService _explainSqlService;
    private readonly IControlPlane _controlPlane;
    private readonly TsdbRegistry _registry;
    private readonly ILogger<CopilotAgent> _logger;

    public CopilotAgent(
        DocsSearchService docsSearchService,
        SkillSearchService skillSearchService,
        SkillRegistry skillRegistry,
        IChatProvider chatProvider,
        SonnetDbMcpSchemaCache schemaCache,
        SonnetDbMcpExplainSqlService explainSqlService,
        IControlPlane controlPlane,
        TsdbRegistry registry,
        ILogger<CopilotAgent> logger)
    {
        _docsSearchService = docsSearchService;
        _skillSearchService = skillSearchService;
        _skillRegistry = skillRegistry;
        _chatProvider = chatProvider;
        _schemaCache = schemaCache;
        _explainSqlService = explainSqlService;
        _controlPlane = controlPlane;
        _registry = registry;
        _logger = logger;
    }

    public async IAsyncEnumerable<CopilotChatEvent> RunAsync(
        CopilotAgentContext context,
        IReadOnlyList<AiMessage> messages,
        int? docsK = null,
        int? skillsK = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(messages);

        var conversation = PrepareConversation(messages);
        var effectiveDocsK = NormalizeLimit(docsK, DefaultDocsK, MaxDocsK);
        var effectiveSkillsK = NormalizeLimit(skillsK, DefaultSkillsK, MaxSkillsK);

        yield return new CopilotChatEvent(
            Type: "start",
            Message: conversation.WasTrimmed
                ? $"开始处理数据库 '{context.DatabaseName}' 上的问题，历史消息已按 token 预算裁剪为 {conversation.Messages.Count} 条。"
                : $"开始处理数据库 '{context.DatabaseName}' 上的问题。");

        var docs = effectiveDocsK > 0
            ? await _docsSearchService.SearchAsync(conversation.RetrievalQuery, effectiveDocsK, cancellationToken).ConfigureAwait(false)
            : [];

        var skillHits = effectiveSkillsK > 0
            ? await _skillSearchService.SearchAsync(conversation.RetrievalQuery, effectiveSkillsK, cancellationToken).ConfigureAwait(false)
            : [];

        var loadedSkills = LoadTopSkills(skillHits);
        var nextCitationNumber = 1;
        var retrievalCitations = BuildRetrievalCitations(docs, loadedSkills, ref nextCitationNumber);
        var suggestedToolNames = loadedSkills
            .SelectMany(static skill => skill.RequiresTools)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        yield return new CopilotChatEvent(
            Type: "retrieval",
            Message: $"已召回 {loadedSkills.Count} 个技能、{docs.Count} 条文档。",
            SkillNames: loadedSkills.Count > 0 ? loadedSkills.Select(static skill => skill.Name).ToArray() : null,
            ToolNames: suggestedToolNames.Length > 0 ? suggestedToolNames : null,
            Citations: retrievalCitations.Count > 0 ? retrievalCitations : null);

        // ReAct 多轮循环：每轮 Planner 看到前面所有工具结果后再决定下一步
        var observations = new List<CopilotToolObservation>(MaxReActRounds);
        var executedToolKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var round = 0; round < MaxReActRounds; round++)
        {
            var plan = await PlanToolsAsync(
                context, conversation, docs, loadedSkills, observations, executedToolKeys, cancellationToken).ConfigureAwait(false);

            if (plan.Count == 0)
                break;  // Planner 说已有足够信息，结束工具循环

            var tool = plan[0];  // 每轮只取第一个（Planner 已改为单步模式）
            var plannedToolKey = GetToolKey(tool);
            var toolArguments = FormatToolArguments(tool);
            yield return new CopilotChatEvent(
                Type: "tool_call",
                Message: $"[第 {round + 1} 轮] 执行工具 {tool.Name}。",
                ToolName: tool.Name,
                ToolArguments: toolArguments);

            var execution = await ExecuteToolAsync(
                context,
                conversation,
                docs,
                loadedSkills,
                tool,
                cancellationToken).ConfigureAwait(false);

            foreach (var evt in execution.Events)
                yield return evt;

            var finalToolArguments = FormatToolArguments(execution.Tool);
            var citation = BuildToolCitation(execution.Tool, execution.ResultJson, ref nextCitationNumber);
            var captured = new CopilotToolObservation(execution.Tool.Name, finalToolArguments, execution.ResultJson, citation);
            observations.Add(captured);
            executedToolKeys.Add(plannedToolKey);
            executedToolKeys.Add(GetToolKey(execution.Tool));

            yield return new CopilotChatEvent(
                Type: "tool_result",
                Message: $"工具 {execution.Tool.Name} 已返回结果。",
                ToolName: execution.Tool.Name,
                ToolArguments: finalToolArguments,
                ToolResult: execution.ResultJson,
                Citations: [citation]);
        }

        var allCitations = new List<CopilotCitation>(retrievalCitations.Count + observations.Count);
        allCitations.AddRange(retrievalCitations);
        allCitations.AddRange(observations.Select(static item => item.Citation));

        var answer = await GenerateAnswerAsync(
            context,
            conversation,
            docs,
            loadedSkills,
            observations,
            allCitations,
            cancellationToken).ConfigureAwait(false);

        yield return new CopilotChatEvent(
            Type: "final",
            Message: "已生成最终回答。",
            Answer: answer,
            Citations: allCitations.Count > 0 ? allCitations : null);

        yield return new CopilotChatEvent(
            Type: "done",
            Message: "completed");
    }

    private static CopilotConversation PrepareConversation(IReadOnlyList<AiMessage> messages)
    {
        var normalized = NormalizeMessages(messages);
        if (normalized.Count == 0)
            throw new ArgumentException("Copilot messages cannot be empty.", nameof(messages));

        var trimmed = TrimConversation(normalized);
        var latestUserIndex = FindLatestUserMessageIndex(trimmed);
        if (latestUserIndex < 0)
            throw new ArgumentException("Copilot messages must contain at least one user message.", nameof(messages));

        var activeMessages = trimmed.Take(latestUserIndex + 1).ToArray();
        var history = latestUserIndex == 0
            ? []
            : activeMessages[..latestUserIndex];
        var latestUserMessage = activeMessages[latestUserIndex].Content;

        return new CopilotConversation(
            Messages: activeMessages,
            History: history,
            LatestUserMessage: latestUserMessage,
            RetrievalQuery: BuildRetrievalQuery(activeMessages),
            WasTrimmed: trimmed.Count != normalized.Count || activeMessages.Length != normalized.Count);
    }

    private IReadOnlyList<SkillLoadResult> LoadTopSkills(IReadOnlyList<SkillSearchHit> skillHits)
    {
        if (skillHits.Count == 0)
            return [];

        var loaded = new List<SkillLoadResult>(Math.Min(skillHits.Count, MaxLoadedSkills));
        foreach (var hit in skillHits.Take(MaxLoadedSkills))
        {
            var full = _skillRegistry.Load(hit.Name);
            if (full is not null)
                loaded.Add(full);
        }

        return loaded;
    }

    private async Task<IReadOnlyList<CopilotToolInvocation>> PlanToolsAsync(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        IReadOnlyList<CopilotToolObservation> observations,
        HashSet<string> executedToolKeys,
        CancellationToken cancellationToken)
    {
        if (TryBuildProvisioningPlan(context, conversation, observations, out var provisioningPlan))
            return FilterExecutedTools(provisioningPlan, executedToolKeys);

        var measurements = GetMeasurements(context.DatabaseName, context.Database);
        var plannerPrompt = BuildPlannerPrompt(context, conversation, docs, loadedSkills, observations);

        try
        {
            var response = await _chatProvider.CompleteAsync(
                [
                    new AiMessage("system", PlannerSystemPrompt),
                    new AiMessage("user", plannerPrompt),
                ],
                context.ModelOverride,
                cancellationToken).ConfigureAwait(false);

            if (TryParsePlan(response, out var plan) && plan is not null)
            {
                var sanitized = SanitizePlan(plan.Tools, measurements, conversation.LatestUserMessage);
                var augmented = EnsureWriteDraftPlan(sanitized, conversation.LatestUserMessage);
                return FilterExecutedTools(augmented, executedToolKeys);
            }

            _logger.LogWarning("Copilot planner returned non-JSON content: {Response}", response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot planner failed, falling back to heuristics.");
        }

        var fallback = EnsureWriteDraftPlan(BuildHeuristicPlan(conversation.LatestUserMessage, measurements), conversation.LatestUserMessage);
        return FilterExecutedTools(fallback, executedToolKeys);
    }

    private async Task<string> GenerateAnswerAsync(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        IReadOnlyList<CopilotToolObservation> observations,
        IReadOnlyList<CopilotCitation> citations,
        CancellationToken cancellationToken)
    {
        var prompt = BuildAnswerPrompt(context, conversation, docs, loadedSkills, observations, citations);

        try
        {
            var answer = await _chatProvider.CompleteAsync(
                [
                    new AiMessage("system", AnswerSystemPrompt),
                    new AiMessage("user", prompt),
                ],
                context.ModelOverride,
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(answer))
                return answer.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot final answer generation failed, using deterministic fallback.");
        }

        return BuildFallbackAnswer(context, conversation, observations, citations);
    }

    private static bool TryParsePlan(string response, out CopilotToolPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        if (TryDeserializePlan(response, out plan))
            return true;

        var json = ExtractJsonObject(response);
        return json is not null && TryDeserializePlan(json, out plan);
    }

    private static bool TryDeserializePlan(string json, out CopilotToolPlan? plan)
    {
        try
        {
            plan = JsonSerializer.Deserialize(json, ServerJsonContext.Default.CopilotToolPlan);
            return plan is not null;
        }
        catch (JsonException)
        {
            plan = null;
            return false;
        }
    }

    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        return text[start..(end + 1)];
    }

    private static IReadOnlyList<CopilotToolInvocation> SanitizePlan(
        IReadOnlyList<CopilotPlannedTool>? plannedTools,
        IReadOnlyList<string> measurements,
        string userMessage)
    {
        if (plannedTools is null || plannedTools.Count == 0)
            return [];

        var tools = new List<CopilotToolInvocation>(plannedTools.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var planned in plannedTools)
        {
            if (string.IsNullOrWhiteSpace(planned.Name))
                continue;

            var normalizedName = planned.Name.Trim();
            CopilotToolInvocation? tool = normalizedName switch
            {
                "list_databases" => new CopilotToolInvocation(normalizedName, MaxRows: null, N: null, Measurement: null, Sql: null),
                "list_measurements" => new CopilotToolInvocation(
                    normalizedName,
                    MaxRows: NormalizeLimit(planned.MaxRows, SonnetDbMcpResults.DefaultToolRowLimit, SonnetDbMcpResults.MaxToolRowLimit),
                    N: null,
                    Measurement: null,
                    Sql: null),
                "describe_measurement" => TryResolveMeasurement(planned.Measurement, measurements, userMessage) is { } describeMeasurement
                    ? new CopilotToolInvocation(normalizedName, null, null, describeMeasurement, null)
                    : null,
                "sample_rows" => TryResolveMeasurement(planned.Measurement, measurements, userMessage) is { } sampleMeasurement
                    ? new CopilotToolInvocation(
                        normalizedName,
                        MaxRows: null,
                        N: NormalizeLimit(planned.N, SonnetDbMcpResults.DefaultSampleRowLimit, SonnetDbMcpResults.MaxSampleRowLimit),
                        Measurement: sampleMeasurement,
                        Sql: null)
                    : null,
                "explain_sql" when !string.IsNullOrWhiteSpace(planned.Sql)
                    => new CopilotToolInvocation(normalizedName, null, null, null, planned.Sql.Trim(), planned.Database),
                "query_sql" when !string.IsNullOrWhiteSpace(planned.Sql)
                    => new CopilotToolInvocation(
                        normalizedName,
                        NormalizeLimit(planned.MaxRows, SonnetDbMcpResults.DefaultToolRowLimit, SonnetDbMcpResults.MaxToolRowLimit),
                        null,
                        null,
                        planned.Sql.Trim(),
                        planned.Database),
                "draft_sql" when !string.IsNullOrWhiteSpace(planned.Sql)
                    => new CopilotToolInvocation(normalizedName, null, null, null, planned.Sql.Trim(), planned.Database),
                "execute_sql" when !string.IsNullOrWhiteSpace(planned.Sql)
                    => new CopilotToolInvocation(
                        normalizedName,
                        NormalizeLimit(planned.MaxRows, SonnetDbMcpResults.DefaultToolRowLimit, SonnetDbMcpResults.MaxToolRowLimit),
                        null,
                        null,
                        planned.Sql.Trim(),
                        planned.Database),
                _ => null,
            };

            if (tool is null)
                continue;

            var key = GetToolKey(tool);
            if (seen.Add(key))
                tools.Add(tool);
        }

        return tools;
    }

    private static IReadOnlyList<CopilotToolInvocation> FilterExecutedTools(
        IReadOnlyList<CopilotToolInvocation> plan,
        HashSet<string> executedToolKeys)
    {
        if (plan.Count == 0 || executedToolKeys.Count == 0)
            return plan;

        var remaining = new List<CopilotToolInvocation>(plan.Count);
        foreach (var tool in plan)
        {
            if (!executedToolKeys.Contains(GetToolKey(tool)))
                remaining.Add(tool);
        }

        return remaining;
    }

    private static IReadOnlyList<CopilotToolInvocation> BuildHeuristicPlan(string message, IReadOnlyList<string> measurements)
    {
        var lowered = message.ToLowerInvariant();
        var tools = new List<CopilotToolInvocation>(2);
        var sql = TryExtractSql(message);
        var measurement = TryResolveMeasurement(null, measurements, message);

        if (!string.IsNullOrWhiteSpace(sql))
        {
            if (lowered.Contains("解释", StringComparison.Ordinal)
                || lowered.Contains("扫描", StringComparison.Ordinal)
                || lowered.Contains("explain", StringComparison.Ordinal))
            {
                tools.Add(new CopilotToolInvocation("explain_sql", null, null, null, sql));
                return tools;
            }

            if (LooksLikeWriteSql(sql))
            {
                tools.Add(new CopilotToolInvocation("draft_sql", null, null, null, sql));
                return tools;
            }

            tools.Add(new CopilotToolInvocation(
                "query_sql",
                SonnetDbMcpResults.DefaultToolRowLimit,
                null,
                null,
                sql));
            return tools;
        }

        if (LooksLikeWriteIntent(lowered))
        {
            // 当用户只描述需求（建表 / 插入数据）但没给 SQL 时，先把已有 measurement 列表喂给 LLM，
            // 让最终回答阶段据此生成可执行的 CREATE MEASUREMENT / INSERT 语句。
            tools.Add(new CopilotToolInvocation(
                "list_measurements",
                SonnetDbMcpResults.DefaultToolRowLimit,
                null,
                null,
                null));
            if (measurement is not null)
                tools.Add(new CopilotToolInvocation("describe_measurement", null, null, measurement, null));
            return tools;
        }

        if (LooksLikeDatabaseOverviewIntent(lowered))
        {
            tools.Add(new CopilotToolInvocation(
                "query_sql",
                SonnetDbMcpResults.DefaultToolRowLimit,
                null,
                null,
                "SHOW MEASUREMENTS"));
            return tools;
        }

        if (LooksLikeMeasurementSchemaIntent(lowered) && measurement is not null)
        {
            tools.Add(new CopilotToolInvocation(
                "query_sql",
                SonnetDbMcpResults.DefaultToolRowLimit,
                null,
                null,
                $"DESCRIBE MEASUREMENT {measurement}"));
            return tools;
        }

        if ((lowered.Contains("字段", StringComparison.Ordinal)
                || lowered.Contains("列", StringComparison.Ordinal)
                || lowered.Contains("schema", StringComparison.Ordinal)
                || lowered.Contains("结构", StringComparison.Ordinal))
            && measurement is not null)
        {
            tools.Add(new CopilotToolInvocation("describe_measurement", null, null, measurement, null));
            return tools;
        }

        if ((lowered.Contains("样例", StringComparison.Ordinal)
                || lowered.Contains("示例", StringComparison.Ordinal)
                || lowered.Contains("sample", StringComparison.Ordinal)
                || lowered.Contains("几行", StringComparison.Ordinal))
            && measurement is not null)
        {
            tools.Add(new CopilotToolInvocation(
                "sample_rows",
                null,
                SonnetDbMcpResults.DefaultSampleRowLimit,
                measurement,
                null));
            return tools;
        }

        if ((lowered.Contains("数据库", StringComparison.Ordinal) || lowered.Contains("db", StringComparison.Ordinal))
            && (lowered.Contains("哪些", StringComparison.Ordinal)
                || lowered.Contains("列表", StringComparison.Ordinal)
                || lowered.Contains("list", StringComparison.Ordinal)))
        {
            tools.Add(new CopilotToolInvocation("list_databases", null, null, null, null));
            return tools;
        }

        if (lowered.Contains("measurement", StringComparison.Ordinal)
            || lowered.Contains("表", StringComparison.Ordinal)
            || lowered.Contains("有哪些", StringComparison.Ordinal)
            || lowered.Contains("列表", StringComparison.Ordinal))
        {
            if (measurement is not null
                && (lowered.Contains("字段", StringComparison.Ordinal) || lowered.Contains("列", StringComparison.Ordinal)))
            {
                tools.Add(new CopilotToolInvocation("describe_measurement", null, null, measurement, null));
            }
            else
            {
                tools.Add(new CopilotToolInvocation(
                    "list_measurements",
                    SonnetDbMcpResults.DefaultToolRowLimit,
                    null,
                    null,
                    null));
            }
        }

        return tools.Count > 0
            ? tools
            : [new CopilotToolInvocation("list_measurements", SonnetDbMcpResults.DefaultToolRowLimit, null, null, null)];
    }

    private static bool LooksLikeDatabaseOverviewIntent(string lowered)
    {
        var refersCurrentDatabase = lowered.Contains("当前这个数据库", StringComparison.Ordinal)
            || lowered.Contains("当前数据库", StringComparison.Ordinal)
            || lowered.Contains("这个数据库", StringComparison.Ordinal)
            || lowered.Contains("当前这个库", StringComparison.Ordinal)
            || lowered.Contains("当前库", StringComparison.Ordinal)
            || lowered.Contains("这个库", StringComparison.Ordinal)
            || lowered.Contains("库里", StringComparison.Ordinal)
            || lowered.Contains("数据库里", StringComparison.Ordinal);
        var asksOverview = lowered.Contains("有什么", StringComparison.Ordinal)
            || lowered.Contains("有哪些", StringComparison.Ordinal)
            || lowered.Contains("里面有什么", StringComparison.Ordinal)
            || lowered.Contains("里有什么", StringComparison.Ordinal)
            || lowered.Contains("看一下", StringComparison.Ordinal)
            || lowered.Contains("看一看", StringComparison.Ordinal)
            || lowered.Contains("查一下", StringComparison.Ordinal)
            || lowered.Contains("查一查", StringComparison.Ordinal)
            || lowered.Contains("看看", StringComparison.Ordinal)
            || lowered.Contains("概览", StringComparison.Ordinal);
        var asksSchema = lowered.Contains("measurement", StringComparison.Ordinal)
            || lowered.Contains("schema", StringComparison.Ordinal)
            || lowered.Contains("结构", StringComparison.Ordinal)
            || lowered.Contains("表", StringComparison.Ordinal)
            || lowered.Contains("字段", StringComparison.Ordinal)
            || lowered.Contains("列", StringComparison.Ordinal);

        return (refersCurrentDatabase && asksOverview)
            || (refersCurrentDatabase && asksSchema)
            || lowered.Contains("当前这个数据库里有什么", StringComparison.Ordinal)
            || lowered.Contains("当前数据库里有什么", StringComparison.Ordinal)
            || lowered.Contains("这个库里有什么", StringComparison.Ordinal);
    }

    private static bool LooksLikeMeasurementSchemaIntent(string lowered)
        => lowered.Contains("字段", StringComparison.Ordinal)
            || lowered.Contains("列", StringComparison.Ordinal)
            || lowered.Contains("schema", StringComparison.Ordinal)
            || lowered.Contains("结构", StringComparison.Ordinal)
            || lowered.Contains("tag", StringComparison.Ordinal)
            || lowered.Contains("field", StringComparison.Ordinal);

    private static IReadOnlyList<CopilotToolInvocation> EnsureWriteDraftPlan(
        IReadOnlyList<CopilotToolInvocation> plan,
        string userMessage)
    {
        if (!LooksLikeCreateMeasurementIntent(userMessage.ToLowerInvariant()))
            return plan;

        if (plan.Any(static tool =>
                string.Equals(tool.Name, "draft_sql", StringComparison.OrdinalIgnoreCase)
                || string.Equals(tool.Name, "execute_sql", StringComparison.OrdinalIgnoreCase)))
        {
            return plan;
        }

        var sql = TryBuildCreateMeasurementSql(userMessage);
        if (sql is null)
            return plan;

        var draft = new CopilotToolInvocation("draft_sql", MaxRows: null, N: null, Measurement: null, Sql: sql, Database: null);
        if (plan.Count == 0)
            return [draft];

        // ReAct 模式下每轮只执行 1 个工具；heuristic 已包含 list_measurements，
        // 追加 draft 后返回，RunAsync 只会取 plan[0]，下一轮再执行 draft。
        var augmented = new List<CopilotToolInvocation>(plan.Count + 1);
        foreach (var tool in plan)
            augmented.Add(tool);

        augmented.Add(draft);
        return augmented;
    }

    private bool TryBuildProvisioningPlan(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<CopilotToolObservation> observations,
        out IReadOnlyList<CopilotToolInvocation> plan)
    {
        var intent = CopilotProvisioning.TryExtractIntent(conversation.LatestUserMessage);
        if (intent is null)
        {
            plan = [];
            return false;
        }

        var createDatabaseSql = CopilotProvisioning.BuildCreateDatabaseSql(intent);
        var createMeasurementSql = CopilotProvisioning.BuildCreateMeasurementSql(intent);
        var databaseExists = context.VisibleDatabases.Any(database =>
                string.Equals(database, intent.DatabaseName, StringComparison.OrdinalIgnoreCase))
            || HasObservedSql(observations, "execute_sql", createDatabaseSql, intent.DatabaseName);

        if (!databaseExists && !HasObservedSql(observations, "draft_sql", createDatabaseSql, intent.DatabaseName))
        {
            plan = [new CopilotToolInvocation("draft_sql", null, null, null, createDatabaseSql, intent.DatabaseName)];
            return true;
        }

        if (createMeasurementSql is not null
            && !HasObservedSql(observations, "draft_sql", createMeasurementSql, intent.DatabaseName))
        {
            plan = [new CopilotToolInvocation("draft_sql", null, null, null, createMeasurementSql, intent.DatabaseName)];
            return true;
        }

        if (intent.ExecuteNow
            && context.CanWrite
            && context.CanUseControlPlane
            && !databaseExists
            && !HasObservedSql(observations, "execute_sql", createDatabaseSql, intent.DatabaseName))
        {
            plan = [new CopilotToolInvocation("execute_sql", SonnetDbMcpResults.DefaultToolRowLimit, null, null, createDatabaseSql, intent.DatabaseName)];
            return true;
        }

        if (intent.ExecuteNow
            && context.CanWrite
            && createMeasurementSql is not null
            && !HasObservedSql(observations, "execute_sql", createMeasurementSql, intent.DatabaseName))
        {
            plan = [new CopilotToolInvocation("execute_sql", SonnetDbMcpResults.DefaultToolRowLimit, null, null, createMeasurementSql, intent.DatabaseName)];
            return true;
        }

        plan = [];
        return true;
    }

    private static bool HasObservedSql(
        IReadOnlyList<CopilotToolObservation> observations,
        string toolName,
        string sql,
        string? database)
    {
        foreach (var observation in observations)
        {
            if (!string.Equals(observation.Name, toolName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryExtractToolArguments(observation.ArgumentsJson, out var observedSql, out var observedDatabase))
                continue;

            if (!string.Equals(CollapseWhitespace(observedSql), CollapseWhitespace(sql), StringComparison.OrdinalIgnoreCase))
                continue;

            var left = observedDatabase ?? string.Empty;
            var right = database ?? string.Empty;
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool TryExtractToolArguments(string argumentsJson, out string sql, out string? database)
    {
        sql = string.Empty;
        database = null;

        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("sql", out var sqlElement)
                || sqlElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            sql = sqlElement.GetString() ?? string.Empty;
            if (document.RootElement.TryGetProperty("database", out var databaseElement)
                && databaseElement.ValueKind == JsonValueKind.String)
            {
                database = databaseElement.GetString();
            }

            return !string.IsNullOrWhiteSpace(sql);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private IReadOnlyList<string> GetMeasurements(string databaseName, Tsdb? database)
        => database is null ? [] : _schemaCache.GetMeasurements(databaseName, database);

    private static string ResolveToolDatabaseName(CopilotAgentContext context, CopilotToolInvocation tool, SqlStatement? statement = null)
    {
        if (!string.IsNullOrWhiteSpace(tool.Database))
            return tool.Database.Trim();
        if (statement is CreateDatabaseStatement createDatabase)
            return createDatabase.DatabaseName;
        return context.DatabaseName;
    }

    private Tsdb? TryResolveToolDatabase(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        if (!string.IsNullOrWhiteSpace(tool.Database))
        {
            if (context.Database is not null
                && string.Equals(context.DatabaseName, tool.Database, StringComparison.OrdinalIgnoreCase))
            {
                return context.Database;
            }

            return _registry.TryGet(tool.Database, out var explicitDatabase) ? explicitDatabase : null;
        }

        return context.Database;
    }

    private Tsdb RequireToolDatabase(CopilotAgentContext context, CopilotToolInvocation tool, string toolName)
    {
        var databaseName = ResolveToolDatabaseName(context, tool);
        return TryResolveToolDatabase(context, tool)
            ?? throw new InvalidOperationException($"工具 {toolName} 需要数据库上下文，但数据库 '{databaseName}' 当前不存在或不可用。");
    }

    private async Task<CopilotToolExecutionResult> ExecuteToolAsync(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        CopilotToolInvocation tool,
        CancellationToken cancellationToken)
    {
        switch (tool.Name)
        {
            case "list_databases":
                return new CopilotToolExecutionResult(
                    tool,
                    SerializeToolResult(
                        new McpDatabaseListResult(context.DatabaseName, context.VisibleDatabases),
                        ServerJsonContext.Default.McpDatabaseListResult),
                    []);
            case "list_measurements":
                return new CopilotToolExecutionResult(tool, ExecuteListMeasurements(context, tool), []);
            case "describe_measurement":
                return new CopilotToolExecutionResult(tool, ExecuteDescribeMeasurement(context, tool), []);
            case "sample_rows":
                return new CopilotToolExecutionResult(tool, ExecuteSampleRows(context, tool), []);
            case "explain_sql":
                return new CopilotToolExecutionResult(tool, ExecuteExplainSql(context, tool), []);
            case "draft_sql":
                return new CopilotToolExecutionResult(tool, ExecuteDraftSql(context, tool), []);
            case "execute_sql":
                return new CopilotToolExecutionResult(tool, ExecuteExecuteSql(context, tool), []);
            case "query_sql":
                return await ExecuteQuerySqlWithRepairAsync(
                    context,
                    conversation,
                    docs,
                    loadedSkills,
                    tool,
                    cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"不支持的 Copilot 工具 '{tool.Name}'。");
        }
    }

    private string ExecuteListMeasurements(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var maxRows = tool.MaxRows ?? SonnetDbMcpResults.DefaultToolRowLimit;
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "list_measurements");
        var measurements = _schemaCache.GetMeasurements(databaseName, database);
        var names = new List<string>(Math.Min(measurements.Count, maxRows));
        for (var i = 0; i < measurements.Count && i < maxRows; i++)
            names.Add(measurements[i]);

        var payload = new McpMeasurementListResult(
            databaseName,
            names,
            Truncated: measurements.Count > maxRows);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpMeasurementListResult);
    }

    private string ExecuteDescribeMeasurement(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var measurement = tool.Measurement
            ?? throw new InvalidOperationException("describe_measurement 缺少 measurement 参数。");
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "describe_measurement");
        var payload = _schemaCache.GetMeasurementSchema(databaseName, measurement, database);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpMeasurementSchemaResult);
    }

    private string ExecuteSampleRows(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var measurement = tool.Measurement
            ?? throw new InvalidOperationException("sample_rows 缺少 measurement 参数。");
        var rows = tool.N ?? SonnetDbMcpResults.DefaultSampleRowLimit;
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "sample_rows");

        var statement = new SelectStatement(
            Projections: [new SelectItem(StarExpression.Instance, Alias: null)],
            Measurement: measurement,
            Where: null,
            GroupBy: [],
            TableValuedFunction: null,
            Pagination: new PaginationSpec(0, checked(rows + 1)));

        var executionResult = SqlExecutor.ExecuteStatement(database, statement);
        if (executionResult is not SelectExecutionResult selectResult)
            throw new InvalidOperationException("sample_rows 未返回结果集。");

        var (resultRows, truncated) = SonnetDbMcpResults.SliceRows(selectResult, rows, canTruncate: true);
        var payload = new McpSampleRowsResult(
            Database: databaseName,
            Measurement: measurement,
            RequestedRows: rows,
            Columns: selectResult.Columns,
            Rows: resultRows,
            ReturnedRows: resultRows.Count,
            Truncated: truncated);

        return SerializeToolResult(payload, ServerJsonContext.Default.McpSampleRowsResult);
    }

    private string ExecuteExplainSql(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("explain_sql 缺少 sql 参数。");
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "explain_sql");
        var statement = SqlParser.Parse(sql);
        if (!IsReadOnlyStatement(statement))
        {
            throw new InvalidOperationException(
                "explain_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES、DESCRIBE [MEASUREMENT|TABLE] 与 EXPLAIN。");
        }

        var payload = _explainSqlService.Explain(databaseName, database, statement);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpExplainSqlResult);
    }

    private string ExecuteDraftSql(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("draft_sql 缺少 sql 参数。");

        SqlStatement statement;
        try
        {
            statement = SqlParser.Parse(sql);
        }
        catch (SqlParseException ex)
        {
            throw new SqlExecutionException(sql, "parse", ex.Message, ex);
        }

        if (!IsDraftableStatement(statement))
        {
            throw new InvalidOperationException(
                "draft_sql 仅支持 CREATE DATABASE、CREATE MEASUREMENT、CREATE TABLE、DROP MEASUREMENT、DROP TABLE、INSERT、UPDATE、DELETE、SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT|TABLE]。");
        }

        var databaseName = ResolveToolDatabaseName(context, tool, statement);
        var database = TryResolveToolDatabase(context, tool);
        var (statementType, measurement, isWrite) = DescribeStatement(statement);
        bool? exists = null;
        var notes = new List<string>(3);

        if (statement is CreateDatabaseStatement createDatabase)
        {
            var alreadyVisible = context.VisibleDatabases.Any(databaseItem =>
                string.Equals(databaseItem, createDatabase.DatabaseName, StringComparison.OrdinalIgnoreCase));
            notes.Add(alreadyVisible
                ? $"数据库 '{createDatabase.DatabaseName}' 已存在，可以直接复用。"
                : $"数据库 '{createDatabase.DatabaseName}' 当前不存在，可以先执行该 CREATE DATABASE 语句创建。");
        }
        else if (database is null)
        {
            notes.Add($"数据库 '{databaseName}' 当前不存在，执行该语句前需要先 CREATE DATABASE {databaseName}。");
        }

        if (isWrite && measurement is not null && database is not null)
        {
            var existingMeasurement = database.Measurements.TryGet(measurement);
            var existingTable = database.Tables.Catalog.TryGet(measurement);
            var existing = existingMeasurement is not null || existingTable is not null;
            exists = existing;
            switch (statement)
            {
                case CreateMeasurementStatement when existingMeasurement is not null:
                    notes.Add($"measurement '{measurement}' 已经存在；如需追加列，请改用 INSERT 而不是 CREATE。");
                    break;
                case CreateMeasurementStatement when existingMeasurement is null:
                    notes.Add($"measurement '{measurement}' 当前不存在，可以执行该 CREATE 语句创建。");
                    break;
                case CreateTableStatement when existingTable is not null:
                    notes.Add($"关系表 '{measurement}' 已经存在；如需重建，请先确认是否需要 DROP TABLE。");
                    break;
                case CreateTableStatement when existingTable is null:
                    notes.Add($"关系表 '{measurement}' 当前不存在，可以执行该 CREATE TABLE 语句创建。");
                    break;
                case InsertStatement when !existing:
                    notes.Add($"'{measurement}' 尚未创建，执行 INSERT 之前需要先 CREATE MEASUREMENT 或 CREATE TABLE。");
                    break;
                case UpdateStatement when existingTable is null:
                    notes.Add($"关系表 '{measurement}' 不存在，UPDATE 无法执行。");
                    break;
                case DeleteStatement when !existing:
                    notes.Add($"'{measurement}' 不存在，DELETE 不会影响任何数据。");
                    break;
                case DropTableStatement when existingTable is null:
                    notes.Add($"关系表 '{measurement}' 不存在，DROP TABLE 不会删除任何数据。");
                    break;
            }
        }

        if (isWrite)
        {
            if (statement is CreateDatabaseStatement)
            {
                notes.Add(context.CanWrite && context.CanUseControlPlane
                    ? "当前会话处于读写模式且具备控制面权限，可以调用 execute_sql 直接创建数据库。"
                    : "当前会话无法直接创建数据库。你可以复制上方 SQL 交给管理员执行，或切换到具备控制面权限的账号后再试。");
            }
            else
            {
                notes.Add(context.CanWrite
                    ? "当前凭据具备写权限，可以调用 execute_sql 直接执行。"
                    : "当前凭据没有写权限。您可以：① 请管理员执行 GRANT WRITE ON DATABASE <db> TO <user> 为您授权（授权后在当前会话中即可生效）；② 将上方 SQL 复制后，切换到 SQL Console 选项卡，以具备写权限的账号粘贴执行。");
            }
        }

        var payload = new McpDraftSqlResult(
            Database: databaseName,
            StatementType: statementType,
            Sql: sql.Trim(),
            Measurement: measurement,
            IsWrite: isWrite,
            MeasurementExists: exists,
            Notes: notes);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpDraftSqlResult);
    }

    private string ExecuteExecuteSql(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("execute_sql 缺少 sql 参数。");

        SqlStatement statement;
        try
        {
            statement = SqlParser.Parse(sql);
        }
        catch (SqlParseException ex)
        {
            throw new SqlExecutionException(sql, "parse", ex.Message, ex);
        }

        if (!IsDraftableStatement(statement))
        {
            throw new SqlExecutionException(
                sql,
                "validate",
                "execute_sql 仅支持 CREATE DATABASE、CREATE MEASUREMENT、CREATE TABLE、DROP MEASUREMENT、DROP TABLE、INSERT、UPDATE、DELETE、SELECT、SHOW MEASUREMENTS / SHOW TABLES 与 DESCRIBE [MEASUREMENT|TABLE]。");
        }

        var databaseName = ResolveToolDatabaseName(context, tool, statement);
        var (statementType, measurement, isWrite) = DescribeStatement(statement);
        if (isWrite && !context.CanWrite)
        {
            throw new SqlExecutionException(
                sql,
                "permission",
                $"当前凭据对数据库 '{databaseName}' 没有写权限，无法执行 {statementType.ToUpperInvariant()} 语句。");
        }

        var maxRows = tool.MaxRows ?? SonnetDbMcpResults.DefaultToolRowLimit;
        SqlStatement executable = statement;
        var canTruncate = false;
        if (statement is SelectStatement selectStatement)
            executable = SonnetDbMcpResults.ApplyToolRowLimit(selectStatement, maxRows, out canTruncate);

        object? executionResult;
        try
        {
            if (statement is CreateDatabaseStatement)
            {
                if (!context.CanUseControlPlane)
                {
                    throw new InvalidOperationException("当前凭据没有控制面权限，无法直接创建数据库。");
                }

                executionResult = SqlExecutor.ExecuteControlPlaneStatement(statement, _controlPlane);
            }
            else
            {
                var database = RequireToolDatabase(context, tool, "execute_sql");
                executionResult = SqlExecutor.ExecuteStatement(database, executable);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new SqlExecutionException(sql, "execute", ex.Message, ex);
        }

        IReadOnlyList<string>? columns = null;
        IReadOnlyList<IReadOnlyList<JsonElementValue>>? rows = null;
        int? returnedRows = null;
        int? rowsAffected = null;
        var truncated = false;

        switch (executionResult)
        {
            case SelectExecutionResult selectResult:
                var (rowList, isTruncated) = SonnetDbMcpResults.SliceRows(selectResult, maxRows, canTruncate);
                columns = selectResult.Columns;
                rows = rowList;
                returnedRows = rowList.Count;
                truncated = isTruncated;
                break;
            case InsertExecutionResult insertResult:
                rowsAffected = insertResult.RowsInserted;
                break;
            case DeleteExecutionResult deleteResult:
                rowsAffected = deleteResult.TombstonesAdded;
                break;
            case RowsAffectedExecutionResult affectedResult:
                rowsAffected = affectedResult.RowsAffected;
                break;
            case MeasurementSchema schema:
                rowsAffected = schema.Columns.Count;
                break;
            case TableSchema schema:
                rowsAffected = schema.Columns.Count;
                break;
            case int affected when statement is CreateDatabaseStatement:
                rowsAffected = affected;
                break;
        }

        var payload = new McpExecuteSqlResult(
            Database: databaseName,
            StatementType: statementType,
            Sql: sql.Trim(),
            Measurement: measurement,
            RowsAffected: rowsAffected,
            Columns: columns,
            Rows: rows,
            ReturnedRows: returnedRows,
            Truncated: truncated);
        return SerializeToolResult(payload, ServerJsonContext.Default.McpExecuteSqlResult);
    }

    private static (string StatementType, string? Measurement, bool IsWrite) DescribeStatement(SqlStatement statement)
        => statement switch
        {
            CreateDatabaseStatement createDatabase => ("create_database", createDatabase.DatabaseName, true),
            CreateMeasurementStatement create => ("create_measurement", create.Name, true),
            InsertStatement insert => ("insert", insert.Measurement, true),
            DeleteStatement delete => ("delete", delete.Measurement, true),
            CreateTableStatement createTable => ("create_table", createTable.Name, true),
            DropMeasurementStatement dropMeasurement => ("drop_measurement", dropMeasurement.Name, true),
            DropTableStatement dropTable => ("drop_table", dropTable.Name, true),
            UpdateStatement update => ("update", update.TableName, true),
            SelectStatement select => ("select", select.Measurement, false),
            ShowMeasurementsStatement => ("show_measurements", null, false),
            ShowTablesStatement => ("show_tables", null, false),
            DescribeMeasurementStatement describe => ("describe_measurement", describe.Name, false),
            DescribeTableStatement describeTable => ("describe_table", describeTable.Name, false),
            _ => ("unknown", null, false),
        };

    private static bool IsDraftableStatement(SqlStatement statement)
        => statement is CreateDatabaseStatement
            or CreateMeasurementStatement
            or CreateTableStatement
            or DropMeasurementStatement
            or DropTableStatement
            or InsertStatement
            or UpdateStatement
            or DeleteStatement
            or SelectStatement
            or ShowMeasurementsStatement
            or ShowTablesStatement
            or DescribeMeasurementStatement
            or DescribeTableStatement;

    private async Task<CopilotToolExecutionResult> ExecuteQuerySqlWithRepairAsync(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        CopilotToolInvocation tool,
        CancellationToken cancellationToken)
    {
        var currentTool = tool;
        var events = new List<CopilotChatEvent>();

        for (var attempt = 1; attempt <= MaxSqlRepairAttempts; attempt++)
        {
            try
            {
                return new CopilotToolExecutionResult(currentTool, TryExecuteQuerySql(context, currentTool), events);
            }
            catch (SqlExecutionException ex) when (attempt < MaxSqlRepairAttempts)
            {
                var rewrittenSql = await RepairSqlAsync(
                    context,
                    conversation,
                    docs,
                    loadedSkills,
                    currentTool,
                    ex,
                    cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(rewrittenSql)
                    || string.Equals(
                        CollapseWhitespace(rewrittenSql),
                        CollapseWhitespace(currentTool.Sql ?? string.Empty),
                        StringComparison.OrdinalIgnoreCase))
                {
                    var errorPayload = BuildSqlErrorPayload(ex, attempt, final: true);
                    events.Add(new CopilotChatEvent(
                        Type: "tool_retry",
                        Message: $"query_sql 第 {attempt} 次失败后未得到可用的改写 SQL。",
                        ToolName: currentTool.Name,
                        ToolArguments: FormatToolArguments(currentTool),
                        ToolResult: errorPayload,
                        Attempt: attempt));
                    return new CopilotToolExecutionResult(currentTool, errorPayload, events);
                }

                currentTool = currentTool with { Sql = rewrittenSql };
                events.Add(new CopilotChatEvent(
                    Type: "tool_retry",
                    Message: $"query_sql 第 {attempt} 次执行失败，已依据错误信息改写 SQL 并重试。",
                    ToolName: currentTool.Name,
                    ToolArguments: FormatToolArguments(currentTool),
                    ToolResult: BuildSqlErrorPayload(ex, attempt, final: false),
                    Attempt: attempt));
            }
            catch (SqlExecutionException ex)
            {
                return new CopilotToolExecutionResult(currentTool, BuildSqlErrorPayload(ex, attempt, final: true), events);
            }
        }

        throw new InvalidOperationException("query_sql 修复循环意外结束。");
    }

    private async Task<string?> RepairSqlAsync(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        CopilotToolInvocation tool,
        SqlExecutionException exception,
        CancellationToken cancellationToken)
    {
        try
        {
            var prompt = BuildSqlRepairPrompt(context, conversation, docs, loadedSkills, tool, exception);
            var response = await _chatProvider.CompleteAsync(
                [
                    new AiMessage("system", SqlRepairSystemPrompt),
                    new AiMessage("user", prompt),
                ],
                context.ModelOverride,
                cancellationToken).ConfigureAwait(false);
            return TryExtractSql(response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot SQL repair failed for sql={Sql}.", tool.Sql);
            return null;
        }
    }

    private string TryExecuteQuerySql(CopilotAgentContext context, CopilotToolInvocation tool)
    {
        var sql = tool.Sql
            ?? throw new InvalidOperationException("query_sql 缺少 sql 参数。");
        var maxRows = tool.MaxRows ?? SonnetDbMcpResults.DefaultToolRowLimit;
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = RequireToolDatabase(context, tool, "query_sql");

        SqlStatement statement;
        try
        {
            statement = SqlParser.Parse(sql);
        }
        catch (SqlParseException ex)
        {
            throw new SqlExecutionException(sql, "parse", ex.Message, ex);
        }

        if (!IsReadOnlyStatement(statement))
        {
            throw new SqlExecutionException(
                sql,
                "validate",
                "query_sql 仅支持 SELECT、SHOW MEASUREMENTS / SHOW TABLES、DESCRIBE [MEASUREMENT|TABLE] 与 EXPLAIN。");
        }

        SqlStatement executable = statement;
        var canTruncate = false;
        if (statement is SelectStatement select)
            executable = SonnetDbMcpResults.ApplyToolRowLimit(select, maxRows, out canTruncate);

        object? executionResult;
        try
        {
            executionResult = SqlExecutor.ExecuteStatement(database, executable);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            throw new SqlExecutionException(sql, "execute", ex.Message, ex);
        }

        if (executionResult is not SelectExecutionResult selectResult)
            throw new SqlExecutionException(sql, "execute", "只读 SQL 未返回结果集。");

        var (rows, truncated) = SonnetDbMcpResults.SliceRows(selectResult, maxRows, canTruncate);
        var payload = new McpSqlQueryResult(
            databaseName,
            StatementType: GetStatementType(statement),
            Columns: selectResult.Columns,
            Rows: rows,
            ReturnedRows: rows.Count,
            Truncated: truncated);

        return SerializeToolResult(payload, ServerJsonContext.Default.McpSqlQueryResult);
    }

    private static string BuildPlannerPrompt(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        IReadOnlyList<CopilotToolObservation> observations)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"当前数据库：{context.DatabaseName}");
        builder.AppendLine($"当前可见数据库：{string.Join(", ", context.VisibleDatabases)}");
        builder.AppendLine();
        AppendConversationHistory(builder, conversation.History);
        builder.AppendLine("当前用户问题：");
        builder.AppendLine(conversation.LatestUserMessage);
        builder.AppendLine();

        if (loadedSkills.Count > 0)
        {
            builder.AppendLine("已召回技能：");
            foreach (var skill in loadedSkills)
            {
                builder.Append("- ");
                builder.Append(skill.Name);
                if (!string.IsNullOrWhiteSpace(skill.Description))
                {
                    builder.Append("：");
                    builder.Append(skill.Description);
                }
                if (skill.RequiresTools.Count > 0)
                {
                    builder.Append("；建议工具=");
                    builder.Append(string.Join(", ", skill.RequiresTools));
                }
                builder.AppendLine();
            }
            builder.AppendLine();
        }

        if (docs.Count > 0)
        {
            builder.AppendLine("已召回文档摘要：");
            foreach (var doc in docs.Take(3))
            {
                builder.Append("- ");
                builder.Append(string.IsNullOrWhiteSpace(doc.Title) ? doc.Source : doc.Title);
                builder.Append("：");
                builder.AppendLine(Truncate(CollapseWhitespace(doc.Content), 240));
            }
        }

        // 把本轮之前已执行的工具结果注入给 Planner，让它基于真实结果决定下一步
        if (observations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("【已执行工具结果】（请基于这些结果决定下一步，不要重复调用已成功的工具）：");
            foreach (var obs in observations)
            {
                builder.Append("- tool=");
                builder.Append(obs.Name);
                builder.Append(" args=");
                builder.Append(obs.ArgumentsJson);
                builder.Append(" result=");
                builder.AppendLine(Truncate(obs.ResultJson, 600));
            }
        }

        return builder.ToString().Trim();
    }

    private static string BuildAnswerPrompt(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        IReadOnlyList<CopilotToolObservation> observations,
        IReadOnlyList<CopilotCitation> citations)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"当前数据库：{context.DatabaseName}");
        builder.AppendLine($"当前可见数据库：{string.Join(", ", context.VisibleDatabases)}");
        builder.AppendLine();
        AppendConversationHistory(builder, conversation.History);
        builder.AppendLine("当前用户问题：");
        builder.AppendLine(conversation.LatestUserMessage);
        builder.AppendLine();

        if (loadedSkills.Count > 0)
        {
            builder.AppendLine("已加载技能：");
            foreach (var skill in loadedSkills)
            {
                builder.Append("- ");
                builder.Append(skill.Name);
                builder.Append("：");
                builder.AppendLine(Truncate(CollapseWhitespace($"{skill.Description} {skill.Body}"), 600));
            }
            builder.AppendLine();
        }

        if (docs.Count > 0)
        {
            builder.AppendLine("文档上下文：");
            foreach (var doc in docs)
            {
                builder.Append("- ");
                builder.Append(doc.Source);
                builder.Append(" / ");
                builder.Append(string.IsNullOrWhiteSpace(doc.Section) ? doc.Title : doc.Section);
                builder.Append("：");
                builder.AppendLine(Truncate(CollapseWhitespace(doc.Content), 400));
            }
            builder.AppendLine();
        }

        if (observations.Count > 0)
        {
            builder.AppendLine("工具结果：");
            foreach (var observation in observations)
            {
                builder.Append("- tool=");
                builder.Append(observation.Name);
                builder.Append(" args=");
                builder.Append(observation.ArgumentsJson);
                builder.Append(" result=");
                builder.AppendLine(observation.ResultJson);
            }
            builder.AppendLine();
        }

        if (citations.Count > 0)
        {
            builder.AppendLine("可用 citations：");
            foreach (var citation in citations)
            {
                builder.Append('[');
                builder.Append(citation.Id);
                builder.Append("] kind=");
                builder.Append(citation.Kind);
                builder.Append("; title=");
                builder.Append(citation.Title);
                builder.Append("; source=");
                builder.Append(citation.Source);
                builder.Append("; snippet=");
                builder.AppendLine(citation.Snippet);
            }
        }

        return builder.ToString().Trim();
    }

    private string BuildSqlRepairPrompt(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        CopilotToolInvocation tool,
        SqlExecutionException exception)
    {
        var databaseName = ResolveToolDatabaseName(context, tool);
        var database = TryResolveToolDatabase(context, tool);
        var builder = new StringBuilder();
        builder.AppendLine($"当前数据库：{databaseName}");
        builder.AppendLine($"当前可见数据库：{string.Join(", ", context.VisibleDatabases)}");
        builder.AppendLine($"已知 measurements：{string.Join(", ", GetMeasurements(databaseName, database))}");
        builder.AppendLine();
        AppendConversationHistory(builder, conversation.History);
        builder.AppendLine("当前用户问题：");
        builder.AppendLine(conversation.LatestUserMessage);
        builder.AppendLine();
        builder.AppendLine("失败 SQL：");
        builder.AppendLine(tool.Sql);
        builder.AppendLine();
        builder.AppendLine($"失败阶段：{exception.Phase}");
        builder.AppendLine("错误消息：");
        builder.AppendLine(exception.Message);
        builder.AppendLine();

        if (loadedSkills.Count > 0)
        {
            builder.AppendLine("已加载技能摘要：");
            foreach (var skill in loadedSkills)
            {
                builder.Append("- ");
                builder.Append(skill.Name);
                builder.Append("：");
                builder.AppendLine(Truncate(CollapseWhitespace($"{skill.Description} {skill.Body}"), 320));
            }
            builder.AppendLine();
        }

        if (docs.Count > 0)
        {
            builder.AppendLine("文档摘要：");
            foreach (var doc in docs.Take(3))
            {
                builder.Append("- ");
                builder.Append(doc.Source);
                builder.Append("：");
                builder.AppendLine(Truncate(CollapseWhitespace(doc.Content), 240));
            }
            builder.AppendLine();
        }

        builder.AppendLine("请返回修正后的只读 SQL。不要解释，不要 Markdown，不要 JSON。");
        return builder.ToString().Trim();
    }

    private static string BuildFallbackAnswer(
        CopilotAgentContext context,
        CopilotConversation conversation,
        IReadOnlyList<CopilotToolObservation> observations,
        IReadOnlyList<CopilotCitation> citations)
    {
        if (TryBuildSqlFallbackAnswer(conversation, observations, out var sqlAnswer))
            return sqlAnswer;

        if (observations.Count == 0)
        {
            return citations.Count > 0
                ? $"我已经完成文档与技能召回，但当前没有额外工具结果可补充；你可以结合已有引用继续追问更具体的字段、SQL 或抽样需求。[{citations[0].Id}]"
                : $"我已经检查了数据库 '{context.DatabaseName}' 的可用上下文，但当前没有足够证据给出更具体的回答。";
        }

        var summary = string.Join("、", observations.Select(static item => item.Name));
        var citationSuffix = citations.Count > 0
            ? string.Concat(citations.Select(static item => $"[{item.Id}]"))
            : string.Empty;
        return $"我已经执行了这些工具：{summary}。请结合返回的结构化结果继续确认或缩小问题范围。{citationSuffix}".Trim();
    }

    private static bool TryBuildSqlFallbackAnswer(
        CopilotConversation conversation,
        IReadOnlyList<CopilotToolObservation> observations,
        out string answer)
    {
        if (TryBuildProvisioningSqlFallbackAnswer(observations, out answer))
            return true;

        foreach (var observation in observations)
        {
            if (string.Equals(observation.Name, "draft_sql", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpDraftSqlResult) is { } draft)
            {
                answer = BuildDraftSqlFallbackAnswer(draft, observation.Citation.Id);
                return true;
            }

            if (string.Equals(observation.Name, "execute_sql", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpExecuteSqlResult) is { } executed)
            {
                answer = BuildExecuteSqlFallbackAnswer(executed, observation.Citation.Id);
                return true;
            }
        }

        foreach (var observation in observations)
        {
            if (string.Equals(observation.Name, "query_sql", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpSqlQueryResult) is { } queryResult
                && TryBuildQuerySqlFallbackAnswer(queryResult, observation.ArgumentsJson, observation.Citation.Id, out answer))
            {
                return true;
            }

            if (string.Equals(observation.Name, "list_measurements", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpMeasurementListResult) is { } measurementList)
            {
                answer = BuildMeasurementListFallbackAnswer(
                    measurementList.Database,
                    measurementList.Measurements,
                    measurementList.Truncated,
                    observation.Citation.Id,
                    fromSql: false);
                return true;
            }

            if (string.Equals(observation.Name, "describe_measurement", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpMeasurementSchemaResult) is { } schema)
            {
                answer = BuildMeasurementSchemaFallbackAnswer(
                    schema.Database,
                    schema.Measurement,
                    schema.Columns,
                    observation.Citation.Id,
                    fromSql: false);
                return true;
            }
        }

        var createSql = TryBuildCreateMeasurementSql(conversation.LatestUserMessage);
        if (createSql is not null)
        {
            answer = BuildInferredCreateSqlFallbackAnswer(createSql);
            return true;
        }

        answer = string.Empty;
        return false;
    }

    private static bool TryBuildQuerySqlFallbackAnswer(
        McpSqlQueryResult queryResult,
        string argumentsJson,
        string citationId,
        out string answer)
    {
        if (string.Equals(queryResult.StatementType, "show_measurements", StringComparison.OrdinalIgnoreCase))
        {
            answer = BuildMeasurementListFallbackAnswer(
                queryResult.Database,
                ExtractMeasurementNames(queryResult),
                queryResult.Truncated,
                citationId,
                fromSql: true);
            return true;
        }

        if (string.Equals(queryResult.StatementType, "describe_measurement", StringComparison.OrdinalIgnoreCase)
            && TryExtractMeasurementSchema(
                queryResult,
                TryExtractMeasurementFromToolArguments(argumentsJson),
                out var measurement,
                out var columns))
        {
            answer = BuildMeasurementSchemaFallbackAnswer(
                queryResult.Database,
                measurement,
                columns,
                citationId,
                fromSql: true);
            return true;
        }

        answer = string.Empty;
        return false;
    }

    private static T? TryDeserializeToolResult<T>(
        string json,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string BuildDraftSqlFallbackAnswer(McpDraftSqlResult draft, string citationId)
    {
        var builder = new StringBuilder();
        builder.AppendLine(draft.StatementType switch
        {
            "create_database" => "已经为你起草建库 SQL：",
            "create_measurement" => "已经为你起草建表 SQL：",
            "insert" => "已经为你起草写入 SQL：",
            "delete" => "已经为你起草删除 SQL：",
            _ => "已经为你起草 SQL：",
        });
        AppendSqlBlock(builder, draft.Sql);
        AppendNotes(builder, draft.Notes);
        AppendCitation(builder, citationId);
        return builder.ToString().Trim();
    }

    private static string BuildMeasurementListFallbackAnswer(
        string database,
        IReadOnlyList<string> measurements,
        bool truncated,
        string citationId,
        bool fromSql)
    {
        var builder = new StringBuilder();
        builder.AppendLine(fromSql
            ? $"我已经对当前数据库 `{database}` 执行了 `SHOW MEASUREMENTS`。"
            : $"我已经查看了当前数据库 `{database}` 的 measurement 列表。");

        if (measurements.Count == 0)
        {
            builder.AppendLine("当前库里还没有任何 measurement。");
            AppendCitation(builder, citationId);
            return builder.ToString().Trim();
        }

        builder.AppendLine($"当前库里有 {measurements.Count}{(truncated ? "+" : string.Empty)} 个 measurement：");
        foreach (var measurement in measurements)
        {
            if (!string.IsNullOrWhiteSpace(measurement))
                builder.AppendLine($"- {measurement}");
        }

        builder.AppendLine("如果你愿意，我可以继续按其中某个 measurement 执行 `DESCRIBE MEASUREMENT <name>` 看字段结构。");
        AppendCitation(builder, citationId);
        return builder.ToString().Trim();
    }

    private static string BuildMeasurementSchemaFallbackAnswer(
        string database,
        string measurement,
        IReadOnlyList<McpMeasurementColumnResult> columns,
        string citationId,
        bool fromSql)
    {
        var builder = new StringBuilder();
        builder.AppendLine(fromSql
            ? $"我已经对数据库 `{database}` 执行了 `DESCRIBE MEASUREMENT {measurement}`。"
            : $"我已经查看了数据库 `{database}` 中 measurement `{measurement}` 的结构。");

        if (columns.Count == 0)
        {
            builder.AppendLine("当前没有读取到任何列定义。");
            AppendCitation(builder, citationId);
            return builder.ToString().Trim();
        }

        builder.AppendLine($"`{measurement}` 目前包含 {columns.Count} 列：");
        foreach (var column in columns)
        {
            builder.AppendLine($"- {column.Name}：{column.ColumnType} / {column.DataType}");
        }

        AppendCitation(builder, citationId);
        return builder.ToString().Trim();
    }

    private static string BuildExecuteSqlFallbackAnswer(McpExecuteSqlResult executed, string citationId)
    {
        var builder = new StringBuilder();
        builder.AppendLine(executed.StatementType switch
        {
            "create_database" => "SQL 已执行，数据库已创建：",
            "create_measurement" => "SQL 已执行，measurement 已创建：",
            "insert" => "SQL 已执行，数据已写入：",
            "delete" => "SQL 已执行，删除标记已写入：",
            _ => "SQL 已执行：",
        });
        AppendSqlBlock(builder, executed.Sql);
        if (executed.RowsAffected is not null)
            builder.AppendLine($"影响行数/列数：{executed.RowsAffected.Value}。");
        if (executed.ReturnedRows is not null)
            builder.AppendLine($"返回行数：{executed.ReturnedRows.Value}。");
        AppendCitation(builder, citationId);
        return builder.ToString().Trim();
    }

    private static bool TryBuildProvisioningSqlFallbackAnswer(
        IReadOnlyList<CopilotToolObservation> observations,
        out string answer)
    {
        var drafted = new List<(McpDraftSqlResult Payload, string CitationId)>();
        var executed = new List<(McpExecuteSqlResult Payload, string CitationId)>();

        foreach (var observation in observations)
        {
            if (string.Equals(observation.Name, "draft_sql", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpDraftSqlResult) is { } draft)
            {
                drafted.Add((draft, observation.Citation.Id));
            }

            if (string.Equals(observation.Name, "execute_sql", StringComparison.OrdinalIgnoreCase)
                && TryDeserializeToolResult(observation.ResultJson, ServerJsonContext.Default.McpExecuteSqlResult) is { } execute)
            {
                executed.Add((execute, observation.Citation.Id));
            }
        }

        var hasCreateDatabase = drafted.Any(static item => string.Equals(item.Payload.StatementType, "create_database", StringComparison.OrdinalIgnoreCase))
            || executed.Any(static item => string.Equals(item.Payload.StatementType, "create_database", StringComparison.OrdinalIgnoreCase));
        if (!hasCreateDatabase)
        {
            answer = string.Empty;
            return false;
        }

        var builder = new StringBuilder();
        if (executed.Count > 0)
        {
            builder.AppendLine("我已经按顺序执行以下 provisioning SQL：");
            foreach (var item in executed)
            {
                AppendSqlBlock(builder, item.Payload.Sql);
            }

            var citations = string.Concat(executed.Select(static item => $"[{item.CitationId}]"));
            if (!string.IsNullOrWhiteSpace(citations))
                builder.Append(citations);
        }
        else
        {
            builder.AppendLine("已经为你按顺序起草以下 provisioning SQL：");
            foreach (var item in drafted)
            {
                AppendSqlBlock(builder, item.Payload.Sql);
            }

            var notes = drafted.SelectMany(static item => item.Payload.Notes)
                .Where(static note => !string.IsNullOrWhiteSpace(note))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            AppendNotes(builder, notes);

            var citations = string.Concat(drafted.Select(static item => $"[{item.CitationId}]"));
            if (!string.IsNullOrWhiteSpace(citations))
                builder.Append(citations);
        }

        answer = builder.ToString().Trim();
        return true;
    }

    private static string BuildInferredCreateSqlFallbackAnswer(string sql)
    {
        var builder = new StringBuilder();
        builder.AppendLine("可以用这条 SonnetDB SQL 创建温湿度监测 measurement：");
        AppendSqlBlock(builder, sql);
        builder.AppendLine("说明：`time` 是写入数据时提供的毫秒时间戳；TAG 用于按设备或位置过滤，温度和湿度作为 FLOAT FIELD 存储。");
        return builder.ToString().Trim();
    }

    private static void AppendSqlBlock(StringBuilder builder, string sql)
    {
        builder.AppendLine("```sql");
        builder.AppendLine(sql.Trim());
        builder.AppendLine("```");
    }

    private static void AppendNotes(StringBuilder builder, IReadOnlyList<string> notes)
    {
        foreach (var note in notes)
        {
            if (!string.IsNullOrWhiteSpace(note))
                builder.AppendLine($"- {note}");
        }
    }

    private static void AppendCitation(StringBuilder builder, string citationId)
    {
        if (!string.IsNullOrWhiteSpace(citationId))
            builder.Append('[').Append(citationId).Append(']');
    }

    private static IReadOnlyList<string> ExtractMeasurementNames(McpSqlQueryResult queryResult)
    {
        var names = new List<string>(queryResult.Rows.Count);
        foreach (var row in queryResult.Rows)
        {
            var name = TryReadCellText(row, 0);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return names;
    }

    private static bool TryExtractMeasurementSchema(
        McpSqlQueryResult queryResult,
        string? measurementHint,
        out string measurement,
        out IReadOnlyList<McpMeasurementColumnResult> columns)
    {
        measurement = measurementHint ?? "目标 measurement";
        columns = [];

        var nameIndex = FindColumnIndex(queryResult.Columns, "column_name");
        var typeIndex = FindColumnIndex(queryResult.Columns, "column_type");
        var dataTypeIndex = FindColumnIndex(queryResult.Columns, "data_type");
        if (nameIndex < 0 || typeIndex < 0 || dataTypeIndex < 0)
            return false;

        var parsed = new List<McpMeasurementColumnResult>(queryResult.Rows.Count);
        foreach (var row in queryResult.Rows)
        {
            var name = TryReadCellText(row, nameIndex);
            var columnType = TryReadCellText(row, typeIndex);
            var dataType = TryReadCellText(row, dataTypeIndex);
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(columnType)
                || string.IsNullOrWhiteSpace(dataType))
            {
                continue;
            }

            parsed.Add(new McpMeasurementColumnResult(name, columnType, dataType));
        }

        if (parsed.Count == 0)
            return false;

        columns = parsed;
        return true;
    }

    private static string? TryExtractMeasurementFromToolArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("sql", out var sqlElement))
                return null;
            if (sqlElement.ValueKind != JsonValueKind.String)
                return null;

            var sql = sqlElement.GetString();
            if (string.IsNullOrWhiteSpace(sql))
                return null;

            var statement = SqlParser.Parse(sql);
            return statement is DescribeMeasurementStatement describe ? describe.Name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int FindColumnIndex(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string? TryReadCellText(IReadOnlyList<JsonElementValue> row, int index)
    {
        if (index < 0 || index >= row.Count)
            return null;

        return row[index].Kind switch
        {
            ScalarKind.String => row[index].StringValue,
            ScalarKind.Integer => row[index].IntegerValue?.ToString(CultureInfo.InvariantCulture),
            ScalarKind.Double => row[index].DoubleValue?.ToString(CultureInfo.InvariantCulture),
            ScalarKind.Boolean => row[index].BooleanValue is true ? "true" : "false",
            _ => null,
        };
    }

    private static List<CopilotCitation> BuildRetrievalCitations(
        IReadOnlyList<DocsSearchResult> docs,
        IReadOnlyList<SkillLoadResult> loadedSkills,
        ref int nextCitationNumber)
    {
        var citations = new List<CopilotCitation>(docs.Count + loadedSkills.Count);
        foreach (var doc in docs)
        {
            citations.Add(new CopilotCitation(
                Id: $"C{nextCitationNumber++}",
                Kind: "doc",
                Title: string.IsNullOrWhiteSpace(doc.Title) ? doc.Source : doc.Title,
                Source: doc.Source,
                Snippet: Truncate(CollapseWhitespace(doc.Content), 220)));
        }

        foreach (var skill in loadedSkills)
        {
            citations.Add(new CopilotCitation(
                Id: $"C{nextCitationNumber++}",
                Kind: "skill",
                Title: skill.Name,
                Source: skill.Source,
                Snippet: Truncate(CollapseWhitespace($"{skill.Description} {skill.Body}"), 220)));
        }

        return citations;
    }

    private static CopilotCitation BuildToolCitation(
        CopilotToolInvocation tool,
        string resultJson,
        ref int nextCitationNumber)
    {
        var title = tool.Measurement is not null
            ? $"{tool.Name}({tool.Measurement})"
            : tool.Sql is not null
                ? $"{tool.Name}({Truncate(CollapseWhitespace(tool.Sql), 48)})"
                : tool.Name;

        return new CopilotCitation(
            Id: $"C{nextCitationNumber++}",
            Kind: "tool",
            Title: title,
            Source: $"tool:{tool.Name}",
            Snippet: Truncate(CollapseWhitespace(resultJson), 220));
    }

    private static string SerializeToolResult<T>(T payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(payload, typeInfo);

    private static int NormalizeLimit(int? requested, int defaultValue, int maxValue)
    {
        if (requested is null || requested <= 0)
            return defaultValue;

        return Math.Min(requested.Value, maxValue);
    }

    private static string? TryResolveMeasurement(string? requested, IReadOnlyList<string> measurements, string message)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var exact = measurements.FirstOrDefault(item =>
                string.Equals(item, requested.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
        }

        foreach (var measurement in measurements)
        {
            if (message.Contains(measurement, StringComparison.OrdinalIgnoreCase))
                return measurement;
        }

        return null;
    }

    private static string? TryExtractSql(string message)
    {
        var trimmed = message.Trim();
        var fencedStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        if (fencedStart >= 0)
        {
            var fencedEnd = trimmed.IndexOf("```", fencedStart + 3, StringComparison.Ordinal);
            if (fencedEnd > fencedStart)
            {
                var payload = trimmed[(fencedStart + 3)..fencedEnd].Trim();
                var newline = payload.IndexOf('\n');
                if (newline >= 0 && payload[..newline].All(static ch => char.IsLetter(ch)))
                    payload = payload[(newline + 1)..].Trim();
                if (LooksLikeSql(payload))
                    return payload;
            }
        }

        if (LooksLikeSql(trimmed))
            return trimmed;

        var selectIndex = message.IndexOf("SELECT ", StringComparison.OrdinalIgnoreCase);
        if (selectIndex >= 0)
            return message[selectIndex..].Trim();

        var showIndex = message.IndexOf("SHOW ", StringComparison.OrdinalIgnoreCase);
        if (showIndex >= 0)
            return message[showIndex..].Trim();

        var describeIndex = message.IndexOf("DESCRIBE ", StringComparison.OrdinalIgnoreCase);
        if (describeIndex >= 0)
            return message[describeIndex..].Trim();

        var createIndex = message.IndexOf("CREATE ", StringComparison.OrdinalIgnoreCase);
        if (createIndex >= 0)
            return message[createIndex..].Trim();

        var insertIndex = message.IndexOf("INSERT ", StringComparison.OrdinalIgnoreCase);
        if (insertIndex >= 0)
            return message[insertIndex..].Trim();

        var deleteIndex = message.IndexOf("DELETE ", StringComparison.OrdinalIgnoreCase);
        if (deleteIndex >= 0)
            return message[deleteIndex..].Trim();

        return null;
    }

    private static bool LooksLikeSql(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("SHOW ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("DESCRIBE ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeWriteSql(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.TrimStart();
        return trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("INSERT ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DELETE ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeWriteIntent(string lowered)
    {
        // 中英文常见的“建表 / 创建 measurement / 插入 / 写入 / 删除”意图关键词。
        return lowered.Contains("建表", StringComparison.Ordinal)
            || lowered.Contains("建一个表", StringComparison.Ordinal)
            || lowered.Contains("建一张表", StringComparison.Ordinal)
            || lowered.Contains("新建表", StringComparison.Ordinal)
            || lowered.Contains("创建表", StringComparison.Ordinal)
            || lowered.Contains("创建 measurement", StringComparison.Ordinal)
            || lowered.Contains("create table", StringComparison.Ordinal)
            || lowered.Contains("create measurement", StringComparison.Ordinal)
            || lowered.Contains("插入", StringComparison.Ordinal)
            || lowered.Contains("写入数据", StringComparison.Ordinal)
            || lowered.Contains("写入一条", StringComparison.Ordinal)
            || lowered.Contains("insert into", StringComparison.Ordinal)
            || lowered.Contains("删除数据", StringComparison.Ordinal)
            || lowered.Contains("delete from", StringComparison.Ordinal);
    }

    private static bool LooksLikeCreateMeasurementIntent(string lowered)
    {
        return lowered.Contains("建表", StringComparison.Ordinal)
            || lowered.Contains("建一个表", StringComparison.Ordinal)
            || lowered.Contains("建一张表", StringComparison.Ordinal)
            || lowered.Contains("新建表", StringComparison.Ordinal)
            || lowered.Contains("创建表", StringComparison.Ordinal)
            || lowered.Contains("创建 measurement", StringComparison.Ordinal)
            || lowered.Contains("create table", StringComparison.Ordinal)
            || lowered.Contains("create measurement", StringComparison.Ordinal)
            || (lowered.Contains("建", StringComparison.Ordinal)
                && (lowered.Contains("表", StringComparison.Ordinal)
                    || lowered.Contains("measurement", StringComparison.Ordinal)));
    }

    private static string? TryBuildCreateMeasurementSql(string message)
    {
        var lowered = message.ToLowerInvariant();
        if (!LooksLikeCreateMeasurementIntent(lowered))
            return null;

        var hasTemperature = lowered.Contains("温度", StringComparison.Ordinal)
            || ContainsIdentifierToken(message, "temperature")
            || ContainsIdentifierToken(message, "temp");
        var hasHumidity = lowered.Contains("湿度", StringComparison.Ordinal)
            || ContainsIdentifierToken(message, "humidity");

        if (!hasTemperature && !hasHumidity)
            return null;

        var measurement = InferCreateMeasurementName(message, hasTemperature, hasHumidity);
        var columns = new List<string>(4);
        AddTagColumns(message, columns);

        if (hasTemperature)
        {
            var name = ContainsIdentifierToken(message, "temperature") && !ContainsIdentifierToken(message, "temp")
                ? "temperature"
                : ContainsIdentifierToken(message, "temp")
                    ? "temp"
                    : "temperature";
            AddUniqueColumn(columns, $"{name} FIELD FLOAT");
        }

        if (hasHumidity)
            AddUniqueColumn(columns, "humidity FIELD FLOAT");

        return $"CREATE MEASUREMENT {measurement} ({string.Join(", ", columns)})";
    }

    private static string InferCreateMeasurementName(string message, bool hasTemperature, bool hasHumidity)
    {
        var explicitName = TryFindIdentifierAfterAny(
                message,
                "名为",
                "命名为",
                "叫做",
                "叫",
                "表名",
                "measurement",
                "table")
            ?? TryFindIdentifierBefore(message, "表")
            ?? TryFindIdentifierBefore(message, "measurement");

        if (explicitName is not null && !IsWeakInferredName(explicitName))
            return explicitName;

        return hasTemperature && hasHumidity
            ? "sensor_temperature"
            : "sensor_data";
    }

    private static void AddTagColumns(string message, List<string> columns)
    {
        if (ContainsIdentifierToken(message, "host"))
            AddUniqueColumn(columns, "host TAG");
        if (ContainsIdentifierToken(message, "device_id") || message.Contains("设备", StringComparison.Ordinal))
            AddUniqueColumn(columns, "device_id TAG");
        if (ContainsIdentifierToken(message, "sensor_id") || message.Contains("传感器", StringComparison.Ordinal))
            AddUniqueColumn(columns, "sensor_id TAG");
        if (ContainsIdentifierToken(message, "location") || message.Contains("位置", StringComparison.Ordinal))
            AddUniqueColumn(columns, "location TAG");

        if (columns.Count == 0)
        {
            AddUniqueColumn(columns, "device_id TAG");
            AddUniqueColumn(columns, "location TAG");
        }
    }

    private static void AddUniqueColumn(List<string> columns, string column)
    {
        var columnNameEnd = column.IndexOf(' ', StringComparison.Ordinal);
        var columnName = columnNameEnd > 0 ? column[..columnNameEnd] : column;
        if (!columns.Any(item => item.StartsWith(columnName + " ", StringComparison.OrdinalIgnoreCase)))
            columns.Add(column);
    }

    private static string? TryFindIdentifierAfterAny(string text, params string[] markers)
    {
        foreach (var marker in markers)
        {
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var identifier = TryReadIdentifierForward(text, index + marker.Length);
                if (identifier is not null)
                    return identifier;

                index = text.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase);
            }
        }

        return null;
    }

    private static string? TryFindIdentifierBefore(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var identifier = TryReadIdentifierBackward(text, index - 1);
            if (identifier is not null)
                return identifier;

            index = text.IndexOf(marker, index + marker.Length, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static string? TryReadIdentifierForward(string text, int start)
    {
        var index = start;
        while (index < text.Length && IsForwardIdentifierSeparator(text[index]))
            index++;

        return TryReadIdentifierAt(text, index);
    }

    private static bool IsForwardIdentifierSeparator(char ch)
        => char.IsWhiteSpace(ch)
            || ch is ':' or '：' or '=' or '"' or '`' or '\'' or '“' or '”';

    private static string? TryReadIdentifierBackward(string text, int start)
    {
        var end = start;
        while (end >= 0 && char.IsWhiteSpace(text[end]))
            end--;
        if (end < 0 || !IsIdentifierPart(text[end]))
            return null;

        var begin = end;
        while (begin >= 0 && IsIdentifierPart(text[begin]))
            begin--;
        begin++;

        if (begin > end || !IsIdentifierStart(text[begin]))
            return null;

        return text[begin..(end + 1)];
    }

    private static string? TryReadIdentifierAt(string text, int index)
    {
        if (index < 0 || index >= text.Length || !IsIdentifierStart(text[index]))
            return null;

        var end = index + 1;
        while (end < text.Length && IsIdentifierPart(text[end]))
            end++;

        return text[index..end];
    }

    private static bool ContainsIdentifierToken(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeOk = index == 0 || !IsIdentifierPart(text[index - 1]);
            var after = index + token.Length;
            var afterOk = after >= text.Length || !IsIdentifierPart(text[after]);
            if (beforeOk && afterOk)
                return true;

            index = text.IndexOf(token, index + token.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsIdentifierStart(char ch)
        => ch == '_' || (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool IsIdentifierPart(char ch)
        => IsIdentifierStart(ch) || (ch >= '0' && ch <= '9');

    private static bool IsWeakInferredName(string identifier)
        => identifier.Equals("for", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("with", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("need", StringComparison.OrdinalIgnoreCase)
            || identifier.Equals("needs", StringComparison.OrdinalIgnoreCase);

    private static bool IsReadOnlyStatement(SqlStatement statement)
        => statement is SelectStatement
            or ShowMeasurementsStatement
            or ShowTablesStatement
            or DescribeMeasurementStatement
            or DescribeTableStatement
            or ExplainStatement;

    private static string GetStatementType(SqlStatement statement) => statement switch
    {
        SelectStatement => "select",
        ShowMeasurementsStatement => "show_measurements",
        ShowTablesStatement => "show_tables",
        DescribeMeasurementStatement => "describe_measurement",
        DescribeTableStatement => "describe_table",
        ExplainStatement => "explain",
        _ => "unknown",
    };

    private static List<AiMessage> NormalizeMessages(IReadOnlyList<AiMessage> messages)
    {
        var normalized = new List<AiMessage>(messages.Count);
        foreach (var message in messages)
        {
            if (message is null || string.IsNullOrWhiteSpace(message.Content))
                continue;

            var role = NormalizeRole(message.Role);
            if (role is null)
                continue;

            normalized.Add(new AiMessage(role, message.Content.Trim()));
        }

        return normalized;
    }

    private static IReadOnlyList<AiMessage> TrimConversation(IReadOnlyList<AiMessage> messages)
    {
        if (messages.Count == 0)
            return [];

        var remaining = HistoryTokenBudget;
        var reversed = new List<AiMessage>(messages.Count);
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            var content = reversed.Count == 0
                ? TruncateToTokenBudget(message.Content, Math.Max(64, remaining))
                : message.Content;
            var estimatedTokens = EstimateTokens(content) + 4;

            if (reversed.Count > 0 && estimatedTokens > remaining)
                break;

            reversed.Add(new AiMessage(message.Role, content));
            remaining = Math.Max(0, remaining - estimatedTokens);
        }

        reversed.Reverse();
        return reversed;
    }

    private static int FindLatestUserMessageIndex(IReadOnlyList<AiMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string BuildRetrievalQuery(IReadOnlyList<AiMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages.TakeLast(4))
        {
            builder.Append(message.Role);
            builder.Append(": ");
            builder.AppendLine(Truncate(CollapseWhitespace(message.Content), 220));
        }

        return builder.ToString().Trim();
    }

    private static string? NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return "user";

        return role.Trim().ToLowerInvariant() switch
        {
            "user" => "user",
            "assistant" => "assistant",
            "system" => "system",
            _ => null,
        };
    }

    private static int EstimateTokens(string text)
        => string.IsNullOrWhiteSpace(text) ? 0 : Math.Max(1, (text.Length + 3) / 4);

    private static string TruncateToTokenBudget(string text, int tokenBudget)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var maxChars = Math.Max(32, tokenBudget * 4);
        return text.Length <= maxChars
            ? text
            : text[..maxChars].TrimEnd() + "...";
    }

    private static string FormatToolArguments(CopilotToolInvocation tool)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();

        if (tool.Database is not null)
            writer.WriteString("database", tool.Database);
        if (tool.Measurement is not null)
            writer.WriteString("measurement", tool.Measurement);
        if (tool.Sql is not null)
            writer.WriteString("sql", tool.Sql);
        if (tool.MaxRows is not null)
            writer.WriteNumber("maxRows", tool.MaxRows.Value);
        if (tool.N is not null)
            writer.WriteNumber("n", tool.N.Value);

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string GetToolKey(CopilotToolInvocation tool)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{tool.Name.ToLowerInvariant()}|{NormalizeKeyPart(tool.Database)}|{NormalizeKeyPart(tool.Measurement)}|{NormalizeSqlKeyPart(tool.Sql)}|{NormalizeNumericKeyPart(tool.MaxRows)}|{NormalizeNumericKeyPart(tool.N)}");

    private static string NormalizeKeyPart(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static string NormalizeSqlKeyPart(string? sql)
        => string.IsNullOrWhiteSpace(sql)
            ? string.Empty
            : CollapseWhitespace(sql);

    private static string NormalizeNumericKeyPart(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string BuildSqlErrorPayload(SqlExecutionException exception, int attempt, bool final)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("error", "sql_error");
        writer.WriteString("phase", exception.Phase);
        writer.WriteString("message", exception.Message);
        writer.WriteString("sql", exception.Sql);
        writer.WriteNumber("attempt", attempt);
        writer.WriteBoolean("final", final);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void AppendConversationHistory(StringBuilder builder, IReadOnlyList<AiMessage> history)
    {
        if (history.Count == 0)
            return;

        builder.AppendLine("最近对话历史：");
        foreach (var message in history)
        {
            builder.Append("- ");
            builder.Append(message.Role switch
            {
                "assistant" => "assistant",
                "system" => "system",
                _ => "user",
            });
            builder.Append("：");
            builder.AppendLine(Truncate(CollapseWhitespace(message.Content), 320));
        }
        builder.AppendLine();
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var builder = new StringBuilder(text.Length);
        var previousWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!previousWhitespace)
                    builder.Append(' ');
                previousWhitespace = true;
            }
            else
            {
                builder.Append(ch);
                previousWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
            return text;

        return text[..Math.Max(0, maxLength - 3)].TrimEnd() + "...";
    }

    private const string PlannerSystemPrompt =
        """
        你是 SonnetDB Copilot 的工具规划器，采用 ReAct（Reason + Act）模式逐步推进。
        你服务于 SonnetDB Web Admin / SQL Console 中的用户，是精通 SonnetDB SQL、时序数据建模、向量检索、地理空间、PID 与运维排查的数据库智能体。
        每次调用你只需决定【下一个最必要的单个工具】，看到该工具结果后再决定下一步。

        通用行为准则：
        - 严格遵循用户需求与当前数据库上下文；不要在证据不足时猜测 measurement、列名、数据或权限。
        - 你是行动型智能体：能通过工具确认的事情优先用工具确认，直到问题已可回答或确实无法继续。
        - 不要向用户索要不必要的细节；当可以做出安全合理的下一步时，直接推进。
        - 遵守安全与版权边界；遇到危险、仇恨、色情、暴力或越权写入请求时，不要规划会造成伤害或越权的工具调用。
        - 不要冒充 GitHub Copilot 或 VS Code Copilot；SonnetDB 产品内你的名称是 SonnetDB Copilot。

        可用工具（共 8 个）：
        - list_databases()
        - list_measurements(maxRows?)
        - describe_measurement(measurement)
        - sample_rows(measurement, n?)
        - explain_sql(sql)
        - query_sql(sql, maxRows?)              // 仅 SELECT/SHOW/DESCRIBE
        - draft_sql(sql)                        // 起草 / 校验 CREATE MEASUREMENT、CREATE TABLE、INSERT、UPDATE、DELETE、SELECT 等 SQL，但不会改写数据
        - execute_sql(sql, maxRows?)            // 真正执行 CREATE MEASUREMENT / CREATE TABLE / INSERT / UPDATE / DELETE / SELECT；写入需调用方具备写权限

        输出必须是严格 JSON，每次只输出 1 个工具（或空数组表示已完成）：
        {"tools":[{"name":"list_databases"}]}
        {"tools":[{"name":"draft_sql","sql":"CREATE MEASUREMENT host_perf (host TAG, cpu_pct FIELD FLOAT, mem_pct FIELD FLOAT, cpu_temp_celsius FIELD FLOAT)"}]}
        {"tools":[]}

        规则：
        - 只能输出 JSON，不要附加解释、Markdown 或代码块。
        - 每次只输出 1 个工具；如果已有上下文足够回答，输出 {"tools":[]} 表示完成。
        - 如果已有【已执行工具结果】，必须先阅读这些结果再决定下一步，不要重复调用已经成功执行过的工具。
        - 询问时序 schema/字段/列结构时，优先 describe_measurement 或 list_measurements；询问关系表清单/结构时，用 query_sql 执行 SHOW TABLES / DESCRIBE TABLE。
        - 用户给出只读 SQL 并询问结果时，优先 query_sql；询问扫描/成本/解释时优先 explain_sql。
        - 用户描述时序建模/创建 measurement/写入时序数据/删除数据等需求时，按以下顺序逐步推进：
            步骤 1：若尚未知道数据库是否存在，先调用 list_databases。
            步骤 2：若数据库存在但不确定 measurement 是否已存在，调用 list_measurements。
            步骤 3：调用 draft_sql 起草 CREATE MEASUREMENT（必须覆盖用户提到的所有字段）。
            步骤 4：仅当用户明确说执行/立即建表/直接写入/帮我跑一下时，才调用 execute_sql。
            不要跳过步骤，不要在没有 draft_sql 验证的情况下直接调用 execute_sql。
        - 用户明确要求创建关系表、主键表、配置表、设备表或元数据表时，使用 CREATE TABLE ... PRIMARY KEY (...)，并先用 query_sql 执行 SHOW TABLES 确认是否已存在，再 draft_sql。
        - 当用户的意图是【创建/新建一个数据库】（"建一个仓库"、"创建数据库"、"新建库"、"create database" 等），不要去 list_measurements / describe_measurement，也不要假设用户想往当前库里建表。优先按以下顺序推进：
            步骤 1：调用 list_databases 确认目标库名是否已存在；如果用户没给名字，跳过该步骤直接进入步骤 2。
            步骤 2：调用 draft_sql 起草 CREATE DATABASE 语句（语法：`CREATE DATABASE <name>`，name 必须是合法标识符；如果用户同时描述了想保存的指标，draft_sql 还需要紧跟一条 CREATE MEASUREMENT，覆盖所有字段）。
            步骤 3：仅当用户明确说"执行/立即建/帮我创建"时才调用 execute_sql；否则直接返回 {"tools":[]} 让回答器把 SQL 给用户。
        - 不要把 `__copilot__` / `_internal` 等系统库当成业务库去操作；它们不会出现在 list_databases 结果里。
        - 不要编造不存在的 measurement 名称、列名或函数。
        - SonnetDB 的 CREATE DATABASE 语法：`CREATE DATABASE name`，不支持 IF NOT EXISTS / WITH 选项；同名库已存在时可直接复用。
        - SonnetDB 的 CREATE MEASUREMENT 语法：CREATE MEASUREMENT name (col TAG, col FIELD type, ...)，FIELD 类型只接受 FLOAT / INT / BOOL / STRING / VECTOR(N)，TAG 列固定为 STRING。
        - SonnetDB 的 CREATE TABLE 语法：CREATE TABLE name (id INT, name STRING NOT NULL, PRIMARY KEY (id))；关系表类型支持 INT / FLOAT / BOOL / STRING / DATETIME / BLOB / JSON，必须声明 PRIMARY KEY。
        - SonnetDB 的 INSERT 语法：measurement 写入通常包含 time 毫秒时间戳；关系表 INSERT 按声明列和值写入。
        """;

    private const string SqlRepairSystemPrompt =
        """
        你是 SonnetDB Copilot 的 SQL 纠错器。
        请根据失败 SQL、错误消息、对话上下文和文档/技能摘要，把 SQL 改写成可执行的只读 SQL。
        规则：
        - 只允许输出一条 SELECT、SHOW MEASUREMENTS / SHOW TABLES 或 DESCRIBE [MEASUREMENT|TABLE]。
        - 只能输出 SQL 本身，不要解释、Markdown、代码块或 JSON。
        - 不要编造不存在的 measurement、列名或函数。
        - 不要把 MySQL、PostgreSQL、SQLite 或 InfluxQL 方言改写成 SonnetDB 不支持的语法；必须落回当前 SonnetDB SQL 方言。
        """;

    private const string AnswerSystemPrompt =
        """
        你是 SonnetDB Copilot 的最终回答器，是 SonnetDB Web Admin / SQL Console 中的数据库智能体。
        请严格基于给定的文档、技能与工具结果作答，不要编造数据库结构、数据或 SQL 结果。
        要求：
        - 使用中文回答。
        - 当用户问你的名称时，回答“SonnetDB Copilot”；不要自称 GitHub Copilot、VS Code Copilot 或其他产品。
        - 当用户问你正在使用的模型时，如上下文提供了模型名则说明“当前会话使用所选模型”；否则说明“模型由 SonnetDB 服务端 Copilot 配置决定”，不要编造固定模型名。
        - 优先给出直接结论，再补充必要说明；回答保持简洁、专业、少寒暄。
        - 严格遵循用户需求；能基于工具结果或文档确认的，使用确认后的事实。
        - 遵守安全与版权边界；对危险、仇恨、色情、暴力、越权写入或明显破坏性请求，应简短拒绝或说明需要权限/审批。
        - 如果给定了 citations，请尽量在对应句子末尾用 [C1] 这样的编号引用。
        - 若证据不足，请明确说明不确定或当前结果不足以确认。
        - 不要向用户复述工具名；对于 SHOW MEASUREMENTS、DESCRIBE MEASUREMENT、measurement 列表或 schema 结果，直接翻译成自然语言结论。
        - 当用户的意图是建表 / 写入 / 删除 / 改 schema 时，必须给出可直接复制执行的 SQL：
            * 把每条 SQL 单独放在 ```sql 代码块中。
            * 优先使用 draft_sql / execute_sql 工具返回的 SQL，不要自行改写列名或类型。
            * 如果工具返回了 notes（例如缺权限、measurement 已存在），请把这些注意事项明确转述给用户。
            * 生成建表 SQL 时，必须覆盖用户提到的所有指标字段，不要只写一两个示例字段就省略其余。例如用户说 CPU 使用率、内存使用率、温度，就必须把三者都建成独立的 FIELD 列。
            * 如果当前数据库不存在（list_databases 结果为空或不含目标库），必须在建表 SQL 之前先给出 CREATE DATABASE 语句，并说明需要先创建数据库。
            * 当 SQL 已准备好且界面提供了 SQL Console 选项卡时，请在回答末尾提示用户：可以点击页面上方的 SQL Console 按鈕，将上方 SQL 粘贴进去直接执行。
        - 当用户的意图是【新建 / 创建一个数据库】（"建一个仓库 / 数据库"、"create database"、"新建库"）：
            * 必须先给出一条 `CREATE DATABASE <name>` SQL（放在 ```sql 代码块内）；如果用户没指定名字，请基于场景给一个合理名（例如 "host_metrics"、"sys_perf"）并在文字里说明可以改名。
            * 如果用户同时描述了想保存的指标（CPU、内存、温度等），紧跟一条 CREATE MEASUREMENT，覆盖所有字段；FIELD 类型按语义选 FLOAT/INT/BOOL/STRING。
            * 不要去 SHOW MEASUREMENTS、不要把 `__copilot__` 之类的系统库当成用户的业务库去描述；如果工具结果显示当前选中库是系统库，请明确告诉用户："这是系统内置库，建议为你新建的指标单独创建一个数据库"。
        - 当用户给出只是描述而工具没生成 SQL 时，根据已有 measurement 列表与字段，自己起草一条最贴近需求的 CREATE MEASUREMENT / INSERT 语句，同样放进 ```sql 代码块。
        """;
}

/// <summary>
/// Copilot 执行上下文。
/// </summary>
/// <param name="DatabaseName">当前数据库名。</param>
/// <param name="Database">当前数据库实例；建库型 provisioning 请求在数据库尚未存在时可为空。</param>
/// <param name="VisibleDatabases">当前凭据可见的数据库集合。</param>
/// <param name="CanWrite">当前请求是否允许直接执行写入类工具（同时受权限模式与凭据本身能力约束）。</param>
/// <param name="ModelOverride">可选模型覆盖（M8）：如果不为空，会传递给 chat provider 作为本次调用的模型名。</param>
/// <param name="CanUseControlPlane">当前凭据是否具备控制面能力（例如 CREATE DATABASE）。</param>
internal sealed record CopilotAgentContext(
    string DatabaseName,
    Tsdb? Database,
    IReadOnlyList<string> VisibleDatabases,
    bool CanWrite = false,
    string? ModelOverride = null,
    bool CanUseControlPlane = false);

/// <summary>
/// 多轮对话的规范化结果。
/// </summary>
internal sealed record CopilotConversation(
    IReadOnlyList<AiMessage> Messages,
    IReadOnlyList<AiMessage> History,
    string LatestUserMessage,
    string RetrievalQuery,
    bool WasTrimmed);

/// <summary>
/// 工具规划结果。
/// </summary>
internal sealed record CopilotToolPlan(IReadOnlyList<CopilotPlannedTool> Tools);

/// <summary>
/// 单个规划出来的工具调用。
/// </summary>
internal sealed record CopilotPlannedTool(
    string Name,
    string? Database = null,
    string? Measurement = null,
    string? Sql = null,
    int? MaxRows = null,
    int? N = null);

/// <summary>
/// 规范化后的工具调用。
/// </summary>
internal sealed record CopilotToolInvocation(
    string Name,
    int? MaxRows,
    int? N,
    string? Measurement,
    string? Sql,
    string? Database = null);

/// <summary>
/// 已执行工具的观测结果。
/// </summary>
internal sealed record CopilotToolObservation(
    string Name,
    string ArgumentsJson,
    string ResultJson,
    CopilotCitation Citation);

/// <summary>
/// 工具执行与修复后的最终结果。
/// </summary>
internal sealed record CopilotToolExecutionResult(
    CopilotToolInvocation Tool,
    string ResultJson,
    IReadOnlyList<CopilotChatEvent> Events);
