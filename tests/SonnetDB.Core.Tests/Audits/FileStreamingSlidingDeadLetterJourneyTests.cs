using System.Text;
using SonnetDB.Streaming;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>死信重放、滑动窗口提交前后恢复及显式完成的本地组合合同。</summary>
public sealed class FileStreamingSlidingDeadLetterJourneyTests
{
    /// <summary>重开保留重放身份，窗口提交后未确认的批次去重，业务确认后条件删除死信。</summary>
    [Fact]
    public async Task ReplayDeadLetter_AfterWindowCommitBeforeAck_ReconcilesAndDeletesConditionally()
    {
        string root = Directory.CreateTempSubdirectory("sdb-sliding-dlq-journey-").FullName;
        string sourcePath = Path.Combine(root, "source");
        string replayPath = Path.Combine(root, "replay");
        string replayWindowPath = Path.Combine(root, "replay-windows.json");
        string tailWindowPath = Path.Combine(root, "tail-windows.json");
        var sourceDefinition = StreamingSubscriptionDefinition.Create("source", "readings", batchSize: 1, capacity: 4);
        var replayDefinition = StreamingSubscriptionDefinition.Create("replay", "readings", batchSize: 1, capacity: 2);
        var sourceOptions = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 1 };
        var replayWindows = Sliding(replayDefinition);
        var tailWindows = Sliding(sourceDefinition);
        DateTimeOffset start = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        StreamingEvent original = new("first", 0, start.AddSeconds(35), "{\"value\":1.5}"u8.ToArray(),
            new Dictionary<string, string> { ["source"] = "meter-1" });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = deadline.Token;
        try
        {
            FileStreamingDeadLetterReplayBatch firstClaim;
            await using (var source = await FileStreamingSubscription.OpenAsync(
                sourcePath, sourceDefinition, sourceOptions, cancellationToken: token))
            {
                await source.PublishAsync(original, token);
                await source.PublishAsync(new StreamingEvent("tail", 1, start.AddSeconds(45),
                    Encoding.UTF8.GetBytes("{\"value\":2.5}")), token);
                await source.CompleteAsync(token);
                StreamingDeliveryBatch failed = (await source.ReadBatchAsync(token))!;
                FileStreamingSubscriptionStatus status = await source.GetStatusAsync(token);
                Assert.True(status.DeliveryAttemptsExhausted);
                FileStreamingDeadLetterReceipt quarantine = await source.MoveExhaustedBatchToDeadLetterAsync(
                    failed.DeliveryId, status.StateRevision, status.InFlightAttempt, "consumer unavailable", token);
                FileStreamingDeadLetterSummary summary = Assert.Single(await source.ListDeadLettersAsync(cancellationToken: token));
                firstClaim = await source.ReplayDeadLetterAsync(quarantine.DeadLetterSequence,
                    summary.DeliveryId, summary.DeadLetterRevision, token);
            }

            // 独立消费者保存原事件；窗口提交后的中断不完成原 DLQ，也不声称跨目录事务。
            await using (var replay = await FileStreamingSubscription.OpenAsync(
                replayPath, replayDefinition, cancellationToken: token))
            await using (var windows = await FileStreamingWindowAggregator.CreateAsync(
                replayWindowPath, replayWindows, replay.Checkpoint, cancellationToken: token))
            {
                await replay.PublishAsync(Assert.Single(firstClaim.Events), token);
                await replay.CompleteAsync(token);
                await windows.ApplyBatchAsync((await replay.ReadBatchAsync(token))!, token);
                Assert.Equal(-1, replay.Checkpoint.CommittedSequence);
                Assert.All((await windows.ReadNumericWindowsAsync(cancellationToken: token)).Windows,
                    window => Assert.Equal(1.5m, window.Sum));
            }

            await using (var source = await FileStreamingSubscription.OpenAsync(
                sourcePath, sourceDefinition, sourceOptions, cancellationToken: token))
            {
                FileStreamingDeadLetterSummary summary = Assert.Single(await source.ListDeadLettersAsync(cancellationToken: token));
                FileStreamingDeadLetterReplayBatch retry = await source.ReplayDeadLetterAsync(
                    summary.Sequence, summary.DeliveryId, summary.DeadLetterRevision, token);
                Assert.Equal(firstClaim.Summary.ReplayId, retry.Summary.ReplayId);
                Assert.Equal(2, retry.Summary.ReplayAttempt);
                StreamingEvent saved = Assert.Single(retry.Events);
                Assert.Equal(original.EventId, saved.EventId);
                Assert.Equal(original.Payload.ToArray(), saved.Payload.ToArray());
                Assert.Equal("meter-1", saved.Headers!["source"]);

                await using (var replay = await FileStreamingSubscription.OpenAsync(
                    replayPath, replayDefinition, cancellationToken: token))
                await using (var windows = await FileStreamingWindowAggregator.OpenAsync(
                    replayWindowPath, replayWindows, cancellationToken: token))
                {
                    // 已持久目标订阅直接重投，不再次向该目标发布原序号。
                    Assert.True(await windows.PumpOnceAsync(replay, token));
                    Assert.False(await windows.PumpOnceAsync(replay, token));
                    await windows.AdvanceWatermarkAsync(start.AddSeconds(90), token);
                }

                FileStreamingDeadLetterSummary completed = retry.Summary;
                await source.DeleteDeadLetterAsync(completed.Sequence, completed.DeliveryId,
                    completed.DeadLetterRevision, completed.ReplayId, completed.ReplayAttempt, token);
                Assert.Empty(await source.ListDeadLettersAsync(cancellationToken: token));
                Assert.Equal(0, source.Checkpoint.CommittedSequence);
                await using var tail = await FileStreamingWindowAggregator.CreateAsync(
                    tailWindowPath, tailWindows, source.Checkpoint, cancellationToken: token);
                Assert.True(await tail.PumpOnceAsync(source, token));
                Assert.False(await tail.PumpOnceAsync(source, token));
                await tail.AdvanceWatermarkAsync(start.AddSeconds(90), token);
            }

            await using var finalSource = await FileStreamingSubscription.OpenAsync(
                sourcePath, sourceDefinition, sourceOptions, cancellationToken: token);
            await using var finalReplay = await FileStreamingWindowAggregator.OpenAsync(
                replayWindowPath, replayWindows, cancellationToken: token);
            await using var finalTail = await FileStreamingWindowAggregator.OpenAsync(
                tailWindowPath, tailWindows, cancellationToken: token);
            StreamingWindowNumericBatch replayResult = await finalReplay.ReadNumericWindowsAsync(closedOnly: true, cancellationToken: token);
            Assert.Equal(2, replayResult.Windows.Count);
            Assert.Equal([start, start.AddSeconds(30)], replayResult.Windows.Select(static window => window.StartUtc));
            Assert.All(replayResult.Windows, window =>
            {
                Assert.Equal(1, window.Count);
                Assert.Equal(1.5m, window.Sum);
            });
            StreamingWindowNumericBatch tailResult = await finalTail.ReadNumericWindowsAsync(closedOnly: true, cancellationToken: token);
            Assert.Equal(2, tailResult.Windows.Count);
            Assert.All(tailResult.Windows, window => Assert.Equal(2.5m, window.Sum));
            Assert.Equal(0, finalSource.PendingEventCount);
            Assert.Equal(1, finalSource.Checkpoint.CommittedSequence);
            Assert.Empty(await finalSource.ListDeadLettersAsync(cancellationToken: token));
        }
        finally
        {
            string target = Path.GetFullPath(root);
            string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(target).StartsWith("sdb-sliding-dlq-journey-", StringComparison.Ordinal))
                throw new InvalidOperationException("组合恢复测试临时目录所有权不匹配。");
            Directory.Delete(target, recursive: true);
        }
    }

    private static StreamingWindowDefinition Sliding(StreamingSubscriptionDefinition subscription)
        => StreamingWindowDefinition.CreateSlidingNumeric(subscription.SubscriptionId, subscription.StreamName,
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), "value");
}
