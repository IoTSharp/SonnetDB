using System.Text.Json.Serialization;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M40 #367 原始 artifact 的可复现运行元数据。</summary>
public sealed record GraphProductionArtifactRun
{
    /// <summary>生成 artifact 的提交 SHA。</summary>
    [JsonRequired]
    public string CommitSha { get; init; } = string.Empty;

    /// <summary>实际执行的可执行文件名。</summary>
    [JsonRequired]
    public string Command { get; init; } = string.Empty;

    /// <summary>实际执行时使用的参数列表；artifact 路径使用 <c>{artifact}</c> 占位符。</summary>
    [JsonRequired]
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>实际执行时相对于仓库根目录的工作目录。</summary>
    [JsonRequired]
    public string WorkingDirectory { get; init; } = ".";

    /// <summary>artifact 生成时记录的退出码。</summary>
    [JsonRequired]
    public int ExitCode { get; init; }
}

/// <summary>M40 固定数据生成的 schema-aware 原始 artifact。</summary>
public sealed record GraphProductionDatasetArtifact
{
    /// <summary>artifact schema。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-dataset-evidence-v1";

    /// <summary>可复现运行元数据。</summary>
    [JsonRequired]
    public GraphProductionArtifactRun Run { get; init; } = new();

    /// <summary>数据档位。</summary>
    public string Tier { get; init; } = string.Empty;

    /// <summary>生成器 schema 与版本。</summary>
    public string Generator { get; init; } = string.Empty;

    /// <summary>固定 seed。</summary>
    public string Seed { get; init; } = string.Empty;

    /// <summary>顶点数。</summary>
    public long VertexCount { get; init; }

    /// <summary>边数。</summary>
    public long EdgeCount { get; init; }

    /// <summary>生成器输入摘要。</summary>
    public string InputDigest { get; init; } = string.Empty;

    /// <summary>生成结果摘要。</summary>
    public string OutputDigest { get; init; } = string.Empty;
}

/// <summary>M40 固定目标机的 schema-aware 原始 artifact。</summary>
public sealed record GraphProductionEnvironmentArtifact
{
    /// <summary>artifact schema。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-environment-evidence-v1";

    /// <summary>可复现运行元数据。</summary>
    [JsonRequired]
    public GraphProductionArtifactRun Run { get; init; } = new();

    /// <summary>原始环境快照。</summary>
    public GraphProductionEnvironmentSnapshot Environment { get; init; } = new();
}

/// <summary>M40 固定目标机的原始环境快照。</summary>
public sealed record GraphProductionEnvironmentSnapshot
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

    /// <summary>磁盘型号。</summary>
    public string DiskName { get; init; } = string.Empty;

    /// <summary>文件系统。</summary>
    public string DiskFormat { get; init; } = string.Empty;

    /// <summary>.NET runtime 描述。</summary>
    public string Runtime { get; init; } = string.Empty;

    /// <summary>.NET SDK 版本。</summary>
    public string SdkVersion { get; init; } = string.Empty;

    /// <summary>GC 模式。</summary>
    public string GcMode { get; init; } = string.Empty;

    /// <summary>电源配置。</summary>
    public string PowerProfile { get; init; } = string.Empty;
}

/// <summary>M40 7 天 mixed workload 的 schema-aware 原始 artifact。</summary>
public sealed record GraphProductionSoakArtifact
{
    /// <summary>artifact schema。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-soak-evidence-v1";

    /// <summary>可复现运行元数据。</summary>
    [JsonRequired]
    public GraphProductionArtifactRun Run { get; init; } = new();

    /// <summary>运行开始 UTC 时间。</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>运行结束 UTC 时间。</summary>
    public DateTimeOffset FinishedUtc { get; init; }

    /// <summary>reader worker 数。</summary>
    public int ReaderWorkers { get; init; }

    /// <summary>update worker 数。</summary>
    public int UpdateWorkers { get; init; }

    /// <summary>更新配置。</summary>
    public string UpdateProfile { get; init; } = string.Empty;

