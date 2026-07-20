using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvCheckpointSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-checkpoint-safety-" + Guid.NewGuid().ToString("N"));

    public KvCheckpointSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Reopen_UncoveredSealedWal_AutomaticallyCheckpointsWithoutAnotherWrite()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("uncovered-restart");
        kv.Put("relationship", [1]);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterFreeze)
                throw new IOException("simulated crash before state write");
        };

        Assert.Throws<IOException>(() => kv.Compact());
        Assert.NotEmpty(SealedWalFiles(kv));
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutomaticCheckpointWithLargeThresholds());
        var restored = reopened.Keyspaces.Open("uncovered-restart");
        WaitForWalCleanup(restored);

        Assert.Equal([1], restored.Get("relationship")!);
        Assert.Empty(SealedWalFiles(restored));
        Assert.Equal(64, restored.ActiveWalLength);
    }

    [Fact]
    public void Reopen_StateCoveredSealedWal_AutomaticallyCleansWithoutAnotherWrite()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("covered-restart");
        kv.Put("relationship", [2]);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterStatePublishBeforeWalCleanup)
                throw new IOException("simulated crash after durable state publish");
        };

        Assert.Throws<IOException>(() => kv.Compact());
        Assert.NotEmpty(SealedWalFiles(kv));
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutomaticCheckpointWithLargeThresholds());
        var restored = reopened.Keyspaces.Open("covered-restart");
        WaitForWalCleanup(restored);

        Assert.Equal([2], restored.Get("relationship")!);
        Assert.Empty(SealedWalFiles(restored));
        Assert.Equal(64, restored.ActiveWalLength);
    }

    [Fact]
    public async Task PausedCheckpoint_StopsWritesAtBudgetUntilNextOverlayIsFrozen()
    {
        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 2,
            CheckpointWriteBackpressureTimeout = TimeSpan.FromSeconds(20),
            SyncWalOnEveryWrite = false,
        });
        var kv = db.Keyspaces.Open("bounded-writes");
        using var firstCheckpointFrozen = new ManualResetEventSlim();
        using var releaseFirstCheckpoint = new ManualResetEventSlim();
        using var writerEnteredBackpressure = new ManualResetEventSlim();
        kv.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            firstCheckpointFrozen.Set();
            if (!releaseFirstCheckpoint.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the paused checkpoint");
        };

        kv.Put("before:1", [1]);
        kv.Put("before:2", [2]);
        Assert.True(firstCheckpointFrozen.Wait(TimeSpan.FromSeconds(10)));

        try
        {
            kv.Put("during:1", [3]);
            kv.Put("during:2", [4]);
            long boundedWalLength = kv.ActiveWalLength;
            Assert.Equal(2, kv.MutableOverlayEntryCount);

            kv.WriteBackpressureTestHook = writerEnteredBackpressure.Set;
            Task<long> blockedWrite = StartDedicated(() => kv.Put("blocked", [5]));
            Assert.True(writerEnteredBackpressure.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(blockedWrite.IsCompleted);
            Assert.Equal(2, kv.MutableOverlayEntryCount);
            Assert.Equal(boundedWalLength, kv.ActiveWalLength);

            kv.CheckpointTestHook = null;
            releaseFirstCheckpoint.Set();
            await blockedWrite.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal([5], kv.Get("blocked")!);
        }
        finally
        {
            kv.CheckpointTestHook = null;
            releaseFirstCheckpoint.Set();
        }

        kv.Compact();
        WaitForCompletedCheckpoint(kv);
    }

    [Fact]
    public async Task DeleteMany_BackpressureWait_ReevaluatesKeysAfterConcurrentClear()
    {
        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 2,
            CheckpointWriteBackpressureTimeout = TimeSpan.FromSeconds(20),
            SyncWalOnEveryWrite = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        });
        var kv = db.Keyspaces.Open("delete-replan-after-wait");
        using var checkpointFrozen = new ManualResetEventSlim();
        using var releaseCheckpoint = new ManualResetEventSlim();
        using var writerWaiting = new ManualResetEventSlim();
        Task<long>? checkpoint = null;
        Task<int>? delete = null;

        kv.Put("expired", [1], DateTimeOffset.UtcNow.AddMinutes(-1));
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterFreeze)
            {
                checkpointFrozen.Set();
                if (!releaseCheckpoint.Wait(TimeSpan.FromSeconds(20)))
                    throw new TimeoutException("test did not release the paused checkpoint");
            }
        };

        try
        {
            checkpoint = StartDedicated(kv.Compact);
            Assert.True(checkpointFrozen.Wait(TimeSpan.FromSeconds(10)));
            kv.Put("pressure:1", [2]);
            kv.Put("pressure:2", [3]);

            kv.WriteBackpressureTestHook = writerWaiting.Set;
            delete = StartDedicated(() => kv.DeleteMany(["expired"]));
            Assert.True(writerWaiting.Wait(TimeSpan.FromSeconds(10)));
            kv.WriteBackpressureTestHook = null;
            long sequenceBeforeClear = kv.LastSequence;

            KvClearResult clear = kv.Clear();

            Assert.Equal(1, clear.Generation);
            Assert.Equal(0, await delete.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(sequenceBeforeClear + 1, kv.LastSequence);
            Assert.Null(kv.Get("expired"));
        }
        finally
        {
            kv.WriteBackpressureTestHook = null;
            kv.CheckpointTestHook = null;
            releaseCheckpoint.Set();
            if (checkpoint is not null)
                await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
            if (delete is not null)
                await delete.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public void PausedCheckpoint_InOneKeyspace_DoesNotStarveAnotherKeyspace()
    {
        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 1,
            SyncWalOnEveryWrite = false,
        });
        var slow = db.Keyspaces.Open("slow-keyspace");
        var independent = db.Keyspaces.Open("independent-keyspace");
        using var slowFrozen = new ManualResetEventSlim();
        using var releaseSlow = new ManualResetEventSlim();
        slow.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            slowFrozen.Set();
            if (!releaseSlow.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the slow keyspace checkpoint");
        };

        slow.Put("slow", [1]);
        Assert.True(slowFrozen.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            independent.Put("independent", [2]);
            WaitForCompletedCheckpoint(independent);
            Assert.Equal([2], independent.Get("independent")!);
        }
        finally
        {
            slow.CheckpointTestHook = null;
            releaseSlow.Set();
        }

        WaitForCompletedCheckpoint(slow);
    }

    [Fact]
    public void AutomaticCheckpoint_TransientDirectoryFsyncFailure_RetriesWithoutAnotherWrite()
    {
        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 1,
            SyncWalOnEveryWrite = false,
        });
        var kv = db.Keyspaces.Open("retry-after-fsync");
        int attempts = 0;
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.BeforeStateDirectoryFsync
                && Interlocked.Increment(ref attempts) == 1)
            {
                throw new IOException("injected directory fsync failure");
            }
        };

        kv.Put("relationship", [3]);
        WaitForCompletedCheckpoint(kv, TimeSpan.FromSeconds(15));

        Assert.True(Volatile.Read(ref attempts) >= 2);
        Assert.Null(kv.LastCheckpointException);
        Assert.Equal([3], kv.Get("relationship")!);
    }

    [Fact]
    public void AutomaticCheckpoint_PostPublishCleanupRetry_ClearsStaleFailureBeforeLaterWrites()
    {
        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 1,
            SyncWalOnEveryWrite = false,
        });
        var kv = db.Keyspaces.Open("retry-after-publish");
        int failures = 0;
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterStatePublishBeforeWalCleanup
                && Interlocked.Increment(ref failures) == 1)
            {
                throw new IOException("injected post-publish cleanup failure");
            }
        };

        kv.Put("first", [1]);
        WaitForWalCleanup(kv);
        Assert.Null(kv.LastCheckpointException);

        kv.Put("second", [2]);
        WaitForCompletedCheckpoint(kv);
        Assert.Equal([2], kv.Get("second")!);
        Assert.Null(kv.LastCheckpointException);
    }

    [Fact]
    public void Compact_DirectoryFsyncFailure_PreservesSealedWalForRecovery()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("fsync-failure");
        kv.Put("relationship", [4]);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.BeforeStateDirectoryFsync)
                throw new IOException("injected directory fsync failure");
        };

        Assert.Throws<IOException>(() => kv.Compact());
        Assert.NotEmpty(SealedWalFiles(kv));
        Assert.Empty(Directory.GetFiles(KvKeyspace.SegmentsDirectory(kv.RootDirectory), "*.SDBKVSEG"));
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        Assert.Equal([4], reopened.Keyspaces.Open("fsync-failure").Get("relationship")!);
    }

    [Fact]
    public void Compact_StateValueCrcFailure_RejectsCandidateAndPreservesSealedWal()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("state-value-corruption");
        byte[] expected = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
        kv.Put("image", expected);
        string statePath = KvKeyspace.SegmentPath(kv.RootDirectory, kv.LastSequence);
        kv.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterStateSavedBeforePublish)
                return;

            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Span<byte> prefix = stackalloc byte[24];
            stream.Position = 64;
            stream.ReadExactly(prefix);
            int keyLength = BinaryPrimitives.ReadInt32LittleEndian(prefix[..4]);
            stream.Position = 64 + prefix.Length + keyLength;
            int valueByte = stream.ReadByte();
            Assert.True(valueByte >= 0);
            stream.Position--;
            stream.WriteByte((byte)(valueByte ^ 0xFF));
            stream.Flush(flushToDisk: true);
        };

        Assert.Throws<InvalidDataException>(() => kv.Compact());
        Assert.False(File.Exists(statePath));
        Assert.NotEmpty(SealedWalFiles(kv));
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        Assert.Equal(expected, reopened.Keyspaces.Open("state-value-corruption").Get("image")!);
    }

    [Fact]
    public void Clear_RebuildsActiveWalWithoutStartingAFullCheckpoint()
    {
        using (var db = Open(AutomaticCheckpointWithLargeThresholds()))
        {
            var kv = db.Keyspaces.Open("clear-reclaims-wal");
            kv.Put("old", [5]);

            KvClearResult result = kv.Clear();

            Assert.Equal(1, result.Generation);
            Assert.Equal(0, kv.Count);
            Assert.Empty(SealedWalFiles(kv));
            Assert.Equal(64, kv.ActiveWalLength);
            Assert.Empty(Directory.GetFiles(
                KvKeyspace.SegmentsDirectory(kv.RootDirectory),
                "*.SDBKVSEG"));
        }

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("clear-reclaims-wal");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old"));
    }

    [Fact]
    public void Clear_GenerationSaveFailure_LeavesCommittedClearVisibleAndFaultsWritesUntilReopen()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("clear-generation-failure");
        kv.Put("old", [1]);
        kv.GenerationSaveTestHook = () => throw new IOException("injected generation save failure");

        Assert.Throws<IOException>(() => kv.Clear());
        Assert.Null(kv.Get("old"));
        long committedWalLength = kv.ActiveWalLength;
        Assert.Throws<IOException>(() => kv.Put("new", [2]));
        Assert.Equal(committedWalLength, kv.ActiveWalLength);
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("clear-generation-failure");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old"));
        Assert.True(restored.Put("new", [2]) > 0);
    }

    [Fact]
    public void DeleteMany_AtomicBatchLargerThanWalBudget_IsRejectedBeforeAppend()
    {
        string[] keys = Enumerable.Range(0, 24).Select(i => $"key:{i:D2}").ToArray();
        using (var preparation = Open(AutoCheckpointDisabled()))
        {
            var kv = preparation.Keyspaces.Open("delete-wal-budget");
            foreach (string key in keys)
                kv.Put(key, [1], DateTimeOffset.UtcNow.AddMinutes(-1));
            kv.Compact();
        }

        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = 256,
            MaxOverlayEntries = int.MaxValue,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        });
        var restored = db.Keyspaces.Open("delete-wal-budget");
        long walLength = restored.ActiveWalLength;

        IOException error = Assert.Throws<IOException>(() => restored.DeleteMany(keys));
        Assert.Contains("fresh checkpoint budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(walLength, restored.ActiveWalLength);
    }

    [Fact]
    public void DeleteMany_AtomicBatchLargerThanOverlayBudget_IsRejectedBeforeAppend()
    {
        string[] keys = ["key:1", "key:2", "key:3"];
        using (var preparation = Open(AutoCheckpointDisabled()))
        {
            var kv = preparation.Keyspaces.Open("delete-overlay-budget");
            foreach (string key in keys)
                kv.Put(key, [1]);
            kv.Compact();
        }

        using var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 2,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        });
        var restored = db.Keyspaces.Open("delete-overlay-budget");
        long walLength = restored.ActiveWalLength;

        IOException error = Assert.Throws<IOException>(() => restored.DeleteMany(keys));
        Assert.Contains("fresh checkpoint budget", error.Message, StringComparison.Ordinal);
        Assert.Equal(walLength, restored.ActiveWalLength);
        Assert.Equal(3, restored.Count);
    }

    [Fact]
    public async Task Dispose_PausedCheckpoint_ReturnsAtConfiguredDeadlineAndDefersStateClose()
    {
        using var db = Open(AutoCheckpointDisabled() with
        {
            CheckpointShutdownTimeout = TimeSpan.FromMilliseconds(100),
        });
        var kv = db.Keyspaces.Open("bounded-dispose");
        kv.Put("base", [5]);
        long baseSequence = kv.Compact();
        string baseStatePath = KvKeyspace.SegmentPath(kv.RootDirectory, baseSequence);
        kv.Put("relationship", [6]);
        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        kv.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            frozen.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("test did not release the checkpoint during dispose");
        };
        Task<long> checkpoint = StartDedicated(kv.Compact);
        Assert.True(frozen.Wait(TimeSpan.FromSeconds(10)));

        var elapsed = Stopwatch.StartNew();
        kv.Dispose();
        elapsed.Stop();

        Assert.InRange(elapsed.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(2));
        release.Set();
        await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        kv.Dispose();
        string movedStatePath = baseStatePath + ".moved";
        File.Move(baseStatePath, movedStatePath);
        File.Delete(movedStatePath);
    }

    [Fact]
    public void Dispose_WalFlushFailure_StillClosesWalAndStateAndRemainsIdempotent()
    {
        using var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("dispose-flush-failure");
        kv.Put("base", [1]);
        long sequence = kv.Compact();
        string statePath = KvKeyspace.SegmentPath(kv.RootDirectory, sequence);
        string walPath = KvKeyspace.ActiveWalPath(kv.RootDirectory);
        kv.WalDisposeFlushTestHook = () => throw new IOException("injected WAL flush failure");

        Assert.Throws<IOException>(kv.Dispose);
        kv.Dispose();

        string movedStatePath = statePath + ".moved";
        string movedWalPath = walPath + ".moved";
        File.Move(statePath, movedStatePath);
        File.Move(walPath, movedWalPath);
        File.Delete(movedStatePath);
        File.Delete(movedWalPath);
    }

    private Tsdb Open(KvOptions options) => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = options with
        {
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    });

    private static KvOptions AutoCheckpointDisabled() => KvOptions.Default with
    {
        AutoCheckpointEnabled = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static KvOptions AutomaticCheckpointWithLargeThresholds() => KvOptions.Default with
    {
        AutoCheckpointEnabled = true,
        MaxWalBytes = long.MaxValue,
        MaxOverlayEntries = int.MaxValue,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static string[] SealedWalFiles(KvKeyspace keyspace)
        => Directory.GetFiles(KvKeyspace.WalDirectory(keyspace.RootDirectory), "sealed-*.SDBKVWAL");

    private static void WaitForWalCleanup(KvKeyspace keyspace)
    {
        bool completed = SpinWait.SpinUntil(
            () => SealedWalFiles(keyspace).Length == 0 && keyspace.ActiveWalLength == 64,
            TimeSpan.FromSeconds(10));
        Assert.True(completed, keyspace.LastCheckpointException?.ToString());
        Assert.Null(keyspace.LastCheckpointException);
    }

    private static void WaitForCompletedCheckpoint(KvKeyspace keyspace, TimeSpan? timeout = null)
    {
        bool completed = SpinWait.SpinUntil(
            () => keyspace.MutableOverlayEntryCount == 0
                && keyspace.PendingOverlayEntryCount == 0
                && Directory.EnumerateFiles(
                    KvKeyspace.SegmentsDirectory(keyspace.RootDirectory),
                    "*.SDBKVSEG").Any(),
            timeout ?? TimeSpan.FromSeconds(10));
        Assert.True(completed, keyspace.LastCheckpointException?.ToString());
        Assert.Null(keyspace.LastCheckpointException);
    }

    private static Task<T> StartDedicated<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "SonnetDB.KvCheckpointSafetyTest",
        };
        thread.Start();
        return completion.Task;
    }
}
