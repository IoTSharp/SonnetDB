using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingSubscriptionDispatcherTests
{
    [Fact]
    public async Task RunAsync_WithHandlerFailures_RetriesStableIdentitySeriallyAndAcknowledgesOnlySuccess()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(1));
        await subscription.PublishAsync(Event(2));
        await subscription.CompleteAsync();
        var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options());
        var seen = new List<StreamingDeliveryBatch>();
        int active = 0;
        int maximumActive = 0;
        FileStreamingDispatcherResult result = await dispatcher.RunAsync(async (batch, cancellationToken) =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            try
            {
                seen.Add(batch);
                Assert.Equal(batch.Events[0].Sequence == 1 ? -1 : 1, subscription.Checkpoint.CommittedSequence);
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken);
                if (batch.Events[0].Sequence == 1 && batch.Attempt < 3)
                    throw new InvalidOperationException("处理失败，允许串行重投。");
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });

        Assert.Equal(FileStreamingDispatcherStopReason.Completed, result.StopReason);
        Assert.Equal(4, result.Deliveries);
        Assert.Equal(2, result.FailedDeliveries);
        Assert.Equal(2, result.AcknowledgedBatches);
        Assert.Equal(1, maximumActive);
        Assert.Equal([1, 2, 3], seen.Take(3).Select(static batch => batch.Attempt));
        Assert.Equal(seen[0].DeliveryId, seen[1].DeliveryId);
        Assert.Equal(seen[0].DeliveryId, seen[2].DeliveryId);
        Assert.Equal(seen[0].CandidateCheckpoint, seen[2].CandidateCheckpoint);
        Assert.Equal(seen[0].Events[0].Payload, seen[2].Events[0].Payload);
        Assert.NotEqual(seen[0].DeliveryId, seen[3].DeliveryId);
        Assert.Equal(2, subscription.Checkpoint.CommittedSequence);
        Assert.Equal(0, subscription.PendingEventCount);
    }

    [Fact]
    public async Task RunAsync_AtDeliveryBudget_PreservesUnacknowledgedIdentityAcrossReopen()
    {
        using var directory = new TemporaryDirectory();
        string deliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition()))
        {
            await subscription.PublishAsync(Event(1));
            var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options() with { MaxDeliveries = 1 });
            FileStreamingDispatcherResult result = await dispatcher.RunAsync(Fail);
            Assert.Equal(FileStreamingDispatcherStopReason.DeliveryLimitReached, result.StopReason);
            Assert.Equal(1, result.Deliveries);
            Assert.Equal(1, result.FailedDeliveries);
            Assert.Equal(0, result.AcknowledgedBatches);
            deliveryId = Assert.IsType<string>(result.LastDeliveryId);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        StreamingDeliveryBatch retry = Assert.IsType<StreamingDeliveryBatch>(await reopened.ReadBatchAsync());
        Assert.Equal(deliveryId, retry.DeliveryId);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(1, Assert.Single(retry.Events).Sequence);
    }

    [Fact]
    public async Task RunAsync_AtBatchBudget_ConfirmsPrefixAndPreservesTailAcrossReopen()
    {
        using var directory = new TemporaryDirectory();
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition()))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            await subscription.CompleteAsync();
            var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options() with { MaxBatches = 1 });
            FileStreamingDispatcherResult result = await dispatcher.RunAsync(Succeed);
            Assert.Equal(FileStreamingDispatcherStopReason.BatchLimitReached, result.StopReason);
            Assert.Equal(1, result.AcknowledgedBatches);
            Assert.Equal(1, result.Deliveries);
            Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(2, Assert.Single((await reopened.ReadBatchAsync())!.Events).Sequence);
    }

    [Fact]
    public async Task RunAsync_WhenAttemptsExhausted_RetainsOriginalBatchAndStopsAfterReopen()
    {
        using var directory = new TemporaryDirectory();
        var storageOptions = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 2 };
        string deliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), storageOptions))
        {
            await subscription.PublishAsync(Event(1));
            var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options());
            FileStreamingDispatcherResult result = await dispatcher.RunAsync(Fail);
            Assert.Equal(FileStreamingDispatcherStopReason.DeliveryAttemptsExhausted, result.StopReason);
            Assert.Equal(2, result.Deliveries);
            Assert.Equal(2, result.FailedDeliveries);
            deliveryId = Assert.IsType<string>(result.LastDeliveryId);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), storageOptions);
        int calls = 0;
        var restoredDispatcher = new FileStreamingSubscriptionDispatcher(reopened, Options());
        FileStreamingDispatcherResult restored = await restoredDispatcher.RunAsync((_, _) =>
        {
            calls++;
            return ValueTask.CompletedTask;
        });
        Assert.Equal(FileStreamingDispatcherStopReason.DeliveryAttemptsExhausted, restored.StopReason);
        Assert.Equal(0, calls);
        Assert.Equal(deliveryId, restored.LastDeliveryId);
        Assert.Equal(1, reopened.PendingEventCount);
        Assert.Equal(-1, reopened.Checkpoint.CommittedSequence);
        Assert.Empty(await reopened.ListDeadLettersAsync());
    }

    [Fact]
    public async Task RunAsync_WithExplicitDeadLetterPolicy_PersistsExhaustedBatchAndContinuesTail()
    {
        using var directory = new TemporaryDirectory();
        var storageOptions = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 2 };
        string failedDeliveryId;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), storageOptions))
        {
            await subscription.PublishAsync(Event(1));
            await subscription.PublishAsync(Event(2));
            await subscription.CompleteAsync();
            var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options() with
            {
                ExhaustedBatchPolicy = FileStreamingExhaustedBatchPolicy.DeadLetter,
                DeadLetterReason = "宿主显式授权的重试耗尽隔离。",
            });
            FileStreamingDispatcherResult result = await dispatcher.RunAsync((batch, cancellationToken) =>
                batch.Events[0].Sequence == 1 ? Fail(batch, cancellationToken) : Succeed(batch, cancellationToken));
            Assert.Equal(FileStreamingDispatcherStopReason.Completed, result.StopReason);
            Assert.Equal(3, result.Deliveries);
            Assert.Equal(1, result.AcknowledgedBatches);
            Assert.Equal(1, result.DeadLetteredBatches);
            FileStreamingDeadLetterSummary summary = Assert.Single(await subscription.ListDeadLettersAsync());
            failedDeliveryId = summary.DeliveryId;
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), storageOptions);
        FileStreamingDeadLetterBatch deadLetter = Assert.IsType<FileStreamingDeadLetterBatch>(
            await reopened.ReadDeadLetterAsync(1));
        Assert.Equal(failedDeliveryId, deadLetter.Summary.DeliveryId);
        Assert.Equal(2, deadLetter.Summary.Attempt);
        Assert.Equal(Event(1).Payload, Assert.Single(deadLetter.Events).Payload);
        Assert.Equal(2, reopened.Checkpoint.CommittedSequence);
        Assert.Equal(0, reopened.PendingEventCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WithNonCooperativeHandlerTimeout_HoldsSharedLeaseAndNeverAcknowledgesLateSuccess(bool useRunBudget)
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(1));
        await subscription.CompleteAsync();
        var options = Options() with
        {
            MaxDuration = useRunBudget ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(3),
            HandlerTimeout = useRunBudget ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(150),
        };
        var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, options);
        var anotherDispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        Task<FileStreamingDispatcherResult> run = dispatcher.RunAsync(async (_, token) =>
        {
            handlerToken = token;
            entered.TrySetResult();
            await release.Task;
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            FileStreamingDispatcherResult timedOut = await run.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(useRunBudget ? FileStreamingDispatcherStopReason.TimeBudgetReached
                : FileStreamingDispatcherStopReason.HandlerTimedOut, timedOut.StopReason);
            Assert.True(handlerToken.IsCancellationRequested);
            Assert.False(dispatcher.HandlerCompletion.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunAsync(Succeed).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => anotherDispatcher.RunAsync(Succeed).AsTask());
            Assert.Equal(1, (await subscription.GetStatusAsync()).InFlightAttempt);
            release.TrySetResult();
            await dispatcher.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            FileStreamingDispatcherResult recovered = await anotherDispatcher.RunAsync(Succeed);
            Assert.Equal(FileStreamingDispatcherStopReason.Completed, recovered.StopReason);
            Assert.Equal(timedOut.LastDeliveryId, recovered.LastDeliveryId);
            Assert.Equal(1, subscription.Checkpoint.CommittedSequence);
        }
        finally
        {
            release.TrySetResult();
            await dispatcher.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task RunAsync_WhenCancelledDuringNonCooperativeHandler_RetainsLeaseUntilFailureSettles()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(1));
        var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FileStreamingDispatcherResult> run = dispatcher.RunAsync(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("取消后才返回的处理异常必须被观察。");
        }, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.RunAsync(Succeed).AsTask());
            release.TrySetResult();
            await dispatcher.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            Assert.Equal(1, subscription.PendingEventCount);
            Assert.Equal(1, (await subscription.GetStatusAsync()).InFlightAttempt);
        }
        finally
        {
            release.TrySetResult();
            await dispatcher.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task RunAsync_WhilePaused_StopsWithoutReadingAndDrainsAfterResume()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        await subscription.PublishAsync(Event(1));
        await subscription.PauseConsumptionAsync(subscription.StateRevision);
        await subscription.CompleteAsync();
        var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options());
        FileStreamingDispatcherResult paused = await dispatcher.RunAsync(Fail);
        Assert.Equal(FileStreamingDispatcherStopReason.Paused, paused.StopReason);
        Assert.Equal(0, paused.Deliveries);
        Assert.Equal(0, (await subscription.GetStatusAsync()).InFlightAttempt);
        await subscription.ResumeConsumptionAsync(subscription.StateRevision);
        FileStreamingDispatcherResult completed = await dispatcher.RunAsync(Succeed);
        Assert.Equal(FileStreamingDispatcherStopReason.Completed, completed.StopReason);
        Assert.Equal(1, completed.AcknowledgedBatches);
    }

    [Fact]
    public async Task RunAsync_WhenEmptyAndPublishingOpen_StopsAtWallClockBudgetWithoutCreatingBatch()
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options() with
        {
            MaxDuration = TimeSpan.FromMilliseconds(100),
        });
        FileStreamingDispatcherResult result = await dispatcher.RunAsync(Fail).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(FileStreamingDispatcherStopReason.TimeBudgetReached, result.StopReason);
        Assert.Equal(0, result.Deliveries);
        Assert.Equal(0, subscription.PendingEventCount);
        Assert.Null((await subscription.GetStatusAsync()).InFlightDeliveryId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenConfirmationCannotCommit_PropagatesStorageFailureAndPreservesRecoverableBatch(bool budgetExpires)
    {
        using var directory = new TemporaryDirectory();
        var storageOptions = new FileStreamingSubscriptionOptions
        {
            OperationTimeoutMilliseconds = budgetExpires ? 5000 : 100,
        };
        string? deliveryId = null;
        await using (var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), storageOptions))
        {
            await subscription.PublishAsync(Event(1));
            string lockPath = Directory.GetFiles(System.IO.Path.Combine(directory.Path, "checkpoints"), "*.lock").Single();
            using var heldLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var dispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options() with
            {
                MaxDuration = budgetExpires ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(3),
            });
            Task<FileStreamingDispatcherResult> run = dispatcher.RunAsync((batch, cancellationToken) =>
            {
                deliveryId = batch.DeliveryId;
                return Succeed(batch, cancellationToken);
            }).AsTask();
            if (budgetExpires)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            else
                await Assert.ThrowsAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.NotNull(deliveryId);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var reopened = await FileStreamingSubscription.OpenAsync(directory.Path, Definition(), storageOptions);
        Assert.Equal(deliveryId, (await reopened.ReadBatchAsync())!.DeliveryId);
        Assert.Equal(1, reopened.PendingEventCount);
    }

    [Theory]
    [InlineData("batch")]
    [InlineData("delivery")]
    [InlineData("duration")]
    [InlineData("handler")]
    [InlineData("backoff")]
    [InlineData("policy")]
    [InlineData("reason")]
    public async Task Constructor_WithInvalidBoundary_RejectsBeforeConsuming(string invalid)
    {
        using var directory = new TemporaryDirectory();
        await using var subscription = await FileStreamingSubscription.OpenAsync(directory.Path, Definition());
        FileStreamingDispatcherOptions options = invalid switch
        {
            "batch" => Options() with { MaxBatches = 0 },
            "delivery" => Options() with { MaxDeliveries = 0 },
            "duration" => Options() with { MaxDuration = Timeout.InfiniteTimeSpan },
            "handler" => Options() with { HandlerTimeout = TimeSpan.Zero },
            "backoff" => Options() with { RetryDelay = TimeSpan.Zero },
            "policy" => Options() with { ExhaustedBatchPolicy = (FileStreamingExhaustedBatchPolicy)42 },
            _ => Options() with { ExhaustedBatchPolicy = FileStreamingExhaustedBatchPolicy.DeadLetter },
        };
        Assert.ThrowsAny<ArgumentException>(() => new FileStreamingSubscriptionDispatcher(subscription, options));
        Assert.Equal(0, subscription.StateRevision);
    }

    private static ValueTask Succeed(StreamingDeliveryBatch _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static ValueTask Fail(StreamingDeliveryBatch _, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException(new InvalidOperationException("测试处理函数失败。"));
    }

    private static StreamingSubscriptionDefinition Definition()
        => StreamingSubscriptionDefinition.Create("dispatcher-test", "orders", batchSize: 1, capacity: 4);

    private static FileStreamingDispatcherOptions Options() => new()
    {
        MaxBatches = 4,
        MaxDeliveries = 10,
        MaxDuration = TimeSpan.FromSeconds(3),
        HandlerTimeout = TimeSpan.FromSeconds(2),
        RetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private static StreamingEvent Event(long sequence)
        => new("event-" + sequence, sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), "payload"u8.ToArray());

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _ownedPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "sonnetdb-dispatcher-" + Guid.NewGuid().ToString("N")));

        public string Path => _ownedPath;

        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!string.Equals(resolved, _ownedPath, StringComparison.Ordinal))
                throw new InvalidOperationException("临时目录不属于当前测试。");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
