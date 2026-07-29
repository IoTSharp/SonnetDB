using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// 运行 M39 #333 成本矩阵并写出机器可读与 Markdown 证据。
/// </summary>
public static class TriggerEvidenceReportRunner
{
    private const string CrashEvidenceVerifiedEnvironmentVariable = "M39_CRASH_EVIDENCE_VERIFIED";
    private static readonly int[] MatrixRows = [1, 100, 10_000];

    /// <summary>
    /// 执行完整成本/回滚矩阵并将结果写入指定目录。
    /// </summary>
    /// <param name="outputDirectory">JSON 与 Markdown 输出目录。</param>
    /// <returns>本次运行的完整报告。</returns>
    public static TriggerEvidenceReport Run(string outputDirectory)
        => Run(outputDirectory, MatrixRows, ResolveCrashTestsVerified());

    /// <summary>
    /// 执行指定行数集合的成本/回滚矩阵；用于快速验证报告管线，正式 #333 证据必须调用
    /// <see cref="Run(string)"/> 覆盖 1、100、10,000 行。
    /// </summary>
    /// <param name="outputDirectory">JSON 与 Markdown 输出目录。</param>
    /// <param name="rowCounts">要测量的正行数集合。</param>
    /// <returns>本次运行的完整报告。</returns>
    public static TriggerEvidenceReport Run(
        string outputDirectory,
        IReadOnlyList<int> rowCounts)
        => Run(outputDirectory, rowCounts, ResolveCrashTestsVerified());

