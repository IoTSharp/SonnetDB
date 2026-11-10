using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>
/// <see cref="KvStorageEngine.Lsm"/> 模式下 <see cref="KvKeyspace"/> 的专属合同（与 legacy 有意不同或 legacy 测试无法覆盖的部分）。
/// </summary>
public sealed class KvLsmKeyspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-lsm-keyspace-" + Guid.NewGuid().ToString("N"));

    public KvLsmKeyspaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NewDirectory_WithLsmEngine_UsesLsmLayoutAndReopensWithAnyOption()
    {
        string path = Path.Combine(_root, "new");
        using (KvKeyspace keyspace = KvKeyspace.Open("new", path, Options()))
        {
            Assert.Equal(KvStorageEngine.Lsm, keyspace.StorageEngine);
            keyspace.Put("a", [1]);
        }

        Assert.True(File.Exists(Path.Combine(path, KvLsmPaths.CurrentFileName)));
        Assert.False(Directory.Exists(KvKeyspace.SnapshotsDirectory(path)));
        Assert.False(File.Exists(KvKeyspace.ActiveWalPath(path)));

        // 已是 LSM 布局的目录总以 LSM 打开，与选项无关。
        using KvKeyspace reopened = KvKeyspace.Open("new", path, Options(KvStorageEngine.Legacy));
        Assert.Equal(KvStorageEngine.Lsm, reopened.StorageEngine);
        Assert.Equal([1], reopened.Get("a"));
    }

    [Fact]
    public void ExistingLegacyDirectory_MigrationDisabled_KeepsLegacyEngine()
    {
        string path = Path.Combine(_root, "legacy");
        using (KvKeyspace legacy = KvKeyspace.Open("legacy", path, Options(KvStorageEngine.Legacy)))
        {
            Assert.Equal(KvStorageEngine.Legacy, legacy.StorageEngine);
            legacy.Put("a", [1]);
            legacy.CreateSnapshot();
        }

        using KvKeyspace reopened = KvKeyspace.Open("legacy", path, Options() with { LegacyMigration = KvLegacyMigrationMode.Disabled });
        Assert.Equal(KvStorageEngine.Legacy, reopened.StorageEngine);
        Assert.Equal([1], reopened.Get("a"));
        Assert.False(File.Exists(Path.Combine(path, KvLsmPaths.CurrentFileName)));
    }

    [Fact]
    public void ExistingLegacyDirectoryWithOnlyWal_MigrationDisabled_KeepsLegacyEngine()
    {
        string path = Path.Combine(_root, "legacy-wal");
        using (KvKeyspace legacy = KvKeyspace.Open("legacy-wal", path, Options(KvStorageEngine.Legacy)))
            legacy.Put("a", [1]);

        Assert.True(KvLsmKeyspaceBackend.HasLegacyData(path));
        using KvKeyspace reopened = KvKeyspace.Open("legacy-wal", path, Options() with { LegacyMigration = KvLegacyMigrationMode.Disabled });
        Assert.Equal(KvStorageEngine.Legacy, reopened.StorageEngine);
        Assert.Equal([1], reopened.Get("a"));
    }

    [Fact]
    public void ReadsOfExpiredKeys_DoNotWriteOrAdvanceSequence()
    {
        using KvKeyspace keyspace = Open("no-lazy-delete");
        DateTimeOffset past = DateTimeOffset.UtcNow.AddMinutes(-1);
        keyspace.Put("expired", [1], past);
        keyspace.Put("alive", [2]);
        long sequence = keyspace.LastSequence;
        long walLength = keyspace.ActiveWalLength;

        Assert.Null(keyspace.Get("expired"));
        Assert.Null(keyspace.GetEntry("expired"));
        Assert.Equal(KvTtlResult.Missing, keyspace.GetTimeToLive("expired").Milliseconds);
        Assert.Single(keyspace.ScanPrefix(""));
        Assert.Equal(1, keyspace.CountPrefix(""));
        Assert.Single(keyspace.ScanRange("", null, null, null));

        Assert.Equal(sequence, keyspace.LastSequence);
        Assert.Equal(walLength, keyspace.ActiveWalLength);

        // Count 不按 TTL 过滤（与 legacy 合同一致），已到期 key 由显式清理移除。
        Assert.Equal(2, keyspace.Count);
        Assert.Equal(1, keyspace.CleanExpired());
        Assert.Equal(1, keyspace.Count);
        Assert.Equal(sequence + 1, keyspace.LastSequence);
    }

    [Fact]
    public void ExpiredKeyCountsAsAbsentForConditionalWritesAndIncrement()
    {
        using KvKeyspace keyspace = Open("expired-absent");
        long expiredVersion = keyspace.Put("k", Encoding.UTF8.GetBytes("41"), DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(keyspace.Set("k", [9], KvSetCondition.IfExists).Applied);
        KvCasResult cas = keyspace.CompareAndSet("k", expiredVersion, [9]);
        Assert.False(cas.Succeeded);
        Assert.Equal(0, cas.CurrentVersion);
        Assert.False(keyspace.Delete("k"));
        Assert.Null(keyspace.GetAndDelete("k").PreviousEntry);
        Assert.False(keyspace.ExpireAt("k", DateTimeOffset.UtcNow.AddHours(1)));
        Assert.False(keyspace.Persist("k"));

        (long value, long version) = keyspace.Increment("k", 5);
        Assert.Equal(5, value);
        Assert.Null(keyspace.GetEntry("k")!.ExpiresAtUtc);
        Assert.Equal(version, keyspace.LastSequence);
        Assert.True(keyspace.Set("k2", [1], KvSetCondition.IfNotExists).Applied);
    }

    [Fact]
    public void PutWalSyncFailure_FailsClosedAndReportsUnknownOutcome()
    {
        string path = Path.Combine(_root, "put-fault");
        var injected = new IOException("injected put sync failure");
        using (KvKeyspace keyspace = KvKeyspace.Open("put-fault", path, Options() with { SyncWalOnEveryWrite = true }))
        {
            keyspace.Put("before", [1]);
            keyspace.WalSyncTestHook = () => throw injected;
            IOException error = Assert.Throws<IOException>(() => keyspace.Put("unknown", [2]));
            Assert.True(keyspace.IsWriteCommitOutcomeUnknown(error));
            keyspace.WalSyncTestHook = null;

            // 决策 2：Put/Increment 同样 fail closed，此后所有写入拒绝。
            IOException rejected = Assert.Throws<IOException>(() => keyspace.Put("after", [3]));
            Assert.Contains("read-only", rejected.Message, StringComparison.Ordinal);
            Assert.True(keyspace.IsWriteCommitOutcomeUnknown(rejected));
            Assert.Throws<IOException>(() => keyspace.Increment("counter"));
            Assert.Throws<IOException>(() => keyspace.ApplyBatch([KvBatchMutation.Put([7], [7])]));
            Assert.Equal([1], keyspace.Get("before"));
        }

        using KvKeyspace reopened = KvKeyspace.Open("put-fault", path, Options());
        Assert.Equal([1], reopened.Get("before"));
        Assert.Equal([2], reopened.Get("unknown"));
        Assert.Null(reopened.Get("after"));
    }

    [Fact]
    public void Clear_ConsumesOneSequenceAdvancesGenerationAndSurvivesReopen()
    {
        string path = Path.Combine(_root, "clear");
        long cleared;
        using (KvKeyspace keyspace = KvKeyspace.Open("clear", path, Options()))
        {
            keyspace.Put("a", [1]);
            keyspace.Put("b", [2], DateTimeOffset.UtcNow.AddMinutes(-1));
            keyspace.CreateSnapshot();
            keyspace.Put("c", [3]);
            long before = keyspace.LastSequence;

            KvClearResult result = keyspace.Clear();
            Assert.Equal(3, result.RemovedKeys);
            Assert.Equal(1, result.Generation);
            Assert.Equal(0, result.CleanupPendingFiles);
            Assert.Equal(before + 1, keyspace.LastSequence);
            Assert.Empty(keyspace.ScanPrefix(""));
            cleared = keyspace.LastSequence;
            Assert.Equal(cleared + 1, keyspace.Put("d", [4]));
        }

        using KvKeyspace reopened = KvKeyspace.Open("clear", path, Options());
        Assert.Equal(1, reopened.Generation);
        Assert.Equal(cleared + 1, reopened.LastSequence);
        Assert.Equal(["d"], reopened.ScanPrefix("").Select(static e => Encoding.UTF8.GetString(e.Key.Span)));
    }

    [Fact]
    public void ReadSnapshot_IsStableAcrossWritesFlushAndCompaction_AndCursorOutlivesSnapshot()
    {
        using KvKeyspace keyspace = Open("snapshot");
        for (int i = 0; i < 300; i++)
            keyspace.Put($"k{i:D3}", [1]);
        KvReadSnapshot snapshot = keyspace.AcquireReadSnapshot();
        long sequence = snapshot.Sequence;

        for (int i = 0; i < 300; i++)
            keyspace.Put($"k{i:D3}", [2]);
        keyspace.Delete("k000");
        keyspace.Compact();

        Assert.Equal([1], snapshot.GetEntry("k000"u8)!.Value.ToArray());
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions { Prefix = "k"u8.ToArray(), PageSize = 64 });
        snapshot.Dispose();
        int count = 0;
        for (var page = cursor.ReadNextPage(); page.Count > 0; page = cursor.ReadNextPage())
        {
            Assert.All(page, static entry => Assert.Equal([1], entry.Value.ToArray()));
            count += page.Count;
        }
        Assert.Equal(300, count);
        Assert.Equal(sequence, cursor.SnapshotSequence);
        Assert.Null(keyspace.Get("k000"));
        Assert.Equal([2], keyspace.Get("k001"));
    }

    [Fact]
    public async Task SequenceFactory_ReceivesTheCommittedSequenceUnderConcurrentWrites()
    {
        using KvKeyspace keyspace = Open("factory");
        using var stop = new CancellationTokenSource();
        Task noise = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
                keyspace.Put($"noise:{i++ % 50}", [1]);
        });

        try
        {
            for (int round = 0; round < 200; round++)
            {
                long committed = keyspace.ApplyBatch(sequence =>
                    [KvBatchMutation.Put(Encoding.UTF8.GetBytes($"doc:{round}"), Encoding.UTF8.GetBytes(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)))]);
                KvEntry entry = keyspace.GetEntry($"doc:{round}")!;
                Assert.Equal(committed, entry.Version);
                Assert.Equal(committed.ToString(System.Globalization.CultureInfo.InvariantCulture), Encoding.UTF8.GetString(entry.Value.Span));
            }
        }
        finally
        {
            stop.Cancel();
            await noise.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ConditionalBatch_ConcurrentCreate_ExactlyOneCommits()
    {
        using KvKeyspace keyspace = Open("conditional");
        byte[] key = "unique"u8.ToArray();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            keyspace.ApplyConditionalBatch(
                [KvBatchMutation.Put(key, [(byte)i])],
                [KvBatchPrecondition.KeyVersion(key, 0)]))));
        Assert.Single(results, static r => r.Applied);
        Assert.Equal(15, results.Count(static r => !r.Applied && r.FailedPreconditionIndex == 0));
    }

    [Fact]
    public void Diagnostics_MapsMemtableAndWalCounters()
    {
        using KvKeyspace keyspace = Open("diagnostics");
        keyspace.Put("a", [1]);
        keyspace.Put("b", [2]);
        KvDiagnostics diagnostics = keyspace.GetDiagnostics();
        Assert.Equal(2, diagnostics.TotalKeys);
        Assert.Equal(2, diagnostics.MutableOverlayEntries);
        Assert.True(diagnostics.WalBytes > 0);
        Assert.Equal(new KvCleanupStatus(0, 0, 0), keyspace.GetCleanupStatus());
        Assert.Equal(0, keyspace.CleanupPendingFiles());
    }

    [Fact]
    public void Dispose_ReleasesLeaseAndRejectsLaterUse()
    {
        string path = Path.Combine(_root, "dispose");
        KvKeyspace keyspace = KvKeyspace.Open("dispose", path, Options());
        Assert.Throws<IOException>(() => KvKeyspace.Open("dispose", path, Options()));
        keyspace.Put("a", [1]);
        keyspace.Dispose();
        keyspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => keyspace.Get("a"));
        Assert.Throws<ObjectDisposedException>(() => keyspace.Put("b", [2]));
        Assert.Throws<ObjectDisposedException>(() => keyspace.AcquireReadSnapshot());

        using KvKeyspace reopened = KvKeyspace.Open("dispose", path, Options());
        Assert.Equal([1], reopened.Get("a"));
    }

    private KvKeyspace Open(string name) => KvKeyspace.Open(name, Path.Combine(_root, name), Options());

    private static KvOptions Options(KvStorageEngine engine = KvStorageEngine.Lsm) => KvOptions.Default with
    {
        StorageEngine = engine,
        SyncWalOnEveryWrite = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };
}
