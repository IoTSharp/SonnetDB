using SonnetDB.Engine;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-kv-checkpoint-" + Guid.NewGuid().ToString("N"));

    public KvCheckpointTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void AutomaticCheckpoint_WalByteThreshold_BoundsHotKeyWalAndRecoversLatestValue()
    {
        const long maxWalBytes = 1024;
        byte[] expected = [];
        using (var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = maxWalBytes,
            MaxOverlayEntries = int.MaxValue,
            SyncWalOnEveryWrite = false,
        }))
        {
            var kv = db.Keyspaces.Open("hot-key");
            for (int i = 0; i < 64; i++)
            {
                expected = Enumerable.Repeat((byte)i, 512).ToArray();
                kv.Put("relationship", expected);
            }

            WaitForCompletedCheckpoint(kv);
            Assert.True(kv.ActiveWalLength < maxWalBytes);
            Assert.Equal(0, kv.MutableOverlayEntryCount);
            Assert.Equal(0, kv.PendingOverlayEntryCount);
            Assert.Equal(expected, kv.Get("relationship"));
        }

        using var reopened = Open(AutoCheckpointDisabled());
        Assert.Equal(expected, reopened.Keyspaces.Open("hot-key").Get("relationship"));
    }

    [Fact]
    public void AutomaticCheckpoint_OverlayEntryThreshold_BoundsDistinctKeysAndRecoversAllValues()
    {
        using (var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = long.MaxValue,
            MaxOverlayEntries = 4,
            SyncWalOnEveryWrite = false,
        }))
        {
            var kv = db.Keyspaces.Open("distinct-keys");
            for (int i = 0; i < 32; i++)
                kv.Put($"relationship:{i:D2}", [(byte)i]);

            WaitForCompletedCheckpoint(kv);
            Assert.True(kv.MutableOverlayEntryCount < 4);
            Assert.Equal(0, kv.PendingOverlayEntryCount);
            Assert.Equal(32, kv.Count);
        }

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("distinct-keys");
        for (int i = 0; i < 32; i++)
            Assert.Equal([(byte)i], restored.Get($"relationship:{i:D2}")!);
    }

    [Fact]
    public void AutomaticCheckpoint_LargeImageLikeValues_BoundsWalAndRecoversEveryValue()
    {
        const int valueBytes = 1024 * 1024;
        const long maxWalBytes = 4L * 1024 * 1024;
        using (var db = Open(KvOptions.Default with
        {
            AutoCheckpointEnabled = true,
            MaxWalBytes = maxWalBytes,
            MaxOverlayEntries = int.MaxValue,
            SyncWalOnEveryWrite = false,
        }))
        {
            var kv = db.Keyspaces.Open("camera-images");
            for (int i = 0; i < 12; i++)
            {
                byte[] value = new byte[valueBytes];
                value.AsSpan().Fill((byte)i);
                kv.Put($"capture:{i:D2}", value);
            }

            WaitForCompletedCheckpoint(kv);
            Assert.True(kv.ActiveWalLength < maxWalBytes);
            Assert.Equal(0, kv.MutableOverlayEntryCount);
            Assert.Equal(0, kv.PendingOverlayEntryCount);
            Assert.Equal(12, kv.Count);
        }

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("camera-images");
        Assert.Equal(12, restored.Count);
        for (int i = 0; i < 12; i++)
        {
            byte[] value = restored.Get($"capture:{i:D2}")!;
            Assert.Equal(valueBytes, value.Length);
            Assert.Equal(-1, value.AsSpan().IndexOfAnyExcept((byte)i));
        }
    }

    [Fact]
    public async Task Compact_WhileStateWritePaused_AllowsReadsAndWritesAndPreservesOverlayPrecedence()
    {
        using var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("concurrent");
        kv.Put("shared", [1]);
        kv.Compact();
        kv.Put("frozen", [4]);

        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        kv.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            frozen.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("测试未释放冻结的 KV checkpoint。");
        };

        Task<long> checkpoint = StartDedicated(kv.Compact);
        Assert.True(frozen.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            await StartDedicated(() =>
            {
                Assert.Equal([1], kv.Get("shared"));
                Assert.Equal([4], kv.Get("frozen"));
                kv.Put("shared", [2]);
                kv.Put("after", [3]);
                return true;
            }).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.Set();
        }

        await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([2], kv.Get("shared"));
        Assert.Equal([4], kv.Get("frozen"));
        Assert.Equal([3], kv.Get("after"));
    }

    [Fact]
    public void Compact_CrashAfterWalRotationBeforeStatePublish_ReplaysSealedThenActive()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("before-publish");
        kv.Put("before", [1]);
        kv.Put("shared", [2]);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterFreeze)
                throw new IOException("simulated crash after WAL rotation");
        };

        Assert.Throws<IOException>(() => kv.Compact());
        Assert.NotEmpty(SealedWalFiles(kv));
        Assert.True(File.Exists(KvKeyspace.ActiveWalPath(kv.RootDirectory)));
        kv.Put("after", [3]);
        kv.Put("shared", [4]);
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("before-publish");
        Assert.Equal([1], restored.Get("before"));
        Assert.Equal([3], restored.Get("after"));
        Assert.Equal([4], restored.Get("shared"));
        Assert.Equal(4, restored.LastSequence);
    }

    [Fact]
    public void Compact_OpenStateFailureAfterAtomicMove_DeletesCandidateAndRecoversFromWal()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("invalid-state");
        kv.Put("before", [1]);
        string statePath = KvKeyspace.SegmentPath(kv.RootDirectory, kv.LastSequence);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterStateSavedBeforePublish)
                File.WriteAllBytes(statePath, [0]);
        };

        Assert.Throws<InvalidDataException>(() => kv.Compact());
        Assert.False(File.Exists(statePath));
        Assert.NotEmpty(SealedWalFiles(kv));
        kv.Put("after", [2]);
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("invalid-state");
        Assert.Equal([1], restored.Get("before"));
        Assert.Equal([2], restored.Get("after"));
        Assert.Equal(2, restored.LastSequence);
    }

    [Fact]
    public void Compact_CrashAfterStatePublishBeforeWalCleanup_SkipsCoveredWalAndRemainsIdempotent()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("after-publish");
        kv.Put("before", [1]);
        kv.Put("shared", [2]);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterStatePublishBeforeWalCleanup)
                throw new IOException("simulated crash before sealed WAL cleanup");
        };

        Assert.Throws<IOException>(() => kv.Compact());
        Assert.NotEmpty(SealedWalFiles(kv));
        kv.Put("after", [3]);
        kv.Put("shared", [4]);
        db.CrashSimulationCloseWal();

        using (var reopened = Open(AutoCheckpointDisabled()))
        {
            var restored = reopened.Keyspaces.Open("after-publish");
            Assert.Equal([1], restored.Get("before"));
            Assert.Equal([3], restored.Get("after"));
            Assert.Equal([4], restored.Get("shared"));
            Assert.Equal(4, restored.LastSequence);
            restored.Compact();
            Assert.Empty(SealedWalFiles(restored));
        }

        using var reopenedAgain = Open(AutoCheckpointDisabled());
        Assert.Equal([4], reopenedAgain.Keyspaces.Open("after-publish").Get("shared"));
    }

    [Fact]
    public void Compact_ReopenWithCoveredSealedWal_CleansWalWithoutRewritingState()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("covered-wal");
        kv.Put("relationship", [1]);
        kv.CheckpointTestHook = phase =>
        {
            if (phase == KvCheckpointPhase.AfterStatePublishBeforeWalCleanup)
                throw new IOException("simulated crash before sealed WAL cleanup");
        };

        Assert.Throws<IOException>(() => kv.Compact());
        Assert.NotEmpty(SealedWalFiles(kv));
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("covered-wal");
        Assert.Equal([1], restored.Get("relationship"));
        restored.Compact();

        Assert.Empty(SealedWalFiles(restored));
        Assert.Equal(64, restored.ActiveWalLength);
        Assert.Equal([1], restored.Get("relationship"));
    }

    [Fact]
    public async Task Clear_WhileCheckpointFrozen_DoesNotPublishOrResurrectPreviousGeneration()
    {
        var db = Open(AutoCheckpointDisabled());
        var kv = db.Keyspaces.Open("clear-race");
        kv.Put("old-disk", [1]);
        kv.Compact();
        kv.Put("old-frozen", [2]);

        using var frozen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        kv.CheckpointTestHook = phase =>
        {
            if (phase != KvCheckpointPhase.AfterFreeze)
                return;
            frozen.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("测试未释放冻结的 KV checkpoint。");
        };

        Task<long> checkpoint = StartDedicated(kv.Compact);
        Assert.True(frozen.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            KvClearResult clear = await StartDedicated(kv.Clear).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, clear.Generation);
            Assert.Null(kv.Get("old-disk"));
            Assert.Null(kv.Get("old-frozen"));
            kv.Put("new", [3]);
        }
        finally
        {
            release.Set();
        }

        await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(kv.Get("old-disk"));
        Assert.Null(kv.Get("old-frozen"));
        Assert.Equal([3], kv.Get("new"));
        Assert.Empty(SealedWalFiles(kv));
        db.CrashSimulationCloseWal();

        using var reopened = Open(AutoCheckpointDisabled());
        var restored = reopened.Keyspaces.Open("clear-race");
        Assert.Equal(1, restored.Generation);
        Assert.Null(restored.Get("old-disk"));
        Assert.Null(restored.Get("old-frozen"));
        Assert.Equal([3], restored.Get("new"));
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

    private static string[] SealedWalFiles(KvKeyspace keyspace)
        => Directory.GetFiles(KvKeyspace.WalDirectory(keyspace.RootDirectory), "sealed-*.SDBKVWAL");

    private static void WaitForCompletedCheckpoint(KvKeyspace keyspace)
    {
        bool completed = SpinWait.SpinUntil(
            () => keyspace.MutableOverlayEntryCount == 0
                && keyspace.PendingOverlayEntryCount == 0
                && Directory.EnumerateFiles(
                    KvKeyspace.SegmentsDirectory(keyspace.RootDirectory),
                    "*.SDBKVSEG").Any(),
            TimeSpan.FromSeconds(10));
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
            Name = "SonnetDB.KvCheckpointTest",
        };
        thread.Start();
        return completion.Task;
    }
}
