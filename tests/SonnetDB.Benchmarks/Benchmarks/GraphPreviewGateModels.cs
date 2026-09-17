using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M40 #352 Native Graph Preview 的原始证据清单。</summary>
public sealed record GraphPreviewGateInput
{
    /// <summary>独立于 Production 的输入格式标识。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-preview-input-v1";

    /// <summary>是否提交完整 Preview 发布门禁证据；本地烟雾测试必须为 false。</summary>
    public bool PreviewRun { get; init; }

    /// <summary>与证据工作树 HEAD 一致的被测提交 SHA。</summary>
    public string CommitSha { get; init; } = "unknown";

    /// <summary>本次混合读写与恢复测量的开始 UTC 时间。</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>本次混合读写与恢复测量的结束 UTC 时间。</summary>
    public DateTimeOffset FinishedUtc { get; init; }

    /// <summary>100 万顶点、1,000 万边的 gate 档位原始数据证据。</summary>
    public GraphProductionDatasetEvidence Dataset { get; init; } = new();

    /// <summary>10 万顶点、100 万边的 preview-small 档位原始数据证据。</summary>
    public GraphProductionDatasetEvidence PreviewSmallDataset { get; init; } = new();

    /// <summary>#341 冻结目标硬件与运行时的原始证据。</summary>
    public GraphProductionEnvironmentEvidence Environment { get; init; } = new();

    /// <summary>复用 mixed-workload 原始格式的耐久、checkpoint、真实 kill/reopen 和冷启动证据。</summary>
    public GraphProductionSoakEvidence Recovery { get; init; } = new();

    /// <summary>SOC、TOP、EVD 与 CPL 各 1～3 的十二条原生图旅程证据。</summary>
    public IReadOnlyList<GraphProductionJourneyEvidence> Journeys { get; init; } = [];

    /// <summary>正确性、外部 Neo4j 对拍与恢复检查。</summary>
    public IReadOnlyList<GraphProductionCheckEvidence> CorrectnessRecoveryChecks { get; init; } = [];

    /// <summary>双档容量、复杂度、实际访问路径与 Couplet C2 联合检查。</summary>
    public IReadOnlyList<GraphProductionCheckEvidence> PerformanceCapacityChecks { get; init; } = [];

    /// <summary>阻塞 Preview 的 M40-GAP-001～007 关闭证据。</summary>
    public IReadOnlyList<GraphProductionGapEvidence> Gaps { get; init; } = [];

    /// <summary>本次证据不能外推的限制。</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>M40 #352 独立的 Preview 正确性与容量双门禁报告。</summary>
public sealed record GraphPreviewGateReport
{
    /// <summary>报告格式标识。</summary>
    public string Schema { get; init; } = "m40-graph-preview-gate-v1";

    /// <summary>路线图验收编号。</summary>
    public string Issue { get; init; } = "#352";

    /// <summary>报告生成 UTC 时间。</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>本地烟雾测试状态，不构成发布证据。</summary>
    public string LocalSmoke { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>从原始证据重算的正确性与恢复结论。</summary>
    public string CorrectnessRecovery { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>从原始证据重算的性能与容量结论。</summary>
    public string PerformanceCapacity { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>仅在两项门禁同时 PASS 时允许 Preview 发布。</summary>
    public string ReleaseDecision { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>经过原始 artifact 重算的输入证据。</summary>
    public GraphPreviewGateInput Input { get; init; } = new();

    /// <summary>阻止或限制门禁的机器可读结论。</summary>
    public IReadOnlyList<GraphProductionGateFinding> Findings { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(GraphPreviewGateInput))]
[JsonSerializable(typeof(GraphPreviewGateReport))]
internal sealed partial class GraphPreviewGateJsonContext : JsonSerializerContext;
