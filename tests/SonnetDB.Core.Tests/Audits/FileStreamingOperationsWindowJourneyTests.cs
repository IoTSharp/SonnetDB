using System.Text;
using SonnetDB.Streaming;

namespace SonnetDB.Core.Tests.Audits;

/// <summary>持久数值窗口与订阅运维控制的真实组合恢复合同。</summary>
public sealed class FileStreamingOperationsWindowJourneyTests
{
    /// <summary>窗口先提交但未确认时，持久暂停与受控重试不会重复数值聚合或丢失尾批次。</summary>
    [Fact]
    public async Task RecoverNumericWindow_AfterPauseAndExhaustedDeliveryReset_ReconcilesValuesAndCheckpoint()
    {
        string directory = Directory.CreateTempSubdirectory("sonnetdb-operations-window-").FullName;
        string subscriptionPath = Path.Combine(directory, "subscription");
        string windowPath = Path.Combine(directory, "windows.json");
        var definition = StreamingSubscriptionDefinition.Create("numeric-journey", "values", batchSize: 2, capacity: 8);
        var options = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 1 };
        var windowDefinition = StreamingWindowDefinition.CreateNumeric(
            definition.SubscriptionId, definition.StreamName, TimeSpan.FromMinutes(1), "value");
        DateTimeOffset start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        try
        {
            string deliveryId;
            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token))
            await using (var windows = await FileStreamingWindowAggregator.CreateAsync(
                windowPath, windowDefinition, subscription.Checkpoint, cancellationToken: token))
            {
                await subscription.PublishAsync(new StreamingEvent("first", 0, start,
                    Encoding.UTF8.GetBytes("{\"value\":-1.25}")), token);
                await subscription.PublishAsync(new StreamingEvent("second", 1, start.AddSeconds(1),
                    Encoding.UTF8.GetBytes("{\"value\":2.50}")), token);
                await subscription.PublishAsync(new StreamingEvent("third", 2, start.AddSeconds(2),
                    Encoding.UTF8.GetBytes("{\"value\":4}")), token);
                await subscription.CompleteAsync(token);
                StreamingDeliveryBatch first = (await subscription.ReadBatchAsync(token))!;
                await windows.ApplyBatchAsync(first, token);
                deliveryId = first.DeliveryId;
                await subscription.PauseConsumptionAsync(subscription.StateRevision, token);
                Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            }

            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token))
            {
                FileStreamingSubscriptionStatus paused = await subscription.GetStatusAsync(token);
                Assert.True(paused.ConsumptionPaused);
                Assert.Equal(deliveryId, paused.InFlightDeliveryId);
                await subscription.ResumeConsumptionAsync(paused.StateRevision, token);
                await Assert.ThrowsAsync<FileStreamingDeliveryAttemptLimitException>(() =>
                    subscription.ReadBatchAsync(token).AsTask());
                FileStreamingSubscriptionStatus exhausted = await subscription.GetStatusAsync(token);
                await subscription.ResetDeliveryAttemptsAsync(
                    deliveryId, exhausted.StateRevision, exhausted.InFlightAttempt, token);
                await subscription.PauseConsumptionAsync(subscription.StateRevision, token);
            }

            await using (var subscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token))
            await using (var windows = await FileStreamingWindowAggregator.OpenAsync(
                windowPath, windowDefinition, cancellationToken: token))
            {
                Assert.True(subscription.ConsumptionPaused);
                await subscription.ResumeConsumptionAsync(subscription.StateRevision, token);
                StreamingDeliveryBatch retry = (await subscription.ReadBatchAsync(token))!;
                Assert.Equal(deliveryId, retry.DeliveryId);
                Assert.Equal(1, retry.Attempt);
                Assert.Equal(StreamingDeliveryStatus.Redelivered, retry.Status);
                await windows.ApplyBatchAsync(retry, token);
                await subscription.AcknowledgeAsync(retry.DeliveryId, token);
                Assert.True(await windows.PumpOnceAsync(subscription, token));
                Assert.False(await windows.PumpOnceAsync(subscription, token));
                await windows.AdvanceWatermarkAsync(start.AddMinutes(1), token);
            }

            await using var finalSubscription = await FileStreamingSubscription.OpenAsync(
                subscriptionPath, definition, options, cancellationToken: token);
            await using var finalWindows = await FileStreamingWindowAggregator.OpenAsync(
                windowPath, windowDefinition, cancellationToken: token);
            StreamingWindowNumericBatch page = await finalWindows.ReadNumericWindowsAsync(
                closedOnly: true, cancellationToken: token);
            StreamingWindowNumeric window = Assert.Single(page.Windows);
            Assert.Equal(3, window.Count);
            Assert.Equal(5.25m, window.Sum);
            Assert.Equal(-1.25m, window.Min);
            Assert.Equal(4m, window.Max);
            Assert.Equal(1.75m, window.Average);
            Assert.True(window.IsClosed);
            Assert.False(page.HasMore);
            Assert.Equal(0, finalSubscription.PendingEventCount);
            Assert.Equal(2, finalSubscription.Checkpoint.CommittedSequence);
            Assert.Equal(finalSubscription.Checkpoint.CommittedSequence, page.State.AppliedSequence);
            Assert.Equal(finalSubscription.Checkpoint.Revision, page.State.AppliedRevision);
        }
        finally
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(directory), parent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(directory).StartsWith("sonnetdb-operations-window-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试临时目录所有权不匹配。");
            Directory.Delete(directory, recursive: true);
        }
    }
}
