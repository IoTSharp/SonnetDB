using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>
/// PR #69：Copilot nightly eval 套件，提供可回归的标准问答评测。
/// </summary>
public sealed class CopilotEvalSuiteTests : IAsyncLifetime
{
    private const string AlphaDatabaseName = "alpha";
    private const string BetaDatabaseName = "beta";
    private const double MinAccuracy = 0.95d;
    private const double MinCitationHitRate = 0.95d;
    private const double MaxP95LatencyMilliseconds = 500d;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private WebApplication? _app;
    private string? _dataRoot;
    private string? _docsRoot;
    private string? _skillsRoot;
    private string? _summaryPath;
    private ScriptedChatProvider? _chatProvider;
    private IReadOnlyList<CopilotEvalScenario> _scenarios = [];

    public async Task InitializeAsync()
    {
        _scenarios = LoadScenarios();
        _dataRoot = CreateTempDirectory("sndb-copilot-eval-data-");
        _docsRoot = CreateTempDirectory("sndb-copilot-eval-docs-");
        _skillsRoot = CreateTempDirectory("sndb-copilot-eval-skills-");

        WriteDocs(_docsRoot);
        WriteSkills(_skillsRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
        };
        options.Copilot.Enabled = true;
        options.Copilot.Embedding.Provider = "openai";
        options.Copilot.Embedding.Endpoint = "https://embedding.example/v1/";
        options.Copilot.Embedding.ApiKey = "embedding-key";
        options.Copilot.Embedding.Model = "embedding-model";
        options.Copilot.Chat.Provider = "openai";
        options.Copilot.Chat.Endpoint = "https://chat.example/v1/";
        options.Copilot.Chat.ApiKey = "chat-key";
        options.Copilot.Chat.Model = "chat-model";
        options.Copilot.Docs.Roots = [_docsRoot];
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.Root = _skillsRoot;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        _chatProvider = new ScriptedChatProvider(_scenarios, _jsonOptions);
        _app = TestServerHost.Build(
            options,
            services =>
            {
                services.AddSingleton<IEmbeddingProvider, KeywordEmbeddingProvider>();
                services.AddSingleton<IChatProvider>(_chatProvider);
            });

        await _app.Services.GetRequiredService<DocsIngestor>()
            .IngestAsync([_docsRoot], force: true, dryRun: false)
            .ConfigureAwait(false);
        await _app.Services.GetRequiredService<SkillRegistry>()
            .IngestAsync(_skillsRoot, force: true, dryRun: false)
            .ConfigureAwait(false);

        SeedDatabases(_app.Services.GetRequiredService<TsdbRegistry>());
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync().ConfigureAwait(false);

        DeleteDirectory(_skillsRoot);
        DeleteDirectory(_docsRoot);
        DeleteDirectory(_dataRoot);
    }

    [Fact]
    public async Task CopilotEvalSuite_NightlyRegression_MeetsThresholds()
    {
        Assert.InRange(_scenarios.Count, 30, 50);

        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        var agent = _app.Services.GetRequiredService<CopilotAgent>();
        Assert.True(registry.TryGet(AlphaDatabaseName, out var alpha));

        var visibleDatabases = new[] { AlphaDatabaseName, BetaDatabaseName };
        var results = new List<CopilotEvalScenarioResult>(_scenarios.Count);

        foreach (var scenario in _scenarios)
        {
            _chatProvider!.Reset();
            var messages = BuildMessages(scenario);
            var events = new List<CopilotChatEvent>();
            var stopwatch = Stopwatch.StartNew();

            await foreach (var evt in agent.RunAsync(
                               new CopilotAgentContext(AlphaDatabaseName, alpha, visibleDatabases, CanWrite: true),
                               messages,
                               docsK: scenario.DocsK,
                               skillsK: scenario.SkillsK))
            {
                events.Add(evt);
            }

            stopwatch.Stop();
            results.Add(EvaluateScenario(scenario, events, stopwatch.Elapsed.TotalMilliseconds));
        }

        var summary = BuildSummary(results);
        _summaryPath = await WriteSummaryAsync(summary);

        Assert.True(
            summary.Accuracy >= MinAccuracy,
            $"Copilot eval accuracy {summary.Accuracy:P2} 低于阈值 {MinAccuracy:P0}。失败场景：{FormatFailures(results)}。Summary: {_summaryPath}");
        Assert.True(
            summary.CitationHitRate >= MinCitationHitRate,
            $"Copilot eval citation hit rate {summary.CitationHitRate:P2} 低于阈值 {MinCitationHitRate:P0}。Summary: {_summaryPath}");
        Assert.True(
            summary.P95LatencyMilliseconds <= MaxP95LatencyMilliseconds,
            $"Copilot eval p95 latency {summary.P95LatencyMilliseconds:F1} ms 超过阈值 {MaxP95LatencyMilliseconds:F0} ms。Summary: {_summaryPath}");
    }

