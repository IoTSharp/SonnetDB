using System.Text.Json;
using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>M36 #323 对象分页本机预检的报告边界合同测试。</summary>
public sealed class ObjectM36ValidationEvidenceTests : IDisposable
{
    private readonly string _outputDirectory = Path.Combine(
        Path.GetTempPath(),
        "sndb-m36-object-validation-test-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建独占报告目录。</summary>
    public ObjectM36ValidationEvidenceTests() => Directory.CreateDirectory(_outputDirectory);

    /// <summary>清理当前测试独占的报告目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Windows 短暂文件句柄不应掩盖报告断言。
        }
        catch (UnauthorizedAccessException)
        {
            // 交由系统临时目录清理，不改变测试结论。
        }
    }

    /// <summary>本机预检必须记录原始交错变更样本，并禁止生成固定硬件或容量通过结论。</summary>
    [Fact]
    public async Task RunContractAsync_InterleavedPagination_EmitsDeferredFieldValidation()
    {
        ObjectM36ValidationReport report = await ObjectM36ValidationEvidenceRunner.RunContractAsync(
            _outputDirectory,
            fixtureObjectCount: 64,
            sampleCount: 8);

        Assert.Equal("m36-object-validation-v1", report.Schema);
        Assert.Equal("#323", report.Issue);
        Assert.Equal("contract_smoke", report.Mode);
        Assert.Equal(ObjectM36ValidationStatus.Pass, report.LocalPrecheck);
        Assert.Equal(ObjectM36ValidationStatus.NotReady, report.FixedHardware);
        Assert.Equal(ObjectM36ValidationStatus.NotRun, report.Capacity);
        Assert.Equal(ObjectM36ValidationStatus.Deferred, report.TransferValidation);
        Assert.Equal(ObjectM36ValidationStatus.Deferred, report.ReleaseDecision);
        Assert.Equal(64, report.Pagination.FixtureObjectCount);
        Assert.Equal(8, report.Pagination.SampleCount);
        Assert.Equal(8, report.Pagination.Samples.Count);
        Assert.True(report.Pagination.Mutations >= report.Pagination.SampleCount);
        Assert.All(report.Pagination.Samples, static sample =>
        {
            Assert.Equal(64, sample.ReturnedObjects);
            Assert.True(sample.ElapsedMilliseconds >= 0);
            Assert.True(sample.AllocatedBytes >= 0);
        });

        string jsonPath = Path.Combine(_outputDirectory, "m36-object-validation.json");
        string markdownPath = Path.Combine(_outputDirectory, "m36-object-validation.md");
        Assert.True(File.Exists(jsonPath));
        Assert.True(File.Exists(markdownPath));
        ObjectM36ValidationVerification verification = ObjectM36ValidationEvidenceRunner.Verify(jsonPath);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Failures));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        Assert.Equal("NOT_READY", document.RootElement.GetProperty("fixedHardware").GetString());
        Assert.Equal("NOT_RUN", document.RootElement.GetProperty("capacity").GetString());
        Assert.Equal("DEFERRED", document.RootElement.GetProperty("releaseDecision").GetString());
        Assert.Contains("Fixed hardware: `NOT_READY`", File.ReadAllText(markdownPath), StringComparison.Ordinal);
    }
}
