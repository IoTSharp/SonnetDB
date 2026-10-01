using SonnetDB.Cdc;
using SonnetDB.Documents;
using SonnetDB.Engine;

namespace SonnetDB.Core.Tests.Cdc;

/// <summary>多分区本地复制的真实文档源、独立恢复和有界调度合同。</summary>
public sealed class CdcLocalReplicaTopologyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sonnetdb-cdc-topology-" + Guid.NewGuid().ToString("N"));

    /// <summary>为当前测试创建独立临时目录。</summary>
    public CdcLocalReplicaTopologyTests() => Directory.CreateDirectory(_root);

    /// <summary>核验并回收当前测试独占的临时目录。</summary>
    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temporaryRoot, path, StringComparison.OrdinalIgnoreCase);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>真实两文档集合在部分快照重开后独立续传并完成结果对账。</summary>
    [Fact]
    public async Task RunAsync_TwoDocumentCollections_ReopensAndReconcilesIndependentPartitions()
    {
        string databasePath = Path.Combine(_root, "source-db");
        CdcSnapshotDescriptor alphaDescriptor;
        CdcSnapshotDescriptor betaDescriptor;
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("alpha"));
            database.Documents.Create(DocumentCollectionSchema.Create("beta"));
            DocumentCollectionStore alpha = database.Documents.Open("alpha");
            DocumentCollectionStore beta = database.Documents.Open("beta");
            alpha.Insert("a", "{\"value\":1}");
            alpha.Insert("b", "{\"value\":1}");
            beta.Insert("a", "{\"value\":1}");
            beta.Insert("b", "{\"value\":1}");
            await using var alphaView = await CdcSourceReadView.CaptureDocumentCollectionAsync(
                Path.Combine(_root, "alpha-view.bin"), alpha, "source-db", partition: 10);
            await using var betaView = await CdcSourceReadView.CaptureDocumentCollectionAsync(
                Path.Combine(_root, "beta-view.bin"), beta, "source-db", partition: 20);
            alphaDescriptor = alphaView.Descriptor;
            betaDescriptor = betaView.Descriptor;
            await using var alphaSpool = new CdcEventSpool(Path.Combine(_root, "alpha-events.log"));
            await using var betaSpool = new CdcEventSpool(Path.Combine(_root, "beta-events.log"));
            await using var alphaCapture = new CdcDocumentSourceCapture(alpha, alphaSpool, CaptureOptions("alpha", 10));
            await using var betaCapture = new CdcDocumentSourceCapture(beta, betaSpool, CaptureOptions("beta", 20));
            await using var alphaReplica = await CdcSnapshotReplica.CreateAsync(
                Path.Combine(_root, "alpha-replica.bin"), alphaDescriptor);
            await using var betaReplica = await CdcSnapshotReplica.CreateAsync(
                Path.Combine(_root, "beta-replica.bin"), betaDescriptor);
            alpha.Replace("a", "{\"value\":2}");
            alpha.Insert("c", "{\"value\":3}");
            beta.Delete("b");
            await alphaCapture.CaptureAsync();
            await betaCapture.CaptureAsync();

            var topology = new CdcLocalReplicaTopology(
            [
                new(alphaView, alphaSpool, alphaReplica),
                new(betaView, betaSpool, betaReplica),
            ]);
            var progress = new List<int>();
            CdcLocalReplicaTopologyRunResult partial = await topology.RunAsync(
                RunOptions(2), new InlineProgress<CdcLocalReplicaTopologyProgress>(value => progress.Add(value.PartitionIndex)));

            Assert.Equal([0, 1], progress);
            Assert.Equal(2, partial.CompletedSteps);
            Assert.True(partial.ReachedStepLimit);
            Assert.False(partial.IsCaughtUp);
            Assert.All(partial.States, state => Assert.Equal(1, state.SnapshotRowsCopied));
            Assert.Equal(2, partial.States[0].AppliedCheckpoint.Offset);
            Assert.Equal(2, partial.States[1].AppliedCheckpoint.Offset);
        }

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            DocumentCollectionStore alpha = database.Documents.Open("alpha");
            DocumentCollectionStore beta = database.Documents.Open("beta");
            alpha.Replace("a", "{\"value\":4}");
            beta.Replace("a", "{\"value\":7}");
            await using var alphaView = CdcSourceReadView.Open(Path.Combine(_root, "alpha-view.bin"), alphaDescriptor);
            await using var betaView = CdcSourceReadView.Open(Path.Combine(_root, "beta-view.bin"), betaDescriptor);
            await using var alphaSpool = new CdcEventSpool(Path.Combine(_root, "alpha-events.log"));
            await using var betaSpool = new CdcEventSpool(Path.Combine(_root, "beta-events.log"));
            await using var alphaCapture = new CdcDocumentSourceCapture(alpha, alphaSpool, CaptureOptions("alpha", 10));
            await using var betaCapture = new CdcDocumentSourceCapture(beta, betaSpool, CaptureOptions("beta", 20));
            Assert.Equal(4, (await alphaCapture.CaptureAsync()).StartSequence);
            Assert.Equal(3, (await betaCapture.CaptureAsync()).StartSequence);
            await using var alphaReplica = CdcSnapshotReplica.Open(Path.Combine(_root, "alpha-replica.bin"), alphaDescriptor);
            await using var betaReplica = CdcSnapshotReplica.Open(Path.Combine(_root, "beta-replica.bin"), betaDescriptor);
            var topology = new CdcLocalReplicaTopology(
            [
                new(alphaView, alphaSpool, alphaReplica),
                new(betaView, betaSpool, betaReplica),
            ]);

            CdcLocalReplicaTopologyRunResult completed = await topology.RunAsync(RunOptions(32));

            Assert.True(completed.IsCaughtUp);
            Assert.False(completed.ReachedStepLimit);
            Assert.False(completed.ReachedTimeLimit);
            Assert.Equal(new CdcCheckpoint(10, 5), completed.States[0].AppliedCheckpoint);
            Assert.Equal(new CdcCheckpoint(20, 4), completed.States[1].AppliedCheckpoint);
            Assert.Equal([Row("a", 4), Row("b", 1), Row("c", 3)], (await alphaReplica.ReadRowsAsync()).Rows);
            Assert.Equal([Row("a", 7)], (await betaReplica.ReadRowsAsync()).Rows);
            Assert.Equal(0, alphaSpool.EventCount);
            Assert.Equal(0, betaSpool.EventCount);
            Assert.Equal(5, alphaSpool.AcknowledgedCheckpoints[10]);
            Assert.Equal(4, betaSpool.AcknowledgedCheckpoints[20]);
            Assert.Equal(0, (await topology.RunAsync(RunOptions(1))).CompletedSteps);
        }
    }

    /// <summary>连续单步运行保留轮转位置，避免其它分区饥饿。</summary>
    [Fact]
    public async Task RunAsync_RepeatedSingleSteps_RotatesWithoutStarvingOtherPartitions()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1), Row("b", 1)]);
        await using Fixture second = await CreateFixtureAsync("second", 20, [Row("a", 2), Row("b", 2)]);
        var topology = new CdcLocalReplicaTopology([first.Partition, second.Partition]);
        var progress = new List<int>();
        var observer = new InlineProgress<CdcLocalReplicaTopologyProgress>(value => progress.Add(value.PartitionIndex));

        await topology.RunAsync(RunOptions(1), observer);
        await topology.RunAsync(RunOptions(1), observer);
        await topology.RunAsync(RunOptions(1), observer);
        await topology.RunAsync(RunOptions(1), observer);

        Assert.Equal([0, 1, 0, 1], progress);
        Assert.All(topology.GetStates(), state => Assert.Equal(2, state.SnapshotRowsCopied));
    }

    /// <summary>一个分区容量失败时保留其它分区提交且不确认失败事件。</summary>
    [Fact]
    public async Task RunAsync_PartitionCapacityFailure_KeepsOtherCommitAndDoesNotAcknowledgeFailedPartition()
    {
        var limits = new CdcSnapshotReplicaOptions { MaxRows = 1, MaxBatchRows = 1, MaxBytes = 4096, MaxBatchBytes = 4096 };
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)], limits);
        await using Fixture second = await CreateFixtureAsync("second", 20, [Row("a", 1)], limits);
        await first.CompleteSnapshotAsync();
        await second.CompleteSnapshotAsync();
        await first.Spool.AppendAsync(Event(first.View.Descriptor, 1, "a", CdcOperation.Update, "{\"value\":2}"));
        await second.Spool.AppendAsync(Event(second.View.Descriptor, 1, "b", CdcOperation.Insert, "{\"value\":3}"));
        var topology = new CdcLocalReplicaTopology([first.Partition, second.Partition]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => topology.RunAsync(RunOptions(2)).AsTask());

        Assert.Equal(1, first.Replica.GetState().AppliedCheckpoint.Offset);
        Assert.Equal(1, first.Spool.AcknowledgedCheckpoints[10]);
        Assert.Equal([Row("a", 2)], (await first.Replica.ReadRowsAsync()).Rows);
        Assert.Equal(0, second.Replica.GetState().AppliedCheckpoint.Offset);
        // 泵可补确认已经持久化的快照边界 0，失败的增量 1 仍须保留。
        Assert.Equal(0, second.Spool.AcknowledgedCheckpoints[20]);
        Assert.Equal(1, second.Spool.EventCount);
        Assert.Equal([Row("a", 1)], (await second.Replica.ReadRowsAsync()).Rows);
    }

    /// <summary>已经物化但未确认的事件先补确认，再继续后续增量。</summary>
    [Fact]
    public async Task RunAsync_AlreadyAppliedEventWithoutAcknowledgement_RecoversConfirmationBeforeNextBatch()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        await first.CompleteSnapshotAsync();
        CdcEvent value = Event(first.View.Descriptor, 1, "a", CdcOperation.Update, "{\"value\":2}");
        await first.Spool.AppendAsync(value);
        await first.Replica.ApplyIncrementalAsync([value]);
        var topology = new CdcLocalReplicaTopology([first.Partition]);

        CdcLocalReplicaTopologyRunResult result = await topology.RunAsync(RunOptions(1));

        Assert.True(result.IsCaughtUp);
        Assert.Equal(1, result.CompletedSteps);
        Assert.Equal(1, first.Spool.AcknowledgedCheckpoints[10]);
        Assert.Equal(0, first.Spool.EventCount);
        Assert.Equal([Row("a", 2)], (await first.Replica.ReadRowsAsync()).Rows);
    }

    /// <summary>并发调度串行完成独立步骤并保留轮转顺序。</summary>
    [Fact]
    public async Task RunAsync_ConcurrentRuns_SerializesStepsAndKeepsRoundRobinOrder()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1), Row("b", 1)]);
        await using Fixture second = await CreateFixtureAsync("second", 20, [Row("a", 2), Row("b", 2)]);
        var topology = new CdcLocalReplicaTopology([first.Partition, second.Partition]);

        CdcLocalReplicaTopologyRunResult[] results = await Task.WhenAll(
            topology.RunAsync(RunOptions(1)).AsTask(),
            topology.RunAsync(RunOptions(1)).AsTask());

        Assert.All(results, result => Assert.Equal(1, result.CompletedSteps));
        Assert.All(topology.GetStates(), state => Assert.Equal(1, state.SnapshotRowsCopied));
    }

    /// <summary>进度通知后取消保留已提交快照以及下一分区位置。</summary>
    [Fact]
    public async Task RunAsync_CancelledAfterFirstStep_PreservesProgressAndNextPartition()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        await using Fixture second = await CreateFixtureAsync("second", 20, [Row("a", 2)]);
        var topology = new CdcLocalReplicaTopology([first.Partition, second.Partition]);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<CdcLocalReplicaTopologyProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            topology.RunAsync(RunOptions(4), progress, cancellation.Token).AsTask());

        Assert.Equal(1, first.Replica.GetState().SnapshotRowsCopied);
        Assert.Equal(0, second.Replica.GetState().SnapshotRowsCopied);
        await topology.RunAsync(RunOptions(1));
        Assert.Equal(1, second.Replica.GetState().SnapshotRowsCopied);
    }

    /// <summary>墙钟上限取消批次间等待并返回可恢复状态。</summary>
    [Fact]
    public async Task RunAsync_ElapsedTimeLimit_CancelsIntervalAndReturnsRecoverableStates()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1), Row("b", 1)]);
        var topology = new CdcLocalReplicaTopology([first.Partition]);

        CdcLocalReplicaTopologyRunResult result = await topology.RunAsync(RunOptions(4) with
        {
            MaxElapsedTime = TimeSpan.FromMilliseconds(100),
            Interval = TimeSpan.FromMinutes(1),
        });

        Assert.True(result.ReachedTimeLimit);
        Assert.False(result.IsCaughtUp);
        Assert.InRange(result.CompletedSteps, 0, 1);
        Assert.Equal(first.Replica.GetState(), result.States[0]);
        Assert.True((await topology.RunAsync(RunOptions(4))).IsCaughtUp);
    }

    /// <summary>开始前取消不改变任何分区。</summary>
    [Fact]
    public async Task RunAsync_CancelledBeforeStart_DoesNotMutateAnyPartition()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        var topology = new CdcLocalReplicaTopology([first.Partition]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            topology.RunAsync(RunOptions(1), cancellationToken: cancellation.Token).AsTask());

        Assert.Equal(0, first.Replica.GetState().SnapshotRowsCopied);
    }

    /// <summary>字节预算无法容纳首行时不推进快照。</summary>
    [Fact]
    public async Task RunAsync_ByteLimitCannotFitSnapshotRow_DoesNotAdvanceState()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        var topology = new CdcLocalReplicaTopology([first.Partition]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            topology.RunAsync(RunOptions(1) with { MaxBytesPerStep = 1 }).AsTask());

        Assert.Equal(0, first.Replica.GetState().SnapshotRowsCopied);
        Assert.Empty(first.Spool.AcknowledgedCheckpoints);
    }

    /// <summary>实际增量身份错配不能推进物化位点或确认错误事件。</summary>
    /// <param name="field">需要制造错配的身份字段。</param>
    [Theory]
    [InlineData("source")]
    [InlineData("entity")]
    [InlineData("schema")]
    public async Task RunAsync_IncrementalIdentityMismatch_DoesNotApplyOrAcknowledgeForeignEvent(string field)
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        await first.CompleteSnapshotAsync();
        await first.Spool.AppendAsync(Event(first.View.Descriptor, 1, "a", CdcOperation.Update, "{\"value\":2}"));
        var topology = new CdcLocalReplicaTopology([first.Partition]);
        Assert.True((await topology.RunAsync(RunOptions(1))).IsCaughtUp);
        CdcEvent next = Event(first.View.Descriptor, 2, "a", CdcOperation.Update, "{\"value\":3}");
        CdcEvent foreign = field switch
        {
            "source" => next with { Source = "other-source" },
            "entity" => next with { Entity = "other-entity" },
            _ => next with { Metadata = next.Metadata with { Schema = "other-schema" } },
        };
        await first.Spool.AppendAsync(foreign);

        await Assert.ThrowsAsync<ArgumentException>(() => topology.RunAsync(RunOptions(1)).AsTask());

        Assert.Equal(1, first.Replica.GetState().AppliedCheckpoint.Offset);
        Assert.Equal(1, first.Spool.AcknowledgedCheckpoints[10]);
        Assert.Equal(1, first.Spool.EventCount);
        Assert.Equal([Row("a", 2)], (await first.Replica.ReadRowsAsync()).Rows);
    }

    /// <summary>重复路由或共享 spool 在调度前被拒绝。</summary>
    [Fact]
    public async Task Constructor_DuplicateRouteOrSharedSpool_RejectsTopologyWithoutMutation()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        await using Fixture second = await CreateFixtureAsync("second", 20, [Row("a", 2)]);

        Assert.Throws<ArgumentException>(() => new CdcLocalReplicaTopology([first.Partition, first.Partition]));
        Assert.Throws<ArgumentException>(() => new CdcLocalReplicaTopology(
        [
            first.Partition,
            new(second.View, first.Spool, second.Replica),
        ]));
        Assert.All(new[] { first.Replica.GetState(), second.Replica.GetState() },
            state => Assert.Equal(0, state.SnapshotRowsCopied));
    }

    /// <summary>视图 descriptor 或专用 spool 分区错配时拒绝构造。</summary>
    [Fact]
    public async Task Constructor_MismatchedDescriptorOrSpoolPartition_FailsClosed()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        await using Fixture second = await CreateFixtureAsync("second", 20, [Row("a", 2)]);
        Assert.Throws<InvalidDataException>(() => new CdcLocalReplicaTopology(
            [new(first.View, first.Spool, second.Replica)]));
        await first.Spool.AppendAsync(Event(first.View.Descriptor with { Checkpoint = new CdcCheckpoint(99, 0) },
            1, "a", CdcOperation.Update, "{\"value\":2}"));

        Assert.Throws<InvalidDataException>(() => new CdcLocalReplicaTopology([first.Partition]));

        Assert.Equal(0, first.Replica.GetState().SnapshotRowsCopied);
        Assert.Equal(1, first.Spool.EventCount);
    }

    /// <summary>空拓扑或超过分区数量上限时拒绝调度。</summary>
    [Fact]
    public async Task Constructor_EmptyOrOversizedTopology_RejectsBeforeScheduling()
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);

        Assert.Throws<ArgumentOutOfRangeException>(() => new CdcLocalReplicaTopology([]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CdcLocalReplicaTopology(Enumerable.Repeat(first.Partition, 65).ToArray()));
    }

    /// <summary>无效运行边界在持久修改之前被拒绝。</summary>
    /// <param name="steps">推进次数上限。</param>
    /// <param name="rows">每步行数上限。</param>
    /// <param name="bytes">每步字节上限。</param>
    /// <param name="elapsedMilliseconds">最长墙钟时间毫秒数。</param>
    /// <param name="intervalMilliseconds">批次间等待毫秒数。</param>
    [Theory]
    [InlineData(0, 1, 4096, 1000, 0)]
    [InlineData(100001, 1, 4096, 1000, 0)]
    [InlineData(1, 0, 4096, 1000, 0)]
    [InlineData(1, 1, 0, 1000, 0)]
    [InlineData(1, 1, 4096, 0, 0)]
    [InlineData(1, 1, 4096, 3600001, 0)]
    [InlineData(1, 1, 4096, 1000, -1)]
    [InlineData(1, 1, 4096, 1000, 60001)]
    public async Task RunAsync_InvalidBounds_RejectsWithoutMutation(int steps, int rows, int bytes, int elapsedMilliseconds, int intervalMilliseconds)
    {
        await using Fixture first = await CreateFixtureAsync("first", 10, [Row("a", 1)]);
        var topology = new CdcLocalReplicaTopology([first.Partition]);
        var options = new CdcLocalReplicaTopologyRunOptions
        {
            MaxSteps = steps,
            MaxRowsPerStep = rows,
            MaxBytesPerStep = bytes,
            MaxElapsedTime = TimeSpan.FromMilliseconds(elapsedMilliseconds),
            Interval = TimeSpan.FromMilliseconds(intervalMilliseconds),
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => topology.RunAsync(options).AsTask());

        Assert.Equal(0, first.Replica.GetState().SnapshotRowsCopied);
    }

    private async ValueTask<Fixture> CreateFixtureAsync(
        string entity, long partition, IReadOnlyList<CdcSnapshotRow> rows, CdcSnapshotReplicaOptions? options = null)
    {
        var descriptor = new CdcSnapshotDescriptor(
            "snapshot-" + entity, "test-source", entity, "documents", 1, new CdcCheckpoint(partition, 0), rows.Count);
        CdcSourceReadView view = await CdcSourceReadView.CaptureAsync(
            Path.Combine(_root, entity + "-view.bin"), descriptor,
            (afterKey, _, _) => ValueTask.FromResult<IReadOnlyList<CdcSnapshotRow>>(afterKey is null ? rows : []), options);
        CdcEventSpool? spool = null;
        try
        {
            spool = new CdcEventSpool(Path.Combine(_root, entity + "-events.log"));
            CdcSnapshotReplica replica = await CdcSnapshotReplica.CreateAsync(
                Path.Combine(_root, entity + "-replica.bin"), descriptor, options);
            return new Fixture(view, spool, replica);
        }
        catch
        {
            spool?.Dispose();
            view.Dispose();
            throw;
        }
    }

    private static CdcLocalReplicaTopologyRunOptions RunOptions(int maxSteps)
        => new() { MaxSteps = maxSteps, MaxRowsPerStep = 1, MaxBytesPerStep = 4096, MaxElapsedTime = TimeSpan.FromSeconds(10) };

    private static CdcDocumentSourceCaptureOptions CaptureOptions(string entity, long partition)
        => new() { Source = "source-db", Entity = entity, Schema = "documents", Partition = partition, BatchSize = 16 };

    private static CdcSnapshotRow Row(string key, int value) => new(key, $"{{\"value\":{value}}}");

    private static CdcEvent Event(CdcSnapshotDescriptor descriptor, long offset, string key, CdcOperation operation, string after)
        => new($"event-{descriptor.Entity}-{offset}", descriptor.Source, descriptor.Entity, key, offset,
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            new CdcEventMetadata(1, descriptor.Schema, 1, operation,
                new CdcCheckpoint(descriptor.Checkpoint.Partition, offset)), null, after);

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        /// <summary>同步执行当前测试的进度回调。</summary>
        public void Report(T value) => callback(value);
    }

    private sealed class Fixture(CdcSourceReadView view, CdcEventSpool spool, CdcSnapshotReplica replica) : IAsyncDisposable
    {
        /// <summary>当前分区的固定读视图。</summary>
        public CdcSourceReadView View { get; } = view;
        /// <summary>当前分区独占的持久事件 spool。</summary>
        public CdcEventSpool Spool { get; } = spool;
        /// <summary>当前分区的持久接收端。</summary>
        public CdcSnapshotReplica Replica { get; } = replica;
        /// <summary>由当前资源组成的独立路由。</summary>
        public CdcLocalReplicaPartition Partition => new(View, Spool, Replica);

        /// <summary>完整提交当前测试分区的快照。</summary>
        public async ValueTask CompleteSnapshotAsync()
        {
            CdcSnapshotRowBatch rows = await View.ReadPageAsync();
            await Replica.WriteSnapshotPageAsync(0, rows.Rows);
            await Replica.CompleteSnapshotAsync();
        }

        /// <summary>依次关闭当前测试独占的接收端、spool 和固定视图。</summary>
        public async ValueTask DisposeAsync()
        {
            await Replica.DisposeAsync();
            await Spool.DisposeAsync();
            await View.DisposeAsync();
        }
    }
}
