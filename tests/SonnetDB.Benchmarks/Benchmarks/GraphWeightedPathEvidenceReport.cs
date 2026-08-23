using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Graphs;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M40 #362 加权路径真实 journey 与算法收益证据 runner。</summary>
public static class GraphWeightedPathEvidenceRunner
{
    private const int QuickSide = 16;
    private const int LocalSide = 64;
    private const int QuickIterations = 5;
    private const int LocalIterations = 31;
    private const double MinimumExpansionReduction = 0.20;
    private const double MaximumP95LatencyRatio = 1.25;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly GraphWeightedShortestPathAlgorithm[] Algorithms =
    [
        GraphWeightedShortestPathAlgorithm.Dijkstra,
        GraphWeightedShortestPathAlgorithm.AStar,
        GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra,
    ];

    /// <summary>运行 quick 或本地基线，并生成 source-generated JSON 与 Markdown 报告。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="quick">是否使用 PR quick smoke 规模。</param>
    /// <returns>结构化算法收益报告。</returns>
    public static GraphWeightedPathEvidenceReport Run(string outputDirectory, bool quick = false)
        => Run(
            outputDirectory,
            quick ? QuickSide : LocalSide,
            quick ? QuickIterations : LocalIterations,
            quick ? "quick_smoke" : "local_benefit");

    /// <summary>以显式规模运行报告合同 smoke；该入口不构成固定硬件证据。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="side">topology 网格边长，至少为 8。</param>
    /// <param name="iterations">每种算法的正式采样次数。</param>
    /// <returns>结构化算法收益报告。</returns>
    public static GraphWeightedPathEvidenceReport Run(string outputDirectory, int side, int iterations)
        => Run(outputDirectory, side, iterations, "contract_smoke");

