using SonnetDB.Cdc;
using SonnetDB.Streaming;

namespace SonnetDB.Core.Tests.Cdc;

/// <summary>真实文件多分区 CDC 到流调度、隔离和独立恢复合同。</summary>
public sealed class CdcStreamingBridgeTopologyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sonnetdb-cdc-stream-topology-" + Guid.NewGuid().ToString("N"));

    /// <summary>为当前测试创建独立目录。</summary>
    public CdcStreamingBridgeTopologyTests() => Directory.CreateDirectory(_root);

    /// <summary>核对绝对路径并回收当前测试独占目录。</summary>
    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        string temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporaryRoot, path, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sonnetdb-cdc-stream-topology-", Path.GetFileName(path), StringComparison.Ordinal);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>独立源位点和目标序号在部分推进后重开，并保留完整 CDC 正文。</summary>
    [Fact]
    public async Task RunAsync_PartialTwoPartitionDelivery_ReopensIndependentOffsetsAndPreservesPayloads()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using (Fixture first = await OpenFixtureAsync("first", 10, deadline.Token))
        await using (Fixture second = await OpenFixtureAsync("second", 20, deadline.Token))
        {
            await first.Source.AppendAsync(Event("first", 10, 10), deadline.Token);
            await first.Source.AppendAsync(Event("first", 10, 30), deadline.Token);
            await second.Source.AppendAsync(Event("second", 20, 100), deadline.Token);
            var topology = new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]);
            var progress = new List<int>();

            CdcStreamingBridgeTopologyRunResult partial = await topology.RunAsync(RunOptions(2),
                new InlineProgress<CdcStreamingBridgeTopologyProgress>(value => progress.Add(value.PartitionIndex)), deadline.Token);

            Assert.Equal([0, 1], progress);
            Assert.True(partial.ReachedStepLimit);
            Assert.False(partial.IsCaughtUp);
            Assert.Equal(10, partial.States[0].CommittedSourceOffset);
            Assert.Equal(100, partial.States[1].CommittedSourceOffset);
            Assert.All(partial.States, state => Assert.Equal(0, state.LastTargetSequence));
            Assert.Equal(1, first.Source.EventCount);
            Assert.Equal(0, second.Source.EventCount);
        }

        await using Fixture reopenedFirst = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture reopenedSecond = await OpenFixtureAsync("second", 20, deadline.Token);
        var reopened = new CdcStreamingBridgeTopology([reopenedFirst.Bridge, reopenedSecond.Bridge]);
        CdcStreamingBridgeTopologyRunResult final = await reopened.RunAsync(RunOptions(4), cancellationToken: deadline.Token);

        Assert.True(final.IsCaughtUp);
        Assert.Equal(30, final.States[0].CommittedSourceOffset);
        Assert.Equal(1, final.States[0].LastTargetSequence);
        Assert.Equal(100, final.States[1].CommittedSourceOffset);
        Assert.Equal(0, final.States[1].LastTargetSequence);
        StreamingDeliveryBatch firstBatch = (await reopenedFirst.Target.ReadBatchAsync(deadline.Token))!;
        Assert.Equal(Event("first", 10, 10), CdcEventCodec.Decode(Assert.Single(firstBatch.Events).Payload));
        await reopenedFirst.Target.AcknowledgeAsync(firstBatch.DeliveryId, deadline.Token);
        StreamingDeliveryBatch nextBatch = (await reopenedFirst.Target.ReadBatchAsync(deadline.Token))!;
        Assert.Equal(Event("first", 10, 30), CdcEventCodec.Decode(Assert.Single(nextBatch.Events).Payload));
        Assert.Equal(1, nextBatch.Events[0].Sequence);
        StreamingDeliveryBatch secondBatch = (await reopenedSecond.Target.ReadBatchAsync(deadline.Token))!;
        Assert.Equal(Event("second", 20, 100), CdcEventCodec.Decode(Assert.Single(secondBatch.Events).Payload));
        Assert.Equal(0, (await reopened.RunAsync(RunOptions(1), cancellationToken: deadline.Token)).CompletedSteps);
    }

    /// <summary>反复单步调用保留轮转位置，繁忙路由不能饿死其它分区。</summary>
    [Fact]
    public async Task RunAsync_RepeatedSingleSteps_PreservesRoundRobinAcrossCalls()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture second = await OpenFixtureAsync("second", 20, deadline.Token);
        await first.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        await first.Source.AppendAsync(Event("first", 10, 2), deadline.Token);
        await second.Source.AppendAsync(Event("second", 20, 1), deadline.Token);
        await second.Source.AppendAsync(Event("second", 20, 2), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]);
        var progress = new List<int>();
        var observer = new InlineProgress<CdcStreamingBridgeTopologyProgress>(value => progress.Add(value.PartitionIndex));

        for (int step = 0; step < 4; step++)
            await topology.RunAsync(RunOptions(1), observer, deadline.Token);

        Assert.Equal([0, 1, 0, 1], progress);
        Assert.All(topology.GetStates(), state => Assert.Equal(2, state.CommittedSourceOffset));
    }

    /// <summary>并发运行在有界排队内串行完成，保持独立 revision 条件。</summary>
    [Fact]
    public async Task RunAsync_ConcurrentCalls_SerializesBridgeSteps()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture second = await OpenFixtureAsync("second", 20, deadline.Token);
        await first.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        await second.Source.AppendAsync(Event("second", 20, 1), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]);

        CdcStreamingBridgeTopologyRunResult[] results = await Task.WhenAll(
            topology.RunAsync(RunOptions(1), cancellationToken: deadline.Token).AsTask(),
            topology.RunAsync(RunOptions(1), cancellationToken: deadline.Token).AsTask());

        Assert.All(results, result => Assert.Equal(1, result.CompletedSteps));
        Assert.All(topology.GetStates(), state => Assert.Equal(1, state.CommittedSourceOffset));
    }

    /// <summary>进度报告后取消保留成功位点以及下一分区轮转位置。</summary>
    [Fact]
    public async Task RunAsync_CancelledAfterFirstCommit_PreservesNextPartitionAndCallerOwnership()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture second = await OpenFixtureAsync("second", 20, deadline.Token);
        await first.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        await second.Source.AppendAsync(Event("second", 20, 1), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]);
        var observer = new InlineProgress<CdcStreamingBridgeTopologyProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => topology.RunAsync(RunOptions(4), observer, cancellation.Token).AsTask());

        Assert.Equal(1, first.Bridge.GetState().CommittedSourceOffset);
        Assert.Equal(-1, second.Bridge.GetState().CommittedSourceOffset);
        Assert.True((await topology.RunAsync(RunOptions(1), cancellationToken: deadline.Token)).IsCaughtUp);
        Assert.NotNull(await first.Target.ReadBatchAsync(deadline.Token));
        Assert.Equal(1, second.Bridge.GetState().CommittedSourceOffset);
    }

    /// <summary>开始前取消不推进任何源或目标。</summary>
    [Fact]
    public async Task RunAsync_CancelledBeforeStart_DoesNotMutateBridge()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource();
        await using Fixture fixture = await OpenFixtureAsync("first", 10, deadline.Token);
        await fixture.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([fixture.Bridge]);
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => topology.RunAsync(RunOptions(1), cancellationToken: cancelled.Token).AsTask());

        Assert.Equal(0, fixture.Bridge.GetState().Revision);
        Assert.Equal(1, fixture.Source.EventCount);
        Assert.Equal(0, fixture.Target.PendingEventCount);
    }

    /// <summary>间隔等待达到墙钟边界时返回已提交独立状态，之后可继续。</summary>
    [Fact]
    public async Task RunAsync_IntervalDeadline_ReturnsTimeLimitAndRecoverableStates()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture fixture = await OpenFixtureAsync("first", 10, deadline.Token);
        await fixture.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        await fixture.Source.AppendAsync(Event("first", 10, 2), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([fixture.Bridge]);
        CdcStreamingBridgeTopologyRunResult result = await topology.RunAsync(RunOptions(4) with
        {
            MaxElapsedTime = TimeSpan.FromSeconds(2),
            Interval = TimeSpan.FromMinutes(1),
        }, cancellationToken: deadline.Token);

        Assert.True(result.ReachedTimeLimit);
        Assert.False(result.IsCaughtUp);
        Assert.Equal(1, result.CompletedSteps);
        Assert.Equal(fixture.Bridge.GetState(), result.States[0]);
        Assert.True((await topology.RunAsync(RunOptions(4), cancellationToken: deadline.Token)).IsCaughtUp);
    }

    /// <summary>批次中取消时间边界不能伪装为成功结果，重开后恢复已发布凭据。</summary>
    [Fact]
    public async Task RunAsync_DeadlineDuringBatch_ThrowsAndRetainsRecoverableOutbox()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reachedBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdBoundary = new ManualResetEventSlim();
        await using (Fixture fixture = await OpenFixtureAsync("first", 10, deadline.Token, onStep: step =>
        {
            if (step == "TargetReceiptCompleted")
            {
                reachedBoundary.TrySetResult();
                holdBoundary.Wait(TimeSpan.FromSeconds(5), deadline.Token);
            }
        }))
        {
            await fixture.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
            var topology = new CdcStreamingBridgeTopology([fixture.Bridge]);
            Task<CdcStreamingBridgeTopologyRunResult> run = Task.Run(async () => await topology.RunAsync(RunOptions(1) with
            {
                MaxElapsedTime = TimeSpan.FromSeconds(3),
            }, cancellationToken: deadline.Token), deadline.Token);
            try
            {
                await reachedBoundary.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(3100), deadline.Token);
                holdBoundary.Set();
                await Assert.ThrowsAsync<TimeoutException>(() => run);
            }
            finally
            {
                holdBoundary.Set();
                await run.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            Assert.Null(fixture.Source.AcknowledgedCheckpoint);
            Assert.Equal(1, fixture.Bridge.GetState().OutboxEventCount);
        }

        await using Fixture recovered = await OpenFixtureAsync("first", 10, deadline.Token);
        Assert.Equal(1, recovered.Bridge.GetState().CommittedSourceOffset);
        Assert.Equal(1, recovered.Target.PendingEventCount);
        Assert.True((await new CdcStreamingBridgeTopology([recovered.Bridge]).RunAsync(RunOptions(1), cancellationToken: deadline.Token)).IsCaughtUp);
    }

    /// <summary>忙运行的后续调用达到排队时间边界时返回结果，不取消拥有门闩的运行。</summary>
    [Fact]
    public async Task RunAsync_QueuedCallDeadline_ReturnsWithoutInterruptingActiveRun()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reachedBoundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var holdBoundary = new ManualResetEventSlim();
        await using Fixture fixture = await OpenFixtureAsync("first", 10, deadline.Token, onStep: step =>
        {
            if (step == "TargetReceiptCompleted")
            {
                reachedBoundary.TrySetResult();
                holdBoundary.Wait(TimeSpan.FromSeconds(5), deadline.Token);
            }
        });
        await fixture.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([fixture.Bridge]);
        Task<CdcStreamingBridgeTopologyRunResult> active = Task.Run(async () =>
            await topology.RunAsync(RunOptions(1), cancellationToken: deadline.Token), deadline.Token);
        try
        {
            await reachedBoundary.Task.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
            CdcStreamingBridgeTopologyRunResult queued = await topology.RunAsync(RunOptions(1) with
            {
                MaxElapsedTime = TimeSpan.FromMilliseconds(100),
            }, cancellationToken: deadline.Token);

            Assert.True(queued.ReachedTimeLimit);
            Assert.Equal(0, queued.CompletedSteps);
            Assert.False(queued.IsCaughtUp);
            Assert.Equal(1, queued.States[0].OutboxEventCount);
        }
        finally
        {
            holdBoundary.Set();
            await active.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        Assert.True((await active).IsCaughtUp);
        Assert.Equal(1, fixture.Source.AcknowledgedCheckpoints[10]);
    }

    /// <summary>一分区失败保留其它分区提交，重开失败 outbox 后各分区独立恢复。</summary>
    [Fact]
    public async Task RunAsync_SecondPartitionFails_ReopenRecoversWithoutRollingBackFirst()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await first.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        await using (Fixture second = await OpenFixtureAsync("second", 20, deadline.Token, onStep: step =>
        {
            if (step == "TargetReceiptCompleted")
                throw new IOException("Injected independent partition interruption.");
        }))
        {
            await second.Source.AppendAsync(Event("second", 20, 5), deadline.Token);
            var topology = new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]);

            await Assert.ThrowsAsync<IOException>(() => topology.RunAsync(RunOptions(4), cancellationToken: deadline.Token).AsTask());

            Assert.Equal(1, first.Bridge.GetState().CommittedSourceOffset);
            Assert.Equal(1, first.Source.AcknowledgedCheckpoints[10]);
            Assert.Null(second.Source.AcknowledgedCheckpoint);
            Assert.Equal(1, second.Bridge.GetState().OutboxEventCount);
            Assert.Equal(1, second.Target.PendingEventCount);
        }

        await using Fixture recovered = await OpenFixtureAsync("second", 20, deadline.Token);
        var rebuilt = new CdcStreamingBridgeTopology([first.Bridge, recovered.Bridge]);
        Assert.True((await rebuilt.RunAsync(RunOptions(2), cancellationToken: deadline.Token)).IsCaughtUp);
        Assert.Equal(5, recovered.Bridge.GetState().CommittedSourceOffset);
        Assert.Equal(1, first.Target.PendingEventCount);
        Assert.Equal(1, recovered.Target.PendingEventCount);
    }

    /// <summary>重复实例、桥接标识或同源分区都在调度前拒绝。</summary>
    [Fact]
    public async Task Constructor_DuplicateRoutes_RejectsWithoutPublishing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture sameId = await OpenFixtureAsync("same-id", 20, deadline.Token, binding: Binding("first", 20));
        await using Fixture samePartition = await OpenFixtureAsync("same-partition", 10, deadline.Token,
            binding: Binding("another", 10) with { Entity = "first" });

        Assert.Throws<ArgumentException>(() => new CdcStreamingBridgeTopology([first.Bridge, first.Bridge]));
        Assert.Throws<ArgumentException>(() => new CdcStreamingBridgeTopology([first.Bridge, sameId.Bridge]));
        Assert.Throws<ArgumentException>(() => new CdcStreamingBridgeTopology([first.Bridge, samePartition.Bridge]));
        Assert.Equal(0, first.Target.PendingEventCount);
    }

    /// <summary>一个桥接状态在其它目标目录内时拒绝交叉路径。</summary>
    [Fact]
    public async Task Constructor_StateWithinAnotherTarget_RejectsCrossRouteStorage()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture second = await OpenFixtureAsync("second", 20, deadline.Token,
            statePath: Path.Combine(first.Target.DirectoryPath, "other-bridge.json"));

        Assert.Throws<ArgumentException>(() => new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]));
    }

    /// <summary>不同路由持有同一个源 spool 时也不能重复消费。</summary>
    [Fact]
    public async Task Constructor_DistinctBridgesShareSource_RejectsDuplicatedPersistentFiles()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using var secondTarget = await FileStreamingSubscription.OpenAsync(Path.Combine(_root, "second-target"),
            StreamingSubscriptionDefinition.Create("second", "second", batchSize: 1, capacity: 8), cancellationToken: deadline.Token);
        await using var secondBridge = await CdcStreamingBridge.OpenAsync(Path.Combine(_root, "second-bridge.json"),
            first.Source, secondTarget, Binding("second", 10), cancellationToken: deadline.Token);

        Assert.Throws<ArgumentException>(() => new CdcStreamingBridgeTopology([first.Bridge, secondBridge]));
        Assert.Equal(0, first.Source.EventCount);
        Assert.Equal(0, first.Target.PendingEventCount);
        Assert.Equal(0, secondTarget.PendingEventCount);
    }

    /// <summary>分区数值相同但源实体不同的独立路由可以正常各自推进。</summary>
    [Fact]
    public async Task RunAsync_EqualPartitionNumbersWithDifferentEntities_DeliversIndependentRoutes()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture second = await OpenFixtureAsync("second", 10, deadline.Token);
        await first.Source.AppendAsync(Event("first", 10, 1), deadline.Token);
        await second.Source.AppendAsync(Event("second", 10, 2), deadline.Token);
        var topology = new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]);

        CdcStreamingBridgeTopologyRunResult result = await topology.RunAsync(RunOptions(2), cancellationToken: deadline.Token);

        Assert.True(result.IsCaughtUp);
        Assert.Equal(1, result.States[0].CommittedSourceOffset);
        Assert.Equal(2, result.States[1].CommittedSourceOffset);
        Assert.All(result.States, state => Assert.Equal(0, state.LastTargetSequence));
    }

    /// <summary>嵌套目标目录不能作为两个独立订阅分区。</summary>
    [Fact]
    public async Task Constructor_NestedTargetDirectories_RejectsOverlappingDestinations()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture first = await OpenFixtureAsync("first", 10, deadline.Token);
        await using Fixture second = await OpenFixtureAsync("second", 20, deadline.Token,
            targetPath: Path.Combine(first.Target.DirectoryPath, "nested"));

        Assert.Throws<ArgumentException>(() => new CdcStreamingBridgeTopology([first.Bridge, second.Bridge]));
    }

    /// <summary>构造之后混入其它源分区仍须在调度前拒绝且不确认事件。</summary>
    [Fact]
    public async Task RunAsync_SourceGainsForeignPartition_RejectsWithoutAcknowledgement()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture fixture = await OpenFixtureAsync("first", 10, deadline.Token);
        var topology = new CdcStreamingBridgeTopology([fixture.Bridge]);
        await fixture.Source.AppendAsync(Event("first", 11, 1), deadline.Token);

        await Assert.ThrowsAsync<InvalidDataException>(() => topology.RunAsync(RunOptions(1), cancellationToken: deadline.Token).AsTask());

        Assert.Null(fixture.Source.AcknowledgedCheckpoint);
        Assert.Equal(0, fixture.Target.PendingEventCount);
    }

    /// <summary>路由和运行边界参数不能被空列表或非法容量绕过。</summary>
    [Fact]
    public async Task ConstructorAndRunAsync_InvalidBounds_RejectBeforeMutation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using Fixture fixture = await OpenFixtureAsync("first", 10, deadline.Token);
        var topology = new CdcStreamingBridgeTopology([fixture.Bridge]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CdcStreamingBridgeTopology([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CdcStreamingBridgeTopology(new CdcStreamingBridge[65]));
        Assert.Throws<ArgumentNullException>(() => new CdcStreamingBridgeTopology([null!]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => topology.RunAsync(RunOptions(0)).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => topology.RunAsync(RunOptions(1) with { MaxElapsedTime = TimeSpan.Zero }).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => topology.RunAsync(RunOptions(1) with { Interval = TimeSpan.FromMinutes(2) }).AsTask());
        Assert.Equal(0, fixture.Bridge.GetState().Revision);
    }

    private async Task<Fixture> OpenFixtureAsync(
        string name, long partition, CancellationToken token, CdcStreamingBridgeBinding? binding = null,
        string? statePath = null, string? targetPath = null, Action<string>? onStep = null)
    {
        var source = new CdcEventSpool(Path.Combine(_root, name + ".spool"));
        FileStreamingSubscription? target = null;
        try
        {
            target = await FileStreamingSubscription.OpenAsync(targetPath ?? Path.Combine(_root, name + "-target"),
                StreamingSubscriptionDefinition.Create(name + "-subscription", name + "-stream", batchSize: 1, capacity: 8), cancellationToken: token);
            CdcStreamingBridge bridge = await CdcStreamingBridge.OpenCoreAsync(statePath ?? Path.Combine(_root, name + "-bridge.json"),
                source, target, binding ?? Binding(name, partition), new() { MaxBatchEvents = 1 }, token, onStep);
            return new(source, target, bridge);
        }
        catch
        {
            if (target is not null)
                await target.DisposeAsync();
            await source.DisposeAsync();
            throw;
        }
    }

    private static CdcStreamingBridgeTopologyRunOptions RunOptions(int steps)
        => new() { MaxSteps = steps, MaxElapsedTime = TimeSpan.FromSeconds(10) };

    private static CdcStreamingBridgeBinding Binding(string name, long partition)
        => new(name + "-bridge", "source-db", name, "documents", 1, partition);

    private static CdcEvent Event(string name, long partition, long offset)
        => new($"{name}-{offset}", "source-db", name, "device", offset,
            new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero),
            new(1, "documents", 1, CdcOperation.Insert, new(partition, offset)), null, "{\"value\":1}");

    private sealed record Fixture(CdcEventSpool Source, FileStreamingSubscription Target, CdcStreamingBridge Bridge) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Bridge.DisposeAsync();
            }
            finally
            {
                try
                {
                    await Target.DisposeAsync();
                }
                finally
                {
                    await Source.DisposeAsync();
                }
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
