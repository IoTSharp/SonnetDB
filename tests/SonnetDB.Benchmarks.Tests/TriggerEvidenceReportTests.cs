using System.Text.Json;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>M39 触发器证据 runner 的机器可读合同测试。</summary>
public sealed class TriggerEvidenceReportTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(
        Path.GetTempPath(),
        "sndb-m39-trigger-report-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建独立的临时报告目录。</summary>
    public TriggerEvidenceReportTests() => Directory.CreateDirectory(_outputDirectory);

    /// <summary>清理本测试生成的证据文件。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 测试清理不能覆盖证据断言结果。
        }
        catch (UnauthorizedAccessException)
        {
            // Windows 文件句柄短暂存活时由系统临时目录后续清理。
        }
    }

    /// <summary>验证 quick 报告完整覆盖三种 DML、三条路径及精确回滚状态。</summary>
    [Fact]
    public void Run_QuickMatrix_EmitsCompleteV3Contract()
    {
        TriggerEvidenceReport report = TriggerEvidenceReportRunner.Run(
            _outputDirectory,
            [1, 3],
            crashTestsVerified: false);

        Assert.Equal("m39-trigger-v2-baseline-v3", report.Schema);
        Assert.Equal(["Insert", "Update", "Delete"], report.Operations);
        Assert.False(report.CrashTestsVerified);
        Assert.Equal(18, report.CostMatrix.Length);
        Assert.Equal(18, report.RollbackMatrix.Length);
        Assert.Equal(18, report.CostMatrix
            .Select(static row => (row.Rows, row.Operation, row.Path))
            .Distinct()
            .Count());
        Assert.Equal(18, report.RollbackMatrix
            .Select(static row => (row.Rows, row.Operation, row.Path))
            .Distinct()
            .Count());

        Assert.All(report.CostMatrix, static row =>
        {
            Assert.Equal(row.Rows, row.RowsAffected);
            Assert.Equal(row.RowsInserted, row.RowsAffected);
            Assert.Equal(row.RowStoreBytes, row.RowStoreBytesDelta);
        });
        Assert.All(report.RollbackMatrix, static row =>
        {
            Assert.True(row.FailedAsExpected);
            Assert.True(row.SourceStateRestored);
            Assert.True(row.AuditStateRestored);
            Assert.Equal(row.Operation == "Insert" ? 0 : row.Rows, row.SourceRowsAfterRollback);
            Assert.Equal(1, row.AuditRowsAfterRollback);
        });

        string jsonPath = Path.Combine(_outputDirectory, "report.json");
        string markdownPath = Path.Combine(_outputDirectory, "report.md");
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(markdownPath));
        byte[] jsonBytes = File.ReadAllBytes(jsonPath);
        Assert.NotEmpty(jsonBytes);
        Assert.Equal((byte)'{', jsonBytes[0]);
        using JsonDocument json = JsonDocument.Parse(jsonBytes);
        Assert.Equal("m39-trigger-v2-baseline-v3", json.RootElement.GetProperty("schema").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("operations").GetArrayLength());
        Assert.Equal(18, json.RootElement.GetProperty("costMatrix").GetArrayLength());
        Assert.Equal(18, json.RootElement.GetProperty("rollbackMatrix").GetArrayLength());
        JsonElement firstCost = json.RootElement.GetProperty("costMatrix")[0];
        Assert.Equal(
            firstCost.GetProperty("rowStoreBytes").GetInt64(),
            firstCost.GetProperty("rowStoreBytesDelta").GetInt64());
        string markdown = File.ReadAllText(markdownPath);
        Assert.Contains("Rowstore bytes delta", markdown, StringComparison.Ordinal);
        Assert.Contains("Audit restored", markdown, StringComparison.Ordinal);
    }
}
