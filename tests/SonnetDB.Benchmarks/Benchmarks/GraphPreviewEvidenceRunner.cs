using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Graphs;
using SonnetDB.Kv;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>Native Graph Preview 本地 evidence runner；不把本地结果提升为固定硬件 gate。</summary>
public static class GraphPreviewEvidenceRunner
{
    /// <summary>生成 M40 Phase 1 本地 correctness/performance evidence。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="quick">使用小规模 smoke 数据集。</param>
    /// <returns>结构化报告。</returns>
    public static GraphPreviewEvidenceReport Run(string outputDirectory, bool quick = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        int vertexCount = quick ? 100 : 1_000;
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-m40-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var stopwatch = Stopwatch.StartNew();
        bool invariantPass = false;
        bool pathPass = false;
        bool restartPass = false;
        bool repairPass = false;
        long sequence = 0;
        try
        {
            using (var manager = new GraphManager(root, KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            }))
            {
                GraphStore store = manager.Create("evidence");
                GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
                for (int id = 1; id <= vertexCount; id++)
                {
                    transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
                    if (id < vertexCount)
                        transaction.UpsertEdge(new GraphElementId(id), 0, new GraphElementId(id), new GraphElementId(id + 1), new LabelId(2), []);
                }
                sequence = transaction.Commit().Sequence;
            }
            using (var reopenedManager = new GraphManager(root, KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            }))
            {
                GraphStore store = reopenedManager.Open("evidence");
                using GraphReadSession read = store.BeginRead();
                restartPass = read.Sequence >= sequence
                    && read.GetVertex(new GraphElementId(vertexCount)) is not null;
                GraphPath? path = read.ShortestPath(new GraphElementId(1), new GraphElementId(vertexCount), options: new GraphTraversalOptions { MaxDepth = vertexCount, MaxFrontier = vertexCount + 1, MaxPaths = vertexCount + 1 });
                pathPass = path is not null && path.Depth == vertexCount - 1;
                _ = read.RefreshStatistics();
                GraphIndexRebuildResult rebuild = store.RebuildIndexes();
                repairPass = rebuild.ScannedRecords == (vertexCount * 2L) - 1;
                GraphInvariantReport report = GraphInvariantChecker.Check(store);
                invariantPass = report.IsComplete && report.TotalIssueCount == 0;
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Preserve the evidence result; cleanup can be retried by the runner host.
            }
        }

        stopwatch.Stop();
        var reportModel = new GraphPreviewEvidenceReport
        {
            Schema = "m40-native-graph-preview-v1",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Dataset = quick ? "quick" : "local",
            VertexCount = vertexCount,
            EdgeCount = Math.Max(0, vertexCount - 1),
            CommitSequence = sequence,
            Correctness = invariantPass && pathPass && restartPass && repairPass ? "PASS" : "FAIL",
            // 本地 runner 覆盖 reopen/replay、path、invariant 和 index repair；真进程 crash、固定硬件和竞品仍是独立发布证据。
            CorrectnessRecovery = invariantPass && pathPass && restartPass && repairPass ? "LOCAL_PASS" : "FAIL",
            PerformanceCapacity = "NOT_RUN",
            ReleaseDecision = "NOT_RUN",
            CorrectnessDetails = new GraphPreviewEvidenceGate
            {
                Status = invariantPass && pathPass && restartPass && repairPass ? "PASS" : "FAIL",
                Invariant = invariantPass ? "PASS" : "FAIL",
                RestartAndReplay = restartPass ? "PASS" : "FAIL",
                PathSemantics = pathPass ? "PASS" : "FAIL",
                IndexRepair = repairPass ? "PASS" : "FAIL",
            },
            Performance = new GraphPreviewEvidenceGate
            {
                Status = "NOT_RUN",
                Invariant = "NOT_RUN",
                RestartAndReplay = "NOT_RUN",
                PathSemantics = "NOT_RUN",
                IndexRepair = "NOT_RUN",
            },
            FixedHardware = "NOT_RUN",
            Neo4jComparison = "NOT_RUN",
            ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        };
        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Combine(outputDirectory, "m40-native-graph-preview.json");
        File.WriteAllText(
            jsonPath,
            JsonSerializer.Serialize(
                reportModel,
                GraphPreviewEvidenceJsonContext.Default.GraphPreviewEvidenceReport));
        File.WriteAllText(
            Path.Combine(outputDirectory, "m40-native-graph-preview.md"),
            $"# M40 Native Graph Preview Evidence\n\n- Local correctness smoke: `{reportModel.Correctness}`\n- Correctness/recovery gate: `{reportModel.CorrectnessRecovery}` (local smoke only)\n- Performance/capacity gate: `{reportModel.PerformanceCapacity}`\n- Release decision: `{reportModel.ReleaseDecision}`\n- Fixed hardware: `{reportModel.FixedHardware}`\n- Neo4j comparison: `{reportModel.Neo4jComparison}`\n");
        return reportModel;
    }
}

/// <summary>M40 evidence 报告。</summary>
public sealed record GraphPreviewEvidenceReport
{
    /// <summary>报告 schema。</summary>
    public required string Schema { get; init; }

    /// <summary>报告生成时间。</summary>
    public DateTimeOffset GeneratedAtUtc { get; init; }

    /// <summary>数据集名称。</summary>
    public required string Dataset { get; init; }

    /// <summary>顶点数量。</summary>
    public int VertexCount { get; init; }

    /// <summary>边数量。</summary>
    public int EdgeCount { get; init; }

    /// <summary>fixture 提交序列号。</summary>
    public long CommitSequence { get; init; }

    /// <summary>本地 correctness smoke 状态，不等于发布 gate。</summary>
    public required string Correctness { get; init; }

    /// <summary>完整 correctness/recovery gate 状态。</summary>
    public required string CorrectnessRecovery { get; init; }

    /// <summary>完整 performance/capacity gate 状态。</summary>
    public required string PerformanceCapacity { get; init; }

    /// <summary>双 gate 发布决定。</summary>
    public required string ReleaseDecision { get; init; }

    /// <summary>本地 correctness 子项。</summary>
    public required GraphPreviewEvidenceGate CorrectnessDetails { get; init; }

    /// <summary>性能子项。</summary>
    public required GraphPreviewEvidenceGate Performance { get; init; }

    /// <summary>固定硬件运行状态。</summary>
    public required string FixedHardware { get; init; }

    /// <summary>Neo4j 语义对照状态。</summary>
    public required string Neo4jComparison { get; init; }

    /// <summary>runner 总耗时毫秒。</summary>
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>Evidence gate 状态。</summary>
public sealed record GraphPreviewEvidenceGate
{
    /// <summary>聚合状态。</summary>
    public required string Status { get; init; }

    /// <summary>不变量状态。</summary>
    public required string Invariant { get; init; }

    /// <summary>重启与 WAL replay 状态。</summary>
    public required string RestartAndReplay { get; init; }

    /// <summary>路径语义状态。</summary>
    public required string PathSemantics { get; init; }

    /// <summary>派生索引修复状态。</summary>
    public required string IndexRepair { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(GraphPreviewEvidenceReport))]
internal sealed partial class GraphPreviewEvidenceJsonContext : JsonSerializerContext;
