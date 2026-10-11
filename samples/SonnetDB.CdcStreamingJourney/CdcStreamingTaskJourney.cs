using SonnetDB.Cdc;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Streaming;

namespace SonnetDB.Samples;

/// <summary>多分区桥接、目录任务重投和排序预算的本地重开结果。</summary>
/// <param name="Partitions">独立分区数量。</param>
/// <param name="Events">实际捕获和确认的文档变更数量。</param>
/// <param name="WindowCount">去重后的持久窗口总计数。</param>
/// <param name="RedeliveryAttempt">窗口提交后未确认批次的重投次数。</param>
/// <param name="SortedTime">按未投影字段降序排序后的首个时间戳。</param>
public sealed record CdcStreamingTaskJourneyResult(int Partitions, int Events, long WindowCount, int RedeliveryAttempt, long SortedTime);

/// <summary>通过真实文档源、独立桥接及注册任务验证本地有界恢复接线。</summary>
public static class CdcStreamingTaskJourney
{
    /// <summary>在专属空目录执行三十秒以内的捕获、部分推进、未确认重开与对账旅程。</summary>
    /// <param name="directory">调用方拥有的空目录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>仅代表本地文件与嵌入式执行合同的结果。</returns>
    public static async Task<CdcStreamingTaskJourneyResult> RunAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("任务旅程要求专属空目录。");
        Directory.CreateDirectory(root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        string databasePath = Path.Combine(root, "database");
        string catalogPath = Path.Combine(root, "tasks");
        var tasks = new[] { TaskDefinition(0), TaskDefinition(1) };
        string? interruptedDelivery = null;
        int redeliveryAttempt = 0;
        using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT readings (host TAG, value FIELD INT)");
            SqlExecutor.Execute(database, "INSERT INTO readings (time,host,value) VALUES(0,'a',10),(1,'b',30),(2,'a',20)");
            VerifySort(database, token);
            database.Documents.Create(DocumentCollectionSchema.Create("north"));
            database.Documents.Create(DocumentCollectionSchema.Create("south"));
            database.Documents.Open("north").Insert("one", "{\"value\":1}");
            database.Documents.Open("north").Insert("two", "{\"value\":2}");
            database.Documents.Open("south").Insert("three", "{\"value\":3}");
            await using var catalog = await FileStreamingTaskCatalog.OpenAsync(catalogPath, cancellationToken: token);
            foreach (StreamingTaskDefinition task in tasks)
            {
                token.ThrowIfCancellationRequested();
                await catalog.RegisterAsync(task, catalog.Revision, token);
            }
            await using var spool0 = new CdcEventSpool(Path.Combine(root, "north.log"));
            await using var spool1 = new CdcEventSpool(Path.Combine(root, "south.log"));
            await CaptureAsync(database.Documents.Open("north"), spool0, 0, token);
            await CaptureAsync(database.Documents.Open("south"), spool1, 1, token);
            await using var target0 = await catalog.OpenSubscriptionAsync(tasks[0].TaskId, token);
            await using var target1 = await catalog.OpenSubscriptionAsync(tasks[1].TaskId, token);
            await using var bridge0 = await OpenBridgeAsync(root, 0, spool0, target0, token);
            await using var bridge1 = await OpenBridgeAsync(root, 1, spool1, target1, token);
            var topology = new CdcStreamingBridgeTopology([bridge0, bridge1], token);
            CdcStreamingBridgeTopologyRunResult partial = await topology.RunAsync(new() { MaxSteps = 2 }, cancellationToken: token);
            Require(partial.CompletedSteps == 2 && partial.ReachedStepLimit && !partial.IsCaughtUp
                && target0.PendingEventCount == 1 && target1.PendingEventCount == 1, "两分区各推进一次");
            await using var windows = await FileStreamingWindowAggregator.CreateAsync(
                WindowPath(root, 0), WindowDefinition(tasks[0]), cancellationToken: token);
            var runner = new FileStreamingTaskRunner(catalog, tasks[0].TaskId, target0,
                DispatchOptions() with { MaxDeliveries = 1 });
            try
            {
                FileStreamingDispatcherResult interrupted = await runner.RunAsync(async (batch, handlerToken) =>
                {
                    await windows.ApplyBatchAsync(batch, handlerToken);
                    interruptedDelivery = batch.DeliveryId;
                    throw new InvalidOperationException("窗口已持久提交；模拟业务处理失败，尚未 ACK。");
                }, token);
                Require(interrupted.FailedDeliveries == 1 && interrupted.AcknowledgedBatches == 0
                    && target0.PendingEventCount == 1, "失败批次保留供重开重投");
            }
            finally
            {
                await runner.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            }
            database.FlushNow();
        }

