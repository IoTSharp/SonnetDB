using System.Text.Json;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>M41 #368 固定工作负载与报告边界合同测试。</summary>
public sealed class M41PerformanceBaselineReportTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(
        Path.GetTempPath(),
        "sndb-m41-baseline-report-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建独立报告目录。</summary>
    public M41PerformanceBaselineReportTests() => Directory.CreateDirectory(_outputDirectory);

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
            // Windows 文件句柄短暂存活时交给系统临时目录后续清理。
        }
    }

    /// <summary>验证五类固定语料、机器合同和未运行生产门禁均被如实输出。</summary>
    [Fact]
    public void Run_ContractSmoke_EmitsFiveWorkloadsWithoutProductionClaim()
    {
        M41PerformanceBaselineReport report = M41PerformanceBaselineRunner.Run(
            _outputDirectory,
            rowCount: 64,
            iterations: 2);

        Assert.Equal("m41-performance-baseline-v1", report.Schema);
        Assert.Equal("#368", report.Issue);
        Assert.Equal("contract_smoke", report.Mode);
        Assert.Equal("PASS", report.LocalCorrectness);
        Assert.Equal("NOT_RUN", report.FixedHardware);
        Assert.Equal("NOT_RUN", report.ProductionGate);
        Assert.Equal("NOT_APPLICABLE_EMBEDDED", report.SqlPermitQueueEvidence);
        Assert.Equal(64, report.Dataset.TaskRows);
        Assert.Equal(64, report.Dataset.AuditRows);
        Assert.Equal(5, report.Queries.Count);
        Assert.Equal(
            ["descending_pagination", "indexed_exists", "multi_table_join", "nullable_or", "scalar_in"],
            report.Queries.Select(static query => query.Name).Order(StringComparer.Ordinal));
        Assert.All(report.Queries, static query =>
        {
            Assert.Equal("PASS", query.Correctness);
            Assert.Equal(2, query.Iterations);
            Assert.NotEmpty(query.Fingerprint);
            Assert.DoesNotContain("north", query.NormalizedSql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("key-00000002", query.NormalizedSql, StringComparison.Ordinal);
            Assert.True(query.ExecutionP95Ms >= query.ExecutionP50Ms);
            Assert.True(query.ExecutionP99Ms >= query.ExecutionP95Ms);
            Assert.True(query.AllocatedBytesP95 >= 0);
            Assert.Equal(0, query.QueueWaitP95Ms);
        });

        string jsonPath = Path.Combine(_outputDirectory, "m41-performance-baseline.json");
        string markdownPath = Path.Combine(_outputDirectory, "m41-performance-baseline.md");
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(markdownPath));
        string json = File.ReadAllText(jsonPath);
        Assert.DoesNotContain("key-00000002", json, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("m41-performance-baseline-v1", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal(5, document.RootElement.GetProperty("queries").GetArrayLength());
        Assert.Equal("NOT_RUN", document.RootElement.GetProperty("fixedHardware").GetString());

        string markdown = File.ReadAllText(markdownPath);
        Assert.Contains("indexed_exists", markdown, StringComparison.Ordinal);
        Assert.Contains("descending_pagination", markdown, StringComparison.Ordinal);
        Assert.Contains("Fixed hardware: `NOT_RUN`", markdown, StringComparison.Ordinal);
    }
}
