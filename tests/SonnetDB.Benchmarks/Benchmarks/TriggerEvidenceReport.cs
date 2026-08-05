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
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly int[] MatrixRows = [1, 100, 10_000];
    private static readonly TriggerDmlOperation[] MatrixOperations =
    [
        TriggerDmlOperation.Insert,
        TriggerDmlOperation.Update,
        TriggerDmlOperation.Delete,
    ];
    private static readonly TriggerPath[] MatrixPaths = Enum.GetValues<TriggerPath>();

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
        // workflow 只会在独立 Core 与真进程终止测试通过后设置环境标记；调用方单独传入
        // true 不能把未经验证的报告提升为已验证的 crash/replay 结论。
        crashTestsVerified = crashTestsVerified && ResolveCrashTestsVerified();
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(rowCounts);
        if (rowCounts.Count == 0 || rowCounts.Any(static rows => rows <= 0))
            throw new ArgumentOutOfRangeException(nameof(rowCounts));
        Directory.CreateDirectory(outputDirectory);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        int matrixCapacity = checked(rowCounts.Count * MatrixOperations.Length * MatrixPaths.Length);
        var costs = new List<TriggerCostEvidence>(matrixCapacity);
        var rollbacks = new List<TriggerRollbackEvidenceRow>(matrixCapacity);
        foreach (int rows in rowCounts)
        {
            foreach (TriggerDmlOperation operation in MatrixOperations)
            {
                foreach (TriggerPath path in MatrixPaths)
                {
                    // 大批量行触发器路径会产生显著分配；样本间主动回收，避免上一组合的
                    // 保留堆压力污染下一组 operation/path 的观测值。
                    ForceCollection();
                    var baseline = new TriggerBaselineBenchmark
                    {
                        Rows = rows,
                        Operation = operation,
                        Path = path,
                    }.RunSingleIteration();
                    if (baseline.Operation != operation || baseline.RowsAffected != rows)
                    {
                        throw new InvalidDataException(
                            $"M39 cost evidence affected {baseline.RowsAffected} rows; expected {rows} "
                            + $"(operation={operation}, sampleOperation={baseline.Operation}, path={path}).");
                    }
                    costs.Add(new TriggerCostEvidence(
                        rows,
                        path.ToString(),
                        baseline.RowsInserted,
                        baseline.ElapsedMilliseconds,
                        RowsPerSecond(baseline.RowsAffected, baseline.ElapsedMilliseconds),
                        baseline.WalBytes,
                        baseline.RowStoreBytesDelta,
                        baseline.WorkingSetBytes,
                        baseline.ManagedBytes,
                        baseline.AllocatedBytes)
                    {
                        Operation = operation.ToString(),
                        RowsAffected = baseline.RowsAffected,
                    });

                    ForceCollection();
                    var rollback = TriggerRollbackEvidence.RunSingleIteration(rows, path, operation);
                    int expectedSourceRows = operation == TriggerDmlOperation.Insert ? 0 : rows;
                    const int expectedAuditRows = 1;
                    if (rollback.Operation != operation
                        || !rollback.FailedAsExpected
                        || !rollback.SourceStateRestored
                        || !rollback.AuditStateRestored
                        || rollback.SourceRowsAfterRollback != expectedSourceRows
                        || rollback.AuditRowsAfterRollback != expectedAuditRows)
                    {
                        throw new InvalidDataException(
                            $"M39 rollback evidence mismatch: rows={rows}, operation={operation}, path={path}, "
                            + $"sampleOperation={rollback.Operation}, failed={rollback.FailedAsExpected}, "
                            + $"sourceRestored={rollback.SourceStateRestored}, "
                            + $"auditRestored={rollback.AuditStateRestored}, "
                            + $"source={rollback.SourceRowsAfterRollback}, audit={rollback.AuditRowsAfterRollback}.");
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
                        rollback.FailureCode)
                    {
                        Operation = operation.ToString(),
                        SourceStateRestored = rollback.SourceStateRestored,
                        AuditStateRestored = rollback.AuditStateRestored,
                    });
                }
            }
        }

        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        bool fullMatrix = IsFullMatrix(rowCounts, costs, rollbacks);
        var report = new TriggerEvidenceReport(
            "m39-trigger-v2-baseline-v3",
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
                    ? "1、100、10,000 行的 INSERT、UPDATE、DELETE 成功与回滚矩阵覆盖无触发器、V1 行触发器和客户端事务参考路径。"
                    : "本次运行覆盖三种 DML 和三条路径，但只覆盖报告中的 RowCounts；它是管线 smoke，不是完整 #333 成本证据。",
                crashTestsVerified
                    ? "提交失败、真进程终止和重启 replay 已由同一证据流程的自动化测试验证；跨 keyspace 掉电原子性不被宣称。"
                    : "报告仅登记提交失败、真进程终止和重启 replay 的测试入口，本命令未执行这些测试；跨 keyspace 掉电原子性不被宣称。",
            ],
            [
                "CandidateStatementReference 是显式事务与汇总写入的客户端参考，不构成产品 statement trigger 的实现或语义证明。",
                "成本与回滚数据只代表报告所记录的本次运行环境，不构成固定硬件容量、生产吞吐或 SLO 声明。",
                "Document/measurement、BEFORE、transition table、deferred 和 exactly-once 语义仍未准入。",
            ])
        {
            Operations = MatrixOperations.Select(static operation => operation.ToString()).ToArray(),
        };

        string json = JsonSerializer.Serialize(report, TriggerEvidenceJsonContext.Default.TriggerEvidenceReport);
        File.WriteAllText(Path.Combine(outputDirectory, "report.json"), json, Utf8WithoutBom);
        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), BuildMarkdown(report), Utf8WithoutBom);
        return report;
    }

    /// <summary>
    /// 验证正式证据同时覆盖固定行数、三种 DML 和全部触发器路径，且成功与回滚矩阵均无缺项或重复项。
    /// </summary>
    private static bool IsFullMatrix(
        IReadOnlyList<int> rowCounts,
        IReadOnlyCollection<TriggerCostEvidence> costs,
        IReadOnlyCollection<TriggerRollbackEvidenceRow> rollbacks)
    {
        if (rowCounts.Count != MatrixRows.Length || !MatrixRows.All(rowCounts.Contains))
            return false;

        // 用三维组合键对拍两张矩阵，避免仅凭样本总数把重复组合误判为完整覆盖。
        var expectedKeys = new HashSet<(int Rows, string Operation, string Path)>();
        foreach (int rows in MatrixRows)
        {
            foreach (TriggerDmlOperation operation in MatrixOperations)
            {
                foreach (TriggerPath path in MatrixPaths)
                    expectedKeys.Add((rows, operation.ToString(), path.ToString()));
            }
        }

        HashSet<(int Rows, string Operation, string Path)> costKeys = costs
            .Select(static row => (row.Rows, row.Operation, row.Path))
            .ToHashSet();
        HashSet<(int Rows, string Operation, string Path)> rollbackKeys = rollbacks
            .Select(static row => (row.Rows, row.Operation, row.Path))
            .ToHashSet();
        return costs.Count == expectedKeys.Count
            && rollbacks.Count == expectedKeys.Count
            && costKeys.SetEquals(expectedKeys)
            && rollbackKeys.SetEquals(expectedKeys);
    }

    /// <summary>读取 workflow 在独立 crash/replay 测试通过后设置的验证标记。</summary>
    private static bool ResolveCrashTestsVerified()
        => string.Equals(
            Environment.GetEnvironmentVariable(CrashEvidenceVerifiedEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>按受影响行数和耗时计算每秒处理行数，并避免零耗时导致除零。</summary>
    private static double RowsPerSecond(int rows, double elapsedMilliseconds)
        => rows / Math.Max(elapsedMilliseconds / 1_000d, 0.000001d);

    /// <summary>在矩阵样本之间执行完整 GC，降低前一个样本保留内存对后续观测的影响。</summary>
    private static void ForceCollection()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }

    /// <summary>解析当前提交 SHA，并在工作区存在改动时附加 dirty 标记。</summary>
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

    /// <summary>将机器可读报告渲染为便于审阅的 Markdown 证据。</summary>
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
        text.AppendLine(CultureInfo.InvariantCulture, $"- Operations: `{string.Join(", ", report.Operations)}`");
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
        text.AppendLine("| Rows | Operation | Path | Rows affected | Rows/sec | Elapsed ms | WAL bytes | Rowstore bytes delta | Working set | Managed | Allocated |");
        text.AppendLine("| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (TriggerCostEvidence row in report.CostMatrix)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Rows:N0} | {row.Operation} | {row.Path} | {row.RowsAffected:N0} | "
                + $"{row.RowsPerSecond:F2} | {row.ElapsedMilliseconds:F2} | "
                + $"{row.WalBytes:N0} | {row.RowStoreBytesDelta:N0} | {row.WorkingSetBytes:N0} | "
                + $"{row.ManagedBytes:N0} | {row.AllocatedBytes:N0} |");
        }

        text.AppendLine();
        text.AppendLine("## Rollback matrix");
        text.AppendLine();
        text.AppendLine("| Rows | Operation | Path | Failed as expected | Source restored | Audit restored | Elapsed ms | WAL bytes | Source after | Audit after | Failure code |");
        text.AppendLine("| ---: | --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | --- |");
        foreach (TriggerRollbackEvidenceRow row in report.RollbackMatrix)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {row.Rows:N0} | {row.Operation} | {row.Path} | {row.FailedAsExpected} | "
                + $"{row.SourceStateRestored} | {row.AuditStateRestored} | {row.ElapsedMilliseconds:F2} | "
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

    /// <summary>向 Markdown 追加已验证或未证明的边界条目。</summary>
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
    string[] NotProven)
{
    /// <summary>本报告覆盖的 DML 类型；旧构造调用默认表示仅覆盖 INSERT。</summary>
    public string[] Operations { get; init; } = [nameof(TriggerDmlOperation.Insert)];
}

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
    long AllocatedBytes)
{
    /// <summary>本样本执行的 DML 类型；旧构造调用默认仍表示 INSERT。</summary>
    public string Operation { get; init; } = nameof(TriggerDmlOperation.Insert);

    /// <summary>源 DML 实际受影响的行数；默认沿用兼容字段 RowsInserted。</summary>
    public int RowsAffected { get; init; } = RowsInserted;

    /// <summary>checkpoint 后 rowstore 文件总量相对 setup 基线的有符号差值。</summary>
    public long RowStoreBytesDelta => RowStoreBytes;
}

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
    string FailureCode)
{
    /// <summary>本样本执行的 DML 类型；旧构造调用默认仍表示 INSERT。</summary>
    public string Operation { get; init; } = nameof(TriggerDmlOperation.Insert);

    /// <summary>失败后源表是否逐行恢复到 DML 前状态；旧构造调用保持保守的未验证值。</summary>
    public bool SourceStateRestored { get; init; }

    /// <summary>失败后审计表是否只保留原始 sentinel；旧构造调用保持保守的未验证值。</summary>
    public bool AuditStateRestored { get; init; }
}

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
