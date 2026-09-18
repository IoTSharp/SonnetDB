using SonnetDB.Data.TimeSeries;
using SonnetDB.Ingest;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.TimeSeries;

public sealed class SndbTimeSeriesClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-timeseries-client-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void PointBuilder_BuildsTypedFieldsAndTags()
    {
        SndbTimeSeriesPoint point = SndbTimeSeriesPoint.Create("cpu")
            .Timestamp(1_700_000_000_123_456_789L)
            .Tag("host", "edge-1")
            .Field("usage", 0.5)
            .Field("count", 2L)
            .Field("ok", true)
            .Field("note", "warm")
            .Build();

        Assert.Equal("cpu", point.Measurement);
        Assert.Equal(1_700_000_000_123_456_789L, point.Timestamp);
        Assert.Equal("edge-1", point.Tags["host"]);
        Assert.Equal(FieldType.Float64, point.Fields["usage"].Type);
        Assert.Equal(FieldType.Int64, point.Fields["count"].Type);
        Assert.Equal(FieldType.Boolean, point.Fields["ok"].Type);
        Assert.Equal(FieldType.String, point.Fields["note"].Type);
    }

    [Fact]
    public async Task EmbeddedWriter_ConvertsPrecisionAndReturnsPerItemErrors()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter(
            "cpu",
            new SndbTimeSeriesWriteOptions
            {
                Precision = TimePrecision.Nanoseconds,
                BatchSize = 2,
                FlushMode = BulkFlushMode.Sync,
            });

        var points = new[]
        {
            SndbTimeSeriesPoint.Create("cpu").Timestamp(1_700_000_000_123_456_789L).Field("usage", 0.5).Build(),
            SndbTimeSeriesPoint.Create("other").Timestamp(1_700_000_000_123_456_789L).Field("usage", 0.6).Build(),
        };

        SndbTimeSeriesWriteResult result = await writer.WriteBatchAsync(points);

        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.True(result.Items[0].Succeeded);
        Assert.Equal("invalid_point", result.Items[1].ErrorCode);

        // The writer has already performed a synchronous flush; reopening verifies the precision conversion.
        using var reopened = new SndbTimeSeriesClient($"Data Source={_root}");
        Point stored = Point.Create("cpu", 1_700_000_000_123L, fields: new Dictionary<string, FieldValue>
        {
            ["usage"] = FieldValue.FromDouble(0.5),
        });
        var series = reopened.Embedded!.Catalog.GetOrAdd(stored);
        Assert.Single(reopened.Embedded.Query.Execute(new PointQuery(
            series.Id,
            "usage",
            new TimeRange(1_700_000_000_123L, 1_700_000_000_123L))));
    }

    [Fact]
    public async Task Writer_CancelledBeforeEnqueueDoesNotAcceptWork()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}-cancel");
        await using var writer = client.CreateWriter("cpu");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            SndbTimeSeriesPoint.Create("cpu").Field("usage", 1d).Build(),
            cancellation.Token));
    }

    [Fact]
    public async Task Writer_DisposeDrainsAcceptedBatches()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}-drain");
        SndbTimeSeriesWriter writer = client.CreateWriter(
            "cpu",
            new SndbTimeSeriesWriteOptions { BatchSize = 2 });
        Task<SndbTimeSeriesWriteResult> pending = writer.WriteBatchAsync(
        [
            SndbTimeSeriesPoint.Create("cpu").Timestamp(1).Field("usage", 1d).Build(),
            SndbTimeSeriesPoint.Create("cpu").Timestamp(2).Field("usage", 2d).Build(),
        ]);

        await writer.DisposeAsync();

        SndbTimeSeriesWriteResult result = await pending;
        Assert.Equal(2, result.SucceededCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
