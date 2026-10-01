using System.Text;
using SonnetDB.Cdc;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Streaming;

namespace SonnetDB.Samples;

/// <summary>真实文档、CDC、持久订阅与窗口重开旅程的对账结果。</summary>
/// <param name="Partitions">成功恢复的 CDC 分区数。</param>
/// <param name="ReconciledRows">与源文档精确对账的物化行数。</param>
/// <param name="WindowCount">重投后持久窗口的最终计数。</param>
/// <param name="RedeliveryAttempt">中断 ACK 后的重投次数。</param>
/// <param name="PendingEvents">最终未确认事件数。</param>
/// <param name="WindowClosed">窗口是否已由 watermark 关闭。</param>
public sealed record CdcStreamingJourneyResult(
    int Partitions, int ReconciledRows, long WindowCount,
    int RedeliveryAttempt, int PendingEvents, bool WindowClosed);

/// <summary>通过真实本地入口演示多分区 CDC 与持久窗口的组合恢复。</summary>
public static class CdcStreamingRecoveryJourney
{
    /// <summary>在专属空目录中执行有界旅程；状态文件由调用方保留或回收。</summary>
    /// <param name="directory">调用方拥有的空目录。</param>
    /// <param name="cancellationToken">取消令牌；旅程另设三十秒上限。</param>
    /// <returns>源与副本、订阅检查点和窗口计数的对账结果。</returns>
    public static async Task<CdcStreamingJourneyResult> RunAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Journey requires an empty, caller-owned directory.");
        Directory.CreateDirectory(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        IReadOnlyList<CdcSnapshotRow> rows = await RecoverCdcAsync(root, token);
        return await RecoverStreamingAsync(root, rows, token);
    }

