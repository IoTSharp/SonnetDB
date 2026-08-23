using System.Text.Json;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>M40 #362 加权路径 benchmark 与 evidence 报告合同测试。</summary>
public sealed class GraphWeightedPathEvidenceReportTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(
        Path.GetTempPath(),
        "sndb-m40-weighted-path-report-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建独立报告目录。</summary>
    public GraphWeightedPathEvidenceReportTests() => Directory.CreateDirectory(_outputDirectory);

    /// <summary>清理测试报告。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理不能覆盖报告断言。
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 文件句柄短暂存活时交给临时目录后续清理。
        }
    }

    /// <summary>验证三算法结果、准入矩阵和未运行生产门禁均被如实输出。</summary>
    [Fact]
    public void Run_ContractSmoke_EmitsAlgorithmMatrixWithoutProductionClaim()
    {
        GraphWeightedPathEvidenceReport report = GraphWeightedPathEvidenceRunner.Run(
            _outputDirectory,
            side: 8,
            iterations: 3);

        Assert.Equal("m40-graph-weighted-path-evidence-v1", report.Schema);
        Assert.Equal("#362", report.Issue);
        Assert.Equal("contract_smoke", report.Mode);
        Assert.Equal("PASS", report.LocalCorrectness);
        Assert.Equal("NOT_RUN", report.FixedHardware);
        Assert.Equal("NOT_RUN", report.ProductionGate);
        Assert.Equal("gj-topology-weighted-route-v1", report.Dataset.Name);
        Assert.Equal(64, report.Dataset.VertexCount);
        Assert.Equal(224, report.Dataset.EdgeCount);
        Assert.Equal(7, report.Dataset.ExpectedDepth);
        Assert.Equal(7d, report.Dataset.ExpectedTotalWeight);
        Assert.Equal(3, report.Algorithms.Count);
        Assert.Equal(
            ["a_star", "bidirectional_dijkstra", "dijkstra"],
            report.Algorithms.Select(static algorithm => algorithm.Algorithm).Order(StringComparer.Ordinal));
        Assert.All(report.Algorithms, static algorithm =>
        {
            Assert.Equal("PASS", algorithm.Correctness);
            Assert.Equal(3, algorithm.Iterations);
            Assert.Equal("native_adjacency", algorithm.AccessPath);
            Assert.Null(algorithm.FallbackReason);
            Assert.Equal(7, algorithm.PathDepth);
            Assert.Equal(7d, algorithm.TotalWeight);
            Assert.True(algorithm.P95Milliseconds >= algorithm.P50Milliseconds);
            Assert.True(algorithm.P99Milliseconds >= algorithm.P95Milliseconds);
            Assert.True(algorithm.AllocatedBytesP95 >= 0);
            Assert.NotEmpty(algorithm.PathDigest);
        });
        GraphWeightedPathAlgorithmEvidence dijkstra = Assert.Single(
            report.Algorithms,
            static algorithm => algorithm.Algorithm == "dijkstra");
        GraphWeightedPathAlgorithmEvidence aStar = Assert.Single(
            report.Algorithms,
            static algorithm => algorithm.Algorithm == "a_star");
        Assert.Equal("BASELINE", dijkstra.Admission);
        Assert.True(aStar.ExpandedEdges < dijkstra.ExpandedEdges);

        string jsonPath = Path.Combine(_outputDirectory, "m40-graph-weighted-path-evidence.json");
        string markdownPath = Path.Combine(_outputDirectory, "m40-graph-weighted-path-evidence.md");
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(markdownPath));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal(
            "m40-graph-weighted-path-evidence-v1",
            document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("algorithms").GetArrayLength());
        Assert.Equal("NOT_RUN", document.RootElement.GetProperty("fixedHardware").GetString());
        string markdown = File.ReadAllText(markdownPath);
        Assert.Contains("bidirectional_dijkstra", markdown, StringComparison.Ordinal);
        Assert.Contains("Fixed hardware: `NOT_RUN`", markdown, StringComparison.Ordinal);
    }

    /// <summary>验证 BenchmarkDotNet fixture 的 setup、三条查询和 cleanup 可完整运行。</summary>
    [Fact]
    public void Benchmark_Smoke_ExecutesAllWeightedPathAlgorithms()
    {
        var benchmark = new GraphWeightedPathBenchmark { Side = 8 };
        try
        {
            benchmark.Setup();
            Assert.True(benchmark.Dijkstra() > 0);
            Assert.True(benchmark.AStar() > 0);
            Assert.True(benchmark.BidirectionalDijkstra() > 0);
        }
        finally
        {
            benchmark.Cleanup();
        }
    }
}