    /// <summary>
    /// 执行指定行数集合的成本/回滚矩阵，并注明独立 crash/replay 测试是否已在同一证据流程中通过。
    /// 只有调用方声明通过且 <c>M39_CRASH_EVIDENCE_VERIFIED=true</c> 时才记录为已验证。
    /// </summary>
    /// <param name="outputDirectory">JSON 与 Markdown 输出目录。</param>
    /// <param name="rowCounts">要测量的正行数集合。</param>
    /// <param name="crashTestsVerified">调用方是否已先运行并通过 Core/CrashTests。</param>
    /// <returns>本次运行的完整报告。</returns>
    public static TriggerEvidenceReport Run(
        string outputDirectory,
        IReadOnlyList<int> rowCounts,
        bool crashTestsVerified)
    {
        // The workflow sets this marker only after the independent Core and
        // process-kill tests pass. A caller-provided true value alone must not
        // turn an unverified report into a positive crash claim.
        crashTestsVerified = crashTestsVerified && ResolveCrashTestsVerified();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(rowCounts);
        if (rowCounts.Count == 0 || rowCounts.Any(static rows => rows <= 0))
            throw new ArgumentOutOfRangeException(nameof(rowCounts));
        Directory.CreateDirectory(outputDirectory);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var costs = new List<TriggerCostEvidence>(rowCounts.Count * 3);
        var rollbacks = new List<TriggerRollbackEvidenceRow>(rowCounts.Count * 3);
        foreach (int rows in rowCounts)
        {
            foreach (TriggerPath path in Enum.GetValues<TriggerPath>())
            {
                // The large row-trigger path allocates several GB by design. Collect
                // between samples so the report measures the next sample rather than
                // retained heap pressure from a previous path.
                ForceCollection();
                var baseline = new TriggerBaselineBenchmark { Rows = rows, Path = path }
                    .RunSingleIteration();
                if (baseline.RowsInserted != rows)
                {
                    throw new InvalidDataException(
                        $"M39 cost evidence inserted {baseline.RowsInserted} rows; expected {rows} ({path}).");
                }
                costs.Add(new TriggerCostEvidence(
                    rows,
                    path.ToString(),
                    baseline.RowsInserted,
                    baseline.ElapsedMilliseconds,
                    RowsPerSecond(rows, baseline.ElapsedMilliseconds),
                    baseline.WalBytes,
                    baseline.RowStoreBytes,
                    baseline.WorkingSetBytes,
                    baseline.ManagedBytes,
                    baseline.AllocatedBytes));

                ForceCollection();
                var rollback = TriggerRollbackEvidence.RunSingleIteration(rows, path);
                int expectedAuditRows = path == TriggerPath.NoTrigger ? 0 : 1;
                if (!rollback.FailedAsExpected
                    || rollback.SourceRowsAfterRollback != 0
                    || rollback.AuditRowsAfterRollback != expectedAuditRows)
                {
                    throw new InvalidDataException(
                        $"M39 rollback evidence mismatch: rows={rows}, path={path}, "
                        + $"failed={rollback.FailedAsExpected}, source={rollback.SourceRowsAfterRollback}, "
                        + $"audit={rollback.AuditRowsAfterRollback}.");
                }
                rollbacks.Add(new TriggerRollbackEvidenceRow(
                    rows,
                    path.ToString(),
                    rollback.FailedAsExpected,
                    rollback.ElapsedMilliseconds,
                    rollback.WalBytes,
                    rollback.SourceRowsAfterRollback,
                    rollback.AuditRowsAfterRollback,
                    rollback.AllocatedBytes,
                    rollback.FailureCode));
            }
        }

        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        bool fullMatrix = rowCounts.Count == MatrixRows.Length
            && MatrixRows.All(rowCounts.Contains);
        var report = new TriggerEvidenceReport(
            "m39-trigger-v2-baseline-v2",
            "#333",
            ResolveCommitSha(),
            startedUtc,
            finishedUtc,
            rowCounts.ToArray(),
            crashTestsVerified,
            new TriggerEnvironment(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            new TriggerJourneyEvidence[]
            {
                new(
                    "audit_outbox",
                    "SqlTriggerV2BaselineTests.GoldenJourney_AuditOutbox_EmitsDurableEventsForEveryRowMutation",
                    crashTestsVerified ? "validated_by_core_test" : "test_reference_not_run"),
                new(
                    "derived_aggregate",
                    "SqlTriggerV2BaselineTests.GoldenJourney_DerivedAggregate_TracksInsertUpdateAndDeleteDeltas",
                    crashTestsVerified ? "validated_by_core_test" : "test_reference_not_run"),
                new(
                    "state_transition_protection",
                    "SqlTriggerV2BaselineTests.GoldenJourney_StateTransitionProtection_RollsBackForbiddenTransition",
                    crashTestsVerified ? "validated_by_core_test" : "test_reference_not_run"),
            },
            costs.ToArray(),
            rollbacks.ToArray(),
            new TriggerCrashEvidence[]
            {
                new(
                    "trigger_action_failure_midway",
                    "SqlTriggerV2BaselineTests.CrashEvidence_TriggerActionFailureMidBatch_RollsBackEarlierRows",
                    crashTestsVerified ? "validated_by_core_test" : "test_reference_not_run"),
                new(
                    "commit_failure",
                    "SqlTriggerV2BaselineTests.CrashEvidence_CommitFailure_RollsBackAllTablesAndMarksTriggerFailed",
                    crashTestsVerified ? "validated_by_core_test" : "test_reference_not_run"),
                new(
                    "process_termination_between_table_wals",
                    "SonnetDB.CrashTests.crash_kill9_betweenTriggerTableCommits_ReopenReportsMeasuredPartialPair",
                    crashTestsVerified ? "validated_by_process_kill_test" : "test_reference_not_run"),
                new(
                    "restart_wal_replay",
                    "SqlTriggerV2BaselineTests.CrashEvidence_RestartReplay_PreservesCommittedTriggerOutbox",
                    crashTestsVerified ? "validated_by_core_test" : "test_reference_not_run"),
            },
            [
                crashTestsVerified
                    ? "当前 V1 AFTER ROW 在关系表上可执行，三条 golden journey 的行级合同与进程内回滚已由 Core 测试验证。"
                    : "当前 V1 AFTER ROW 在关系表上可执行；本次命令实际执行了成本/回滚矩阵，golden journey 的 Core 断言未在本命令中运行。",
                fullMatrix
                    ? "1、100、10,000 行成本矩阵覆盖无触发器、V1 行触发器和客户端候选 statement 参考路径。"
                    : "本次运行只覆盖报告中的 RowCounts；它是管线 smoke，不是完整 #333 成本证据。",
                crashTestsVerified
                    ? "提交失败、真进程终止和重启 replay 已由同一证据流程的自动化测试验证；跨 keyspace 掉电原子性不被宣称。"
                    : "报告仅登记提交失败、真进程终止和重启 replay 的测试入口，本命令未执行这些测试；跨 keyspace 掉电原子性不被宣称。",
            ],
            [
                "CandidateStatementReference 是显式事务 + 单条汇总写入的客户端参考，不是产品 statement trigger。",
                "本轮成本矩阵以批量 INSERT 代表写放大；UPDATE/DELETE 只在 golden journey 做功能对拍，尚未有同规模成本样本。",
                "本报告不是固定硬件容量声明；正式结论还需在目标机器重复并归档原始输出。",
                "Document/measurement、BEFORE、transition table、deferred 和 exactly-once 语义仍未准入。",
            ]);

        string json = JsonSerializer.Serialize(report, TriggerEvidenceJsonContext.Default.TriggerEvidenceReport);
        File.WriteAllText(Path.Combine(outputDirectory, "report.json"), json, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), BuildMarkdown(report), Encoding.UTF8);
        return report;
    }