    private static async Task<IReadOnlyList<CdcSnapshotRow>> RecoverCdcAsync(string root, CancellationToken token)
    {
        string databasePath = Path.Combine(root, "source");
        string firstView = Path.Combine(root, "devices.view");
        string secondView = Path.Combine(root, "alerts.view");
        string firstReplica = Path.Combine(root, "devices.replica");
        string secondReplica = Path.Combine(root, "alerts.replica");
        string firstSpool = Path.Combine(root, "devices.spool");
        string secondSpool = Path.Combine(root, "alerts.spool");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("devices"));
            database.Documents.Create(DocumentCollectionSchema.Create("alerts"));
            DocumentCollectionStore devices = database.Documents.Open("devices");
            DocumentCollectionStore alerts = database.Documents.Open("alerts");
            devices.Insert("one", "{\"value\":1}");
            alerts.Insert("one", "{\"value\":1}");
            await using var viewA = await CdcSourceReadView.CaptureDocumentCollectionAsync(
                firstView, devices, "journey-source", partition: 0, cancellationToken: token);
            await using var viewB = await CdcSourceReadView.CaptureDocumentCollectionAsync(
                secondView, alerts, "journey-source", partition: 1, cancellationToken: token);
            await using var replicaA = await CdcSnapshotReplica.CreateAsync(
                firstReplica, viewA.Descriptor, cancellationToken: token);
            await using var replicaB = await CdcSnapshotReplica.CreateAsync(
                secondReplica, viewB.Descriptor, cancellationToken: token);
            await using var spoolA = new CdcEventSpool(firstSpool);
            await using var spoolB = new CdcEventSpool(secondSpool);
            devices.Insert("two", "{\"value\":2}");
            alerts.Insert("two", "{\"value\":2}");
            await CaptureAsync(devices, spoolA, 0, token);
            await CaptureAsync(alerts, spoolB, 1, token);
            var topology = new CdcLocalReplicaTopology([
                new(viewA, spoolA, replicaA), new(viewB, spoolB, replicaB)]);
            await topology.RunAsync(RunOptions(1), cancellationToken: token);
            Require(replicaA.GetState().SnapshotRowsCopied == 1
                && replicaB.GetState().SnapshotRowsCopied == 0, "One-step fair snapshot boundary");
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        DocumentCollectionStore reopenedDevices = reopened.Documents.Open("devices");
        DocumentCollectionStore reopenedAlerts = reopened.Documents.Open("alerts");
        reopenedDevices.Replace("one", "{\"value\":3}");
        reopenedAlerts.Delete("one");
        await using var resumedViewA = CdcSourceReadView.OpenExisting(
            firstView, "journey-source", "devices", "documents", CdcEventCodec.CurrentSchemaVersion, 0);
        await using var resumedViewB = CdcSourceReadView.OpenExisting(
            secondView, "journey-source", "alerts", "documents", CdcEventCodec.CurrentSchemaVersion, 1);
        await using var resumedReplicaA = CdcSnapshotReplica.Open(firstReplica, resumedViewA.Descriptor);
        await using var resumedReplicaB = CdcSnapshotReplica.Open(secondReplica, resumedViewB.Descriptor);
        await using var resumedSpoolA = new CdcEventSpool(firstSpool);
        await using var resumedSpoolB = new CdcEventSpool(secondSpool);
        await CaptureAsync(reopenedDevices, resumedSpoolA, 0, token);
        await CaptureAsync(reopenedAlerts, resumedSpoolB, 1, token);
        var resumedTopology = new CdcLocalReplicaTopology([
            new(resumedViewA, resumedSpoolA, resumedReplicaA),
            new(resumedViewB, resumedSpoolB, resumedReplicaB)]);
        await resumedTopology.RunAsync(RunOptions(32), cancellationToken: token);
        CdcSnapshotRowBatch devicesPage = await resumedReplicaA.ReadRowsAsync(cancellationToken: token);
        CdcSnapshotRowBatch alertsPage = await resumedReplicaB.ReadRowsAsync(cancellationToken: token);
        CdcSnapshotRow[] expectedDevices = [
            new("one", reopenedDevices.Get("one")!.Json), new("two", reopenedDevices.Get("two")!.Json)];
        CdcSnapshotRow[] expectedAlerts = [new("two", reopenedAlerts.Get("two")!.Json)];
        Require(!devicesPage.HasMore && !alertsPage.HasMore
            && devicesPage.Rows.SequenceEqual(expectedDevices)
            && alertsPage.Rows.SequenceEqual(expectedAlerts), "Source/replica values and deletes");
        Require(devicesPage.AppliedCheckpoint == new CdcCheckpoint(0, 3)
            && alertsPage.AppliedCheckpoint == new CdcCheckpoint(1, 3)
            && resumedSpoolA.EventCount == 0 && resumedSpoolB.EventCount == 0, "Independent durable CDC checkpoints");
        return devicesPage.Rows.Concat(alertsPage.Rows).ToArray();
    }

    private static async Task<CdcStreamingJourneyResult> RecoverStreamingAsync(
        string root, IReadOnlyList<CdcSnapshotRow> rows, CancellationToken token)
    {
        Require(rows.Count == 3, "Fixed export capacity");
        var definition = StreamingSubscriptionDefinition.Create("journey-count", "replica-rows", batchSize: 2, capacity: 8);
        var windowDefinition = StreamingWindowDefinition.Create(
            definition.SubscriptionId, definition.StreamName, TimeSpan.FromMinutes(1));
        string subscriptionPath = Path.Combine(root, "subscription");
        string windowPath = Path.Combine(root, "windows.json");
        DateTimeOffset start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        string interruptedDelivery;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(subscriptionPath, definition, cancellationToken: token))
        await using (var windows = await FileStreamingWindowAggregator.CreateAsync(
            windowPath, windowDefinition, subscription.Checkpoint, cancellationToken: token))
        {
            // This is a bounded fixed replica export, with stable sequences; it is not a continuous CDC bridge.
            for (int index = 0; index < rows.Count && index < 8; index++)
            {
                token.ThrowIfCancellationRequested();
                await subscription.PublishAsync(new StreamingEvent(
                    $"row-{index}", index, start.AddSeconds(index), Encoding.UTF8.GetBytes(rows[index].ValueJson)), token);
            }
            await subscription.CompleteAsync(token);
            StreamingDeliveryBatch batch = await subscription.ReadBatchAsync(token)
                ?? throw new InvalidDataException("Expected first delivery.");
            await windows.ApplyBatchAsync(batch, token);
            interruptedDelivery = batch.DeliveryId;
            // Close both handles after durable aggregation and before ACK, then rebuild from disk.
        }

        await using (var subscription = await FileStreamingSubscription.OpenAsync(subscriptionPath, definition, cancellationToken: token))
        await using (var windows = await FileStreamingWindowAggregator.OpenAsync(windowPath, windowDefinition, cancellationToken: token))
        {
            StreamingDeliveryBatch redelivery = await subscription.ReadBatchAsync(token)
                ?? throw new InvalidDataException("Expected durable redelivery.");
            Require(redelivery.DeliveryId == interruptedDelivery && redelivery.Attempt == 2, "Stable redelivery identity");
            await windows.ApplyBatchAsync(redelivery, token);
            await subscription.AcknowledgeAsync(redelivery.DeliveryId, token);
            Require(await windows.PumpOnceAsync(subscription, token), "Remaining tail batch");
            Require(!await windows.PumpOnceAsync(subscription, token), "Completed subscription drain");
            await windows.AdvanceWatermarkAsync(start.AddMinutes(1), token);
        }

        await using var finalSubscription = await FileStreamingSubscription.OpenAsync(subscriptionPath, definition, cancellationToken: token);
        await using var finalWindows = await FileStreamingWindowAggregator.OpenAsync(windowPath, windowDefinition, cancellationToken: token);
        var page = await finalWindows.ReadWindowsAsync(maxWindows: 8, cancellationToken: token);
        var state = await finalWindows.GetStateAsync(token);
        Require(page.Windows.Count == 1 && !page.HasMore && page.Windows[0].Count == rows.Count
            && page.Windows[0].IsClosed, "Persistent closed count without duplicate aggregation");
        Require(finalSubscription.PendingEventCount == 0 && state.AppliedSequence == rows.Count - 1
            && finalSubscription.Checkpoint.CommittedSequence == state.AppliedSequence, "Window/subscription durable checkpoints");
        return new(2, rows.Count, page.Windows[0].Count, 2, finalSubscription.PendingEventCount, page.Windows[0].IsClosed);
    }

    private static async Task CaptureAsync(DocumentCollectionStore store, CdcEventSpool spool, long partition, CancellationToken token)
    {
        await using var capture = new CdcDocumentSourceCapture(store, spool, new CdcDocumentSourceCaptureOptions
        {
            Source = "journey-source",
            Entity = store.Schema.Name,
            Partition = partition,
            BatchSize = 8,
        });
        CdcDocumentSourceCaptureResult result = await capture.CaptureAsync(token);
        Require(!result.HasMore, "Bounded source capture exhausted");
    }

    private static CdcLocalReplicaTopologyRunOptions RunOptions(int steps) => new()
    {
        MaxSteps = steps,
        MaxElapsedTime = TimeSpan.FromSeconds(10),
        MaxRowsPerStep = 1,
        MaxBytesPerStep = 4096,
    };

    private static void Require(bool condition, string contract)
    {
        if (!condition)
            throw new InvalidDataException($"Journey reconciliation failed: {contract}.");
    }
}