    private static List<AiMessage> BuildMessages(CopilotEvalScenario scenario)
    {
        var messages = new List<AiMessage>(scenario.History.Count + 1);
        messages.AddRange(scenario.History);
        messages.Add(new AiMessage("user", scenario.Question));
        return messages;
    }

    private static CopilotEvalScenarioResult EvaluateScenario(
        CopilotEvalScenario scenario,
        IReadOnlyList<CopilotChatEvent> events,
        double elapsedMilliseconds)
    {
        var failures = new List<string>();
        var toolCalls = events.Where(static evt => string.Equals(evt.Type, "tool_call", StringComparison.Ordinal))
            .ToArray();
        var toolNames = toolCalls.Select(static evt => evt.ToolName ?? string.Empty).ToArray();
        IReadOnlyList<string> expectedToolNames = scenario.ExpectedToolNames.Count > 0
            ? scenario.ExpectedToolNames.ToArray()
            : scenario.PlannedTools.Select(static tool => tool.Name).ToArray();

        if (!toolNames.SequenceEqual(expectedToolNames, StringComparer.Ordinal))
        {
            failures.Add(
                $"tool 序列不匹配，期望 [{string.Join(", ", expectedToolNames)}]，实际 [{string.Join(", ", toolNames)}]");
        }

        if (!events.Any(static evt => string.Equals(evt.Type, "start", StringComparison.Ordinal)))
            failures.Add("缺少 start 事件。");
        if (!events.Any(static evt => string.Equals(evt.Type, "retrieval", StringComparison.Ordinal)))
            failures.Add("缺少 retrieval 事件。");

        var final = events.LastOrDefault(static evt => string.Equals(evt.Type, "final", StringComparison.Ordinal));
        if (final is null)
            failures.Add("缺少 final 事件。");

        if (events.Count == 0 || !string.Equals(events[^1].Type, "done", StringComparison.Ordinal))
            failures.Add("事件流没有以 done 结束。");

        var retryEvents = events.Where(static evt => string.Equals(evt.Type, "tool_retry", StringComparison.Ordinal))
            .ToArray();
        if (retryEvents.Length != scenario.ExpectedRetryCount)
            failures.Add($"tool_retry 次数不匹配，期望 {scenario.ExpectedRetryCount}，实际 {retryEvents.Length}。");

        var allToolArguments = string.Join(
            "\n",
            events.Where(static evt => !string.IsNullOrWhiteSpace(evt.ToolArguments))
                .Select(static evt => ExpandToolArgumentText(evt.ToolArguments)));
        foreach (var expected in scenario.ExpectedToolArgumentsContains)
        {
            if (!allToolArguments.Contains(expected, StringComparison.OrdinalIgnoreCase))
                failures.Add($"未命中预期 tool arguments 片段：{expected}");
        }

        var retryArguments = string.Join(
            "\n",
            retryEvents.Where(static evt => !string.IsNullOrWhiteSpace(evt.ToolArguments))
                .Select(static evt => ExpandToolArgumentText(evt.ToolArguments)));
        foreach (var expected in scenario.ExpectedRetryArgumentsContains)
        {
            if (!retryArguments.Contains(expected, StringComparison.OrdinalIgnoreCase))
                failures.Add($"未命中预期 retry SQL 片段：{expected}");
        }

        var toolResults = string.Join(
            "\n",
            events.Where(static evt => string.Equals(evt.Type, "tool_result", StringComparison.Ordinal)
                                       && !string.IsNullOrWhiteSpace(evt.ToolResult))
                .Select(static evt => evt.ToolResult));
        foreach (var expected in scenario.ExpectedToolResultContains)
        {
            if (!toolResults.Contains(expected, StringComparison.OrdinalIgnoreCase))
                failures.Add($"未命中预期 tool result 片段：{expected}");
        }

        if (final is not null)
        {
            foreach (var expected in scenario.ExpectedAnswerContains)
            {
                if (!(final.Answer?.Contains(expected, StringComparison.OrdinalIgnoreCase) ?? false))
                    failures.Add($"最终回答未命中片段：{expected}");
            }
        }

        var citationCount = final?.Citations?.Count ?? 0;
        if (citationCount < scenario.MinCitationCount)
            failures.Add($"citation 数不足，至少需要 {scenario.MinCitationCount}，实际 {citationCount}。");

        var citationHit = citationCount >= scenario.MinCitationCount
                          && (final?.Answer?.Contains("[C", StringComparison.Ordinal) ?? false);

        return new CopilotEvalScenarioResult(
            Id: scenario.Id,
            Category: scenario.Category,
            Passed: failures.Count == 0,
            CitationHit: citationHit,
            ElapsedMilliseconds: elapsedMilliseconds,
            ToolNames: toolNames,
            RetryCount: retryEvents.Length,
            CitationCount: citationCount,
            FailureReason: failures.Count == 0 ? null : string.Join(" | ", failures));
    }