    /// <summary>每次写是否同步 WAL。</summary>
    public bool SyncWalOnEveryWrite { get; init; }

    /// <summary>是否启用自动 checkpoint。</summary>
    public bool AutoCheckpointEnabled { get; init; }

    /// <summary>WAL 上限。</summary>
    public long MaxWalBytes { get; init; }

    /// <summary>overlay 条目上限。</summary>
    public int MaxOverlayEntries { get; init; }

    /// <summary>采样期失败操作数。</summary>
    public long FailedOperationCount { get; init; }

    /// <summary>采样期非计划重启数。</summary>
    public int UnexpectedRestartCount { get; init; }

    /// <summary>按发生时间记录的 checkpoint。</summary>
    public IReadOnlyList<DateTimeOffset> CheckpointsUtc { get; init; } = [];

    /// <summary>真 kill/reopen 原始样本。</summary>
    public IReadOnlyList<GraphProductionKillReopenSample> KillReopenSamples { get; init; } = [];

    /// <summary>checkpoint 后冷 open 原始毫秒样本。</summary>
    public IReadOnlyList<double> ColdOpenMilliseconds { get; init; } = [];

    /// <summary>长稳资源原始样本。</summary>
    public IReadOnlyList<GraphProductionSoakResourceSample> ResourceSamples { get; init; } = [];
}

/// <summary>M40 真 kill/reopen 的单次原始样本。</summary>
public sealed record GraphProductionKillReopenSample
{
    /// <summary>发生 UTC 时间。</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>是否由外部进程执行真 kill。</summary>
    public bool ProcessKilled { get; init; }

    /// <summary>是否成功重开。</summary>
    public bool Reopened { get; init; }

    /// <summary>重开后的完整 invariant 是否通过。</summary>
    public bool InvariantPassed { get; init; }

    /// <summary>恢复耗时毫秒。</summary>
    public double RecoveryMilliseconds { get; init; }
}

/// <summary>M40 长稳资源的单次原始样本。</summary>
public sealed record GraphProductionSoakResourceSample
{
    /// <summary>采样 UTC 时间。</summary>
    public DateTimeOffset TimestampUtc { get; init; }

    /// <summary>进程 working set。</summary>
    public long WorkingSetBytes { get; init; }

    /// <summary>累计 WAL 字节数。</summary>
    public long WalBytes { get; init; }
}

/// <summary>M40 单个 journey 的 schema-aware 原始 artifact。</summary>
public sealed record GraphProductionJourneyArtifact
{
    /// <summary>artifact schema。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-journey-evidence-v1";

    /// <summary>可复现运行元数据。</summary>
    [JsonRequired]
    public GraphProductionArtifactRun Run { get; init; } = new();

    /// <summary>冻结 journey ID。</summary>
    public string JourneyId { get; init; } = string.Empty;

    /// <summary>独立正式轮次。</summary>
    public IReadOnlyList<GraphProductionJourneyRoundArtifact> Rounds { get; init; } = [];
}

/// <summary>M40 journey 的单轮逐样本原始证据。</summary>
public sealed record GraphProductionJourneyRoundArtifact
{
    /// <summary>从 1 开始的轮次号。</summary>
    public int Round { get; init; }

    /// <summary>不计时 warmup 次数。</summary>
    public int WarmupCount { get; init; }

    /// <summary>完整查询耗时微秒。</summary>
    public IReadOnlyList<long> ElapsedMicroseconds { get; init; } = [];

    /// <summary>首行耗时微秒。</summary>
    public IReadOnlyList<long> TimeToFirstRowMicroseconds { get; init; } = [];

    /// <summary>冷启动后首次完整查询耗时微秒。</summary>
    public IReadOnlyList<long> ColdFirstQueryMicroseconds { get; init; } = [];

    /// <summary>托管分配字节。</summary>
    public IReadOnlyList<long> AllocatedBytes { get; init; } = [];

    /// <summary>query-owned peak live bytes。</summary>
    public IReadOnlyList<long> QueryPeakLiveBytes { get; init; } = [];

