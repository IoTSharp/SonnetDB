using System.Text.Json;
using SonnetDB.Benchmarks.Benchmarks;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Benchmarks.Tests;

public sealed class KvLsmCapacityBenchmarkTests : IDisposable
{
    private readonly string _output = Path.Combine(Path.GetTempPath(), "sndb-kv-capacity-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>最小与跨页语料在双引擎下完成全量校验、双向扫描和写后快照，证据可读回且目录回收。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy, 1)]
    [InlineData(KvStorageEngine.Lsm, 1)]
    [InlineData(KvStorageEngine.Legacy, 257)]
    [InlineData(KvStorageEngine.Lsm, 257)]
    public void KvCorpus_BothEngines_RoundTripsAndMeasures(KvStorageEngine engine, int entries)
    {
        KvLsmCapacityReport report = KvLsmCapacityBenchmark.Run(Options() with { Engine = engine, Entries = entries, RandomOrder = true });
        Assert.Equal("PASS", report.Status);
        Assert.True(report.CleanupComplete);
        Assert.False(Directory.Exists(report.DataDirectory));
        Assert.Equal(1, report.FullReadbacks);
        Assert.Contains(report.Measurements, item => item.Name == "seek-scan100-reverse" && item.Operations == 4);
        Assert.Contains(report.Measurements, item => item.Name == "snapshot-after-write" && item.P99Microseconds >= 0);
        KvLsmCapacityReport saved = JsonSerializer.Deserialize(File.ReadAllText(report.Options.Output), KvLsmCapacityJsonContext.Default.KvLsmCapacityReport)!;
        Assert.Equal(report.RunId, saved.RunId);
        Assert.Equal(engine, saved.Options.Engine);
        Assert.Equal(entries, saved.Options.Entries);
        Assert.Equal("1.2", saved.SchemaVersion);
        if (engine == KvStorageEngine.Lsm)
        {
            Assert.NotNull(saved.CompletedLsmIo);
            Assert.True(saved.CompletedLsmIo.FlushOutputBytes > 0);
            Assert.Equal(report.CompletedLsmIo, saved.CompletedLsmIo);
            Assert.Contains("not total write amplification", saved.CompletedLsmIo.Scope, StringComparison.Ordinal);
        }
        else
        {
            Assert.Null(saved.CompletedLsmIo);
        }
        Assert.Throws<IOException>(() => KvLsmCapacityBenchmark.Run(report.Options));
    }

    /// <summary>迁移语料全量对拍并再次重开，不能把空间不足回退到 legacy 当作迁移成功。</summary>
    [Fact]
    public void Migration_ReadsEveryRowAcrossReopen()
    {
        KvLsmCapacityReport report = KvLsmCapacityBenchmark.Run(Options() with { Scenario = "migration", Entries = 257 });
        Assert.Equal("PASS", report.Status);
        Assert.Equal(2, report.FullReadbacks);
        Assert.Null(report.CompletedLsmIo);
        Assert.True(report.MigrationSecondsPerLogicalGiB > 0);
        Assert.True(report.CleanupComplete);
    }

    /// <summary>多表语料实际并发写热表、读冷表，并校验冷热表计数。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void Tables_ConcurrentWriterPreservesColdRows(KvStorageEngine engine)
    {
        KvLsmCapacityReport report = KvLsmCapacityBenchmark.Run(Options() with { Scenario = "tables", Engine = engine, Entries = 1000, Samples = 20 });
        Assert.Equal("PASS", report.Status);
        Assert.True(report.HotTableWrites > 0);
        Assert.True(report.CleanupComplete);
        Assert.Contains(report.Measurements, item => item.Name == "cold-tables-with-hot-writer");
    }

    /// <summary>取消产生未完成证据且回收本任务目录，不伪装成通过。</summary>
    [Fact]
    public void CancelledRun_IsUnknownAndCleansUp()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        KvLsmCapacityReport report = KvLsmCapacityBenchmark.Run(Options(), cancelled.Token);
        Assert.Equal("UNKNOWN", report.Status);
        Assert.Empty(report.Measurements);
        Assert.Null(report.CompletedLsmIo);
        Assert.True(report.CleanupComplete);
    }

    /// <summary>稳态观察使用完整十秒窗口，保留真实稳定性结果而非断言硬件一定稳定。</summary>
    [Fact]
    public void Memory_RecordsBoundedStabilityWindow()
    {
        KvLsmCapacityReport report = KvLsmCapacityBenchmark.Run(Options() with { Scenario = "memory" });
        Assert.Equal("PASS", report.Status);
        Assert.Equal(100, report.PostCheckpointRssBytes.Count);
        Assert.True(report.RssObservationSeconds >= 9.5);
        Assert.NotNull(report.RssStable);
        Assert.Equal(report.RssStable == true, report.ObservedStableRssBytes.HasValue);
        Assert.True(report.CleanupComplete);
    }

    /// <summary>非法、缺失和重复参数在创建数据目录前拒绝。</summary>
    [Theory]
    [InlineData("--entries", "0")]
    [InlineData("--entries", "100000001")]
    [InlineData("--value-bytes", "7")]
    [InlineData("--value-bytes", "65537")]
    [InlineData("--seconds", "0")]
    [InlineData("--samples", "-1")]
    [InlineData("--engine", "42")]
    [InlineData("--scenario", "unknown")]
    [InlineData("--order", "unknown")]
    public void InvalidOptions_AreRejected(string option, string value)
        => Assert.ThrowsAny<ArgumentException>(() => KvLsmCapacityCommand.Parse(["--kv-lsm-capacity", "--source", "test", "--output", "unused.json", option, value]));

    /// <summary>缺失来源、输出或参数值，以及重复参数，不能启动基准。</summary>
    [Fact]
    public void MissingOrDuplicateOptions_AreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => KvLsmCapacityCommand.Parse(["--kv-lsm-capacity"]));
        Assert.ThrowsAny<ArgumentException>(() => KvLsmCapacityCommand.Parse(["--kv-lsm-capacity", "--entries"]));
        Assert.ThrowsAny<ArgumentException>(() => KvLsmCapacityCommand.Parse(["--kv-lsm-capacity", "--source", "a", "--source", "b"]));
    }

    private KvLsmCapacityOptions Options() => new()
    {
        Entries = 10,
        Samples = 4,
        ColdTables = 2,
        RowsPerTable = 3,
        TimeoutSeconds = 30,
        Source = "test-fixture",
        Output = Path.Combine(_output, Guid.NewGuid().ToString("N") + ".json"),
    };

    /// <summary>删除本测试独占的结果目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_output)) Directory.Delete(_output, recursive: true);
    }
}
