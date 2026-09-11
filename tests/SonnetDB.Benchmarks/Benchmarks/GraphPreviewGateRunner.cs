using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M40 #352 Native Graph Preview 的本地恢复验证和正式证据判定入口。</summary>
public static class GraphPreviewGateRunner
{
    private static readonly TimeSpan ManifestEvaluationTimeout = TimeSpan.FromHours(12);

    /// <summary>复用真实并发、checkpoint、kill/reopen 和 backup/restore 验证；发布双门禁保持未运行。</summary>
    /// <param name="outputDirectory">本地证据、报告和清单模板的输出目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收任务子进程。</param>
    /// <returns>本地验证报告。</returns>
    public static GraphPreviewGateReport RunQuick(
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        GraphProductionGateReport smoke = GraphProductionGateRunner.RunQuickCore(
            outputDirectory,
            writeProductionReports: false,
            cancellationToken);
        GraphProductionGateInput local = smoke.Input;
        var input = new GraphPreviewGateInput
        {
            PreviewRun = false,
            CommitSha = local.CommitSha,
            StartedUtc = local.StartedUtc,
            FinishedUtc = local.FinishedUtc,
            Dataset = local.Dataset,
            Environment = local.Environment,
            Recovery = local.Soak,
            Journeys = local.Journeys,
            CorrectnessRecoveryChecks = local.CorrectnessRecoveryChecks,
            PerformanceCapacityChecks = local.PerformanceCapacityChecks,
            Limitations =
            [
                "64 vertex/192 edge 的本地恢复验证不等于 preview-small 或 gate 容量证据。",
                "单次真实 kill/reopen 不等于完整故障注入矩阵；本次未运行 Neo4j 或 Couplet C2 联合门禁。",
                "固定硬件、逐 journey 性能/复杂度及 Native Graph Preview 发布准入仍为 NOT_RUN。",
            ],
        };
        GraphPreviewGateReport report = GraphPreviewGateEvaluator.Evaluate(
            input,
            outputDirectory,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        WriteReport(report, Path.GetFullPath(outputDirectory));
        cancellationToken.ThrowIfCancellationRequested();
        WriteTemplate(outputDirectory, local.Environment);
        return report;
    }

    /// <summary>读取 source-generated 清单并判定 #352 的独立双门禁。</summary>
    /// <param name="manifestPath">证据清单路径；artifact 相对路径以其所在目录为基准。</param>
    /// <param name="outputDirectory">JSON 和 Markdown 报告输出目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收复现进程。</param>
    /// <returns>严格门禁报告。</returns>
    public static GraphPreviewGateReport EvaluateManifest(
        string manifestPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
        => EvaluateManifestAsync(manifestPath, outputDirectory, cancellationToken)
            .ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>异步读取有界清单、校验原始证据并写入 #352 双门禁报告。</summary>
    /// <param name="manifestPath">证据清单路径；最多 4 MiB。</param>
    /// <param name="outputDirectory">JSON 和 Markdown 报告输出目录。</param>
    /// <param name="cancellationToken">取消令牌；取消后先回收复现进程。</param>
    /// <returns>严格门禁报告。</returns>
    public static async Task<GraphPreviewGateReport> EvaluateManifestAsync(
        string manifestPath,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(ManifestEvaluationTimeout);
        CancellationToken token = cancellation.Token;
        token.ThrowIfCancellationRequested();
        string fullManifestPath = Path.GetFullPath(manifestPath);
        await using var stream = new FileStream(
            fullManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4_096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > GraphProductionGateRunner.MaximumManifestBytes)
            throw new InvalidDataException("M40 #352 证据清单不能超过 4 MiB。");
        GraphPreviewGateInput input = await JsonSerializer.DeserializeAsync(
            stream,
            GraphPreviewGateJsonContext.Default.GraphPreviewGateInput,
            token).ConfigureAwait(false)
            ?? throw new InvalidDataException("M40 #352 证据清单为空。");
        string artifactRoot = Path.GetDirectoryName(fullManifestPath)!;
        GraphPreviewGateReport report = await GraphPreviewGateEvaluator.EvaluateAsync(
            input, artifactRoot, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        WriteReport(report, outputRoot);
        token.ThrowIfCancellationRequested();
        return report;
    }

    /// <summary>生成 #352 清单模板；占位值和缺失 artifact 会阻止正式门禁通过。</summary>
    /// <param name="outputDirectory">模板输出目录。</param>
    /// <param name="environment">可选的本地环境信息；仍需正式采集的原始 artifact。</param>
    /// <returns>模板绝对路径。</returns>
    public static string WriteTemplate(
        string outputDirectory,
        GraphProductionEnvironmentEvidence? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        string outputRoot = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputRoot);
        var missing = new GraphProductionArtifactEvidence
        {
            Path = "artifacts/REPLACE_ME.json",
            Sha256 = new string('0', 64),
            Command = "REPLACE_ME",
            Arguments = ["REPLACE_ME", "{artifact}"],
        };
        var input = new GraphPreviewGateInput
        {
            PreviewRun = true,
            CommitSha = new string('0', 40),
            Environment = (environment ?? new GraphProductionEnvironmentEvidence()) with { Artifact = missing },
            Dataset = Dataset("gate", 1_000_000, 10_000_000, missing),
            PreviewSmallDataset = Dataset("preview-small", 100_000, 1_000_000, missing),
            Recovery = new GraphProductionSoakEvidence
            {
                ReaderWorkers = 8,
                UpdateWorkers = 1,
                UpdateProfile = "m40-frozen-update-profile-v1",
                SyncWalOnEveryWrite = true,
                AutoCheckpointEnabled = true,
                MaxWalBytes = 256L * 1024 * 1024,
                MaxOverlayEntries = 100_000,
                Artifact = missing,
            },
            Journeys = GraphPreviewGateEvaluator.GetRequiredJourneyIds()
                .Select(id => new GraphProductionJourneyEvidence { Id = id, Artifact = missing }).ToArray(),
            CorrectnessRecoveryChecks = GraphPreviewGateEvaluator.GetRequiredCorrectnessCheckIds()
                .Select(id => new GraphProductionCheckEvidence { Id = id, Artifact = missing }).ToArray(),
            PerformanceCapacityChecks = GraphPreviewGateEvaluator.GetRequiredPerformanceCheckIds()
                .Select(id => new GraphProductionCheckEvidence { Id = id, Artifact = missing }).ToArray(),
            Gaps = GraphPreviewGateEvaluator.GetRequiredGapIds()
                .Select(id => new GraphProductionGapEvidence
                {
                    Id = id,
                    Status = "open",
                    Blocks = "Preview; Couplet C2",
                    Severity = "correctness_recovery",
                }).ToArray(),
            Limitations = ["替换全部占位值并附固定版本的原始证据；模板本身不能通过 #352。"],
        };
        string path = Path.Combine(outputRoot, "m40-graph-preview-input.template.json");
        GraphProductionGateRunner.WriteAtomically(path,
            JsonSerializer.Serialize(input, GraphPreviewGateJsonContext.Default.GraphPreviewGateInput));
        return path;
    }

    private static GraphProductionDatasetEvidence Dataset(
        string tier, long vertices, long edges, GraphProductionArtifactEvidence artifact)
        => new()
        {
            Tier = tier,
            VertexCount = vertices,
            EdgeCount = edges,
            InputDigest = new string('0', 64),
            OutputDigest = new string('0', 64),
            Artifact = artifact,
        };

    private static void WriteReport(GraphPreviewGateReport report, string outputRoot)
    {
        var markdown = new StringBuilder();
        markdown.AppendLine("# M40 #352 Native Graph Preview Gate");
        markdown.AppendLine();
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.Input.CommitSha}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Local smoke: `{report.LocalSmoke}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Correctness/recovery: `{report.CorrectnessRecovery}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Performance/capacity: `{report.PerformanceCapacity}`");
        markdown.AppendLine(CultureInfo.InvariantCulture, $"- Release decision: `{report.ReleaseDecision}`");
        markdown.AppendLine();
        markdown.AppendLine("逐项原始证据、查询指标与阻塞原因见同名 JSON 报告。");
        GraphProductionGateRunner.WriteAtomically(
            Path.Combine(outputRoot, "m40-graph-preview-gate.json"),
            JsonSerializer.Serialize(report, GraphPreviewGateJsonContext.Default.GraphPreviewGateReport));
        GraphProductionGateRunner.WriteAtomically(
            Path.Combine(outputRoot, "m40-graph-preview-gate.md"), markdown.ToString());
    }
}
