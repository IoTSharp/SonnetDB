using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvConditionalBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-conditional-tests",
        Guid.NewGuid().ToString("N"));

    public KvConditionalBatchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyConditionalBatch_KeyVersionConflict_DoesNotAppendWal()
    {
        using var keyspace = Open("version-conflict");
        long version = keyspace.Put("record", [1]);
        long walLength = keyspace.ActiveWalLength;

        KvConditionalBatchResult conflict = keyspace.ApplyConditionalBatch(
            [KvBatchMutation.Put("record"u8.ToArray(), [2])],
            [KvBatchPrecondition.KeyVersion("record"u8.ToArray(), version + 1)]);

        Assert.False(conflict.Applied);
        Assert.Equal(0, conflict.FailedPreconditionIndex);
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal([1], keyspace.Get("record"));
    }

    [Fact]
    public void ApplyConditionalBatch_PrefixEmpty_RejectsVisibleChild()
    {
        using var keyspace = Open("prefix-conflict");
        keyspace.Put("adjacency:vertex:edge", [1]);
        long walLength = keyspace.ActiveWalLength;

        KvConditionalBatchResult conflict = keyspace.ApplyConditionalBatch(
            [KvBatchMutation.Delete("vertex"u8.ToArray())],
            [KvBatchPrecondition.PrefixEmpty("adjacency:vertex:"u8.ToArray())]);

        Assert.False(conflict.Applied);
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.NotNull(keyspace.Get("adjacency:vertex:edge"));
    }

    [Fact]
    public async Task ApplyConditionalBatch_ConcurrentCreate_ExactlyOneCommits()
    {
        using var keyspace = Open("concurrent-create");
        using var ready = new Barrier(2);

        Task<KvConditionalBatchResult> first = Task.Run(() =>
        {
            ready.SignalAndWait();
            return CreateOnce(keyspace, 1);
        });
        Task<KvConditionalBatchResult> second = Task.Run(() =>
        {
            ready.SignalAndWait();
            return CreateOnce(keyspace, 2);
        });

        KvConditionalBatchResult[] results = await Task.WhenAll(first, second);

        Assert.Single(results, static result => result.Applied);
        Assert.Single(results, static result => !result.Applied);
        byte[] persisted = keyspace.Get("record")!;
        Assert.Single(persisted);
        Assert.True(persisted[0] is 1 or 2);
    }

    [Fact]
    public void ApplyConditionalBatch_CanceledBeforeAppend_LeavesStateUntouched()
    {
        using var keyspace = Open("canceled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => keyspace.ApplyConditionalBatch(
            [KvBatchMutation.Put("record"u8.ToArray(), [1])],
            [KvBatchPrecondition.KeyVersion("record"u8.ToArray(), 0)],
            cancellation.Token));

        Assert.Equal(0, keyspace.LastSequence);
        Assert.Equal(KvWalFile.HeaderSize, keyspace.ActiveWalLength);
        Assert.Null(keyspace.Get("record"));
    }

    [Fact]
    public async Task ApplyConditionalBatch_CanceledDuringCheckpointBackpressure_DoesNotWaitOrAppend()
    {
        using var keyspace = KvKeyspace.Open(
            "backpressure-canceled",
            Path.Combine(_root, "backpressure-canceled"),
            KvOptions.Default with
            {
                AutoCheckpointEnabled = true,
                MaxWalBytes = long.MaxValue,
                MaxOverlayEntries = 2,
                CheckpointWriteBackpressureTimeout = TimeSpan.FromSeconds(30),
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
        keyspace.Put("baseline", [0]);
        using var checkpointFrozen = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        keyspace.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            checkpointFrozen.Set();
            if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the checkpoint");
        };
        Task<long> checkpoint = Task.Run(keyspace.Compact);
        Assert.True(checkpointFrozen.Wait(TimeSpan.FromSeconds(10)));
        keyspace.Put("existing:1", [1]);
        keyspace.Put("existing:2", [2]);
        long walLength = keyspace.ActiveWalLength;
        using var cancellation = new CancellationTokenSource();
        keyspace.WriteBackpressureTestHook = cancellation.Cancel;
        try
        {
            Assert.Throws<OperationCanceledException>(() => keyspace.ApplyConditionalBatch(
                [KvBatchMutation.Put("new"u8.ToArray(), [2])],
                [KvBatchPrecondition.KeyVersion("new"u8.ToArray(), 0)],
                cancellation.Token));

            Assert.Equal(walLength, keyspace.ActiveWalLength);
            Assert.Null(keyspace.Get("new"));
        }
        finally
        {
            releaseCheckpoint.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private KvKeyspace Open(string name)
        => KvKeyspace.Open(
            name,
            Path.Combine(_root, name),
            KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });

    private static KvConditionalBatchResult CreateOnce(KvKeyspace keyspace, byte value)
        => keyspace.ApplyConditionalBatch(
            [KvBatchMutation.Put("record"u8.ToArray(), [value])],
            [KvBatchPrecondition.KeyVersion("record"u8.ToArray(), 0)]);
}
