using SonnetDB.Cdc;
using SonnetDB.Documents;
using SonnetDB.Engine;

namespace SonnetDB.Core.Tests.Cdc;

/// <summary>
/// 文档 CDC 源捕获自动调度器的有界推进和恢复回归。
/// </summary>
public sealed class CdcDocumentSourceCaptureSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-cdc-capture-scheduler-" + Guid.NewGuid().ToString("N"));

    public CdcDocumentSourceCaptureSchedulerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task RunAsync_WithEmptySource_CompletesOneBatchWithoutMoreWork()
    {
        string databasePath = Path.Combine(_root, "empty-db");
        string spoolPath = Path.Combine(_root, "empty-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        await using var spool = new CdcEventSpool(spoolPath);
        await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions());
        var scheduler = new CdcDocumentSourceCaptureScheduler(
            capture,
            new CdcDocumentSourceCaptureScheduleOptions { MaxBatches = 4, MaxEvents = 8 });

        CdcDocumentSourceCaptureScheduleResult result = await scheduler.RunAsync();

        Assert.Equal(0, result.StartSequence);
        Assert.Equal(0, result.EndSequence);
        Assert.Equal(0, result.CapturedEvents);
        Assert.Equal(1, result.CompletedBatches);
        Assert.False(result.HasMore);
        Assert.False(result.ReachedBatchLimit);
        Assert.False(result.ReachedEventLimit);
        Assert.Single(result.Progress);
    }

    [Fact]
    public async Task RunAsync_WithEventLimit_AdvancesAcrossBatchesWithoutExceedingLimit()
    {
        string databasePath = Path.Combine(_root, "bounded-db");
        string spoolPath = Path.Combine(_root, "bounded-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        for (int value = 2; value <= 5; value++)
            store.Replace("a", $"{{\"value\":{value}}}");

        await using var spool = new CdcEventSpool(spoolPath);
        await using var capture = new CdcDocumentSourceCapture(
            store,
            spool,
            CaptureOptions(batchSize: 2));
        var scheduler = new CdcDocumentSourceCaptureScheduler(
            capture,
            new CdcDocumentSourceCaptureScheduleOptions { MaxBatches = 5, MaxEvents = 3 });

        CdcDocumentSourceCaptureScheduleResult result = await scheduler.RunAsync();

        Assert.Equal(0, result.StartSequence);
        Assert.Equal(3, result.EndSequence);
        Assert.Equal(3, result.CapturedEvents);
        Assert.Equal(2, result.CompletedBatches);
        Assert.True(result.HasMore);
        Assert.False(result.ReachedBatchLimit);
        Assert.True(result.ReachedEventLimit);
        Assert.Equal([1L, 2L, 3L], (await spool.ReplayAsync()).Select(static value => value.Sequence));
    }

    [Fact]
    public async Task RunAsync_WhenCancelledBeforeStart_DoesNotCaptureEvents()
    {
        string databasePath = Path.Combine(_root, "cancelled-db");
        string spoolPath = Path.Combine(_root, "cancelled-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        await using var spool = new CdcEventSpool(spoolPath);
        await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions());
        var scheduler = new CdcDocumentSourceCaptureScheduler(
            capture,
            new CdcDocumentSourceCaptureScheduleOptions { MaxBatches = 3, MaxEvents = 3 });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scheduler.RunAsync(cancellation.Token).AsTask());

        Assert.Equal(0, spool.EventCount);
    }

    [Fact]
    public async Task RunAsync_AfterReopen_ResumesFromSpoolHighWatermark()
    {
        string databasePath = Path.Combine(_root, "reopen-db");
        string spoolPath = Path.Combine(_root, "reopen-events.log");
        CdcDocumentSourceCaptureScheduleResult first;
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("docs"));
            var store = database.Documents.Open("docs");
            store.Insert("a", "{\"value\":1}");
            for (int value = 2; value <= 5; value++)
                store.Replace("a", $"{{\"value\":{value}}}");

            await using var spool = new CdcEventSpool(spoolPath);
            await using var capture = new CdcDocumentSourceCapture(
                store,
                spool,
                CaptureOptions(batchSize: 2));
            var scheduler = new CdcDocumentSourceCaptureScheduler(
                capture,
                new CdcDocumentSourceCaptureScheduleOptions { MaxBatches = 1, MaxEvents = 10 });
            first = await scheduler.RunAsync();
            Assert.Equal(2, first.CapturedEvents);
            Assert.True(first.HasMore);
        }

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            var store = database.Documents.Open("docs");
            await using var spool = new CdcEventSpool(spoolPath);
            await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions(batchSize: 2));
            var scheduler = new CdcDocumentSourceCaptureScheduler(
                capture,
                new CdcDocumentSourceCaptureScheduleOptions { MaxBatches = 4, MaxEvents = 10 });

            CdcDocumentSourceCaptureScheduleResult resumed = await scheduler.RunAsync();

            Assert.Equal(2, resumed.StartSequence);
            Assert.Equal(5, resumed.EndSequence);
            Assert.Equal(3, resumed.CapturedEvents);
            Assert.Equal(2, resumed.CompletedBatches);
            Assert.False(resumed.HasMore);
            Assert.Equal([1L, 2L, 3L, 4L, 5L],
                (await spool.ReplayAsync()).Select(static value => value.Sequence));
        }
    }

    private static CdcDocumentSourceCaptureOptions CaptureOptions(int batchSize = 16)
        => new()
        {
            Source = "scheduler-source",
            Entity = "docs",
            Schema = "documents",
            SchemaVersion = 1,
            Partition = 11,
            BatchSize = batchSize,
        };
}