    private static GraphWeightedPathEvidenceReport Run(
        string outputDirectory,
        int side,
        int iterations,
        string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (side < 8)
            throw new ArgumentOutOfRangeException(nameof(side), "加权路径 evidence topology 的边长至少为 8。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);

        Directory.CreateDirectory(outputDirectory);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        using var fixture = new GraphWeightedPathBenchmarkFixture(side);
        fixture.ValidateAlgorithms();
        int warmupIterations = mode == "local_benefit" ? 3 : 1;
        for (int iteration = 0; iteration < warmupIterations; iteration++)
        foreach (GraphWeightedShortestPathAlgorithm algorithm in Algorithms)
            _ = fixture.Execute(algorithm);

        Dictionary<GraphWeightedShortestPathAlgorithm, List<GraphWeightedPathSample>> samples = Algorithms
            .ToDictionary(
                static algorithm => algorithm,
                static _ => new List<GraphWeightedPathSample>());
        using (Process process = Process.GetCurrentProcess())
        {
            for (int iteration = 0; iteration < iterations; iteration++)
            for (int offset = 0; offset < Algorithms.Length; offset++)
            {
                GraphWeightedShortestPathAlgorithm algorithm = Algorithms[(iteration + offset) % Algorithms.Length];
                samples[algorithm].Add(Measure(fixture, algorithm, process));
            }
        }

        GraphWeightedPathAggregate baseline = Aggregate(
            GraphWeightedShortestPathAlgorithm.Dijkstra,
            samples[GraphWeightedShortestPathAlgorithm.Dijkstra]);
        GraphWeightedPathAlgorithmEvidence[] algorithms = Algorithms
            .Select(algorithm => CreateEvidence(
                Aggregate(algorithm, samples[algorithm]),
                baseline,
                iterations))
            .ToArray();
        string benefit = algorithms
            .Where(static algorithm => algorithm.Algorithm != "dijkstra")
            .All(static algorithm => algorithm.Admission == "PASS")
            ? "PASS"
            : "FAIL";
        DateTimeOffset finishedUtc = DateTimeOffset.UtcNow;
        var report = new GraphWeightedPathEvidenceReport(
            "m40-graph-weighted-path-evidence-v1",
            "#362",
            ResolveCommitSha(),
            startedUtc,
            finishedUtc,
            mode,
            new GraphWeightedPathDatasetEvidence(
                GraphWeightedPathBenchmarkFixture.DatasetName,
                GraphWeightedPathBenchmarkFixture.Seed,
                side,
                fixture.VertexCount,
                fixture.EdgeCount,
                fixture.StartId.Value,
                fixture.TargetId.Value,
                side - 1,
                side - 1,
                CreateInputDigest(fixture)),
            CreateEnvironment(),
            algorithms,
            "PASS",
            benefit,
            "NOT_RUN",
            "NOT_RUN",
            [
                "本报告验证 #362 journey 结果与局部算法收益，不替代 #367 的 1m vertex/10m edge 固定硬件容量门禁。",
                "A* 收益样本使用嵌入式调用方提供的可采纳且一致的 Manhattan 启发式；远程零启发式 A* 不据此获得收益声明。",
                "固定目标硬件需另行完成供电、磁盘、后台负载、10,000 样本三轮和 7 天 mixed workload 取证，因此保持 NOT_RUN。",
            ]);

        File.WriteAllText(
            Path.Combine(outputDirectory, "m40-graph-weighted-path-evidence.json"),
            JsonSerializer.Serialize(
                report,
                GraphWeightedPathEvidenceJsonContext.Default.GraphWeightedPathEvidenceReport),
            Utf8WithoutBom);
        File.WriteAllText(
            Path.Combine(outputDirectory, "m40-graph-weighted-path-evidence.md"),
            BuildMarkdown(report),
            Utf8WithoutBom);
        return report;
    }

    private static GraphWeightedPathSample Measure(
        GraphWeightedPathBenchmarkFixture fixture,
        GraphWeightedShortestPathAlgorithm algorithm,
        Process process)
    {
        process.Refresh();
        long workingSetBefore = process.WorkingSet64;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        GraphWeightedPath path = fixture.Execute(algorithm);
        double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        process.Refresh();
        long workingSetDelta = Math.Max(0, process.WorkingSet64 - workingSetBefore);
        ValidatePath(fixture, algorithm, path);
        return new GraphWeightedPathSample(
            path.Depth,
            path.TotalWeight,
            path.ExpandedVertices,
            path.ExpandedEdges,
            path.SnapshotSequence,
            CreatePathDigest(path),
            elapsedMilliseconds,
            allocatedBytes,
            workingSetDelta);
    }

    private static void ValidatePath(
        GraphWeightedPathBenchmarkFixture fixture,
        GraphWeightedShortestPathAlgorithm algorithm,
        GraphWeightedPath path)
    {
        if (path.Algorithm != algorithm
            || path.Depth != fixture.Side - 1
            || path.TotalWeight != fixture.Side - 1
            || path.VertexIds[0] != fixture.StartId
            || path.VertexIds[^1] != fixture.TargetId
            || path.SnapshotSequence != fixture.SnapshotSequence)
        {
            throw new InvalidDataException(
                $"{algorithm} 的路径结果、算法标识或 statement snapshot 不符合固定 oracle。");
        }
    }

    private static GraphWeightedPathAggregate Aggregate(
        GraphWeightedShortestPathAlgorithm algorithm,
        IReadOnlyList<GraphWeightedPathSample> samples)
    {
        if (samples.Count == 0)
            throw new InvalidDataException($"{algorithm} 没有正式 evidence 样本。");
        return new GraphWeightedPathAggregate(
            algorithm,
            RequireStable(samples.Select(static sample => sample.PathDepth), "path depth"),
            RequireStable(samples.Select(static sample => sample.TotalWeight), "total weight"),
            RequireStable(samples.Select(static sample => sample.ExpandedVertices), "expanded vertices"),
            RequireStable(samples.Select(static sample => sample.ExpandedEdges), "expanded edges"),
            RequireStable(samples.Select(static sample => sample.SnapshotSequence), "snapshot sequence"),
            RequireStable(samples.Select(static sample => sample.PathDigest), "path digest"),
            Percentile(samples.Select(static sample => sample.ElapsedMilliseconds), 0.50),
            Percentile(samples.Select(static sample => sample.ElapsedMilliseconds), 0.95),
            Percentile(samples.Select(static sample => sample.ElapsedMilliseconds), 0.99),
            samples.Max(static sample => sample.ElapsedMilliseconds),
            Percentile(samples.Select(static sample => sample.AllocatedBytes), 0.50),
            Percentile(samples.Select(static sample => sample.AllocatedBytes), 0.95),
            Percentile(samples.Select(static sample => sample.AllocatedBytes), 0.99),
            samples.Max(static sample => sample.WorkingSetDeltaBytes));
    }

    private static GraphWeightedPathAlgorithmEvidence CreateEvidence(
        GraphWeightedPathAggregate aggregate,
        GraphWeightedPathAggregate baseline,
        int iterations)
    {
        bool isBaseline = aggregate.Algorithm == GraphWeightedShortestPathAlgorithm.Dijkstra;
        double expansionReduction = isBaseline || baseline.ExpandedEdges == 0
            ? 0
            : 1 - aggregate.ExpandedEdges / (double)baseline.ExpandedEdges;
        double latencyRatio = isBaseline || baseline.P95Milliseconds == 0
            ? 1
            : aggregate.P95Milliseconds / baseline.P95Milliseconds;
        string admission;
        string admissionReason;
        if (isBaseline)
        {
            admission = "BASELINE";
            admissionReason = "reference_algorithm";
        }
        else if (expansionReduction < MinimumExpansionReduction)
        {
            admission = "FAIL";
            admissionReason = "expanded_edge_reduction_below_20_percent";
        }
        else if (latencyRatio > MaximumP95LatencyRatio)
        {
            admission = "FAIL";
            admissionReason = "p95_latency_regression_over_25_percent";
        }
        else
        {
            admission = "PASS";
            admissionReason = "expanded_edges_reduced_without_material_p95_regression";
        }

        return new GraphWeightedPathAlgorithmEvidence(
            ToStableName(aggregate.Algorithm),
            "PASS",
            admission,
            admissionReason,
            iterations,
            aggregate.PathDepth,
            aggregate.TotalWeight,
            aggregate.ExpandedVertices,
            aggregate.ExpandedEdges,
            expansionReduction,
            latencyRatio,
            aggregate.P50Milliseconds,
            aggregate.P95Milliseconds,
            aggregate.P99Milliseconds,
            aggregate.MaxMilliseconds,
            aggregate.AllocatedBytesP50,
            aggregate.AllocatedBytesP95,
            aggregate.AllocatedBytesP99,
            aggregate.WorkingSetDeltaMaxBytes,
            aggregate.SnapshotSequence,
            aggregate.PathDigest,
            "native_adjacency",
            null);
    }

    private static T RequireStable<T>(IEnumerable<T> values, string field)
    {
        T[] materialized = values.ToArray();
        T first = materialized[0];
        if (materialized.Any(value => !EqualityComparer<T>.Default.Equals(value, first)))
            throw new InvalidDataException($"加权路径 evidence 的 {field} 在正式样本间不稳定。");
        return first;
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        long[] ordered = values.Order().ToArray();
        int index = Math.Clamp((int)Math.Ceiling(percentile * ordered.Length) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    private static string CreateInputDigest(GraphWeightedPathBenchmarkFixture fixture)
        => Hash(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{GraphWeightedPathBenchmarkFixture.DatasetName}|{GraphWeightedPathBenchmarkFixture.Seed}|"
                + $"{fixture.Side}|{fixture.VertexCount}|{fixture.EdgeCount}|"
                + $"{fixture.StartId.Value}|{fixture.TargetId.Value}"));

    private static string CreatePathDigest(GraphWeightedPath path)
        => Hash(
            string.Join(',', path.VertexIds.Select(static id => id.Value))
            + "|"
            + string.Join(',', path.EdgeIds.Select(static id => id.Value))
            + string.Create(CultureInfo.InvariantCulture, $"|{path.TotalWeight:R}"));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ToStableName(GraphWeightedShortestPathAlgorithm algorithm)
        => algorithm switch
        {
            GraphWeightedShortestPathAlgorithm.Dijkstra => "dijkstra",
            GraphWeightedShortestPathAlgorithm.AStar => "a_star",
            GraphWeightedShortestPathAlgorithm.BidirectionalDijkstra => "bidirectional_dijkstra",
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

    private static GraphWeightedPathEnvironmentEvidence CreateEnvironment()
    {
        string root = Path.GetPathRoot(Path.GetTempPath()) ?? Path.DirectorySeparatorChar.ToString();
        var drive = new DriveInfo(root);
        return new GraphWeightedPathEnvironmentEvidence(
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            drive.DriveFormat,
            drive.AvailableFreeSpace,
            GCSettings.IsServerGC);
    }

    private static string ResolveCommitSha()
    {
        string? configured = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("HEAD");
            using var process = Process.Start(startInfo);
            if (process is null)
                return "unknown";
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0 || output.Length == 0)
                return "unknown";
            return output;
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static string BuildMarkdown(GraphWeightedPathEvidenceReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# M40 #362 Weighted Path Evidence");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Schema: `{report.Schema}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Commit: `{report.CommitSha}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Mode: `{report.Mode}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Local correctness: `{report.LocalCorrectness}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Algorithm benefit: `{report.AlgorithmBenefit}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Fixed hardware: `{report.FixedHardware}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Production gate: `{report.ProductionGate}`");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"- Dataset: `{report.Dataset.Name}`, {report.Dataset.VertexCount:N0} vertices / "
            + $"{report.Dataset.EdgeCount:N0} edges");
        text.AppendLine();
        text.AppendLine("## Algorithm matrix");
        text.AppendLine();
        text.AppendLine("| Algorithm | Correctness | Admission | Expanded vertices | Expanded edges | Edge reduction | P50 ms | P95 ms | P99 ms | P95 ratio | Alloc P95 |");
        text.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (GraphWeightedPathAlgorithmEvidence algorithm in report.Algorithms)
        {
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"| {algorithm.Algorithm} | {algorithm.Correctness} | {algorithm.Admission} | "
                + $"{algorithm.ExpandedVertices:N0} | {algorithm.ExpandedEdges:N0} | "
                + $"{algorithm.ExpandedEdgeReductionVsDijkstra:P1} | "
                + $"{algorithm.P50Milliseconds:F3} | {algorithm.P95Milliseconds:F3} | "
                + $"{algorithm.P99Milliseconds:F3} | {algorithm.P95LatencyRatioVsDijkstra:F3} | "
                + $"{algorithm.AllocatedBytesP95:N0} |");
        }
        text.AppendLine();
        text.AppendLine("## Boundaries");
        text.AppendLine();
        foreach (string limitation in report.Limitations)
            text.AppendLine("- " + limitation);
        return text.ToString();
    }

    private sealed record GraphWeightedPathSample(
        int PathDepth,
        double TotalWeight,
        int ExpandedVertices,
        long ExpandedEdges,
        long SnapshotSequence,
        string PathDigest,
        double ElapsedMilliseconds,
        long AllocatedBytes,
        long WorkingSetDeltaBytes);

    private sealed record GraphWeightedPathAggregate(
        GraphWeightedShortestPathAlgorithm Algorithm,
        int PathDepth,
        double TotalWeight,
        int ExpandedVertices,
        long ExpandedEdges,
        long SnapshotSequence,
        string PathDigest,
        double P50Milliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaxMilliseconds,
        long AllocatedBytesP50,
        long AllocatedBytesP95,
        long AllocatedBytesP99,
        long WorkingSetDeltaMaxBytes);
}

/// <summary>M40 #362 加权路径算法收益报告。</summary>
/// <param name="Schema">报告 schema。</param>
/// <param name="Issue">ROADMAP 编号。</param>
/// <param name="CommitSha">运行对应的提交。</param>
/// <param name="StartedUtc">开始时间。</param>
/// <param name="FinishedUtc">结束时间。</param>
/// <param name="Mode">运行规模模式。</param>
/// <param name="Dataset">固定 topology 数据集。</param>
/// <param name="Environment">本地运行环境。</param>
/// <param name="Algorithms">三种算法的结果与收益矩阵。</param>
/// <param name="LocalCorrectness">本地路径正确性状态。</param>
/// <param name="AlgorithmBenefit">A* 与双向算法的局部收益准入状态。</param>
/// <param name="FixedHardware">固定硬件门禁状态。</param>
/// <param name="ProductionGate">#367 生产门禁状态。</param>
/// <param name="Limitations">不得从本报告外推的边界。</param>
public sealed record GraphWeightedPathEvidenceReport(
    string Schema,
    string Issue,
    string CommitSha,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    string Mode,
    GraphWeightedPathDatasetEvidence Dataset,
    GraphWeightedPathEnvironmentEvidence Environment,
    IReadOnlyList<GraphWeightedPathAlgorithmEvidence> Algorithms,
    string LocalCorrectness,
    string AlgorithmBenefit,
    string FixedHardware,
    string ProductionGate,
    IReadOnlyList<string> Limitations);

/// <summary>M40 #362 固定 topology 数据集合同。</summary>
/// <param name="Name">数据集名称与版本。</param>
/// <param name="Seed">#341 冻结的确定性 seed。</param>
/// <param name="Side">网格边长。</param>
/// <param name="VertexCount">顶点总数。</param>
/// <param name="EdgeCount">有向边总数。</param>
/// <param name="StartVertexId">路由起点。</param>
/// <param name="TargetVertexId">路由终点。</param>
/// <param name="ExpectedDepth">oracle 路径 hop 数。</param>
/// <param name="ExpectedTotalWeight">oracle 路径总权重。</param>
/// <param name="InputDigest">生成器输入摘要。</param>
public sealed record GraphWeightedPathDatasetEvidence(
    string Name,
    string Seed,
    int Side,
    int VertexCount,
    int EdgeCount,
    long StartVertexId,
    long TargetVertexId,
    int ExpectedDepth,
    double ExpectedTotalWeight,
    string InputDigest);

/// <summary>M40 #362 本地证据运行环境。</summary>
/// <param name="Framework">.NET 运行时。</param>
/// <param name="Os">操作系统。</param>
/// <param name="Architecture">进程架构。</param>
/// <param name="ProcessorIdentifier">不含主机名的处理器标识。</param>
/// <param name="ProcessorCount">逻辑处理器数。</param>
/// <param name="AvailableMemoryBytes">GC 可用内存估计。</param>
/// <param name="DiskFormat">临时证据盘文件系统。</param>
/// <param name="DiskAvailableBytes">运行时磁盘可用字节数。</param>
/// <param name="ServerGc">是否启用 Server GC。</param>
public sealed record GraphWeightedPathEnvironmentEvidence(
    string Framework,
    string Os,
    string Architecture,
    string ProcessorIdentifier,
    int ProcessorCount,
    long AvailableMemoryBytes,
    string DiskFormat,
    long DiskAvailableBytes,
    bool ServerGc);

/// <summary>M40 #362 单种加权路径算法的聚合证据。</summary>
/// <param name="Algorithm">稳定算法名称。</param>
/// <param name="Correctness">路径 oracle 状态。</param>
/// <param name="Admission">局部收益准入状态。</param>
/// <param name="AdmissionReason">准入或拒绝原因。</param>
/// <param name="Iterations">正式样本数。</param>
/// <param name="PathDepth">稳定路径 hop 数。</param>
/// <param name="TotalWeight">稳定路径总权重。</param>
/// <param name="ExpandedVertices">稳定扩展状态数。</param>
/// <param name="ExpandedEdges">稳定检查邻接边数。</param>
/// <param name="ExpandedEdgeReductionVsDijkstra">相对 Dijkstra 的邻接检查降幅。</param>
/// <param name="P95LatencyRatioVsDijkstra">P95 与 Dijkstra P95 的比值。</param>
/// <param name="P50Milliseconds">执行耗时 P50。</param>
/// <param name="P95Milliseconds">执行耗时 P95。</param>
/// <param name="P99Milliseconds">执行耗时 P99。</param>
/// <param name="MaxMilliseconds">执行耗时最大值。</param>
/// <param name="AllocatedBytesP50">当前线程分配字节 P50。</param>
/// <param name="AllocatedBytesP95">当前线程分配字节 P95。</param>
/// <param name="AllocatedBytesP99">当前线程分配字节 P99。</param>
/// <param name="WorkingSetDeltaMaxBytes">采样前后进程 working set 最大正增量。</param>
/// <param name="SnapshotSequence">所有样本共享的 statement snapshot sequence。</param>
/// <param name="PathDigest">稳定结果路径摘要。</param>
/// <param name="AccessPath">实际访问路径。</param>
/// <param name="FallbackReason">回退原因；原生邻接正常执行时为 null。</param>
public sealed record GraphWeightedPathAlgorithmEvidence(
    string Algorithm,
    string Correctness,
    string Admission,
    string AdmissionReason,
    int Iterations,
    int PathDepth,
    double TotalWeight,
    int ExpandedVertices,
    long ExpandedEdges,
    double ExpandedEdgeReductionVsDijkstra,
    double P95LatencyRatioVsDijkstra,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    long AllocatedBytesP50,
    long AllocatedBytesP95,
    long AllocatedBytesP99,
    long WorkingSetDeltaMaxBytes,
    long SnapshotSequence,
    string PathDigest,
    string AccessPath,
    string? FallbackReason);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(GraphWeightedPathEvidenceReport))]
internal sealed partial class GraphWeightedPathEvidenceJsonContext : JsonSerializerContext;
