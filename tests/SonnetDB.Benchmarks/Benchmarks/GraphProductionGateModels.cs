using System.Text.Json.Serialization;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M40 #367 证据状态常量。</summary>
public static class GraphProductionEvidenceStatus
{
    /// <summary>证据通过。</summary>
    public const string Pass = "PASS";

    /// <summary>证据失败。</summary>
    public const string Fail = "FAIL";

    /// <summary>证据尚未运行。</summary>
    public const string NotRun = "NOT_RUN";
}

/// <summary>M40 #367 production gate 的原始证据清单。</summary>
public sealed record GraphProductionGateInput
{
    /// <summary>输入 schema。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-production-input-v2";

    /// <summary>是否正在提交完整 Production 门禁证据。</summary>
    public bool ProductionRun { get; init; }

    /// <summary>被测提交 SHA。</summary>
    public string CommitSha { get; init; } = "unknown";

    /// <summary>运行开始 UTC 时间。</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>运行结束 UTC 时间。</summary>
    public DateTimeOffset FinishedUtc { get; init; }

    /// <summary>固定数据生成证据。</summary>
    public GraphProductionDatasetEvidence Dataset { get; init; } = new();

    /// <summary>固定硬件与运行时证据。</summary>
    public GraphProductionEnvironmentEvidence Environment { get; init; } = new();

    /// <summary>7 天 mixed workload 证据。</summary>
    public GraphProductionSoakEvidence Soak { get; init; } = new();

    /// <summary>逐 journey 性能与访问路径证据。</summary>
    public IReadOnlyList<GraphProductionJourneyEvidence> Journeys { get; init; } = [];

    /// <summary>正确性与恢复子门禁证据。</summary>
    public IReadOnlyList<GraphProductionCheckEvidence> CorrectnessRecoveryChecks { get; init; } = [];

    /// <summary>性能、容量和发布构建子门禁证据。</summary>
    public IReadOnlyList<GraphProductionCheckEvidence> PerformanceCapacityChecks { get; init; } = [];

    /// <summary>#341 capability gap catalog 快照。</summary>
    public IReadOnlyList<GraphProductionGapEvidence> Gaps { get; init; } = [];

    /// <summary>本次证据不能外推的边界。</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>M40 固定数据生成证据。</summary>
public sealed record GraphProductionDatasetEvidence
{
    /// <summary>数据档位。</summary>
    public string Tier { get; init; } = "quick";

    /// <summary>生成器 schema 与版本。</summary>
    public string Generator { get; init; } = "m40-graph-generator-v1";

    /// <summary>固定 64 位 seed 的十六进制表示。</summary>
    public string Seed { get; init; } = "0x534F4E4E45544442";

    /// <summary>顶点数。</summary>
    public long VertexCount { get; init; }

    /// <summary>边数。</summary>
    public long EdgeCount { get; init; }

    /// <summary>生成器输入 SHA-256。</summary>
    public string InputDigest { get; init; } = string.Empty;

    /// <summary>生成结果 SHA-256。</summary>
    public string OutputDigest { get; init; } = string.Empty;

    /// <summary>可独立解析的数据生成原始证据。</summary>
    public GraphProductionArtifactEvidence Artifact { get; init; } = new();
}

/// <summary>M40 固定目标机和运行时证据。</summary>
public sealed record GraphProductionEnvironmentEvidence
{
    /// <summary>操作系统描述。</summary>
    public string OsDescription { get; init; } = string.Empty;

    /// <summary>操作系统构建号。</summary>
    public string OsBuild { get; init; } = string.Empty;

    /// <summary>进程架构。</summary>
    public string Architecture { get; init; } = string.Empty;

    /// <summary>CPU 型号。</summary>
    public string CpuName { get; init; } = string.Empty;

    /// <summary>物理核心数。</summary>
    public int PhysicalCoreCount { get; init; }

    /// <summary>逻辑处理器数。</summary>
    public int LogicalProcessorCount { get; init; }

    /// <summary>物理内存字节数。</summary>
    public long PhysicalMemoryBytes { get; init; }

    /// <summary>承载数据目录的磁盘型号。</summary>
    public string DiskName { get; init; } = string.Empty;

    /// <summary>承载数据目录的文件系统。</summary>
    public string DiskFormat { get; init; } = string.Empty;

    /// <summary>.NET runtime 描述。</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>.NET SDK 版本。</summary>
    public string SdkVersion { get; init; } = string.Empty;

    /// <summary>GC 模式。</summary>
    public string GcMode { get; init; } = string.Empty;

