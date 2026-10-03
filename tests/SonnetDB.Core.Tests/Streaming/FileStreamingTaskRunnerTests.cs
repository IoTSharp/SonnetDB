using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class FileStreamingTaskRunnerTests
{
    [Fact]
    public async Task RunAsync_AfterCatalogAndSubscriptionReopen_RunsRegisteredPersistentTask()
    {
        using var directory = new TemporaryDirectory();
        StreamingTaskDefinition definition = Definition();
        await using (var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path))
        {
            await catalog.RegisterAsync(definition);
            await using var subscription = await catalog.OpenSubscriptionAsync(definition.TaskId);
            await subscription.PublishAsync(Event(1));
            await subscription.CompleteAsync();
        }

        await using var reopenedCatalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await using var reopened = await reopenedCatalog.OpenSubscriptionAsync(definition.TaskId);
        var runner = new FileStreamingTaskRunner(reopenedCatalog, definition.TaskId, reopened, Options());
        FileStreamingDispatcherResult result = await runner.RunAsync(Succeed);
        Assert.Equal(FileStreamingDispatcherStopReason.Completed, result.StopReason);
        Assert.Equal(1, result.AcknowledgedBatches);
        Assert.Equal(1, reopened.Checkpoint.CommittedSequence);
        Assert.True(runner.HandlerCompletion.IsCompleted);
        Assert.NotNull(await reopenedCatalog.GetAsync(definition.TaskId));
        Assert.Equal(0, (await reopened.GetStatusAsync()).PendingEventCount);
    }

    [Fact]
    public async Task RunAsync_WhenPausedThenResumed_UsesPersistentConsumptionState()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await catalog.RegisterAsync(Definition());
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        await subscription.PublishAsync(Event(1));
        await subscription.CompleteAsync();
        await subscription.PauseConsumptionAsync(subscription.StateRevision);
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        int calls = 0;
        ValueTask Handle(StreamingDeliveryBatch batch, CancellationToken cancellationToken)
        {
            calls++;
            return ValueTask.CompletedTask;
        }

        Assert.Equal(FileStreamingDispatcherStopReason.Paused, (await runner.RunAsync(Handle)).StopReason);
        Assert.Equal(0, calls);
        await subscription.ResumeConsumptionAsync(subscription.StateRevision);
        Assert.Equal(FileStreamingDispatcherStopReason.Completed, (await runner.RunAsync(Handle)).StopReason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_AfterWindowCommitWithoutAck_DeduplicatesAfterTaskReopen()
    {
        using var directory = new TemporaryDirectory();
        StreamingWindowDefinition windowDefinition = StreamingWindowDefinition.Create(
            "task", "orders", TimeSpan.FromSeconds(10));
        string windowPath = Path.Combine(directory.Path, "window.json");
        string deliveryId;
        await using (var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path))
        {
            await catalog.RegisterAsync(Definition());
            await using var subscription = await catalog.OpenSubscriptionAsync("task");
            await using var window = await FileStreamingWindowAggregator.CreateAsync(windowPath, windowDefinition);
            await subscription.PublishAsync(Event(1));
            await subscription.CompleteAsync();
            var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options() with { MaxDeliveries = 1 });
            FileStreamingDispatcherResult failed = await runner.RunAsync(async (batch, cancellationToken) =>
            {
                await window.ApplyBatchAsync(batch, cancellationToken);
                throw new InvalidOperationException("窗口已提交，业务回调未成功，订阅不能确认。");
            });
            deliveryId = Assert.IsType<string>(failed.LastDeliveryId);
            Assert.Equal(1, failed.FailedDeliveries);
            Assert.Equal(0, failed.AcknowledgedBatches);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        }

        await using var reopenedCatalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await using var reopened = await reopenedCatalog.OpenSubscriptionAsync("task");
        await using var reopenedWindow = await FileStreamingWindowAggregator.OpenAsync(windowPath, windowDefinition);
        var recoveredRunner = new FileStreamingTaskRunner(reopenedCatalog, "task", reopened, Options());
        FileStreamingDispatcherResult recovered = await recoveredRunner.RunAsync(async (batch, cancellationToken) =>
        {
            Assert.Equal(deliveryId, batch.DeliveryId);
            Assert.Equal(2, batch.Attempt);
            await reopenedWindow.ApplyBatchAsync(batch, cancellationToken);
        });
        Assert.Equal(1, recovered.AcknowledgedBatches);
        Assert.Equal(1, Assert.Single((await reopenedWindow.ReadWindowsAsync()).Windows).Count);
        Assert.Equal(1, reopened.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task RunAsync_WithFailureThenSuccess_RetriesStableDeliveryBeforeAcknowledging()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await catalog.RegisterAsync(Definition());
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        await subscription.PublishAsync(Event(1));
        await subscription.CompleteAsync();
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        var batches = new List<StreamingDeliveryBatch>();
        FileStreamingDispatcherResult result = await runner.RunAsync((batch, _) =>
        {
            batches.Add(batch);
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            return batch.Attempt == 1 ? ValueTask.FromException(new InvalidOperationException("retry")) : ValueTask.CompletedTask;
        });
        Assert.Equal(2, result.Deliveries);
        Assert.Equal(1, result.FailedDeliveries);
        Assert.Equal(1, result.AcknowledgedBatches);
        Assert.Equal(batches[0].DeliveryId, batches[1].DeliveryId);
    }

    [Fact]
    public async Task RunAsync_WithExhaustedAttempts_RetainsBatchWithoutImplicitDeadLetter()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await catalog.RegisterAsync(Definition() with { SubscriptionOptions = new FileStreamingSubscriptionOptions { MaxDeliveryAttempts = 1 } });
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        await subscription.PublishAsync(Event(1));
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        FileStreamingDispatcherResult result = await runner.RunAsync((_, _) => ValueTask.FromException(new InvalidOperationException("failed")));
        Assert.Equal(FileStreamingDispatcherStopReason.DeliveryAttemptsExhausted, result.StopReason);
        Assert.Equal(0, result.DeadLetteredBatches);
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Empty(await subscription.ListDeadLettersAsync());
    }

    [Fact]
    public async Task RunAsync_WithMissingTask_FailsBeforeCallingHandlerOrCreatingFiles()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await using var subscription = await FileStreamingSubscription.OpenAsync(
            Path.Combine(directory.Path, "actual"), Definition().SubscriptionDefinition);
        var runner = new FileStreamingTaskRunner(catalog, "missing", subscription, Options());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => runner.RunAsync(Succeed).AsTask());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "missing")));
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
    }

    [Fact]
    public async Task RunAsync_WhenSubscriptionStateDeleted_FailsWithoutCreatingNewBaseline()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await catalog.RegisterAsync(Definition());
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        string statePath = Path.Combine(subscription.DirectoryPath, "subscription.json");
        File.Delete(statePath);
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(Succeed).AsTask());
        Assert.False(File.Exists(statePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WithWrongPathOrDefinition_FailsBeforeConsuming(bool changeDefinition)
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        StreamingTaskDefinition definition = Definition();
        await catalog.RegisterAsync(definition);
        string path = Path.Combine(directory.Path, changeDefinition ? "subscription" : "other");
        StreamingSubscriptionDefinition subscriptionDefinition = changeDefinition
            ? definition.SubscriptionDefinition with { StreamName = "other-stream" }
            : definition.SubscriptionDefinition;
        await using var subscription = await FileStreamingSubscription.OpenAsync(path, subscriptionDefinition);
        await subscription.PublishAsync(Event(1));
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(Succeed).AsTask());
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WhenCatalogChangesDuringHandler_UsesSnapshotAndRejectsNextRun(bool remove)
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        StreamingTaskDefinition definition = Definition();
        await catalog.RegisterAsync(definition);
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        await subscription.PublishAsync(Event(1));
        await subscription.CompleteAsync();
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        FileStreamingDispatcherResult result = await runner.RunAsync(async (_, token) =>
        {
            if (remove)
                await catalog.RemoveAsync("task", catalog.Revision, token);
            else
                await catalog.UpdateAsync(definition with { DirectoryName = "new-path" }, catalog.Revision, token);
        });
        Assert.Equal(1, result.AcknowledgedBatches);
        if (remove)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => runner.RunAsync(Succeed).AsTask());
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(Succeed).AsTask());
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "new-path")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_WithNonCooperativeTimeoutOrCancellation_HoldsObjectAndFileLeasesUntilHandlerSettles(bool cancel)
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await catalog.RegisterAsync(Definition());
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        await subscription.PublishAsync(Event(1));
        await subscription.CompleteAsync();
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options() with
        {
            HandlerTimeout = cancel ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(150),
        });
        var otherRunner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        var directDispatcher = new FileStreamingSubscriptionDispatcher(subscription, Options());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<FileStreamingDispatcherResult> run = runner.RunAsync(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        }, cancellation.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                Assert.Equal(FileStreamingDispatcherStopReason.HandlerTimedOut,
                    (await run.WaitAsync(TimeSpan.FromSeconds(2))).StopReason);
            }
            Assert.False(runner.HandlerCompletion.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => otherRunner.RunAsync(Succeed).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => directDispatcher.RunAsync(Succeed).AsTask());
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.OpenSubscriptionAsync("task").AsTask());
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            release.TrySetResult();
            await runner.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
            Assert.Equal(1, (await otherRunner.RunAsync(Succeed)).AcknowledgedBatches);
        }
        finally
        {
            release.TrySetResult();
            await runner.HandlerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task RunAsync_WhenCancelledBeforePreflight_LeavesTaskAndSubscriptionUntouched()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        await catalog.RegisterAsync(Definition());
        await using var subscription = await catalog.OpenSubscriptionAsync("task");
        await subscription.PublishAsync(Event(1));
        var runner = new FileStreamingTaskRunner(catalog, "task", subscription, Options());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Succeed, cancellation.Token).AsTask());
        Assert.Equal(1, subscription.PendingEventCount);
        Assert.Equal(-1, subscription.Checkpoint.CommittedSequence);
        Assert.True(runner.HandlerCompletion.IsCompleted);
    }

    private static StreamingTaskDefinition Definition()
        => StreamingTaskDefinition.Create("task", "subscription",
            StreamingSubscriptionDefinition.Create("task", "orders", batchSize: 1, capacity: 8));

    private static FileStreamingDispatcherOptions Options()
        => new()
        {
            MaxBatches = 4,
            MaxDeliveries = 8,
            MaxDuration = TimeSpan.FromSeconds(5),
            HandlerTimeout = TimeSpan.FromSeconds(2),
            RetryDelay = TimeSpan.FromMilliseconds(1),
        };

    private static StreamingEvent Event(long sequence)
        => new($"event-{sequence}", sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), [1, 2, 3]);

    private static ValueTask Succeed(StreamingDeliveryBatch batch, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sonnetdb-task-runner-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            string absolutePath = System.IO.Path.GetFullPath(Path);
            string tempRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()))
                + System.IO.Path.DirectorySeparatorChar;
            if (!absolutePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(absolutePath).StartsWith("sonnetdb-task-runner-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试目录不属于任务运行器测试。");
            Directory.Delete(absolutePath, recursive: true);
        }
    }
}
