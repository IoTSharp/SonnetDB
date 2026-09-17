using System.Text;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvAtomicCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sonnetdb-kv-atomic-cancellation-tests", Guid.NewGuid().ToString("N"));

    public KvAtomicCancellationTests() => Directory.CreateDirectory(_root);

    public static TheoryData<string, int> Operations => new()
    {
        { "set", 0 }, { "nx", 0 }, { "xx", 0 }, { "exchange", 0 },
        { "delete", 0 }, { "cas", 0 }, { "expire", 0 }, { "persist", 0 },
        { "set", 1 }, { "nx", 1 }, { "xx", 1 }, { "exchange", 1 },
        { "delete", 1 }, { "cas", 1 }, { "expire", 1 }, { "persist", 1 },
        { "set", 2 }, { "nx", 2 }, { "xx", 2 }, { "exchange", 2 },
        { "delete", 2 }, { "cas", 2 }, { "expire", 2 }, { "persist", 2 },
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void AtomicWrite_PreCanceledToken_DoesNotAppendOrConsumeSequence(string operation, int keyKind)
    {
        using var keyspace = Open("pre-canceled");
        string physicalKey = PhysicalKey(keyKind);
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(1);
        long version = keyspace.Put(physicalKey, [1], expiry);
        long walLength = keyspace.ActiveWalLength;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Execute(
            keyspace, operation, keyKind, version, cancellation.Token));

        Assert.Equal(version, keyspace.LastSequence);
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal([1], keyspace.Get(physicalKey));
        Assert.Equal(expiry, keyspace.GetEntry(physicalKey)!.ExpiresAtUtc);
        Assert.Null(keyspace.Get(keyKind == 2 ? "scope:missing" : "missing"));
        Execute(keyspace, operation, keyKind, version, CancellationToken.None);
        Assert.Equal(version + 1, keyspace.LastSequence);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("nx")]
    [InlineData("xx")]
    [InlineData("exchange")]
    [InlineData("delete")]
    [InlineData("cas")]
    [InlineData("expire")]
    [InlineData("persist")]
    public async Task AtomicWrite_CanceledDuringBackpressure_DoesNotAppendAndAllowsNextWrite(string operation)
    {
        using var keyspace = Open("backpressure", TestOptions() with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 2,
            CheckpointWriteBackpressureTimeout = TimeSpan.FromSeconds(20),
        });
        long version = keyspace.Put("record", [1], DateTimeOffset.UtcNow.AddHours(1));
        using var checkpointFrozen = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        using var writerWaiting = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        keyspace.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            checkpointFrozen.Set();
            if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("The test did not release the checkpoint.");
        };
        Task<long> checkpoint = Task.Run(keyspace.Compact);
        Task<Exception?>? writer = null;
        long sequence = 0;
        try
        {
            Assert.True(checkpointFrozen.Wait(TimeSpan.FromSeconds(5)));
            keyspace.Put("budget:1", [1]);
            keyspace.Put("budget:2", [2]);
            sequence = keyspace.LastSequence;
            long walLength = keyspace.ActiveWalLength;
            keyspace.WriteBackpressureTestHook = writerWaiting.Set;
            writer = Task.Run<Exception?>(() => Record.Exception(() => Execute(
                keyspace, operation, 0, version, cancellation.Token)));
            Assert.True(writerWaiting.Wait(TimeSpan.FromSeconds(5)));
            cancellation.Cancel();

            Assert.IsType<OperationCanceledException>(await writer.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(sequence, keyspace.LastSequence);
            Assert.Equal(walLength, keyspace.ActiveWalLength);
            Assert.Equal([1], keyspace.Get("record"));
            Assert.Equal(version, keyspace.GetEntry("record")!.Version);
            Assert.Null(keyspace.Get("missing"));
        }
        finally
        {
            cancellation.Cancel();
            keyspace.CheckpointTestHook = null;
            keyspace.WriteBackpressureTestHook = null;
            releaseCheckpoint.Set();
            await checkpoint.WaitAsync(TimeSpan.FromSeconds(5));
            if (writer is not null)
                await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }

        keyspace.Compact();
        Execute(keyspace, operation, 0, version, CancellationToken.None);
        Assert.Equal(sequence + 1, keyspace.LastSequence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AtomicWrite_BlockedKeyspaceLock_CancelsOrTimesOutBeforeLockIsReleased(bool cancel)
    {
        using var keyspace = Open("locked", TestOptions() with
        {
            SyncWalOnEveryWrite = true,
            CheckpointWriteBackpressureTimeout = cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(100),
        });
        long version = keyspace.Put("record", [1]);
        using var lockHeld = new ManualResetEventSlim();
        using var releaseLock = new ManualResetEventSlim();
        using var writerStarted = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        keyspace.WalSyncTestHook = () =>
        {
            lockHeld.Set();
            if (!releaseLock.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("The test did not release the keyspace lock.");
        };
        Task<long> blocker = Task.Run(() => keyspace.Put("blocker", [0]));
        Task<Exception?>? writer = null;
        try
        {
            Assert.True(lockHeld.Wait(TimeSpan.FromSeconds(5)));
            writer = Task.Run<Exception?>(() =>
            {
                writerStarted.Set();
                return Record.Exception(() => keyspace.GetAndSet("record", [2], null, cancellation.Token));
            });
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));
            if (cancel)
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            Exception? error = await writer.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancel)
                Assert.IsType<OperationCanceledException>(error);
            else
                Assert.IsType<TimeoutException>(error);
        }
        finally
        {
            cancellation.Cancel();
            releaseLock.Set();
            await blocker.WaitAsync(TimeSpan.FromSeconds(5));
            if (writer is not null)
                await writer.WaitAsync(TimeSpan.FromSeconds(5));
            keyspace.WalSyncTestHook = null;
        }

        Assert.Equal([1], keyspace.Get("record"));
        Assert.Equal(version + 1, keyspace.LastSequence);
        Assert.Equal(version + 2, keyspace.Put("next", [3]));
    }

    [Theory]
    [InlineData("set")]
    [InlineData("nx")]
    [InlineData("xx")]
    [InlineData("exchange")]
    [InlineData("delete")]
    [InlineData("cas")]
    [InlineData("expire")]
    [InlineData("persist")]
    public void AtomicWrite_CanceledAfterWalAppend_ReturnsCommitAndRecoversIt(string operation)
    {
        KvOptions options = TestOptions() with { SyncWalOnEveryWrite = true };
        long committedSequence;
        using (var keyspace = Open("committed", options))
        {
            long version = keyspace.Put("record", [1], DateTimeOffset.UtcNow.AddHours(1));
            using var cancellation = new CancellationTokenSource();
            keyspace.WalSyncTestHook = cancellation.Cancel;
            Execute(keyspace, operation, 0, version, cancellation.Token);
            keyspace.WalSyncTestHook = null;
            Assert.True(cancellation.IsCancellationRequested);
            committedSequence = keyspace.LastSequence;
            Assert.Equal(version + 1, committedSequence);
        }

        using var reopened = Open("committed", options);
        Assert.Equal(committedSequence, reopened.LastSequence);
        if (operation == "delete")
            Assert.Null(reopened.Get("record"));
        else if (operation == "nx")
            Assert.Equal([2], reopened.Get("missing"));
        else if (operation == "persist")
            Assert.Null(reopened.GetEntry("record")!.ExpiresAtUtc);
        else if (operation == "expire")
            Assert.NotNull(reopened.GetEntry("record")!.ExpiresAtUtc);
        else
            Assert.Equal([2], reopened.Get("record"));
    }

    [Fact]
    public void CompareAndSet_CanceledAfterExpiredCleanup_CompletesTheReplacement()
    {
        KvOptions options = TestOptions() with { SyncWalOnEveryWrite = true };
        long committedSequence;
        using (var keyspace = Open("expired-cas", options))
        {
            long version = keyspace.Put("record", [1], DateTimeOffset.UtcNow.AddMinutes(-1));
            using var cancellation = new CancellationTokenSource();
            keyspace.WalSyncTestHook = cancellation.Cancel;
            KvCasResult result = keyspace.CompareAndSet("record", 0, [2], null, cancellation.Token);
            keyspace.WalSyncTestHook = null;

            Assert.True(cancellation.IsCancellationRequested);
            Assert.True(result.Succeeded);
            Assert.Equal([2], keyspace.Get("record"));
            committedSequence = keyspace.LastSequence;
            Assert.Equal(version + 2, committedSequence);
        }

        using var reopened = Open("expired-cas", options);
        Assert.Equal(committedSequence, reopened.LastSequence);
        Assert.Equal([2], reopened.Get("record"));
    }

    [Fact]
    public void GetAndDelete_CanceledBeforeDeleteAppend_DoesNotFaultOrConsumeSequence()
    {
        using var keyspace = Open("delete-pre-append");
        long version = keyspace.Put("record", [1]);
        long walLength = keyspace.ActiveWalLength;
        using var cancellation = new CancellationTokenSource();
        keyspace.BaseLookupTestHook = cancellation.Cancel;
        try
        {
            Assert.Throws<OperationCanceledException>(() => keyspace.GetAndDelete("record", cancellation.Token));
        }
        finally
        {
            keyspace.BaseLookupTestHook = null;
        }

        Assert.Equal(version, keyspace.LastSequence);
        Assert.Equal(walLength, keyspace.ActiveWalLength);
        Assert.Equal([1], keyspace.Get("record"));
        Assert.Equal(version + 1, keyspace.GetAndDelete("record").MutationVersion);
    }

    [Theory]
    [InlineData("cas")]
    [InlineData("expire")]
    [InlineData("persist")]
    public void AtomicWrite_SyncFailure_FaultsSubsequentWritesAndRecoversUnknownOutcome(string operation)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        const string name = "atomic-sync-failure";
        string path = Path.GetFullPath(Path.Combine(_root, name));
        KvOptions options = TestOptions() with { SyncWalOnEveryWrite = true };
        DateTimeOffset initialExpiry = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).AddHours(1);
        DateTimeOffset replacementExpiry = initialExpiry.AddHours(1);
        long originalVersion;
        try
        {
            using (var keyspace = Open(name, options))
            {
                originalVersion = keyspace.Put("record", [1], initialExpiry);
                long initialWalLength = keyspace.ActiveWalLength;
                var expectedError = new IOException("Injected atomic write WAL sync failure.");
                Action mutation = operation switch
                {
                    "cas" => () => keyspace.CompareAndSet("record", originalVersion, [2], null, deadline.Token),
                    "expire" => () => keyspace.ExpireAt("record", replacementExpiry, deadline.Token),
                    "persist" => () => keyspace.Persist("record", deadline.Token),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation)),
                };
                keyspace.WalSyncTestHook = () => throw expectedError;
                try
                {
                    IOException actualError = Assert.Throws<IOException>(mutation);
                    Assert.Same(expectedError, actualError);
                    Assert.True(keyspace.IsWriteCommitOutcomeUnknown(actualError));
                }
                finally
                {
                    keyspace.WalSyncTestHook = null;
                }

                Assert.True(keyspace.ActiveWalLength > initialWalLength);
                Assert.Equal(originalVersion, keyspace.LastSequence);
                Assert.Equal([1], keyspace.Get("record"));
                Assert.Equal(initialExpiry, keyspace.GetEntry("record")!.ExpiresAtUtc);
                Assert.Throws<IOException>(mutation);
                Assert.Throws<IOException>(() => keyspace.Put("blocked", [9]));
                Assert.Equal(originalVersion, keyspace.LastSequence);
            }

            using var reopened = Open(name, options);
            KvEntry recovered = Assert.IsType<KvEntry>(reopened.GetEntry("record"));
            Assert.Equal(originalVersion + 1, recovered.Version);
            Assert.Equal(operation == "cas" ? new byte[] { 2 } : new byte[] { 1 }, recovered.Value.ToArray());
            Assert.Equal(operation == "expire" ? replacementExpiry : (DateTimeOffset?)null, recovered.ExpiresAtUtc);
            Assert.Null(reopened.Get("blocked"));
            Assert.Equal(originalVersion + 2, reopened.Put("after-reopen", [3]));
        }
        finally
        {
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The sync failure test cleanup path is not owned by this test.");
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    public void Dispose()
    {
        string resolved = Path.GetFullPath(_root);
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sonnetdb-kv-atomic-cancellation-tests"));
        if (!string.Equals(Path.GetDirectoryName(resolved), expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test cleanup path is outside its owned directory.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    private KvKeyspace Open(string name, KvOptions? options = null) =>
        KvKeyspace.Open(name, Path.Combine(_root, name), options ?? TestOptions());

    private static KvOptions TestOptions() => KvOptions.Default with
    {
        AutoCheckpointEnabled = false,
        SyncWalOnEveryWrite = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static string PhysicalKey(int keyKind) => keyKind == 2 ? "scope:record" : "record";

    private static void Execute(
        KvKeyspace keyspace, string operation, int keyKind, long version, CancellationToken cancellationToken)
    {
        string key = operation == "nx" ? "missing" : "record";
        DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(2);
        KvSetCondition condition = operation switch
        {
            "nx" => KvSetCondition.IfNotExists,
            "xx" => KvSetCondition.IfExists,
            _ => KvSetCondition.Always,
        };
        if (keyKind == 2)
        {
            KvNamespace view = keyspace.Namespace("scope");
            _ = operation switch
            {
                "set" or "nx" or "xx" => (object)view.Set(key, [2], condition, null, cancellationToken),
                "exchange" => view.GetAndSet(key, [2], null, cancellationToken),
                "delete" => view.GetAndDelete(key, cancellationToken),
                "cas" => view.CompareAndSet(key, version, [2], null, cancellationToken),
                "expire" => view.ExpireAt(key, expiry, cancellationToken),
                "persist" => view.Persist(key, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            return;
        }
        if (keyKind == 1)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            _ = operation switch
            {
                "set" or "nx" or "xx" => (object)keyspace.Set(bytes, [2], condition, null, cancellationToken),
                "exchange" => keyspace.GetAndSet(bytes, [2], null, cancellationToken),
                "delete" => keyspace.GetAndDelete(bytes, cancellationToken),
                "cas" => keyspace.CompareAndSet(bytes, version, [2], null, cancellationToken),
                "expire" => keyspace.ExpireAt(bytes, expiry, cancellationToken),
                "persist" => keyspace.Persist(bytes, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            return;
        }

        _ = operation switch
        {
            "set" or "nx" or "xx" => (object)keyspace.Set(key, [2], condition, null, cancellationToken),
            "exchange" => keyspace.GetAndSet(key, [2], null, cancellationToken),
            "delete" => keyspace.GetAndDelete(key, cancellationToken),
            "cas" => keyspace.CompareAndSet(key, version, [2], null, cancellationToken),
            "expire" => keyspace.ExpireAt(key, expiry, cancellationToken),
            "persist" => keyspace.Persist(key, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }
}