    /// <summary>电源配置。</summary>
    public string PowerProfile { get; init; } = string.Empty;

    /// <summary>可独立解析的环境采集原始证据。</summary>
    public GraphProductionArtifactEvidence Artifact { get; init; } = new();
}

/// <summary>M40 7 天 mixed workload 的汇总证据。</summary>
public sealed record GraphProductionSoakEvidence
{
    /// <summary>实际运行小时数。</summary>
    public double DurationHours { get; init; }

    /// <summary>并发 reader worker 数。</summary>
    public int ReaderWorkers { get; init; }

    /// <summary>journey update worker 数。</summary>
    public int UpdateWorkers { get; init; }

    /// <summary>更新速率配置名。</summary>
    public string UpdateProfile { get; init; } = string.Empty;

    /// <summary>最大 checkpoint 间隔分钟数。</summary>
    public double MaximumCheckpointIntervalMinutes { get; init; }

    /// <summary>已完成 checkpoint 数。</summary>
    public int CheckpointCount { get; init; }

    /// <summary>真进程 kill/reopen 周期数。</summary>
    public int KillReopenCount { get; init; }

    /// <summary>kill/reopen 后完整 invariant check 数。</summary>
    public int InvariantCheckCount { get; init; }

    /// <summary>失败的业务或维护操作数。</summary>
    public long FailedOperationCount { get; init; }

    /// <summary>非计划进程重启数。</summary>
    public int UnexpectedRestartCount { get; init; }

    /// <summary>进程峰值 working set。</summary>
    public long PeakWorkingSetBytes { get; init; }

    /// <summary>累计 WAL 字节数。</summary>
    public long WalBytes { get; init; }

    /// <summary>checkpoint 后冷启动 open P95 毫秒。</summary>
    public double ColdOpenP95Milliseconds { get; init; }

    /// <summary>checkpoint 后冷启动 open P99 毫秒。</summary>
    public double ColdOpenP99Milliseconds { get; init; }

    /// <summary>kill/reopen 恢复 P50 毫秒。</summary>
    public double RecoveryP50Milliseconds { get; init; }

    /// <summary>kill/reopen 恢复 P95 毫秒。</summary>
    public double RecoveryP95Milliseconds { get; init; }

    /// <summary>kill/reopen 恢复 P99 毫秒。</summary>
    public double RecoveryP99Milliseconds { get; init; }

    /// <summary>是否保持每次写 fsync。</summary>
    public bool SyncWalOnEveryWrite { get; init; }

    /// <summary>是否保持自动 checkpoint。</summary>
    public bool AutoCheckpointEnabled { get; init; }

    /// <summary>WAL 上限字节数。</summary>
    public long MaxWalBytes { get; init; }

    /// <summary>overlay 条目上限。</summary>
    public int MaxOverlayEntries { get; init; }