    private static CopilotEvalSummary BuildSummary(IReadOnlyList<CopilotEvalScenarioResult> results)
    {
        var orderedLatency = results.Select(static result => result.ElapsedMilliseconds)
            .OrderBy(static value => value)
            .ToArray();
        var passed = results.Count(static result => result.Passed);
        var citationHits = results.Count(static result => result.CitationHit);

        return new CopilotEvalSummary(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            ScenarioCount: results.Count,
            PassedCount: passed,
            FailedCount: results.Count - passed,
            Accuracy: results.Count == 0 ? 0d : (double)passed / results.Count,
            CitationHitRate: results.Count == 0 ? 0d : (double)citationHits / results.Count,
            P50LatencyMilliseconds: Percentile(orderedLatency, 0.50d),
            P95LatencyMilliseconds: Percentile(orderedLatency, 0.95d),
            MaxLatencyMilliseconds: orderedLatency.Length == 0 ? 0d : orderedLatency[^1],
            ThresholdAccuracy: MinAccuracy,
            ThresholdCitationHitRate: MinCitationHitRate,
            ThresholdP95LatencyMilliseconds: MaxP95LatencyMilliseconds,
            Results: results);
    }

    private async Task<string> WriteSummaryAsync(CopilotEvalSummary summary)
    {
        var resultDirectory = ResolveResultsDirectory();
        Directory.CreateDirectory(resultDirectory);
        var path = Path.Combine(resultDirectory, "copilot-eval-summary.json");
        var json = JsonSerializer.Serialize(summary, _jsonOptions);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8).ConfigureAwait(false);
        return path;
    }

    private static double Percentile(double[] orderedValues, double percentile)
    {
        if (orderedValues.Length == 0)
            return 0d;

        var index = Math.Max(0, (int)Math.Ceiling(orderedValues.Length * percentile) - 1);
        return orderedValues[index];
    }

    private static string FormatFailures(IEnumerable<CopilotEvalScenarioResult> results)
    {
        var failed = results.Where(static result => !result.Passed)
            .Select(static result => $"{result.Id}: {result.FailureReason}")
            .ToArray();
        return failed.Length == 0 ? "无" : string.Join(" || ", failed);
    }

    private static string ExpandToolArgumentText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        var builder = new StringBuilder(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return builder.ToString();

            if (document.RootElement.TryGetProperty("sql", out var sql)
                && sql.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(sql.GetString()))
            {
                builder.AppendLine();
                builder.Append(sql.GetString());
            }

            if (document.RootElement.TryGetProperty("measurement", out var measurement)
                && measurement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(measurement.GetString()))
            {
                builder.AppendLine();
                builder.Append(measurement.GetString());
            }
        }
        catch (JsonException)
        {
            // 保留原始 JSON 文本即可。
        }

        return builder.ToString();
    }

    private static IReadOnlyList<CopilotEvalScenario> LoadScenarios()
    {
        var path = ResolveScenarioFile();
        var json = File.ReadAllText(path);
        var scenarios = JsonSerializer.Deserialize<List<CopilotEvalScenario>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        if (scenarios is null || scenarios.Count == 0)
            throw new InvalidOperationException("Copilot eval 场景文件为空。");

        return scenarios;
    }

    private static string ResolveScenarioFile()
    {
        foreach (var candidate in EnumerateScenarioFileCandidates())
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException("未找到 Copilot eval 场景文件 tests/SonnetDB.Tests/Copilot/copilot-eval-scenarios.json。");
    }

    private static IEnumerable<string> EnumerateScenarioFileCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var relative = Path.Combine("Copilot", "copilot-eval-scenarios.json");
        var projectRelative = Path.Combine("tests", "SonnetDB.Tests", "Copilot", "copilot-eval-scenarios.json");

        foreach (var seed in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            if (string.IsNullOrWhiteSpace(seed))
                continue;

            var current = Path.GetFullPath(seed);
            for (var depth = 0; depth < 8; depth++)
            {
                var local = Path.GetFullPath(relative, current);
                if (seen.Add(local))
                    yield return local;

                var project = Path.GetFullPath(projectRelative, current);
                if (seen.Add(project))
                    yield return project;

                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;

                current = parent;
            }
        }
    }

    private string ResolveResultsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("SONNETDB_COPILOT_EVAL_RESULTS_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.IsPathRooted(configured)
                ? Path.GetFullPath(configured)
                : Path.GetFullPath(configured, Directory.GetCurrentDirectory());

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "CopilotEval"));
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static void WriteDocs(string docsRoot)
    {
        File.WriteAllText(
            Path.Combine(docsRoot, "schema.md"),
            """
            # Schema Guide

            ## Metadata

            使用 `list_databases` 查看当前可见数据库，使用 `list_measurements` 查看 measurement 列表。

            ## Schema Inspection

            遇到字段、列、tag、field 相关问题时，优先调用 `describe_measurement`。

            ## Sampling

            需要确认原始数据格式时，优先使用 `sample_rows` 抽样少量行。
            """);

        File.WriteAllText(
            Path.Combine(docsRoot, "sql-aggregation.md"),
            """
            # Query And Aggregation

            ## Time Filter

            SonnetDB 支持 `WHERE time >= ... AND time < ...` 做时间范围过滤。

            ## Aggregation

            常见聚合包括 `avg`、`max`、`sum`、`count`，并支持 `GROUP BY time(...)`。

            ## Explain

            `explain_sql` 会返回 matchedSeries、segment、block 与扫描行数估算。
            """);

        File.WriteAllText(
            Path.Combine(docsRoot, "pid.md"),
            """
            # PID Control

            ## Functions

            `pid` 返回聚合控制量，`pid_series` 返回逐点控制序列。

            ## Auto Tuning

            `pid_estimate(..., 'zn', ...)` 与 `pid_estimate(..., 'imc', ...)` 可根据阶跃响应估算参数。
            """);

        File.WriteAllText(
            Path.Combine(docsRoot, "forecast.md"),
            """
            # Forecast And Detection

            ## Forecast

            `forecast(measurement, field, horizon, 'linear'|'holt_winters', season?)` 用于线性或季节性预测。

            ## Detection

            `anomaly` 可以标记离群点，`changepoint` 可以识别 level shift。
            """);

        File.WriteAllText(
            Path.Combine(docsRoot, "troubleshooting.md"),
            """
            # Troubleshooting

            ## Slow Query

            使用 `sample_rows(slow_query_log, n)` 看样例，再用 `explain_sql` 估算扫描范围。

            ## SQL Repair

            当 `query_sql` 因 measurement 或字段名错误而失败时，Copilot 会根据错误消息尝试改写 SQL。
            """);
    }

    private static void WriteSkills(string skillsRoot)
    {
        File.WriteAllText(
            Path.Combine(skillsRoot, "schema-design.md"),
            """
            ---
            name: schema-design
            description: 用于回答数据库、measurement、字段和 schema 问题
            triggers: [schema, 数据库, measurement, 字段, 列, tag, field]
            requires_tools: [list_databases, list_measurements, describe_measurement, sample_rows]
            ---

            当用户想知道数据库列表、measurement 列表或列定义时，先走 schema 工具；需要确认原始格式时再补 sample_rows。
            """);

        File.WriteAllText(
            Path.Combine(skillsRoot, "query-aggregation.md"),
            """
            ---
            name: query-aggregation
            description: 用于范围查询、聚合、GROUP BY time、LIMIT/OFFSET 和 explain
            triggers: [查询, 聚合, avg, max, sum, count, time filter, explain, offset, limit]
            requires_tools: [query_sql, explain_sql]
            ---

            用户问题涉及原始点、聚合、时间过滤、分页或 explain 时，优先让模型规划只读 SQL。
            """);

        File.WriteAllText(
            Path.Combine(skillsRoot, "pid-control-tuning.md"),
            """
            ---
            name: pid-control-tuning
            description: 用于 PID、pid_series、pid_estimate zn/imc 整定
            triggers: [pid, pid_series, pid_estimate, zn, imc, 整定]
            requires_tools: [query_sql, sample_rows]
            ---

            涉及 PID 控制、逐点控制序列或自动整定时，优先使用 query_sql 执行现有内置函数。
            """);

        File.WriteAllText(
            Path.Combine(skillsRoot, "forecast-howto.md"),
            """
            ---
            name: forecast-howto
            description: 用于 forecast、holt_winters、anomaly 与 changepoint
            triggers: [forecast, 预测, anomaly, 异常, changepoint, 变点, holt_winters]
            requires_tools: [query_sql, explain_sql]
            ---

            预测或检测问题应优先走 query_sql；若用户关心成本和扫描范围，再补 explain_sql。
            """);

        File.WriteAllText(
            Path.Combine(skillsRoot, "troubleshoot-slow-query.md"),
            """
            ---
            name: troubleshoot-slow-query
            description: 用于慢查询排查、sample_rows 和 explain_sql
            triggers: [慢查询, explain, 扫描, latency, rows_scanned]
            requires_tools: [sample_rows, explain_sql, query_sql]
            ---

            排查慢查询时，先 sample_rows 看数据，再 explain_sql 看估算，必要时再实际执行 query_sql。
            """);

        File.WriteAllText(
            Path.Combine(skillsRoot, "multi-turn-repair.md"),
            """
            ---
            name: multi-turn-repair
            description: 用于多轮追问和 SQL 自我纠错
            triggers: [追问, 上一步, 刚才, 修复, 改写 SQL, retry]
            requires_tools: [describe_measurement, sample_rows, query_sql]
            ---

            多轮对话要沿用最近上下文；当 query_sql 失败时，结合错误消息改写 measurement、列名或函数参数。
            """);
    }

    private static void SeedDatabases(TsdbRegistry registry)
    {
        registry.TryCreate(AlphaDatabaseName, out var alpha);
        registry.TryCreate(BetaDatabaseName, out var beta);

        SeedAlphaDatabase(alpha);
        SeedBetaDatabase(beta);
    }

    private static void SeedAlphaDatabase(Tsdb database)
    {
        SqlExecutor.Execute(database, "CREATE MEASUREMENT cpu (host TAG, region TAG, usage FIELD FLOAT, temp FIELD INT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT memory (host TAG, used FIELD INT, free FIELD INT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT reactor (device TAG, temperature FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT reactor_step (device TAG, temperature FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT reactor_step_fast (device TAG, temperature FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT meter (device TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT meter_seasonal (device TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT cpu_anomaly (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT cpu_shift (host TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT slow_query_log (db TAG, latency_ms FIELD INT, rows_scanned FIELD INT)");

        SqlExecutor.Execute(
            database,
            "INSERT INTO cpu (time, host, region, usage, temp) VALUES " +
            "(0, 'edge-01', 'cn-hz', 0.50, 40), " +
            "(1000, 'edge-01', 'cn-hz', 0.65, 42), " +
            "(2000, 'edge-01', 'cn-hz', 0.72, 43), " +
            "(3000, 'edge-02', 'cn-bj', 0.80, 47), " +
            "(4000, 'edge-02', 'cn-bj', 0.78, 46), " +
            "(5000, 'edge-02', 'cn-hz', 0.81, 48), " +
            "(6000, 'edge-01', 'cn-hz', 0.60, 41), " +
            "(7000, 'edge-02', 'cn-bj', 0.76, 45)");

        SqlExecutor.Execute(
            database,
            "INSERT INTO memory (time, host, used, free) VALUES " +
            "(0, 'edge-01', 30, 70), " +
            "(1000, 'edge-01', 34, 66), " +
            "(2000, 'edge-01', 35, 65), " +
            "(0, 'edge-02', 50, 50), " +
            "(1000, 'edge-02', 54, 46), " +
            "(2000, 'edge-02', 58, 42)");

        SqlExecutor.Execute(
            database,
            "INSERT INTO reactor (time, device, temperature) VALUES " +
            "(0, 'r1', 0), (1000, 'r1', 4), (2000, 'r1', 7)");

        InsertFopdtStep(database, "reactor_step", K: 2.0d, tau: 100.0d, theta: 200.0d, n: 300, dtMs: 5);
        InsertFopdtStep(database, "reactor_step_fast", K: 2.0d, tau: 100.0d, theta: 20.0d, n: 400, dtMs: 1);

        SqlExecutor.Execute(
            database,
            "INSERT INTO meter (time, device, value) VALUES " +
            string.Join(
                ", ",
                Enumerable.Range(0, 20)
                    .Select(static i => $"({i * 1000L}, 'm1', {10 + i * 2})")));

        var seasonalRows = new List<string>();
        for (var season = 0; season < 6; season++)
        {
            for (var index = 0; index < 4; index++)
            {
                var pointIndex = season * 4 + index;
                var value = index switch
                {
                    0 => 10,
                    1 => 20,
                    2 => 30,
                    _ => 20,
                };
                seasonalRows.Add($"({pointIndex * 1000L}, 'm1', {value.ToString(CultureInfo.InvariantCulture)})");
            }
        }

        SqlExecutor.Execute(
            database,
            "INSERT INTO meter_seasonal (time, device, value) VALUES " + string.Join(", ", seasonalRows));

        SqlExecutor.Execute(
            database,
            "INSERT INTO cpu_anomaly (time, host, usage) VALUES " +
            "(0,'h1',10),(1000,'h1',11),(2000,'h1',9),(3000,'h1',10)," +
            "(4000,'h1',12),(5000,'h1',100),(6000,'h1',11),(7000,'h1',9)");

        SqlExecutor.Execute(
            database,
            "INSERT INTO cpu_shift (time, host, value) VALUES " +
            "(0,'h1',10),(1000,'h1',10.2),(2000,'h1',9.8),(3000,'h1',10.1),(4000,'h1',10)," +
            "(5000,'h1',9.9),(6000,'h1',10.3),(7000,'h1',10),(8000,'h1',9.7),(9000,'h1',10.1)," +
            "(10000,'h1',20),(11000,'h1',20.1),(12000,'h1',19.9),(13000,'h1',20.2),(14000,'h1',20)," +
            "(15000,'h1',19.8),(16000,'h1',20.1),(17000,'h1',19.9),(18000,'h1',20),(19000,'h1',20.1)");

        SqlExecutor.Execute(
            database,
            "INSERT INTO slow_query_log (time, db, latency_ms, rows_scanned) VALUES " +
            "(0, 'alpha', 120, 1000), " +
            "(1000, 'alpha', 260, 2400), " +
            "(2000, 'alpha', 420, 4200), " +
            "(3000, 'beta', 180, 1600)");
    }

    private static void SeedBetaDatabase(Tsdb database)
    {
        SqlExecutor.Execute(database, "CREATE MEASUREMENT remote_cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(database, "INSERT INTO remote_cpu (time, host, usage) VALUES (0, 'beta-01', 0.42)");
    }

    private static void InsertFopdtStep(
        Tsdb database,
        string measurement,
        double K,
        double tau,
        double theta,
        int n,
        long dtMs)
    {
        var builder = new StringBuilder($"INSERT INTO {measurement} (time, device, temperature) VALUES ");
        var rows = FopdtStep(K, tau, theta, n, dtMs);
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                builder.Append(", ");

            builder.Append('(')
                .Append(rows[i].Timestamp)
                .Append(", 'r1', ")
                .Append(rows[i].Value.ToString(CultureInfo.InvariantCulture))
                .Append(')');
        }

        SqlExecutor.Execute(database, builder.ToString());
    }

    private static FopdtPoint[] FopdtStep(double K, double tau, double theta, int n, long dtMs)
    {
        var samples = new FopdtPoint[n];
        for (var i = 0; i < n; i++)
        {
            var timestamp = i * dtMs;
            var elapsed = timestamp - theta;
            var value = elapsed <= 0d ? 0d : K * (1d - Math.Exp(-elapsed / tau));
            samples[i] = new FopdtPoint(timestamp, value);
        }

        return samples;
    }

    private sealed record FopdtPoint(long Timestamp, double Value);

    private sealed class KeywordEmbeddingProvider : IEmbeddingProvider
    {
        private static readonly string[][] Buckets =
        [
            ["database", "数据库", "alpha", "beta", "list_databases"],
            ["measurement", "show measurements", "list_measurements", "表", "库里"],
            ["schema", "字段", "列", "tag", "field", "describe_measurement"],
            ["sample", "样例", "几行", "sample_rows"],
            ["cpu", "host", "region", "usage", "temp"],
            ["memory", "used", "free"],
            ["reactor", "pid", "pid_series", "pid_estimate", "zn", "imc"],
            ["meter", "forecast", "holt_winters", "linear", "预测"],
            ["anomaly", "异常"],
            ["changepoint", "变点", "shift"],
            ["slow_query_log", "慢查询", "latency", "rows_scanned", "扫描", "explain"],
            ["time", "group by time", "bucket", "offset", "limit", "范围"],
        ];

        public ValueTask<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            var normalized = string.IsNullOrWhiteSpace(text) ? string.Empty : text.ToLowerInvariant();
            var embedding = new float[DocsIngestor.ExpectedEmbeddingDimensions];
            embedding[0] = 1.0f;
            embedding[1] = normalized.Length / 256f;

            for (var bucketIndex = 0; bucketIndex < Buckets.Length; bucketIndex++)
            {
                var score = 0f;
                foreach (var keyword in Buckets[bucketIndex])
                {
                    if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                        score += 1f;
                }

                embedding[bucketIndex + 2] = score;
            }

            return ValueTask.FromResult(embedding);
        }
    }

    private sealed class ScriptedChatProvider : IChatProvider
    {
        private readonly Dictionary<string, CopilotEvalScenario> _scenariosByQuestion;
        private readonly JsonSerializerOptions _jsonOptions;

        public ScriptedChatProvider(IReadOnlyList<CopilotEvalScenario> scenarios, JsonSerializerOptions jsonOptions)
        {
            _scenariosByQuestion = scenarios.ToDictionary(
                static scenario => scenario.Question,
                static scenario => scenario,
                StringComparer.Ordinal);
            _jsonOptions = jsonOptions;
        }

        public List<IReadOnlyList<AiMessage>> Calls { get; } = [];

        public void Reset() => Calls.Clear();

        public ValueTask<string> CompleteAsync(
            IReadOnlyList<AiMessage> messages,
            string? modelOverride = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToArray());
            var systemPrompt = messages.Count > 0 ? messages[0].Content : string.Empty;
            var prompt = messages.Count > 1 ? messages[1].Content : string.Empty;
            var question = ExtractCurrentUserQuestion(prompt);
            if (question is null || !_scenariosByQuestion.TryGetValue(question, out var scenario))
            {
                return ValueTask.FromResult(systemPrompt.Contains("工具规划器", StringComparison.Ordinal)
                    ? """{"tools":[]}"""
                    : "当前场景未命中脚本化评测用例。[C1]");
            }

            if (systemPrompt.Contains("工具规划器", StringComparison.Ordinal))
                return ValueTask.FromResult(BuildPlanJson(scenario));

            if (systemPrompt.Contains("SQL 纠错器", StringComparison.Ordinal))
                return ValueTask.FromResult(scenario.RepairedSql ?? string.Empty);

            return ValueTask.FromResult($"{scenario.AnswerSummary}[C1][C2][C3]");
        }

        private string BuildPlanJson(CopilotEvalScenario scenario)
        {
            var payload = new PlanEnvelope(scenario.PlannedTools.Select(static tool => new PlannedToolPayload(
                tool.Name,
                tool.Measurement,
                tool.Sql,
                tool.MaxRows,
                tool.N)).ToArray());
            return JsonSerializer.Serialize(payload, _jsonOptions);
        }

        private static string? ExtractCurrentUserQuestion(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return null;

            const string marker = "当前用户问题：";
            var start = prompt.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += marker.Length;
            while (start < prompt.Length && (prompt[start] == '\r' || prompt[start] == '\n'))
                start++;

            var end = prompt.IndexOf(Environment.NewLine + Environment.NewLine, start, StringComparison.Ordinal);
            if (end < 0)
            {
                end = prompt.IndexOf("\n\n", start, StringComparison.Ordinal);
            }

            var question = end < 0 ? prompt[start..] : prompt[start..end];
            return question.Trim();
        }

        private sealed record PlanEnvelope(IReadOnlyList<PlannedToolPayload> Tools);

        private sealed record PlannedToolPayload(
            string Name,
            string? Measurement,
            string? Sql,
            int? MaxRows,
            int? N);
    }

    private sealed record CopilotEvalScenario
    {
        public string Id { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Question { get; init; } = string.Empty;

        public List<AiMessage> History { get; init; } = [];

        public List<CopilotEvalPlannedTool> PlannedTools { get; init; } = [];

        public string? RepairedSql { get; init; }

        public List<string> ExpectedToolNames { get; init; } = [];

        public List<string> ExpectedToolArgumentsContains { get; init; } = [];

        public List<string> ExpectedRetryArgumentsContains { get; init; } = [];

        public List<string> ExpectedToolResultContains { get; init; } = [];

        public List<string> ExpectedAnswerContains { get; init; } = [];

        public string AnswerSummary { get; init; } = string.Empty;

        public int MinCitationCount { get; init; } = 3;

        public int ExpectedRetryCount { get; init; }

        public int? DocsK { get; init; }

        public int? SkillsK { get; init; }
    }

    private sealed record CopilotEvalPlannedTool
    {
        public string Name { get; init; } = string.Empty;

        public string? Measurement { get; init; }

        public string? Sql { get; init; }

        public int? MaxRows { get; init; }

        public int? N { get; init; }
    }

    private sealed record CopilotEvalScenarioResult(
        string Id,
        string Category,
        bool Passed,
        bool CitationHit,
        double ElapsedMilliseconds,
        IReadOnlyList<string> ToolNames,
        int RetryCount,
        int CitationCount,
        string? FailureReason);

    private sealed record CopilotEvalSummary(
        DateTimeOffset GeneratedAtUtc,
        int ScenarioCount,
        int PassedCount,
        int FailedCount,
        double Accuracy,
        double CitationHitRate,
        double P50LatencyMilliseconds,
        double P95LatencyMilliseconds,
        double MaxLatencyMilliseconds,
        double ThresholdAccuracy,
        double ThresholdCitationHitRate,
        double ThresholdP95LatencyMilliseconds,
        IReadOnlyList<CopilotEvalScenarioResult> Results);
}