    /// <summary>进程 working set。</summary>
    public IReadOnlyList<long> WorkingSetBytes { get; init; } = [];

    /// <summary>逻辑读取字节。</summary>
    public IReadOnlyList<long> LogicalReadBytes { get; init; } = [];

    /// <summary>物理读取字节。</summary>
    public IReadOnlyList<long> PhysicalReadBytes { get; init; } = [];

    /// <summary>WAL 字节。</summary>
    public IReadOnlyList<long> WalBytes { get; init; } = [];

    /// <summary>候选条目数。</summary>
    public IReadOnlyList<long> Candidates { get; init; } = [];

    /// <summary>检查条目数。</summary>
    public IReadOnlyList<long> Examined { get; init; } = [];

    /// <summary>返回条目数。</summary>
    public IReadOnlyList<long> Returned { get; init; } = [];

    /// <summary>扩展边数。</summary>
    public IReadOnlyList<long> ExpandedEdges { get; init; } = [];

    /// <summary>frontier 峰值。</summary>
    public IReadOnlyList<long> FrontierPeak { get; init; } = [];

    /// <summary>逐样本 Gen0 GC 增量。</summary>
    public IReadOnlyList<long> Gen0Collections { get; init; } = [];

    /// <summary>逐样本 Gen1 GC 增量。</summary>
    public IReadOnlyList<long> Gen1Collections { get; init; } = [];

    /// <summary>逐样本 Gen2 GC 增量。</summary>
    public IReadOnlyList<long> Gen2Collections { get; init; } = [];

    /// <summary>逐样本 GC pause 微秒。</summary>
    public IReadOnlyList<long> GcPauseMicroseconds { get; init; } = [];

    /// <summary>本轮实际 access path。</summary>
    public string AccessPath { get; init; } = string.Empty;

    /// <summary>本轮 fallback reason。</summary>
    public string? FallbackReason { get; init; }

    /// <summary>逐 ID/property/path oracle。</summary>
    public IReadOnlyList<GraphProductionOracleAssertion> OracleAssertions { get; init; } = [];
}

/// <summary>M40 oracle 的单项期望与实际摘要。</summary>
public sealed record GraphProductionOracleAssertion
{
    /// <summary>稳定断言名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>期望结果摘要。</summary>
    public string ExpectedDigest { get; init; } = string.Empty;

    /// <summary>实际结果摘要。</summary>
    public string ActualDigest { get; init; } = string.Empty;
}

/// <summary>M40 子门禁或 gap close 的 schema-aware 原始 artifact。</summary>
public sealed record GraphProductionCheckArtifact
{
    /// <summary>artifact schema。</summary>
    [JsonRequired]
    public string Schema { get; init; } = "m40-graph-check-evidence-v1";

    /// <summary>可复现运行元数据。</summary>
    [JsonRequired]
    public GraphProductionArtifactRun Run { get; init; } = new();

    /// <summary>检查或 gap ID。</summary>
    public string CheckId { get; init; } = string.Empty;

    /// <summary>可审计说明。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>不能仅由 status 字段替代的原始断言。</summary>
    public IReadOnlyList<GraphProductionCheckAssertion> Assertions { get; init; } = [];
}

/// <summary>M40 子门禁原始断言。</summary>
public sealed record GraphProductionCheckAssertion
{
    /// <summary>稳定断言名。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>期望值或摘要。</summary>
    public string Expected { get; init; } = string.Empty;

    /// <summary>实际值或摘要。</summary>
    public string Actual { get; init; } = string.Empty;
}

/// <summary>M40 #367 紧凑原始 artifact 的 Native AOT JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GraphProductionDatasetArtifact))]
[JsonSerializable(typeof(GraphProductionEnvironmentArtifact))]
[JsonSerializable(typeof(GraphProductionSoakArtifact))]
[JsonSerializable(typeof(GraphProductionJourneyArtifact))]
[JsonSerializable(typeof(GraphProductionCheckArtifact))]
public sealed partial class GraphProductionArtifactJsonContext : JsonSerializerContext;
