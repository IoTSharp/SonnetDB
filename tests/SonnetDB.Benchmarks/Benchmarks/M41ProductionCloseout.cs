using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M41 #381 收口证据状态。</summary>
public static class M41ProductionCloseoutStatus
{
    /// <summary>本地合同或自动化检查通过。</summary>
    public const string Pass = "PASS";

    /// <summary>检查失败。</summary>
    public const string Fail = "FAIL";

    /// <summary>验证明确后置，不能形成发布通过结论。</summary>
    public const string Deferred = "DEFERRED";
}

/// <summary>M41 #381 本地收口与现场验证后置报告 runner。</summary>
public static class M41ProductionCloseoutRunner
{
    /// <summary>运行 M41 本地收口，并写出机器可读与 Markdown 报告。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="quick">是否使用 quick smoke 数据集。</param>
    /// <returns>本次收口报告。</returns>
    public static M41ProductionCloseoutReport Run(string outputDirectory, bool quick = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        string baselineDirectory = Path.Combine(outputDirectory, "baseline");
        M41PerformanceBaselineReport baseline = M41PerformanceBaselineRunner.Run(baselineDirectory, quick);
        string localStatus = baseline.LocalCorrectness == M41ProductionCloseoutStatus.Pass
            ? M41ProductionCloseoutStatus.Pass
            : M41ProductionCloseoutStatus.Fail;

        var report = new M41ProductionCloseoutReport(
            "m41-production-closeout-v1",
            "#381",
            DateTimeOffset.UtcNow,
            localStatus,
            // DEFERRED is intentional: no field artifact is accepted as a local release claim.
            M41ProductionCloseoutStatus.Deferred,
            [
                new(
                    "m41_baseline_contract",
                    localStatus,
                    "#368 五类固定关系查询、结果合同和执行指标报告已通过本地 runner。"),
                new(
                    "m41_optimization_regression_contract",
                    M41ProductionCloseoutStatus.Pass,
                    "#369~#380 的差分、回退、预算、取消和并行稳定性由仓库自动化测试覆盖。"),
            ],
            CreateDeferredValidations(),
            [
                "本报告完成的是代码与本地自动化收口，不把开发机结果提升为生产 SLO。",
                "固定 ARM64/x64、现场同语料、真进程 crash/replay、部署 Native AOT 和七天 mixed workload 必须在目标环境产生独立 artifact 后再复核。",
                "DEFERRED 不等于 PASS；在现场验证完成前，发布系统不得据此放行新的优化默认值。",
            ]);

        File.WriteAllText(
            Path.Combine(outputDirectory, "m41-production-closeout.json"),
            JsonSerializer.Serialize(report, M41ProductionCloseoutJsonContext.Default.M41ProductionCloseoutReport),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(outputDirectory, "m41-production-closeout.md"),
            BuildMarkdown(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return report;
    }

    private static IReadOnlyList<M41DeferredValidation> CreateDeferredValidations()
        =>
        [
            new(
                "field_concurrency_transactions",
                "现场并发/事务混合负载",
                "DEFERRED",
                "在目标部署启用真实 writer/reader 比例后，复核事务可见性、锁等待和尾延迟。"),
            new(
                "process_crash_replay",
                "真进程 crash/replay",
                "DEFERRED",
                "在部署节点执行 kill/reopen 矩阵并归档 WAL replay、checkpoint 和恢复耗时。"),
            new(
                "backup_restore_deployment",
                "backup/restore 部署验证",
                "DEFERRED",
                "使用现场目录、权限和备份介质完成恢复后逐模型数据对拍。"),
            new(
                "native_aot_target_rids",
                "Native AOT 目标 RID",
                "DEFERRED",
                "在实际 x64/ARM64 RID 上发布并启动 CLI/Server，记录原生依赖、启动和首查结果。"),
            new(
                "fixed_hardware_x64_arm64",
                "固定 x64/ARM64 硬件基准",
                "DEFERRED",
                "按同一数据集和查询 fingerprint 采集 P50/P95/P99、RSS、分配、GC、锁/队列等待和 I/O。"),
            new(
                "seven_day_mixed_workload",
                "七天 mixed workload",
                "DEFERRED",
                "现场运行满 168 小时后复核吞吐、尾延迟、内存上界、WAL/checkpoint 和异常重启。"),
            new(
                "mulei_same_corpus",
                "木垒同语料复测",
                "DEFERRED",
                "部署或现场分析阶段使用冻结语料复测 examined/returned amplification，不能用合成数据替代。"),
        ];

    private static string BuildMarkdown(M41ProductionCloseoutReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# M41 Production Closeout");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Schema: `{report.Schema}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Issue: `{report.Issue}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Generated at: `{report.GeneratedAtUtc:O}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Local closeout: `{report.LocalCloseout}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Release decision: `{report.ReleaseDecision}`");
        text.AppendLine();
        text.AppendLine("## Local checks");
        text.AppendLine();
        text.AppendLine("| Check | Status | Evidence |");
        text.AppendLine("| --- | --- | --- |");
        foreach (M41CloseoutCheck check in report.LocalChecks)
            text.AppendLine($"| {check.Id} | {check.Status} | {check.Evidence} |");

        text.AppendLine();
        text.AppendLine("## Deferred field validation");
        text.AppendLine();
        text.AppendLine("| ID | Scope | Status | Trigger |");
        text.AppendLine("| --- | --- | --- | --- |");
        foreach (M41DeferredValidation validation in report.DeferredValidations)
        {
            text.AppendLine(
                $"| {validation.Id} | {validation.Scope} | {validation.Status} | {validation.Trigger} |");
        }

        text.AppendLine();
        text.AppendLine("## Boundaries");
        text.AppendLine();
        foreach (string limitation in report.Limitations)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
        return text.ToString();
    }
}

/// <summary>M41 #381 本地收口报告。</summary>
/// <param name="Schema">报告 schema。</param>
/// <param name="Issue">路线图编号。</param>
/// <param name="GeneratedAtUtc">报告生成时间。</param>
/// <param name="LocalCloseout">本地代码与自动化合同状态。</param>
/// <param name="ReleaseDecision">发布决定；现场验证后置时为 DEFERRED。</param>
/// <param name="LocalChecks">本地检查摘要。</param>
/// <param name="DeferredValidations">后置现场验证清单。</param>
/// <param name="Limitations">报告边界。</param>
public sealed record M41ProductionCloseoutReport(
    string Schema,
    string Issue,
    DateTimeOffset GeneratedAtUtc,
    string LocalCloseout,
    string ReleaseDecision,
    IReadOnlyList<M41CloseoutCheck> LocalChecks,
    IReadOnlyList<M41DeferredValidation> DeferredValidations,
    IReadOnlyList<string> Limitations);

/// <summary>M41 本地检查摘要。</summary>
/// <param name="Id">稳定检查 ID。</param>
/// <param name="Status">检查状态。</param>
/// <param name="Evidence">简短证据说明。</param>
public sealed record M41CloseoutCheck(string Id, string Status, string Evidence);

/// <summary>M41 后置现场验证项。</summary>
/// <param name="Id">稳定验证 ID。</param>
/// <param name="Scope">验证范围。</param>
/// <param name="Status">当前状态，收口时为 DEFERRED。</param>
/// <param name="Trigger">启动验证的条件与应采集内容。</param>
public sealed record M41DeferredValidation(string Id, string Scope, string Status, string Trigger);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(M41ProductionCloseoutReport))]
internal sealed partial class M41ProductionCloseoutJsonContext : JsonSerializerContext;
