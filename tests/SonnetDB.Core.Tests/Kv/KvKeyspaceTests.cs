using System.Text;
using SonnetDB.Engine;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvKeyspaceTests : IDisposable
{
    private readonly string _root;

    public KvKeyspaceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PutGetDelete_WithBytes_RoundTripsCurrentValue()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("devices");

        long v1 = kv.Put("device:1", Encoding.UTF8.GetBytes("online"));
        long v2 = kv.Put("device:1", Encoding.UTF8.GetBytes("offline"));

        Assert.True(v2 > v1);
        Assert.Equal("offline", Encoding.UTF8.GetString(kv.Get("device:1")!));
        Assert.True(kv.Delete("device:1"));
        Assert.Null(kv.Get("device:1"));
        Assert.False(kv.Delete("device:1"));
    }

    [Fact]
    public void ScanPrefix_ReturnsSortedLimitedSnapshot()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("device:2", [2]);
        kv.Put("site:1", [9]);
        kv.Put("device:1", [1]);
        kv.Put("device:3", [3]);

        var rows = kv.ScanPrefix("device:", limit: 2);

        Assert.Equal(2, rows.Count);
        Assert.Equal("device:1", Encoding.UTF8.GetString(rows[0].Key.Span));
        Assert.Equal("device:2", Encoding.UTF8.GetString(rows[1].Key.Span));
        Assert.Equal([1], rows[0].Value.ToArray());
        Assert.Equal([2], rows[1].Value.ToArray());
    }

    [Fact]
    public void ScanPrefixAfter_WithContinuationKey_ReturnsNextPage()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("device:1", [1]);
        kv.Put("device:2", [2]);
        kv.Put("device:3", [3]);
        kv.Put("site:1", [9]);

        var rows = kv.ScanPrefixAfter("device:", "device:1", limit: 2);

        Assert.Equal(["device:2", "device:3"], rows.Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());
    }

    /// <summary>
    /// 验证内存态范围扫描同时遵守前缀、半开边界、页大小和严格排他的 continuation。
    /// </summary>
    [Fact]
    public void ScanRange_InMemory_EnforcesPrefixBoundsAndExclusiveContinuation()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("idx:a:00", [0]);
        kv.Put("idx:a:01", [1]);
        kv.Put("idx:a:02", [2]);
        kv.Put("idx:a:03", [3]);
        kv.Put("idx:a:04", [4]);
        kv.Put("idx:b:02", [9]);

        var firstPage = kv.ScanRange(
            "idx:a:",
            startInclusive: "idx:a:01",
            endExclusive: "idx:a:04",
            afterKey: null,
            limit: 2);
        var secondPage = kv.ScanRange(
            "idx:a:",
            startInclusive: "idx:a:01",
            endExclusive: "idx:a:04",
            afterKey: "idx:a:02",
            limit: 2);

        Assert.Equal(
            ["idx:a:01", "idx:a:02"],
            firstPage.Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());
        Assert.Equal(
            ["idx:a:03"],
            secondPage.Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());
    }

    /// <summary>
    /// 验证 checkpoint 冻结期间范围扫描正确合并可变层、冻结层和磁盘层的覆盖与删除。
    /// </summary>
    [Fact]
    public async Task ScanRange_DuringCheckpoint_MergesMutableFrozenAndDiskLayers()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");
        kv.Put("idx:01", Encoding.UTF8.GetBytes("disk-one"));
        kv.Put("idx:02", Encoding.UTF8.GetBytes("disk-two"));
        kv.Put("idx:03", Encoding.UTF8.GetBytes("disk-three"));
        kv.Compact();

        kv.Put("idx:02", Encoding.UTF8.GetBytes("frozen-two"));
        kv.Put("idx:04", Encoding.UTF8.GetBytes("frozen-four"));
        Assert.True(kv.Delete("idx:03"));

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

        Task<long> checkpoint = Task.Run(kv.Compact);
        try
        {
            Assert.True(frozen.Wait(TimeSpan.FromSeconds(10)));
            kv.Put("idx:01", Encoding.UTF8.GetBytes("mutable-one"));
            Assert.True(kv.Delete("idx:02"));
            kv.Put("idx:05", Encoding.UTF8.GetBytes("mutable-five"));

            var rows = kv.ScanRange("idx:", "idx:01", "idx:06", afterKey: null, limit: 10);

            Assert.Equal(
                ["idx:01", "idx:04", "idx:05"],
                rows.Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());
            Assert.Equal(
                ["mutable-one", "frozen-four", "mutable-five"],
                rows.Select(static row => Encoding.UTF8.GetString(row.Value.Span)).ToArray());
        }
        finally
        {
            release.Set();
        }

        await checkpoint.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CountPrefix_MatchesScanAndSkipsExpiredAndDeleted()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("device:1", [1]);
        kv.Put("device:2", [2]);
        kv.Put("device:3", [3]);
        kv.Put("site:1", [9]);
        kv.Put("device:expired", [4], DateTimeOffset.UtcNow.AddMilliseconds(-1));
        Assert.True(kv.Delete("device:2"));

        Assert.Equal(2, kv.CountPrefix("device:"));
        Assert.Equal(kv.ScanPrefix("device:", int.MaxValue).Count, kv.CountPrefix("device:"));
        Assert.Equal(3, kv.CountPrefix(""));
        Assert.Equal(0, kv.CountPrefix("missing:"));
    }

    [Fact]
    public void CountPrefix_AfterCompact_CountsDiskAndOverlay()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("device:1", [1]);
        kv.Put("device:2", [2]);
        kv.Compact();
        kv.Put("device:3", [3]);
        Assert.True(kv.Delete("device:1"));

        Assert.Equal(2, kv.CountPrefix("device:"));
        Assert.Equal(kv.ScanPrefix("device:", int.MaxValue).Count, kv.CountPrefix("device:"));
    }

    [Fact]
    public void Delete_DiskResidentKeyAfterCompact_HidesKeyInSession()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("assets");

        kv.Put("device:1", [1]);
        kv.Put("device:2", [2]);
        kv.Compact();

        Assert.True(kv.Delete("device:1"));

        Assert.Null(kv.Get("device:1"));
        Assert.False(kv.Delete("device:1"));
        Assert.Equal(1, kv.Count);
        Assert.Equal(
            ["device:2"],
            kv.ScanPrefix("device:", int.MaxValue).Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());
    }

    [Fact]
    public void Reopen_AfterWalOnlyWrites_ReplaysPutAndDelete()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("metadata");
            kv.Put("tenant:1", Encoding.UTF8.GetBytes("alpha"));
            kv.Put("tenant:2", Encoding.UTF8.GetBytes("beta"));
            Assert.True(kv.Delete("tenant:1"));
        }

        using (var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = reopened.Keyspaces.Open("metadata");
            Assert.Null(kv.Get("tenant:1"));
            Assert.Equal("beta", Encoding.UTF8.GetString(kv.Get("tenant:2")!));
            Assert.Equal(1, kv.Count);
        }
    }

    [Fact]
    public void CreateSnapshot_Reopen_LoadsSnapshotAndReplaysLaterWal()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("docs");
            kv.Put("doc:1", Encoding.UTF8.GetBytes("one"));
            long snapshotSequence = kv.CreateSnapshot();
            Assert.True(snapshotSequence > 0);

            kv.Put("doc:2", Encoding.UTF8.GetBytes("two"));
            Assert.True(kv.Delete("doc:1"));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("docs");
        Assert.Null(restored.Get("doc:1"));
        Assert.Equal("two", Encoding.UTF8.GetString(restored.Get("doc:2")!));
        Assert.Equal(1, restored.Count);
    }

    [Fact]
    public void Compact_Reopen_LoadsSegmentAndTruncatesWal()
    {
        string walPath;
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("small-objects");
            kv.Put("obj:a", [1, 2, 3]);
            kv.Put("obj:b", [4, 5]);
            long compactedSequence = kv.Compact();
            Assert.True(compactedSequence > 0);
            walPath = Path.Combine(_root, "kv", "keyspaces", "small-objects", "wal", "active.SDBKVWAL");
            Assert.True(File.Exists(walPath));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv2 = reopened.Keyspaces.Open("small-objects");
        Assert.Equal([1, 2, 3], kv2.Get("obj:a"));
        Assert.Equal([4, 5], kv2.Get("obj:b"));
        Assert.Equal(2, kv2.Count);
    }

    [Fact]
    public void Compact_Reopen_UsesDiskOrderedSegmentForPrefixScan()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("docs");
            kv.Put("doc:003", Encoding.UTF8.GetBytes("three"));
            kv.Put("doc:001", Encoding.UTF8.GetBytes("one"));
            kv.Put("doc:002", Encoding.UTF8.GetBytes("two"));
            kv.Compact();
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("docs");

        var firstPage = restored.ScanPrefix("doc:", limit: 2);
        Assert.Equal(["doc:001", "doc:002"], firstPage.Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());

        var nextPage = restored.ScanPrefixAfter("doc:", "doc:002", limit: 2);
        Assert.Single(nextPage);
        Assert.Equal("doc:003", Encoding.UTF8.GetString(nextPage[0].Key.Span));
        Assert.Equal("three", Encoding.UTF8.GetString(nextPage[0].Value.Span));
    }

    /// <summary>
    /// 验证重开后的磁盘范围扫描与后续内存覆盖、删除和新增记录按同一视图合并。
    /// </summary>
    [Fact]
    public void ScanRange_AfterCompactAndReopen_MergesDiskAndOverlayWithinBounds()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("range-disk");
            kv.Put("idx:01", Encoding.UTF8.GetBytes("one"));
            kv.Put("idx:02", Encoding.UTF8.GetBytes("old-two"));
            kv.Put("idx:03", Encoding.UTF8.GetBytes("three"));
            kv.Put("other:01", [9]);
            kv.Compact();
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("range-disk");
        restored.Put("idx:02", Encoding.UTF8.GetBytes("new-two"));
        Assert.True(restored.Delete("idx:03"));
        restored.Put("idx:04", Encoding.UTF8.GetBytes("four"));

        var rows = restored.ScanRange(
            Encoding.UTF8.GetBytes("idx:"),
            Encoding.UTF8.GetBytes("idx:01"),
            Encoding.UTF8.GetBytes("idx:05"),
            Encoding.UTF8.GetBytes("idx:01"),
            limit: 10);

        Assert.Equal(
            ["idx:02", "idx:04"],
            rows.Select(static row => Encoding.UTF8.GetString(row.Key.Span)).ToArray());
        Assert.Equal(
            ["new-two", "four"],
            rows.Select(static row => Encoding.UTF8.GetString(row.Value.Span)).ToArray());
    }

    [Fact]
    public void Compact_ReopenLaterWal_OverlaysDiskSegment()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("docs");
            kv.Put("doc:001", Encoding.UTF8.GetBytes("old"));
            kv.Put("doc:002", Encoding.UTF8.GetBytes("two"));
            kv.Compact();
            kv.Put("doc:001", Encoding.UTF8.GetBytes("new"));
            Assert.True(kv.Delete("doc:002"));
            kv.Put("doc:003", Encoding.UTF8.GetBytes("three"));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("docs");

        Assert.Equal("new", Encoding.UTF8.GetString(restored.Get("doc:001")!));
        Assert.Null(restored.Get("doc:002"));
        Assert.Equal("three", Encoding.UTF8.GetString(restored.Get("doc:003")!));
        Assert.Equal(["doc:001", "doc:003"], restored.ScanPrefix("doc:", int.MaxValue)
            .Select(static row => Encoding.UTF8.GetString(row.Key.Span))
            .ToArray());
    }

    [Fact]
    public void Compact_CrashAfterDelete_DoesNotReviveDiskKey()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("docs");
            kv.Put("doc:001", Encoding.UTF8.GetBytes("one"));
            kv.Compact();
            Assert.True(kv.Delete("doc:001"));
            db.CrashSimulationCloseWal();
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("docs");

        Assert.Null(restored.Get("doc:001"));
        Assert.Empty(restored.ScanPrefix("doc:", int.MaxValue));
    }

    [Fact]
    public void KeyspaceManager_List_ReturnsExistingKeyspaces()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        db.Keyspaces.Open("a");
        db.Keyspaces.Open("b");

        Assert.Equal(["a", "b"], db.Keyspaces.List());
    }

    [Fact]
    public void Open_WithInvalidName_ThrowsArgumentException()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Throws<ArgumentException>(() => db.Keyspaces.Open("../bad"));
        Assert.Throws<ArgumentException>(() => db.Keyspaces.Open(""));
    }

    [Fact]
    public void Put_WithExpiresAt_HidesExpiredKeyAndPreservesMetadata()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("cache");
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        kv.Put("token", Encoding.UTF8.GetBytes("abc"), expiresAt);
        var entry = kv.GetEntry("token");

        Assert.NotNull(entry);
        Assert.Equal(expiresAt, entry.ExpiresAtUtc);
        Assert.Equal("abc", Encoding.UTF8.GetString(entry.Value.Span));

        kv.Put("old", Encoding.UTF8.GetBytes("gone"), DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Null(kv.Get("old"));
        Assert.Equal(1, kv.Count);
    }

    [Fact]
    public void Reopen_AfterWalAndSnapshot_RestoresExpiresAtMetadata()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var kv = db.Keyspaces.Open("cache");
            kv.Put("a", [1], expiresAt);
            kv.CreateSnapshot();
            kv.Put("b", [2], expiresAt.AddHours(1));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var restored = reopened.Keyspaces.Open("cache");

        Assert.Equal(expiresAt, restored.GetEntry("a")!.ExpiresAtUtc);
        Assert.Equal(expiresAt.AddHours(1), restored.GetEntry("b")!.ExpiresAtUtc);
    }

    [Fact]
    public void CleanExpired_AndExpirationStats_ReportExpectedCounts()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("cache");
        var now = DateTimeOffset.UtcNow;

        kv.Put("alive", [1], now.AddMinutes(1));
        kv.Put("expired:1", [2], now.AddSeconds(-1));
        kv.Put("expired:2", [3], now.AddSeconds(-2));
        kv.Put("forever", [4]);

        var stats = kv.GetExpirationStats(now);
        Assert.Equal(4, stats.TotalKeys);
        Assert.Equal(2, stats.ExpiredKeys);
        Assert.Equal(3, stats.ExpiringKeys);
        Assert.Equal(now.AddMinutes(1), stats.NearestExpiresAtUtc);

        Assert.Equal(1, kv.CleanExpired(now, limit: 1));
        Assert.Equal(3, kv.Count);
        Assert.Equal(1, kv.CleanExpired(now));
        Assert.Equal(2, kv.Count);
    }

    [Fact]
    public void BatchAndPrefixOperations_WorkEndToEnd()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("cache");

        kv.PutMany(new Dictionary<string, byte[]>
        {
            ["tenant:1:a"] = [1],
            ["tenant:1:b"] = [2],
            ["tenant:2:a"] = [3],
        });

        var many = kv.GetMany(["tenant:1:a", "tenant:missing"]);
        Assert.Equal([1], many["tenant:1:a"]);
        Assert.Null(many["tenant:missing"]);

        Assert.Equal(2, kv.DeletePrefix("tenant:1:"));
        Assert.Null(kv.Get("tenant:1:a"));
        Assert.Equal([3], kv.Get("tenant:2:a"));
        Assert.Equal(1, kv.DeleteMany(["tenant:2:a", "tenant:2:a"]));
    }

    [Fact]
    public void IncrementDecrement_WithConcurrentWriters_ProducesAtomicCounter()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with { SyncWalOnEveryWrite = false },
        });
        var kv = db.Keyspaces.Open("cache");

        Parallel.For(0, 16, _ =>
        {
            for (int i = 0; i < 500; i++)
                kv.Increment("counter");
        });

        var afterIncrement = kv.Increment("counter", 0);
        Assert.Equal(8_000, afterIncrement.Value);

        var afterDecrement = kv.Decrement("counter", 125);
        Assert.Equal(7_875, afterDecrement.Value);
    }

    [Fact]
    public void CompareAndSet_WithVersionMismatch_DoesNotOverwrite()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("cache");

        long version = kv.Put("item", Encoding.UTF8.GetBytes("v1"));
        var fail = kv.CompareAndSet("item", version + 1, Encoding.UTF8.GetBytes("bad"));
        var ok = kv.CompareAndSet("item", version, Encoding.UTF8.GetBytes("v2"));

        Assert.False(fail.Succeeded);
        Assert.Null(fail.NewVersion);
        Assert.True(ok.Succeeded);
        Assert.True(ok.NewVersion > version);
        Assert.Equal("v2", Encoding.UTF8.GetString(kv.Get("item")!));
    }

    [Fact]
    public void ExpirePersistAndTtl_FollowRedisSentinelSemantics()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("cache");

        Assert.Equal(-2, kv.GetTimeToLive("missing").Milliseconds);

        kv.Put("forever", [1]);
        Assert.Equal(-1, kv.GetTimeToLive("forever").Milliseconds);

        Assert.True(kv.Expire("forever", TimeSpan.FromSeconds(5)));
        Assert.True(kv.GetTimeToLive("forever").Milliseconds > 0);
        Assert.True(kv.Persist("forever"));
        Assert.Equal(-1, kv.GetTimeToLive("forever").Milliseconds);
    }

    [Fact]
    public async Task BackgroundExpirer_RemovesOpenedExpiredKeys()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with
            {
                ExpirerPollInterval = TimeSpan.FromMilliseconds(50),
                ExpirerBatchSize = 10,
            },
        });
        var kv = db.Keyspaces.Open("cache");
        kv.Put("short", [1], DateTimeOffset.UtcNow.AddMilliseconds(100));

        await Task.Delay(500);

        Assert.Null(kv.Get("short"));
    }

    [Fact]
    public void BackgroundExpirer_Failure_RaisesDiagnosticEvent()
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with
            {
                ExpirerPollInterval = TimeSpan.FromMilliseconds(20),
                ExpirerShutdownTimeout = TimeSpan.FromSeconds(10),
            },
        });

        var expected = new InvalidOperationException("kv expirer boom");
        TsdbDiagnosticEvent? diagnostic = null;
        using var signaled = new ManualResetEventSlim();
        db.DiagnosticEvent += (_, e) =>
        {
            diagnostic = e;
            signaled.Set();
        };

        // 注入后台过期清理故障：worker 必须捕获并上报诊断事件，而不是静默失败。
        db._kvExpirerFaultHook = () => throw expected;

        Assert.True(signaled.Wait(TimeSpan.FromSeconds(3)), "后台 KV 过期清理失败应触发诊断事件。");
        Assert.NotNull(diagnostic);
        Assert.Equal("KvExpirerWorker.CleanExpired", diagnostic!.Operation);
        Assert.Equal(TsdbDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Same(expected, diagnostic.Exception);
    }

    [Fact]
    public void Namespace_QualifiesKeysAndStripsScanResults()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var kv = db.Keyspaces.Open("cache");
        var ns = kv.Namespace("iotsharp");

        ns.Put("flow:a", [1]);
        kv.Put("flow:a", [9]);

        Assert.Equal([1], ns.Get("flow:a"));
        Assert.Equal([9], kv.Get("flow:a"));

        var rows = ns.ScanPrefix("flow:");
        Assert.Single(rows);
        Assert.Equal("flow:a", Encoding.UTF8.GetString(rows[0].Key.Span));

        Assert.Equal(1, ns.DeletePrefix("flow:"));
        Assert.Null(ns.Get("flow:a"));
        Assert.NotNull(kv.Get("flow:a"));
    }
}
