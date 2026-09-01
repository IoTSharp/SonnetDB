using SonnetDB.Benchmarks.Benchmarks;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

/// <summary>请求级尾延迟报告的分位口径与输入边界测试。</summary>
public sealed class ModelReadLatencyEvidenceTests
{
    /// <summary>nearest-rank 必须选择向上取整的秩，避免用插值掩盖真实尾部样本。</summary>
    [Fact]
    public void NearestRank_DeterministicSamples_ReturnsObservedValues()
    {
        double[] samples = Enumerable.Range(1, 100).Select(static value => (double)value).ToArray();

        Assert.Equal(50, ModelReadLatencyEvidenceRunner.NearestRank(samples, 0.50));
        Assert.Equal(95, ModelReadLatencyEvidenceRunner.NearestRank(samples, 0.95));
        Assert.Equal(99, ModelReadLatencyEvidenceRunner.NearestRank(samples, 0.99));
    }

    /// <summary>空样本和越界百分位必须明确失败，未知分配的空集合则保持 -1。</summary>
    [Fact]
    public void NearestRank_InvalidInput_FailsExplicitly()
    {
        Assert.Throws<ArgumentException>(
            () => ModelReadLatencyEvidenceRunner.NearestRank(Array.Empty<double>(), 0.95));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModelReadLatencyEvidenceRunner.NearestRank([1d], 0));
        Assert.Equal(-1, ModelReadLatencyEvidenceRunner.NearestRank(Array.Empty<long>(), 0.99));
    }
}
