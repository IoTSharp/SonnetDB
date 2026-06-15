using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Adapters.Postgres;
using SonnetDB.Parity.Adapters.SonnetDb;
using SonnetDB.Parity.Runner.Reporting;
using SonnetDB.Parity.Scenarios;
using SonnetDB.Parity.Scenarios.Relational;
using Xunit;

namespace SonnetDB.Parity.Runner;

/// <summary>
/// Parity 冒烟驱动（PR #127）。在单个进程内同时实例化 SonnetDB（嵌入式，无需 docker）与
/// Postgres（竞品）两个适配器，跑同一个关系型场景并内联做容差 diff，同时落地 JSON / Markdown 报告。
/// </summary>
/// <remarks>
/// SonnetDB 一侧始终被断言通过；Postgres 一侧通过 <see cref="PostgresAdapter.TryConnectAsync"/>
/// 快速探测，不可达时记录 <c>gap_reason</c> 并 SKIP（不判 FAIL），与 <c>docs/parity-roadmap.md</c>
/// 的"后端不支持 = SKIPPED 而非 fail"约定一致。
/// 跨进程分跑 + 报告归并的完整架构留到 PR #136。
/// </remarks>
public sealed class ParityRunner
{
    /// <summary>关系型 hello-world：SonnetDB 自检通过，且（若 Postgres 可达）两边结果在容差内一致。</summary>
    [Fact]
    public async Task RelationalHelloWorld_SonnetDbMatchesPostgres()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cts.Token;

        IScenario scenario = new HelloWorldRelationalScenario();
        var runId = "smoke-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };

        var backends = new List<BackendOutcome>();
        bool? withinTolerance = null;
        IReadOnlyList<string> differences = [];

        try
        {
            // ── SonnetDB：始终运行并断言 ──────────────────────────────────────
            await using var sonnet = new SonnetDbAdapter();
            var sonnetResult = await scenario.RunAsync(sonnet, ctx);
            backends.Add(new BackendOutcome(
                "sonnetdb",
                sonnetResult.Pass ? "pass" : "fail",
                sonnetResult.GapReason,
                sonnetResult.Rows.Count));

            Assert.True(sonnetResult.Pass, "SonnetDB 关系型冒烟自检未通过。");

            // ── Postgres：可达才跑，否则 SKIP 并记录 gap_reason ───────────────
            if (await PostgresAdapter.TryConnectAsync(ct))
            {
                await using var pg = new PostgresAdapter();
                await pg.OpenAsync(ct);
                var pgResult = await scenario.RunAsync(pg, ctx);

                var diff = ResultDiffer.DiffRows(sonnetResult.Rows, pgResult.Rows);
                withinTolerance = diff.WithinTolerance;
                differences = diff.Differences;

                backends.Add(new BackendOutcome(
                    "postgres",
                    pgResult.Pass && diff.WithinTolerance ? "pass" : "fail",
                    pgResult.GapReason,
                    pgResult.Rows.Count));

                Assert.True(pgResult.Pass, "Postgres 关系型冒烟自检未通过。");
                Assert.True(diff.WithinTolerance,
                    "SonnetDB 与 Postgres 结果超出容差：" + string.Join("; ", diff.Differences));
            }
            else
            {
                backends.Add(new BackendOutcome(
                    "postgres",
                    "skipped",
                    "postgres unreachable (compose 未启动或 PARITY_PG_* 未配置)",
                    0));
            }
        }
        finally
        {
            var report = new ParityReport(
                runId,
                startedAt,
                [new ScenarioReport(scenario.Name, withinTolerance, differences, backends)]);

            await JsonReporter.WriteAsync(report, reportDir);
            await MarkdownReporter.WriteAsync(report, reportDir);
        }
    }

    private static string ResolveReportDirectory(string runId)
    {
        var overrideDir = Environment.GetEnvironmentVariable("PARITY_REPORT_DIR");
        var baseDir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(AppContext.BaseDirectory, "parity-reports")
            : overrideDir;
        return Path.Combine(baseDir, runId);
    }
}
