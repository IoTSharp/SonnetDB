using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Model;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>验证新写入重复时间戳策略在 MemTable、Flush 与 WAL 重开边界内的行为。</summary>
public sealed class DuplicateTimestampPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-duplicate-policy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(DuplicateTimestampPolicy.KeepAll, 2, 2d)]
    [InlineData(DuplicateTimestampPolicy.KeepFirst, 1, 1d)]
    [InlineData(DuplicateTimestampPolicy.KeepLast, 1, 2d)]
    public void Write_DuplicateTimestamp_AppliesConfiguredPolicy(
        DuplicateTimestampPolicy policy,
        int expectedCount,
        double expectedValue)
    {
        using var db = Tsdb.Open(CreateOptions(policy));
        db.Write(MakePoint(1d));
        db.Write(MakePoint(2d));

        var series = Assert.Single(db.Catalog.Find("cpu", new Dictionary<string, string> { ["host"] = "h1" }));
        var points = db.Query.Execute(new PointQuery(series.Id, "usage", TimeRange.All)).ToArray();

        Assert.Equal(expectedCount, points.Length);
        Assert.Equal(expectedValue, points[^1].Value.AsDouble());

        var aggregate = Assert.Single(db.Query.Execute(
            new AggregateQuery(series.Id, "usage", TimeRange.All, Aggregator.Sum, 0)));
        Assert.Equal(expectedCount, aggregate.Count);
        Assert.Equal(policy == DuplicateTimestampPolicy.KeepAll ? 3d : expectedValue, aggregate.Value);
    }

    [Fact]
    public void KeepLast_FlushAndReopen_PreservesCollapsedMemTableValue()
    {
        using (var db = Tsdb.Open(CreateOptions(DuplicateTimestampPolicy.KeepLast)))
        {
            db.Write(MakePoint(1d));
            db.Write(MakePoint(2d));
            db.FlushNow();
        }

        using var reopened = Tsdb.Open(CreateOptions(DuplicateTimestampPolicy.KeepLast));
        var series = Assert.Single(reopened.Catalog.Find("cpu", new Dictionary<string, string> { ["host"] = "h1" }));
        var points = reopened.Query.Execute(new PointQuery(series.Id, "usage", TimeRange.All)).ToArray();

        var point = Assert.Single(points);
        Assert.Equal(2d, point.Value.AsDouble());
    }

    private TsdbOptions CreateOptions(DuplicateTimestampPolicy policy)
        => new()
        {
            RootDirectory = _root,
            DuplicateTimestampPolicy = policy,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            SegmentWriterOptions = new SonnetDB.Storage.Segments.SegmentWriterOptions { FsyncOnCommit = false },
        };

    private static Point MakePoint(double value)
        => Point.Create(
            "cpu",
            1_000L,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(value) });
}
