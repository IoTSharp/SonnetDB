using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;
using System.Threading;

namespace SonnetDB.Core.Tests.Query;

/// <summary>时序类型化查询构建器的范围、窗口、补桶和诊断合同测试。</summary>
public sealed class TimeSeriesQueryBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly TsdbOptions _options;

    public TimeSeriesQueryBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
        _options = new TsdbOptions
        {
            RootDirectory = _root,
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Windows 文件句柄释放存在短暂延迟，测试结果不依赖临时目录清理。
        }
    }

    [Fact]
    public void ForSeries_ReadPoints_BuildsExistingPointQueryLazily()
    {
        using var database = Tsdb.Open(_options);
        Write(database, 0, 1);
        Write(database, 100, 2);
        Write(database, 200, 3);
        ulong seriesId = database.Catalog.Snapshot().Single().Id;

        var builder = database.ForSeries(seriesId, "value")
            .Between(0, 200)
            .Descending()
            .Limit(2);

        PointQuery query = builder.BuildPointQuery();
        Assert.Equal(QueryDirection.Descending, query.Direction);
        Assert.Equal(2, query.Limit);
        Assert.Equal([200L, 100L], builder.ReadPoints().Select(static point => point.Timestamp));
    }

    [Fact]
    public void WindowGapFill_UsesExistingAggregateEngine_AndEmitsBoundedEmptyBuckets()
    {
        using var database = Tsdb.Open(_options);
        Write(database, 0, 1);
        Write(database, 100, 2);
        Write(database, 300, 4);
        ulong seriesId = database.Catalog.Snapshot().Single().Id;

        var buckets = database.ForSeries(seriesId, "value")
            .Between(0, 399)
            .Window(100, Aggregator.Sum)
            .GapFill(-1)
            .ReadAggregates()
            .ToList();

        Assert.Equal(4, buckets.Count);
        Assert.Equal([1d, 2d, -1d, 4d], buckets.Select(static bucket => bucket.Value));
        Assert.Equal(0L, buckets[2].Count);
        Assert.Equal(300L, buckets[3].BucketStart);
    }

    [Fact]
    public void Diagnose_ReportsMissingSeriesAndUnboundedGapFillWithStableCodes()
    {
        using var database = Tsdb.Open(_options);
        var diagnostics = database.ForSeries(0xDEAD_BEEFuL, "value")
            .Window(100, Aggregator.Avg)
            .GapFill()
            .Diagnose();

        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics.Issues, static issue => issue.Code == "TSQ001");
        Assert.Contains(diagnostics.Issues, static issue => issue.Code == "TSQ003");
    }

    [Fact]
    public void Diagnose_UnboundedPointQuery_ProvidesMemoryBoundHint()
    {
        using var database = Tsdb.Open(_options);
        Write(database, 0, 1);
        ulong seriesId = database.Catalog.Snapshot().Single().Id;
        var diagnostics = database.ForSeries(seriesId, "value").Diagnose();

        Assert.False(diagnostics.HasErrors);
        var issue = Assert.Single(diagnostics.Issues);
        Assert.Equal("TSQ004", issue.Code);
        Assert.Equal(TimeSeriesQueryDiagnosticSeverity.Info, issue.Severity);
    }

    [Fact]
    public void ReadPoints_PreCanceledToken_StopsBeforePullingEngine()
    {
        using var database = Tsdb.Open(_options);
        Write(database, 0, 1);
        ulong seriesId = database.Catalog.Snapshot().Single().Id;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => database
            .ForSeries(seriesId, "value")
            .ReadPoints(cancellation.Token)
            .ToList());
    }

    private static void Write(Tsdb database, long timestamp, double value)
    {
        database.Write(Point.Create(
            "sensor",
            timestamp,
            new Dictionary<string, string> { ["host"] = "node-1" },
            new Dictionary<string, FieldValue> { ["value"] = FieldValue.FromDouble(value) }));
    }
}
