using System.Text;
using SonnetDB.Streaming;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>分组数值窗口、持久隔离和显式恢复基线的本地组合合同。</summary>
public sealed class FileStreamingGroupedDeadLetterJourneyTests
{
    /// <summary>窗口提交后未确认的批次重开重投时，不重复分组累计，分页保留键大小写。</summary>
    [Fact]
    public async Task RecoverGroupedNumericWindow_AfterCommitBeforeAck_ReconcilesGroupsAndCheckpoint()
    {
        string root = Directory.CreateTempSubdirectory("sdb-grouped-journey-").FullName;
        string subscriptionPath = Path.Combine(root, "subscription");
        string windowPath = Path.Combine(root, "windows.json");
        var definition = StreamingSubscriptionDefinition.Create("grouped", "readings", batchSize: 2, capacity: 8);
        var windowDefinition = StreamingWindowDefinition.CreateGroupedNumeric(
            definition.SubscriptionId, definition.StreamName, TimeSpan.FromMinutes(1), "device", "value");
        DateTimeOffset start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        try
        {
            string deliveryId;
            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, cancellationToken: token))
            await using (var windows = await FileStreamingWindowAggregator.CreateAsync(
                windowPath, windowDefinition, subscription.Checkpoint, cancellationToken: token))
            {
                await subscription.PublishAsync(Event("first", 0, start, "{\"device\":\"Pump\",\"value\":1.1}"), token);
                await subscription.PublishAsync(Event("second", 1, start.AddSeconds(1), "{\"device\":\"pump\",\"value\":2.2}"), token);
                await subscription.PublishAsync(Event("third", 2, start.AddSeconds(2), "{\"device\":\"Pump\",\"value\":-0.1}"), token);
                await subscription.CompleteAsync(token);
                StreamingDeliveryBatch batch = (await subscription.ReadBatchAsync(token))!;
                deliveryId = batch.DeliveryId;
                await windows.ApplyBatchAsync(batch, token);
                Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            }

            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, cancellationToken: token))
            await using (var windows = await FileStreamingWindowAggregator.OpenAsync(
                windowPath, windowDefinition, cancellationToken: token))
            {
                StreamingDeliveryBatch retry = (await subscription.ReadBatchAsync(token))!;
                Assert.Equal(deliveryId, retry.DeliveryId);
                Assert.Equal(StreamingDeliveryStatus.Redelivered, retry.Status);
                await windows.ApplyBatchAsync(retry, token);
                await subscription.AcknowledgeAsync(retry.DeliveryId, token);
                Assert.True(await windows.PumpOnceAsync(subscription, token));
                Assert.False(await windows.PumpOnceAsync(subscription, token));
                await windows.AdvanceWatermarkAsync(start.AddMinutes(1), token);
            }

            await using var finalSubscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, cancellationToken: token);
            await using var finalWindows = await FileStreamingWindowAggregator.OpenAsync(
                windowPath, windowDefinition, cancellationToken: token);
            StreamingGroupedWindowBatch first = await finalWindows.ReadGroupedWindowsAsync(
                maxWindows: 1, closedOnly: true, cancellationToken: token);
            StreamingGroupedWindow pump = Assert.Single(first.Windows);
            Assert.Equal("Pump", pump.GroupKey);
            Assert.Equal(2, pump.Count);
            Assert.Equal(1m, pump.Sum);
            Assert.Equal(-0.1m, pump.Min);
            Assert.Equal(1.1m, pump.Max);
            Assert.Equal(0.5m, pump.Average);
            Assert.True(pump.IsClosed);
            Assert.True(first.HasMore);
            StreamingGroupedWindowBatch last = await finalWindows.ReadGroupedWindowsAsync(
                maxWindows: 1, after: first.NextCursor, closedOnly: true, cancellationToken: token);
            StreamingGroupedWindow lower = Assert.Single(last.Windows);
            Assert.Equal("pump", lower.GroupKey);
            Assert.Equal(1, lower.Count);
            Assert.Equal(2.2m, lower.Sum);
            Assert.False(last.HasMore);
            Assert.Equal(0, finalSubscription.PendingEventCount);
            Assert.Equal(finalSubscription.Checkpoint.CommittedSequence, last.State.AppliedSequence);
            Assert.Equal(finalSubscription.Checkpoint.Revision, last.State.AppliedRevision);
        }
        finally
        {
            DeleteOwnedRoot(root);
        }
    }

    /// <summary>坏分组键整批拒绝后，隔离保留原事件，旧窗口拒绝跳位点，新基线可处理正常尾批次。</summary>
    [Fact]
    public async Task QuarantinePoisonGroup_AfterRejectedBatch_ReopensAndRequiresExplicitWindowBaseline()
    {
        string root = Directory.CreateTempSubdirectory("sdb-grouped-journey-").FullName;
        string subscriptionPath = Path.Combine(root, "subscription");
        string rejectedPath = Path.Combine(root, "rejected.json");
        string resumedPath = Path.Combine(root, "resumed.json");
        var definition = StreamingSubscriptionDefinition.Create("quarantine", "readings", batchSize: 1, capacity: 4);
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 1 };
        var windowDefinition = StreamingWindowDefinition.CreateGroupedNumeric(
            definition.SubscriptionId, definition.StreamName, TimeSpan.FromMinutes(1), "device", "value");
        DateTimeOffset start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        StreamingEvent poison = Event("poison", 0, start, "{\"device\":42,\"value\":1}") with
        {
            Headers = new Dictionary<string, string> { ["source"] = "meter-1" },
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        try
        {
            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token))
            await using (var rejected = await FileStreamingWindowAggregator.CreateAsync(
                rejectedPath, windowDefinition, subscription.Checkpoint, cancellationToken: token))
            {
                await subscription.PublishAsync(poison, token);
                await subscription.PublishAsync(Event("good-1", 1, start.AddSeconds(1), "{\"device\":\"B\",\"value\":2.5}"), token);
                await subscription.PublishAsync(Event("good-2", 2, start.AddSeconds(2), "{\"device\":\"B\",\"value\":-0.5}"), token);
                await subscription.CompleteAsync(token);
                await Assert.ThrowsAsync<InvalidDataException>(() => rejected.PumpOnceAsync(subscription, token).AsTask());
                Assert.Empty((await rejected.ReadGroupedWindowsAsync(cancellationToken: token)).Windows);
                FileStreamingSubscriptionStatus exhausted = await subscription.GetStatusAsync(token);
                Assert.True(exhausted.DeliveryAttemptsExhausted);
                FileStreamingDeadLetterReceipt receipt = await subscription.MoveExhaustedBatchToDeadLetterAsync(
                    exhausted.InFlightDeliveryId!, exhausted.StateRevision, exhausted.InFlightAttempt,
                    "device must be a JSON string", token);
                Assert.Equal(0, receipt.Checkpoint.CommittedSequence);
                Assert.Equal(2, subscription.PendingEventCount);
                await subscription.PauseConsumptionAsync(subscription.StateRevision, token);
            }

            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token))
            await using (var rejected = await FileStreamingWindowAggregator.OpenAsync(
                rejectedPath, windowDefinition, cancellationToken: token))
            {
                FileStreamingDeadLetterSummary summary = Assert.Single(await subscription.ListDeadLettersAsync(cancellationToken: token));
                FileStreamingDeadLetterBatch saved = (await subscription.ReadDeadLetterAsync(summary.Sequence, token))!;
                StreamingEvent original = Assert.Single(saved.Events);
                Assert.Equal(poison.EventId, original.EventId);
                Assert.Equal(poison.Payload.ToArray(), original.Payload.ToArray());
                Assert.Equal("meter-1", original.Headers!["source"]);
                Assert.Equal("device must be a JSON string", summary.Reason);
                Assert.True(subscription.ConsumptionPaused);
                await Assert.ThrowsAsync<InvalidDataException>(() => rejected.PumpOnceAsync(subscription, token).AsTask());
                await using var resumed = await FileStreamingWindowAggregator.CreateAsync(
                    resumedPath, windowDefinition, subscription.Checkpoint, cancellationToken: token);
                await subscription.ResumeConsumptionAsync(subscription.StateRevision, token);
                Assert.True(await resumed.PumpOnceAsync(subscription, token));
                Assert.True(await resumed.PumpOnceAsync(subscription, token));
                Assert.False(await resumed.PumpOnceAsync(subscription, token));
                await resumed.AdvanceWatermarkAsync(start.AddMinutes(1), token);
            }

            await using var finalSubscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token);
            await using var finalWindows = await FileStreamingWindowAggregator.OpenAsync(
                resumedPath, windowDefinition, cancellationToken: token);
            StreamingGroupedWindowBatch page = await finalWindows.ReadGroupedWindowsAsync(
                closedOnly: true, cancellationToken: token);
            StreamingGroupedWindow value = Assert.Single(page.Windows);
            Assert.Equal("B", value.GroupKey);
            Assert.Equal(2, value.Count);
            Assert.Equal(2m, value.Sum);
            Assert.Equal(1m, value.Average);
            Assert.Equal(0, finalSubscription.PendingEventCount);
            Assert.Equal(2, page.State.AppliedSequence);
            Assert.Equal(finalSubscription.Checkpoint.Revision, page.State.AppliedRevision);
            Assert.Single(await finalSubscription.ListDeadLettersAsync(cancellationToken: token));
        }
        finally
        {
            DeleteOwnedRoot(root);
        }
    }

    private static StreamingEvent Event(string id, long sequence, DateTimeOffset time, string json)
        => new(id, sequence, time, Encoding.UTF8.GetBytes(json));

    private static void DeleteOwnedRoot(string root)
    {
        string target = Path.GetFullPath(root);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(target), temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("sdb-grouped-journey-", StringComparison.Ordinal))
            throw new InvalidOperationException("组合测试临时目录所有权不匹配。");
        Directory.Delete(target, recursive: true);
    }
}