    /// <summary>可独立解析的长稳原始证据。</summary>
    public GraphProductionArtifactEvidence Artifact { get; init; } = new();
}

/// <summary>M40 单个 golden journey 的聚合样本。</summary>
public sealed record GraphProductionJourneyEvidence
{
    /// <summary>#341 冻结的查询 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>结果逐项对拍状态。</summary>
    public string Status { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>不计时 warmup 次数。</summary>
    public int WarmupCount { get; init; }

    /// <summary>独立正式轮次。</summary>
    public int Rounds { get; init; }

    /// <summary>每轮完整消费样本数。</summary>
    public int SamplesPerRound { get; init; }

    /// <summary>完整查询 P50 毫秒。</summary>
    public double P50Milliseconds { get; init; }

    /// <summary>完整查询 P95 毫秒。</summary>
    public double P95Milliseconds { get; init; }

    /// <summary>完整查询 P99 毫秒。</summary>
    public double P99Milliseconds { get; init; }

    /// <summary>完整查询最大耗时毫秒。</summary>
    public double MaxMilliseconds { get; init; }

    /// <summary>完整消费吞吐量（次/秒）。</summary>
    public double ThroughputPerSecond { get; init; }

    /// <summary>首行时间 P99 毫秒。</summary>
    public double TimeToFirstRowP99Milliseconds { get; init; }

    /// <summary>冷启动后该 journey 首次完整查询 P99 毫秒。</summary>
    public double ColdFirstQueryP99Milliseconds { get; init; }

    /// <summary>query-owned peak live bytes。</summary>
    public long QueryPeakLiveBytes { get; init; }

    /// <summary>采样期进程 peak working set。</summary>
    public long PeakWorkingSetBytes { get; init; }

    /// <summary>每次样本 P95 托管分配字节数。</summary>
    public long AllocatedBytesP95 { get; init; }

    /// <summary>正式样本累计 Gen0 GC 次数。</summary>
    public long Gen0Collections { get; init; }

    /// <summary>正式样本累计 Gen1 GC 次数。</summary>
    public long Gen1Collections { get; init; }

    /// <summary>正式样本累计 Gen2 GC 次数。</summary>
    public long Gen2Collections { get; init; }

    /// <summary>正式样本 GC pause P99 毫秒。</summary>
    public double GcPauseP99Milliseconds { get; init; }

    /// <summary>逻辑读取字节数。</summary>
    public long LogicalReadBytes { get; init; }

    /// <summary>物理读取字节数。</summary>
    public long PhysicalReadBytes { get; init; }

    /// <summary>WAL 字节数。</summary>
    public long WalBytes { get; init; }

    /// <summary>候选条目数。</summary>
    public long Candidates { get; init; }

    /// <summary>检查条目数。</summary>
    public long Examined { get; init; }

    /// <summary>返回条目数。</summary>
    public long Returned { get; init; }

    /// <summary>扩展边数。</summary>
    public long ExpandedEdges { get; init; }

    /// <summary>frontier 峰值。</summary>
    public long FrontierPeak { get; init; }

    /// <summary>实际访问路径。</summary>
    public string AccessPath { get; init; } = string.Empty;

    /// <summary>回退原因；无回退时为 null。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>原始样本 artifact。</summary>
    public GraphProductionArtifactEvidence Artifact { get; init; } = new();
}

/// <summary>M40 单个正确性、恢复或容量检查。</summary>
public sealed record GraphProductionCheckEvidence
{
    /// <summary>稳定检查 ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>检查状态。</summary>
    public string Status { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>可审计的简短结果。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>原始 artifact。</summary>
    public GraphProductionArtifactEvidence Artifact { get; init; } = new();
}

/// <summary>M40 原始证据 artifact 引用。</summary>
public sealed record GraphProductionArtifactEvidence
{
    /// <summary>相对于清单的 artifact 路径。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>artifact SHA-256。</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>生成或验证 artifact 的可执行文件名。</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>按原样传给可执行文件的参数列表；Production evidence 必须含唯一 <c>{artifact}</c> 占位符。</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>相对于仓库根目录的复现工作目录。</summary>
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>复现命令的期望退出码。</summary>
    public int ExpectedExitCode { get; init; }

    /// <summary>复现命令的超时秒数。</summary>
    public int TimeoutSeconds { get; init; } = 900;
}

/// <summary>#341 capability gap catalog 的一项。</summary>
public sealed record GraphProductionGapEvidence
{
    /// <summary>稳定 gap ID。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>open、in_progress、closed 或 not_planned。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>受阻阶段。</summary>
    public string Blocks { get; init; } = string.Empty;

    /// <summary>缺口严重级别。</summary>
    public string Severity { get; init; } = string.Empty;

    /// <summary>关闭证据；未关闭时可为空。</summary>
    public GraphProductionArtifactEvidence? CloseEvidence { get; init; }
}

/// <summary>M40 #367 严格双门禁判定报告。</summary>
public sealed record GraphProductionGateReport
{
    /// <summary>报告 schema。</summary>
    public string Schema { get; init; } = "m40-graph-production-gate-v2";

    /// <summary>路线图编号。</summary>
    public string Issue { get; init; } = "#367";

    /// <summary>报告生成 UTC 时间。</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>本地 smoke 状态。</summary>
    public string LocalSmoke { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>正确性与恢复 gate。</summary>
    public string CorrectnessRecovery { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>性能与容量 gate。</summary>
    public string PerformanceCapacity { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>仅在两个 gate 都 PASS 时为 PASS。</summary>
    public string ReleaseDecision { get; init; } = GraphProductionEvidenceStatus.NotRun;

    /// <summary>输入证据清单。</summary>
    public GraphProductionGateInput Input { get; init; } = new();

    /// <summary>阻止或限制门禁的机器可读 findings。</summary>
    public IReadOnlyList<GraphProductionGateFinding> Findings { get; init; } = [];
}

/// <summary>M40 #367 门禁判定 finding。</summary>
public sealed record GraphProductionGateFinding
{
    /// <summary>所属 gate。</summary>
    public string Gate { get; init; } = string.Empty;

    /// <summary>稳定 finding code。</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>可读说明。</summary>
    public string Message { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(GraphProductionGateInput))]
[JsonSerializable(typeof(GraphProductionGateReport))]
internal sealed partial class GraphProductionGateJsonContext : JsonSerializerContext;
