using SonnetDB.Streaming;

namespace SonnetDB.Core.Tests.Streaming;

public sealed class StreamingSubscriptionCheckpointStoreTests
{
    [Fact]
    public async Task SaveThenReopen_RestoresLatestCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        StreamingSubscriptionCheckpoint first = Checkpoint("sub-persist", -1, -1, 0);
        StreamingSubscriptionCheckpoint second = first with
        {
            CommittedSequence = 7,
            WatermarkUtc = Utc(7),
            Revision = 1,
        };

        await using (var store = new FileStreamingSubscriptionCheckpointStore(directory.Path))
        {
            Assert.Null(await store.LoadAsync(first.SubscriptionId));
            await store.SaveAsync(first, expectedRevision: -1);
            await store.SaveAsync(second, expectedRevision: 0);
        }

        await using var reopened = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        Assert.Equal(second, await reopened.LoadAsync(second.SubscriptionId));
    }

    [Fact]
    public async Task SaveWithStaleRevision_RejectsWriteAndPreservesCommittedValue()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint first = Checkpoint("sub-revision", 2, 2, 0);
        StreamingSubscriptionCheckpoint committed = first with
        {
            CommittedSequence = 3,
            WatermarkUtc = Utc(3),
            Revision = 1,
        };

        await store.SaveAsync(first, expectedRevision: -1);
        await store.SaveAsync(committed, expectedRevision: 0);

        StreamingSubscriptionCheckpoint stale = first with
        {
            CommittedSequence = 4,
            WatermarkUtc = Utc(4),
            Revision = 1,
        };
        StreamingSubscriptionCheckpointConflictException exception = await Assert.ThrowsAsync<StreamingSubscriptionCheckpointConflictException>(
            () => store.SaveAsync(stale, expectedRevision: 0).AsTask());

        Assert.Equal("sub-revision", exception.SubscriptionId);
        Assert.Equal(0, exception.ExpectedRevision);
        Assert.Equal(1, exception.ActualRevision);
        Assert.Equal(committed, await store.LoadAsync("sub-revision"));
    }

    [Fact]
    public async Task SaveWithNonInitialRevision_WhenCheckpointIsMissing_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint candidate = Checkpoint("sub-missing", 4, 4, 1);

        StreamingSubscriptionCheckpointConflictException exception =
            await Assert.ThrowsAsync<StreamingSubscriptionCheckpointConflictException>(
                () => store.SaveAsync(candidate, expectedRevision: 0).AsTask());

        Assert.Equal("sub-missing", exception.SubscriptionId);
        Assert.Equal(0, exception.ExpectedRevision);
        Assert.Equal(-1, exception.ActualRevision);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.checkpoint.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"formatVersion\":1")]
    public async Task LoadWithCorruptOrTruncatedJson_FailsClosed(string content)
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint checkpoint = Checkpoint("sub-corrupt", 1, 1, 0);
        await store.SaveAsync(checkpoint, expectedRevision: -1);

        string path = Directory.GetFiles(directory.Path, "*.checkpoint.json").Single();
        await File.WriteAllTextAsync(path, content);

        StreamingSubscriptionCheckpointCorruptException exception = await Assert.ThrowsAsync<StreamingSubscriptionCheckpointCorruptException>(
            () => store.LoadAsync(checkpoint.SubscriptionId).AsTask());
        Assert.Equal(path, exception.Path);
    }

    [Fact]
    public async Task LoadWithMismatchedSubscriptionId_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint checkpoint = Checkpoint("sub-original", 1, 1, 0);
        await store.SaveAsync(checkpoint, expectedRevision: -1);

        string path = Directory.GetFiles(directory.Path, "*.checkpoint.json").Single();
        StreamingSubscriptionCheckpoint tampered = Checkpoint("sub-other", 2, 2, 0);
        await File.WriteAllBytesAsync(path, StreamingSubscriptionJson.Serialize(tampered));

        await Assert.ThrowsAsync<StreamingSubscriptionCheckpointCorruptException>(
            () => store.LoadAsync(checkpoint.SubscriptionId).AsTask());
    }

    [Fact]
    public async Task ConcurrentStores_OnlyOneConditionalWriteWins()
    {
        using var directory = new TemporaryDirectory();
        await using var firstStore = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        await using var secondStore = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint initial = Checkpoint("sub-concurrent", 0, 0, 0);
        await firstStore.SaveAsync(initial, expectedRevision: -1);

        StreamingSubscriptionCheckpoint firstCandidate = initial with
        {
            CommittedSequence = 1,
            WatermarkUtc = Utc(1),
            Revision = 1,
        };
        StreamingSubscriptionCheckpoint secondCandidate = initial with
        {
            CommittedSequence = 2,
            WatermarkUtc = Utc(2),
            Revision = 1,
        };

        Task<Exception?> first = TrySaveAsync(firstStore, firstCandidate);
        Task<Exception?> second = TrySaveAsync(secondStore, secondCandidate);
        Exception?[] outcomes = await Task.WhenAll(first, second);

        Assert.Equal(1, outcomes.Count(static outcome => outcome is null));
        StreamingSubscriptionCheckpointConflictException conflict = Assert.IsType<StreamingSubscriptionCheckpointConflictException>(
            outcomes.Single(static outcome => outcome is not null));
        Assert.Equal(1, conflict.ActualRevision);

        StreamingSubscriptionCheckpoint? winner = await firstStore.LoadAsync(initial.SubscriptionId);
        Assert.NotNull(winner);
        Assert.Contains(winner.CommittedSequence, new long[] { 1, 2 });
        Assert.Equal(1, winner.Revision);
    }

    [Fact]
    public async Task HeldFileLock_StopsAtConfiguredTimeout()
    {
        using var directory = new TemporaryDirectory();
        await using var seedStore = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint initial = Checkpoint("sub-timeout", 0, 0, 0);
        await seedStore.SaveAsync(initial, expectedRevision: -1);

        string lockPath = Directory.GetFiles(directory.Path, "*.lock").Single();
        using var heldLock = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using var blockedStore = new FileStreamingSubscriptionCheckpointStore(
            directory.Path,
            operationTimeout: TimeSpan.FromMilliseconds(100));
        StreamingSubscriptionCheckpoint next = initial with
        {
            CommittedSequence = 1,
            WatermarkUtc = Utc(1),
            Revision = 1,
        };

        await Assert.ThrowsAsync<TimeoutException>(
            () => blockedStore.SaveAsync(next, expectedRevision: 0).AsTask());
    }

    [Fact]
    public async Task CancelledOperation_DoesNotLeaveTemporaryPublication()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.LoadAsync("sub-cancelled", cancellation.Token).AsTask());
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp-*"));
    }

    [Fact]
    public async Task SaveWithRegressingSequenceOrWatermark_RejectsWrite()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new FileStreamingSubscriptionCheckpointStore(directory.Path);
        StreamingSubscriptionCheckpoint initial = Checkpoint("sub-monotonic", 5, 5, 0);
        await store.SaveAsync(initial, expectedRevision: -1);
        StreamingSubscriptionCheckpoint regressing = initial with
        {
            CommittedSequence = 4,
            WatermarkUtc = Utc(4),
            Revision = 1,
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.SaveAsync(regressing, expectedRevision: 0).AsTask());
        Assert.Equal(initial, await store.LoadAsync(initial.SubscriptionId));
    }

    private static async Task<Exception?> TrySaveAsync(
        FileStreamingSubscriptionCheckpointStore store,
        StreamingSubscriptionCheckpoint checkpoint)
    {
        try
        {
            await store.SaveAsync(checkpoint, expectedRevision: 0);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static StreamingSubscriptionCheckpoint Checkpoint(
        string subscriptionId,
        long sequence,
        long watermarkSeconds,
        long revision)
        => new(
            StreamingSubscriptionCheckpoint.CurrentFormatVersion,
            subscriptionId,
            sequence,
            Utc(watermarkSeconds),
            revision);

    private static DateTimeOffset Utc(long seconds)
        => DateTimeOffset.UnixEpoch.AddSeconds(seconds);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sonnetdb-streaming-checkpoint-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
