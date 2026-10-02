using SonnetDB.Cdc;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Streaming;

namespace SonnetDB.Samples;

/// <summary>真实文档变更桥接、自动重投及持久窗口重开的本地对账结果。</summary>
/// <param name="SourceOffset">源 CDC 已确认位点。</param>
/// <param name="TargetSequence">目标流已确认序号。</param>
/// <param name="WindowCount">窗口累计变更数。</param>
/// <param name="RedeliveryAttempt">首次处理已提交窗口后失败的重投次数。</param>
/// <param name="PendingEvents">完成后目标未确认事件数。</param>
public sealed record CdcStreamingBridgeJourneyResult(
    long SourceOffset, long TargetSequence, long WindowCount, int RedeliveryAttempt, int PendingEvents);

/// <summary>通过实际文档 change feed、CDC 桥接和自动投递演示本地恢复合同。</summary>
public static class CdcStreamingBridgeJourney
{
    /// <summary>在空目录执行有三十秒时限的真实文件组合旅程，不覆盖调用方文件。</summary>
    /// <param name="directory">调用方提供的专属空目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>四次文档变更与持久窗口、源和目标位点的对账结果。</returns>
    public static async Task<CdcStreamingBridgeJourneyResult> RunAsync(
        string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("桥接旅程要求专属空目录。");
        Directory.CreateDirectory(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        string databasePath = Path.Combine(root, "source-db");
        string spoolPath = Path.Combine(root, "source.log");
        string targetPath = Path.Combine(root, "subscription");
        string bridgePath = Path.Combine(root, "bridge.json");
        string windowPath = Path.Combine(root, "windows.json");
        var definition = StreamingSubscriptionDefinition.Create("bridge-journey", "document-changes", batchSize: 1, capacity: 8);
        var windowDefinition = StreamingWindowDefinition.Create(
            definition.SubscriptionId, definition.StreamName, TimeSpan.FromMinutes(1));
        var binding = new CdcStreamingBridgeBinding("journey", "journey-source", "docs", "documents", 1, 7);
        var bridgeOptions = new CdcStreamingBridgeOptions { MaxBatchEvents = 1 };
        int redeliveryAttempt = 0;
        string? firstDeliveryId = null;

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("docs"));
            DocumentCollectionStore store = database.Documents.Open("docs");
            store.Insert("one", "{\"value\":1}");
            store.Insert("two", "{\"value\":2}");
            await using var spool = new CdcEventSpool(spoolPath);
            await CaptureAsync(store, spool, token);
            await using var target = await FileStreamingSubscription.OpenAsync(targetPath, definition, cancellationToken: token);
            await using var bridge = await CdcStreamingBridge.OpenAsync(bridgePath, spool, target, binding, bridgeOptions, token);
            await using var windows = await FileStreamingWindowAggregator.CreateAsync(windowPath, windowDefinition, cancellationToken: token);
            CdcStreamingBridgeResult first = await bridge.PumpOnceAsync(bridge.GetState().Revision, token);
            Require(first.PublishedEvents == 1 && first.CommittedSourceOffset == 1, "首次源交付位点");
            var dispatcher = new FileStreamingSubscriptionDispatcher(target, DispatchOptions(1));
            FileStreamingDispatcherResult dispatched;
            try
            {
                dispatched = await dispatcher.RunAsync(async (batch, handlerToken) =>
                {
                    await windows.ApplyBatchAsync(batch, handlerToken);
                    if (firstDeliveryId is null)
                    {
                        firstDeliveryId = batch.DeliveryId;
                        throw new InvalidOperationException("模拟窗口提交后处理失败，尚未 ACK。");
                    }
                    Require(batch.DeliveryId == firstDeliveryId, "重投保留批次身份");
                    redeliveryAttempt = batch.Attempt;
                }, token);
            }
            finally
            {
                await dispatcher.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Require(dispatched.AcknowledgedBatches == 1 && dispatched.FailedDeliveries == 1
                && redeliveryAttempt == 2 && target.PendingEventCount == 0, "自动重投与持久窗口去重");
        }

        // 所有文件句柄已关闭；重开源后写入更新和删除，并继续原有未交付前缀。
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            DocumentCollectionStore store = database.Documents.Open("docs");
            store.Replace("two", "{\"value\":3}");
            store.Delete("one");
            await using var spool = new CdcEventSpool(spoolPath);
            await CaptureAsync(store, spool, token);
            await using var target = await FileStreamingSubscription.OpenAsync(targetPath, definition, cancellationToken: token);
            // 在消费者开始前恢复已有 outbox/接收凭证。
            await using var bridge = await CdcStreamingBridge.OpenAsync(bridgePath, spool, target, binding, bridgeOptions, token);
            await using var windows = await FileStreamingWindowAggregator.OpenAsync(windowPath, windowDefinition, cancellationToken: token);
            for (int step = 0; step < 3; step++)
            {
                token.ThrowIfCancellationRequested();
                CdcStreamingBridgeResult pumped = await bridge.PumpOnceAsync(bridge.GetState().Revision, token);
                Require(pumped.PublishedEvents == 1, "一批一事件的有界增量");
            }
            Require(spool.EventCount == 0 && spool.AcknowledgedCheckpoints[7] == 4, "完整 change feed 源确认");
            await target.CompleteAsync(token);
            var dispatcher = new FileStreamingSubscriptionDispatcher(target, DispatchOptions(4));
            FileStreamingDispatcherResult dispatched;
            try
            {
                dispatched = await dispatcher.RunAsync(async (batch, handlerToken) =>
                {
                    await windows.ApplyBatchAsync(batch, handlerToken);
                }, token);
            }
            finally
            {
                await dispatcher.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Require(dispatched.StopReason == FileStreamingDispatcherStopReason.Completed
                && dispatched.AcknowledgedBatches == 3, "尾部三个事件完成自动投递");
        }

        await using var finalSpool = new CdcEventSpool(spoolPath);
        await using var finalTarget = await FileStreamingSubscription.OpenAsync(targetPath, definition, cancellationToken: token);
        await using var finalBridge = await CdcStreamingBridge.OpenAsync(bridgePath, finalSpool, finalTarget, binding, bridgeOptions, token);
        await using var finalWindows = await FileStreamingWindowAggregator.OpenAsync(windowPath, windowDefinition, cancellationToken: token);
        StreamingWindowBatch page = await finalWindows.ReadWindowsAsync(maxWindows: 8, cancellationToken: token);
        long count = page.Windows.Sum(static window => window.Count);
        Require(!page.HasMore && count == 4 && finalTarget.Checkpoint.CommittedSequence == 3
            && finalTarget.PendingEventCount == 0 && finalBridge.GetState().CommittedSourceOffset == 4, "再次重开后窗口及源/目标位点对账");
        return new(4, 3, count, redeliveryAttempt, finalTarget.PendingEventCount);
    }

    private static FileStreamingDispatcherOptions DispatchOptions(int maxBatches) => new()
    {
        MaxBatches = maxBatches,
        MaxDeliveries = 8,
        MaxDuration = TimeSpan.FromSeconds(10),
        HandlerTimeout = TimeSpan.FromSeconds(5),
        RetryDelay = TimeSpan.FromMilliseconds(10),
    };

    private static async Task CaptureAsync(DocumentCollectionStore store, CdcEventSpool spool, CancellationToken token)
    {
        await using var capture = new CdcDocumentSourceCapture(store, spool, new CdcDocumentSourceCaptureOptions
        {
            Source = "journey-source",
            Entity = "docs",
            Schema = "documents",
            Partition = 7,
            BatchSize = 8,
        });
        await capture.CaptureAsync(token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
