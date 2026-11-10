using System.Text;
using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>K6：共享 LSM 实例中的逻辑 keyspace（按 StorageId 前缀隔离）。</summary>
public sealed class KvLsmStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-lsm-store-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string StoreRoot => Path.Combine(_root, "lsm");

    private string Sidecar(string name) => Path.Combine(_root, "tables", name);

    /// <summary>同名 key 在不同逻辑 keyspace 中互不可见；扫描、计数、快照与条件写都只作用于本前缀。</summary>
    [Fact]
    public void LogicalKeyspaces_AreIsolated_ForReadsScansSnapshotsAndConditionalWrites()
    {
        using var budget = new KvDiskReadBudget(4);
        using KvLsmStore store = KvLsmStore.Open(StoreRoot, Options(), budget);
        long alphaId = store.Create("alpha");
        long betaId = store.Create("beta");
        Assert.NotEqual(alphaId, betaId);
        using KvKeyspace alpha = KvKeyspace.OpenShared("alpha", Sidecar("alpha"), store, alphaId, Options(), budget);
        using KvKeyspace beta = KvKeyspace.OpenShared("beta", Sidecar("beta"), store, betaId, Options(), budget);

        alpha.Put("k", "a"u8);
        alpha.Put("k2", "a2"u8);
        beta.Put("k", "b"u8);

        Assert.Equal("a", Text(alpha.Get("k")));
        Assert.Equal("b", Text(beta.Get("k")));
        Assert.Null(beta.Get("k2"));
        Assert.Equal(["k", "k2"], alpha.ScanPrefix("k"u8).Select(static e => Encoding.UTF8.GetString(e.Key.Span)).ToArray());
        Assert.Equal(["k"], beta.ScanPrefix([]).Select(static e => Encoding.UTF8.GetString(e.Key.Span)).ToArray());
        Assert.Equal(2, alpha.Count);
        Assert.Equal(1, beta.Count);

        using (KvReadSnapshot snapshot = beta.AcquireReadSnapshot())
        using (KvRangeCursor cursor = snapshot.OpenRangeCursor())
        {
            alpha.Put("k3", "a3"u8);
            beta.Put("late", "x"u8);
            IReadOnlyList<KvEntry> page = cursor.ReadNextPage();
            Assert.Equal(["k"], page.Select(static e => Encoding.UTF8.GetString(e.Key.Span)).ToArray());
        }

        KvCasResult created = beta.CompareAndSet("k2"u8, 0, "b2"u8);
        Assert.True(created.Succeeded);
        Assert.False(beta.CompareAndSet("k2"u8, 0, "again"u8).Succeeded);
        Assert.Equal("a2", Text(alpha.Get("k2")));
    }

    /// <summary>LastSequence 只随本逻辑 keyspace 的提交变化；Clear 只清本前缀、推进本 keyspace 的 generation 并消耗一个 sequence。</summary>
    [Fact]
    public void LastSequenceAndClear_AreScopedToTheLogicalKeyspace()
    {
        using var budget = new KvDiskReadBudget(4);
        using KvLsmStore store = KvLsmStore.Open(StoreRoot, Options(), budget);
        long alphaId = store.Create("alpha");
        long betaId = store.Create("beta");
        using KvKeyspace alpha = KvKeyspace.OpenShared("alpha", Sidecar("alpha"), store, alphaId, Options(), budget);
        using KvKeyspace beta = KvKeyspace.OpenShared("beta", Sidecar("beta"), store, betaId, Options(), budget);

        alpha.Put("a", [1]);
        long alphaSequence = alpha.LastSequence;
        for (int i = 0; i < 5; i++)
            beta.Put("b" + i, [2]);
        Assert.Equal(alphaSequence, alpha.LastSequence);
        Assert.True(beta.LastSequence > alphaSequence);

        long betaBefore = beta.LastSequence;
        KvClearResult cleared = beta.Clear();
        Assert.Equal(5, cleared.RemovedKeys);
        Assert.Equal(1, cleared.Generation);
        Assert.Equal(1, beta.Generation);
        Assert.Equal(0, alpha.Generation);
        Assert.Equal(betaBefore + 1, beta.LastSequence);
        Assert.Equal(0, beta.Count);
        Assert.Equal([1], alpha.Get("a"));

        beta.Put("after", [3]);
        Assert.Equal([3], beta.Get("after"));
        Assert.Equal(alphaSequence, alpha.LastSequence);
    }

    /// <summary>重开后逻辑 keyspace 的数据、generation、LastSequence 与编号分配都保持；删除的编号不复用。</summary>
    [Fact]
    public void Reopen_RestoresMetadata_AndDroppedIdsAreNeverReused()
    {
        long alphaId, betaId, alphaSequence;
        using (var budget = new KvDiskReadBudget(4))
        {
            using KvLsmStore store = KvLsmStore.Open(StoreRoot, Options(), budget);
            alphaId = store.Create("alpha");
            betaId = store.Create("beta");
            using (KvKeyspace alpha = KvKeyspace.OpenShared("alpha", Sidecar("alpha"), store, alphaId, Options(), budget))
            {
                alpha.Put("x", [1]);
                alpha.Clear();
                alpha.Put("y", [2]);
                alphaSequence = alpha.LastSequence;
            }
            using (KvKeyspace beta = KvKeyspace.OpenShared("beta", Sidecar("beta"), store, betaId, Options(), budget))
                beta.Put("z", [3]);
            Assert.True(store.Drop(betaId));
            Assert.False(store.Drop(betaId));
        }

        using var reopenBudget = new KvDiskReadBudget(4);
        using KvLsmStore reopened = KvLsmStore.Open(StoreRoot, Options(), reopenBudget);
        Assert.Equal([(alphaId, "alpha")], reopened.List());
        using (KvKeyspace alpha = KvKeyspace.OpenShared("alpha", Sidecar("alpha"), reopened, alphaId, Options(), reopenBudget))
        {
            Assert.Null(alpha.Get("x"));
            Assert.Equal([2], alpha.Get("y"));
            Assert.Equal(1, alpha.Generation);
            Assert.Equal(alphaSequence, alpha.LastSequence);
        }

        long gammaId = reopened.Create("gamma");
        Assert.True(gammaId > betaId);
        Assert.Throws<KeyNotFoundException>(() => KvKeyspace.OpenShared("beta", Sidecar("beta-new"), reopened, betaId, Options(), reopenBudget));
        using KvKeyspace gamma = KvKeyspace.OpenShared("gamma", Sidecar("gamma"), reopened, gammaId, Options(), reopenBudget);
        Assert.Equal(0, gamma.Count);
    }

    /// <summary>附属目录写入 v3 标记：旧版本 generation 读取器拒绝；按独立目录打开或绑定到其他编号都被拒绝。</summary>
    [Fact]
    public void SidecarMarker_RejectsDowngradeAndMismatchedBindings()
    {
        using var budget = new KvDiskReadBudget(4);
        using KvLsmStore store = KvLsmStore.Open(StoreRoot, Options(), budget);
        long alphaId = store.Create("alpha");
        long otherId = store.Create("other");
        using (KvKeyspace alpha = KvKeyspace.OpenShared("alpha", Sidecar("alpha"), store, alphaId, Options(), budget))
            alpha.Put("a", [1]);

        Assert.True(KvLsmLayoutMarkerFile.TryRead(Sidecar("alpha"), out KvLsmLayoutMarker marker));
        Assert.Equal(new KvLsmLayoutMarker(KvLsmLayoutKind.Shared, alphaId), marker);
        // 旧版本只接受 v1/v2 generation 文件：v3 标记让它明确失败，而不是当作空 keyspace 打开。
        Assert.Throws<InvalidDataException>(() => KvGenerationFile.LoadMetadata(Sidecar("alpha")));
        Assert.Throws<InvalidOperationException>(() => KvKeyspace.Open("alpha", Sidecar("alpha"), Options()));
        Assert.Throws<InvalidOperationException>(() => KvKeyspace.OpenShared("alpha", Sidecar("alpha"), store, otherId, Options(), budget));

        // 独立 LSM 目录同样带标记（kind = Standalone）。
        string standalone = Path.Combine(_root, "standalone");
        using (KvKeyspace keyspace = KvKeyspace.Open("standalone", standalone, Options()))
            keyspace.Put("s", [1]);
        Assert.True(KvLsmLayoutMarkerFile.TryRead(standalone, out KvLsmLayoutMarker standaloneMarker));
        Assert.Equal(KvLsmLayoutKind.Standalone, standaloneMarker.Kind);
        Assert.Throws<InvalidDataException>(() => KvGenerationFile.LoadMetadata(standalone));
        Assert.Throws<InvalidOperationException>(() => KvKeyspace.OpenShared("standalone", standalone, store, otherId, Options(), budget));
    }

    /// <summary>未刷盘的提交经 WAL 重放后，逻辑 keyspace 的数据与 LastSequence 都恢复。</summary>
    [Fact]
    public void WalReplay_RestoresDataAndPerKeyspaceSequence()
    {
        long id, sequence;
        using (var budget = new KvDiskReadBudget(4))
        using (KvLsmStore store = KvLsmStore.Open(StoreRoot, Options() with { SyncWalOnEveryWrite = true }, budget))
        {
            id = store.Create("wal");
            using KvKeyspace keyspace = KvKeyspace.OpenShared("wal", Sidecar("wal"), store, id, Options(), budget);
            keyspace.ApplyBatch([KvBatchMutation.Put("p"u8.ToArray(), [1]), KvBatchMutation.Put("q"u8.ToArray(), [2])]);
            sequence = keyspace.LastSequence;
        }

        using var reopenBudget = new KvDiskReadBudget(4);
        using KvLsmStore reopened = KvLsmStore.Open(StoreRoot, Options(), reopenBudget);
        using KvKeyspace restored = KvKeyspace.OpenShared("wal", Sidecar("wal"), reopened, id, Options(), reopenBudget);
        Assert.Equal(sequence, restored.LastSequence);
        Assert.Equal([2], restored.Get("q"));
    }

    private static KvOptions Options() => KvOptions.Default with
    {
        StorageEngine = KvStorageEngine.Lsm,
        SyncWalOnEveryWrite = false,
        ExpirerEnabled = false,
        CleanupEnabled = false,
    };

    private static string? Text(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
}