        await using (var catalog = await FileStreamingTaskCatalog.OpenAsync(catalogPath, cancellationToken: token))
        {
            await using var spool0 = new CdcEventSpool(Path.Combine(root, "north.log"));
            await using var spool1 = new CdcEventSpool(Path.Combine(root, "south.log"));
            await using var target0 = await catalog.OpenSubscriptionAsync(tasks[0].TaskId, token);
            await using var target1 = await catalog.OpenSubscriptionAsync(tasks[1].TaskId, token);
            await using var bridge0 = await OpenBridgeAsync(root, 0, spool0, target0, token);
            await using var bridge1 = await OpenBridgeAsync(root, 1, spool1, target1, token);
            var topology = new CdcStreamingBridgeTopology([bridge0, bridge1], token);
            CdcStreamingBridgeTopologyRunResult completed = await topology.RunAsync(new() { MaxSteps = 4 }, cancellationToken: token);
            Require(completed.IsCaughtUp && bridge0.GetState().CommittedSourceOffset == 2
                && bridge1.GetState().CommittedSourceOffset == 1 && spool0.EventCount == 0 && spool1.EventCount == 0,
                "重开后独立源位点追平");
            await target0.CompleteAsync(token);
            await target1.CompleteAsync(token);
            await using var windows0 = await FileStreamingWindowAggregator.OpenAsync(
                WindowPath(root, 0), WindowDefinition(tasks[0]), cancellationToken: token);
            await using var windows1 = await FileStreamingWindowAggregator.CreateAsync(
                WindowPath(root, 1), WindowDefinition(tasks[1]), cancellationToken: token);
            var runner0 = new FileStreamingTaskRunner(catalog, tasks[0].TaskId, target0, DispatchOptions());
            var runner1 = new FileStreamingTaskRunner(catalog, tasks[1].TaskId, target1, DispatchOptions());
            try
            {
                FileStreamingDispatcherResult first = await runner0.RunAsync(async (batch, handlerToken) =>
                {
                    await windows0.ApplyBatchAsync(batch, handlerToken);
                    if (batch.DeliveryId == interruptedDelivery)
                        redeliveryAttempt = batch.Attempt;
                }, token);
                FileStreamingDispatcherResult second = await runner1.RunAsync(async (batch, handlerToken) =>
                {
                    await windows1.ApplyBatchAsync(batch, handlerToken);
                }, token);
                Require(first.AcknowledgedBatches == 2 && second.AcknowledgedBatches == 1
                    && redeliveryAttempt == 2, "目录任务恢复投递且批次身份不变");
            }
            finally
            {
                await Task.WhenAll(runner0.HandlerCompletion, runner1.HandlerCompletion).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        long count = 0;
        await using (var catalog = await FileStreamingTaskCatalog.OpenAsync(catalogPath, cancellationToken: token))
        {
            for (int index = 0; index < 2; index++)
            {
                token.ThrowIfCancellationRequested();
                await using var target = await catalog.OpenSubscriptionAsync(tasks[index].TaskId, token);
                Require(target.PendingEventCount == 0 && target.Checkpoint.CommittedSequence == (index == 0 ? 1 : 0),
                    "再次重开目标位点对账");
                await using var windows = await FileStreamingWindowAggregator.OpenAsync(
                    WindowPath(root, index), WindowDefinition(tasks[index]), cancellationToken: token);
                StreamingWindowBatch page = await windows.ReadWindowsAsync(maxWindows: 8, cancellationToken: token);
                Require(!page.HasMore, "完整窗口页");
                count += page.Windows.Sum(static window => window.Count);
            }
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        VerifySort(reopened, token);
        Require(count == 3, "三次实际文档变更无重投重复计数");
        return new(2, 3, count, redeliveryAttempt, 1);
    }

    private static StreamingTaskDefinition TaskDefinition(int index) => StreamingTaskDefinition.Create(
        $"task-{index}", $"subscription-{index}",
        StreamingSubscriptionDefinition.Create($"sub-{index}", $"changes-{index}", batchSize: 1, capacity: 8));

    private static StreamingWindowDefinition WindowDefinition(StreamingTaskDefinition task) => StreamingWindowDefinition.Create(
        task.Definition.SubscriptionId, task.Definition.StreamName, TimeSpan.FromMinutes(1));

    private static string WindowPath(string root, int index) => Path.Combine(root, $"window-{index}.json");

    private static FileStreamingDispatcherOptions DispatchOptions() => new()
    {
        MaxBatches = 4,
        MaxDeliveries = 8,
        MaxDuration = TimeSpan.FromSeconds(5),
        HandlerTimeout = TimeSpan.FromSeconds(2),
        RetryDelay = TimeSpan.FromMilliseconds(10),
    };

    private static ValueTask<CdcStreamingBridge> OpenBridgeAsync(
        string root, int index, CdcEventSpool spool, FileStreamingSubscription target, CancellationToken token) =>
        CdcStreamingBridge.OpenAsync(Path.Combine(root, $"bridge-{index}.json"), spool, target,
            new CdcStreamingBridgeBinding($"bridge-{index}", $"site-{index}", index == 0 ? "north" : "south", "documents", 1, index),
            new CdcStreamingBridgeOptions { MaxBatchEvents = 1 }, token);

    private static async Task CaptureAsync(DocumentCollectionStore store, CdcEventSpool spool, int index, CancellationToken token)
    {
        await using var capture = new CdcDocumentSourceCapture(store, spool, new CdcDocumentSourceCaptureOptions
        {
            Source = $"site-{index}",
            Entity = index == 0 ? "north" : "south",
            Schema = "documents",
            Partition = index,
            BatchSize = 8,
        });
        await capture.CaptureAsync(token);
    }

    private static void VerifySort(Tsdb database, CancellationToken token)
    {
        const string sql = "SELECT time FROM readings ORDER BY value DESC, time ASC LIMIT 1";
        var options = new SqlExecutionOptions { MaxMaterializedRows = 32, MaxMaterializedBytes = 8192, CancellationToken = token };
        SelectExecutionResult result = SqlExecutor.Execute(database, null, sql, null, null, options)
            as SelectExecutionResult ?? throw new InvalidDataException("排序查询未返回行结果。");
        Require(result.Rows.Count == 1 && Equals(result.Rows[0][0], 1L) && !result.Truncated, "未投影排序键预算结果");
        try
        {
            SqlExecutor.Execute(database, null, sql, null, null, options with { MaxMaterializedRows = 1 });
        }
        catch (InvalidOperationException error) when (error.Message.Contains("累计物化", StringComparison.Ordinal))
        {
            return;
        }
        throw new InvalidDataException("LIMIT 未拒绝全部排序候选预算。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