    private static bool ResolveCrashTestsVerified()
        => string.Equals(
            Environment.GetEnvironmentVariable(CrashEvidenceVerifiedEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static double RowsPerSecond(int rows, double elapsedMilliseconds)
        => rows / Math.Max(elapsedMilliseconds / 1_000d, 0.000001d);

    private static void ForceCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    private static string ResolveCommitSha()
    {
        string? configured = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        try
        {
            var gitHeadStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            gitHeadStartInfo.ArgumentList.Add("rev-parse");
            gitHeadStartInfo.ArgumentList.Add("HEAD");
            using var process = Process.Start(gitHeadStartInfo);
            if (process is null)
                return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0 || output.Length == 0)
                return "unknown";

            var gitStatusStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            gitStatusStartInfo.ArgumentList.Add("status");
            gitStatusStartInfo.ArgumentList.Add("--porcelain");
            gitStatusStartInfo.ArgumentList.Add("--untracked-files=normal");
            using var statusProcess = Process.Start(gitStatusStartInfo);
            if (statusProcess is null)
                return output + "-status-unknown";
            string status = statusProcess.StandardOutput.ReadToEnd();
            statusProcess.WaitForExit();
            if (statusProcess.ExitCode != 0)
                return output + "-status-unknown";
            return status.Length != 0 ? output + "-dirty" : output;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string BuildMarkdown(TriggerEvidenceReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# M39 SQL Trigger V2 Baseline Report");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Schema: `{report.Schema}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Issue: `{report.Issue}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.CommitSha}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Started UTC: `{report.StartedUtc:O}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Finished UTC: `{report.FinishedUtc:O}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Row counts: `{string.Join(", ", report.RowCounts.Select(static value => value.ToString("N0", CultureInfo.InvariantCulture)))}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Crash/replay tests verified in this flow: `{report.CrashTestsVerified}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Runtime: `{report.Environment.Framework}` / `{report.Environment.Os}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Architecture: `{report.Environment.Architecture}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- CPU count: `{report.Environment.ProcessorCount}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Available memory bytes: `{report.Environment.AvailableMemoryBytes:N0}`");
        text.AppendLine();
        text.AppendLine("## Golden journeys");
        text.AppendLine();
        foreach (TriggerJourneyEvidence journey in report.Journeys)
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{journey.Name}`: `{journey.Status}` (`{journey.TestName}`)");

        text.AppendLine();
        text.AppendLine("## Cost matrix");
        text.AppendLine();
        text.AppendLine("| Rows | Path | Rows/sec | Elapsed ms | WAL bytes | Rowstore bytes | Working set | Managed | Allocated |");
        text.AppendLine("| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (TriggerCostEvidence row in report.CostMatrix)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Rows:N0} | {row.Path} | {row.RowsPerSecond:F2} | {row.ElapsedMilliseconds:F2} | "
                + $"{row.WalBytes:N0} | {row.RowStoreBytes:N0} | {row.WorkingSetBytes:N0} | "
                + $"{row.ManagedBytes:N0} | {row.AllocatedBytes:N0} |");
        }

        text.AppendLine();
        text.AppendLine("## Rollback matrix");
        text.AppendLine();
        text.AppendLine("| Rows | Path | Failed as expected | Elapsed ms | WAL bytes | Source after | Audit after | Failure code |");
        text.AppendLine("| ---: | --- | --- | ---: | ---: | ---: | ---: | --- |");
        foreach (TriggerRollbackEvidenceRow row in report.RollbackMatrix)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Rows:N0} | {row.Path} | {row.FailedAsExpected} | {row.ElapsedMilliseconds:F2} | "
                + $"{row.WalBytes:N0} | {row.SourceRowsAfterRollback} | {row.AuditRowsAfterRollback} | {row.FailureCode} |");
        }

        text.AppendLine();
        text.AppendLine("## Crash and replay evidence");
        text.AppendLine();
        foreach (TriggerCrashEvidence evidence in report.CrashEvidence)
            text.AppendLine(CultureInfo.InvariantCulture, $"- `{evidence.Scenario}`: `{evidence.Status}` (`{evidence.TestName}`)");

        AppendBoundary(text, "Validated", report.Validated);
        AppendBoundary(text, "Not Proven", report.NotProven);
        return text.ToString();
    }

    private static void AppendBoundary(StringBuilder text, string heading, IReadOnlyList<string> values)
    {
        text.AppendLine();
        text.AppendLine("## " + heading);
        text.AppendLine();
        foreach (string value in values)
            text.AppendLine("- " + value);
    }
}

/// <summary>M39 证据报告顶层模型。</summary>
public sealed record TriggerEvidenceReport(
    string Schema,
    string Issue,
    string CommitSha,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    int[] RowCounts,
    bool CrashTestsVerified,
    TriggerEnvironment Environment,
    TriggerJourneyEvidence[] Journeys,
    TriggerCostEvidence[] CostMatrix,
    TriggerRollbackEvidenceRow[] RollbackMatrix,
    TriggerCrashEvidence[] CrashEvidence,
    string[] Validated,
    string[] NotProven);

/// <summary>证据运行环境信息。</summary>
public sealed record TriggerEnvironment(
    string Framework,
    string Os,
    string Architecture,
    int ProcessorCount,
    long AvailableMemoryBytes);

/// <summary>工业 journey 的自动化验证索引。</summary>
public sealed record TriggerJourneyEvidence(
    string Name,
    string TestName,
    string Status);

/// <summary>成功 DML 成本样本。</summary>
public sealed record TriggerCostEvidence(
    int Rows,
    string Path,
    int RowsInserted,
    double ElapsedMilliseconds,
    double RowsPerSecond,
    long WalBytes,
    long RowStoreBytes,
    long WorkingSetBytes,
    long ManagedBytes,
    long AllocatedBytes);

/// <summary>失败回滚成本样本。</summary>
public sealed record TriggerRollbackEvidenceRow(
    int Rows,
    string Path,
    bool FailedAsExpected,
    double ElapsedMilliseconds,
    long WalBytes,
    int SourceRowsAfterRollback,
    int AuditRowsAfterRollback,
    long AllocatedBytes,
    string FailureCode);

/// <summary>崩溃/重放场景的自动化验证索引。</summary>
public sealed record TriggerCrashEvidence(
    string Scenario,
    string TestName,
    string Status);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(TriggerEvidenceReport))]
internal sealed partial class TriggerEvidenceJsonContext : JsonSerializerContext;
