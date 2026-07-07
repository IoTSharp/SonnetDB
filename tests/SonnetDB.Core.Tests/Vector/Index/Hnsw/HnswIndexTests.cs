using SonnetDB.Vector.Index.Hnsw;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.Hnsw;

public sealed class HnswIndexTests
{
    private static HnswOptions DeterministicOptions(int seed = 42, int? m = null, int? efC = null, int? efS = null, double autoCompact = 0.2)
        => new()
        {
            M = m ?? 16,
            EfConstruction = efC ?? 200,
            EfSearch = efS ?? 50,
            Seed = seed,
            AutoCompactTombstoneRatio = autoCompact,
        };

    [Fact]
    public void Ctor_HammingMetric_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new HnswIndex<int>(8, Metric.Hamming));
    }

    [Fact]
    public void Ctor_NonPositiveDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswIndex<int>(0, Metric.L2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswIndex<int>(-1, Metric.L2));
    }

    [Fact]
    public void Ctor_NullOptions_UsesDefault()
    {
        using var index = new HnswIndex<int>(4, Metric.L2);
        Assert.Equal(HnswOptions.Default.M, index.Options.M);
        Assert.Equal(HnswOptions.Default.EfConstruction, index.Options.EfConstruction);
        Assert.Equal(HnswOptions.Default.EfSearch, index.Options.EfSearch);
    }

    [Fact]
    public void Add_DimensionMismatch_Throws()
    {
        using var index = new HnswIndex<int>(4, Metric.L2, DeterministicOptions());
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        using var index = new HnswIndex<int>(2, Metric.L2, DeterministicOptions());
        index.Add(1, new float[] { 1f, 2f });
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 3f, 4f }));
    }

    [Fact]
    public void Search_DimensionMismatch_Throws()
    {
        using var index = new HnswIndex<int>(4, Metric.L2, DeterministicOptions());
        index.Add(1, new float[] { 1f, 2f, 3f, 4f });
        var buf = new (int, float)[1];
        Assert.Throws<ArgumentException>(() => index.Search(new float[] { 1f, 2f }, 1, buf));
    }

    [Fact]
    public void Search_OnEmptyIndex_ReturnsZero()
    {
        using var index = new HnswIndex<int>(4, Metric.L2, DeterministicOptions());
        var buf = new (int, float)[5];
        int n = index.Search(new float[4], 5, buf);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Add_ThenSearch_ReturnsExactMatchAtTop()
    {
        using var index = new HnswIndex<int>(3, Metric.L2, DeterministicOptions());
        index.Add(1, new float[] { 0f, 0f, 0f });
        index.Add(2, new float[] { 1f, 0f, 0f });
        index.Add(3, new float[] { 5f, 5f, 5f });

        var buf = new (int, float)[3];
        int n = index.Search(new float[] { 1f, 0f, 0f }, 3, buf);

        Assert.Equal(3, n);
        Assert.Equal(2, buf[0].Item1);
    }

    [Theory]
    [InlineData(Metric.L2)]
    [InlineData(Metric.Cosine)]
    [InlineData(Metric.DotProduct)]
    [InlineData(Metric.InnerProduct)]
    public void Search_TopK_ReturnsResultsSortedByMetric(Metric metric)
    {
        const int N = 50;
        const int Dim = 8;
        var rng = new Random(123);
        using var index = new HnswIndex<int>(Dim, metric, DeterministicOptions());
        for (int i = 0; i < N; i++)
        {
            var v = new float[Dim];
            for (int j = 0; j < Dim; j++) { v[j] = (float)(rng.NextDouble() * 2 - 1); }
            index.Add(i, v);
        }

        var query = new float[Dim];
        for (int j = 0; j < Dim; j++) { query[j] = (float)(rng.NextDouble() * 2 - 1); }

        var buf = new (int, float)[10];
        int n = index.Search(query, 10, buf);
        Assert.Equal(10, n);

        // 校验顺序
        if (metric.IsLargerBetter())
        {
            for (int i = 1; i < n; i++)
            {
                Assert.True(buf[i - 1].Item2 >= buf[i].Item2);
            }
        }
        else
        {
            for (int i = 1; i < n; i++)
            {
                Assert.True(buf[i - 1].Item2 <= buf[i].Item2);
            }
        }
    }

    [Fact]
    public void Remove_TombstonesNode_AndExcludesFromSearch()
    {
        using var index = new HnswIndex<int>(3, Metric.L2, DeterministicOptions());
        index.Add(1, new float[] { 0f, 0f, 0f });
        index.Add(2, new float[] { 1f, 0f, 0f });
        index.Add(3, new float[] { 0f, 1f, 0f });

        Assert.Equal(3, index.Count);
        Assert.True(index.Remove(2));
        Assert.Equal(2, index.Count);
        Assert.False(index.ContainsKey(2));
        Assert.False(index.Remove(2));

        var buf = new (int, float)[3];
        int n = index.Search(new float[] { 1f, 0f, 0f }, 3, buf);
        Assert.Equal(2, n);
        for (int i = 0; i < n; i++) { Assert.NotEqual(2, buf[i].Item1); }
    }

    // ── #193：删除后重插同 key，快照往返（持久化重载）不应因重复 key 抛异常 ────────
    [Fact]
    public void Snapshot_RoundTrip_AfterDeleteAndReinsertSameKey_Reloads()
    {
        // autoCompact: 0 —— 关闭自动重建，保留 tombstone 行进入快照以复现 #193 重复 key 路径。
        using var index = new HnswIndex<int>(3, Metric.L2, DeterministicOptions(autoCompact: 0));
        index.Add(1, new float[] { 0f, 0f, 0f });
        index.Add(2, new float[] { 1f, 0f, 0f });   // key 2 → row 1
        Assert.True(index.Remove(2));                // row 1 tombstoned，_keys 仍保留 key 2
        index.Add(2, new float[] { 0f, 1f, 0f });    // key 2 重插 → 新 row 2；快照中 key 2 出现在两行

        // 快照往返：修复前 PopulateFromSnapshot 会对重复 key 2 无差别 _keyToRow.Add → ArgumentException。
        var snapshot = index.CreateSnapshot();
        using var reloaded = HnswIndex<int>.FromSnapshot(snapshot); // 不应抛

        Assert.Equal(2, reloaded.Count);            // key 1 + 重插的 key 2（tombstoned 行不计）
        Assert.True(reloaded.ContainsKey(1));
        Assert.True(reloaded.ContainsKey(2));

        // 重插的 key 2 指向新向量 (0,1,0)：查询该点应命中 key 2。
        var buf = new (int, float)[3];
        int n = reloaded.Search(new float[] { 0f, 1f, 0f }, 3, buf);
        Assert.True(n >= 1);
        Assert.Contains(buf[..n], r => r.Item1 == 2);
    }

    // ── #226 / I6：删除后搜索按 tombstone 比例放大 ef，不欠返回 topK ──────────────────
    [Fact]
    public void Search_AfterManyDeletes_StillReturnsFullTopK()
    {
        const int N = 300;
        const int Dim = 8;
        var rng = new Random(20260707);
        // autoCompact: 0 —— 关闭自动重建，强制留下大量 tombstone 以考验搜索侧 ef 补偿。
        using var index = new HnswIndex<int>(Dim, Metric.L2, DeterministicOptions(efS: 20, autoCompact: 0));
        var vectors = new float[N][];
        for (int i = 0; i < N; i++)
        {
            var v = new float[Dim];
            for (int j = 0; j < Dim; j++) { v[j] = (float)(rng.NextDouble() * 2 - 1); }
            vectors[i] = v;
            index.Add(i, v);
        }

        // 删除约 70% 的键（保留 key % 10 == 0 的行），tombstone 远多于存活。
        int survivors = 0;
        for (int i = 0; i < N; i++)
        {
            if (i % 10 == 0) { survivors++; continue; }
            Assert.True(index.Remove(i));
        }
        Assert.Equal(survivors, index.Count);

        var query = new float[Dim];
        for (int j = 0; j < Dim; j++) { query[j] = (float)(rng.NextDouble() * 2 - 1); }

        const int TopK = 10;
        var buf = new (int, float)[TopK];
        int n = index.Search(query, TopK, buf);

        // 修复前：ef=max(20,10) 窗口被 tombstone 挤占，过滤后欠返回 < 10；修复后应集满 10 个存活结果。
        Assert.Equal(TopK, n);
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(0, buf[i].Item1 % 10);   // 只可能命中存活行
        }
        // 结果不含重复键
        Assert.Equal(n, buf[..n].Select(r => r.Item1).Distinct().Count());
    }

    [Fact]
    public void Search_WhenLiveFewerThanTopK_ReturnsAllLive()
    {
        const int Dim = 4;
        using var index = new HnswIndex<int>(Dim, Metric.L2, DeterministicOptions(autoCompact: 0));
        for (int i = 0; i < 20; i++)
        {
            var v = new float[Dim];
            v[0] = i;
            index.Add(i, v);
        }
        // 删到只剩 3 个存活。
        for (int i = 0; i < 20; i++)
        {
            if (i is 5 or 10 or 15) { continue; }
            index.Remove(i);
        }
        Assert.Equal(3, index.Count);

        var buf = new (int, float)[10];
        int n = index.Search(new float[Dim], 10, buf);   // 请求 topK=10 > 存活 3
        Assert.Equal(3, n);                              // 不无限循环，返回全部存活
        var keys = buf[..n].Select(r => r.Item1).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { 5, 10, 15 }, keys);
    }

    // ── #226 / I14：tombstone 占比越阈值自动重建，物理回收内存并重指入口点 ────────────
    [Fact]
    public void Remove_AutoCompacts_WhenTombstoneRatioExceedsThreshold()
    {
        const int N = 100;
        const int Dim = 6;
        var rng = new Random(555);
        using var index = new HnswIndex<int>(Dim, Metric.L2, DeterministicOptions(autoCompact: 0.5));
        for (int i = 0; i < N; i++)
        {
            var v = new float[Dim];
            for (int j = 0; j < Dim; j++) { v[j] = (float)(rng.NextDouble() * 2 - 1); }
            index.Add(i, v);
        }

        // 删够 50%（阈值 0.5）触发一次重建；物理行随之收缩，后续查询仍正确。
        for (int i = 0; i < 50; i++) { Assert.True(index.Remove(i)); }
        Assert.Equal(50, index.Count);

        // 物理行数（含 tombstone）通过快照观察：重建后应无 tombstone、行数==存活数。
        var snapshot = index.CreateSnapshot();
        Assert.Empty(snapshot.Tombstones);
        Assert.Equal(50, snapshot.Keys.Length);

        // 存活键均可查回、被删键均不存在。
        for (int i = 0; i < N; i++)
        {
            Assert.Equal(i >= 50, index.ContainsKey(i));
        }
        var buf = new (int, float)[5];
        int n = index.Search(new float[Dim], 5, buf);
        Assert.Equal(5, n);
        for (int i = 0; i < n; i++) { Assert.True(buf[i].Item1 >= 50); }
    }

    [Fact]
    public void Compact_ExplicitCall_ReclaimsTombstonesAndPreservesSearch()
    {
        const int Dim = 5;
        // autoCompact: 0 —— 手动验证显式 Compact()。
        using var index = new HnswIndex<int>(Dim, Metric.L2, DeterministicOptions(autoCompact: 0));
        for (int i = 0; i < 40; i++)
        {
            var v = new float[Dim];
            v[0] = i;
            v[1] = i % 3;
            index.Add(i, v);
        }
        for (int i = 0; i < 40; i += 2) { index.Remove(i); }   // 删偶数键 → 20 tombstone

        var before = index.CreateSnapshot();
        Assert.Equal(20, before.Tombstones.Length);
        Assert.Equal(40, before.Keys.Length);

        int reclaimed = index.Compact();
        Assert.Equal(20, reclaimed);

        var after = index.CreateSnapshot();
        Assert.Empty(after.Tombstones);
        Assert.Equal(20, after.Keys.Length);
        Assert.Equal(20, index.Count);

        // 重建后精确匹配存活向量仍命中对应键。
        var buf = new (int, float)[3];
        var q = new float[Dim];
        q[0] = 7; q[1] = 1;   // key 7（奇数，存活）
        int n = index.Search(q, 3, buf);
        Assert.True(n >= 1);
        Assert.Equal(7, buf[0].Item1);
    }

    [Fact]
    public void Compact_OnEmptyOrNoTombstones_IsNoOp()
    {
        using var index = new HnswIndex<int>(3, Metric.L2, DeterministicOptions());
        Assert.Equal(0, index.Compact());        // 空索引
        index.Add(1, new float[] { 1f, 2f, 3f });
        index.Add(2, new float[] { 4f, 5f, 6f });
        Assert.Equal(0, index.Compact());        // 无 tombstone
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void Add_AfterAutoCompact_ReusesFreedRowsWithoutError()
    {
        const int Dim = 4;
        using var index = new HnswIndex<int>(Dim, Metric.L2, DeterministicOptions(autoCompact: 0.3));
        for (int i = 0; i < 30; i++)
        {
            var v = new float[Dim];
            v[0] = i;
            index.Add(i, v);
        }
        for (int i = 0; i < 10; i++) { index.Remove(i); }   // 触发重建（10/30 ≥ 0.3）

        // 重插一个此前被删除的键：重建后其 row 已释放，应可无冲突重插。
        index.Add(3, new float[] { 3f, 0f, 0f, 0f });
        Assert.True(index.ContainsKey(3));
        Assert.Equal(21, index.Count);   // 20 存活 + 重插 1

        var buf = new (int, float)[1];
        int n = index.Search(new float[] { 3f, 0f, 0f, 0f }, 1, buf);
        Assert.Equal(1, n);
        Assert.Equal(3, buf[0].Item1);
    }

    [Fact]
    public void Options_InvalidAutoCompactRatio_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswOptions { AutoCompactTombstoneRatio = -0.1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new HnswOptions { AutoCompactTombstoneRatio = 1.5 }.Validate());
    }

    [Fact]
    public void Concurrent_Reads_AreSafe()
    {
        const int N = 200;
        const int Dim = 16;
        var rng = new Random(7);
        using var index = new HnswIndex<int>(Dim, Metric.L2, DeterministicOptions());
        for (int i = 0; i < N; i++)
        {
            var v = new float[Dim];
            for (int j = 0; j < Dim; j++) { v[j] = (float)(rng.NextDouble() * 2 - 1); }
            index.Add(i, v);
        }

        var query = new float[Dim];
        for (int j = 0; j < Dim; j++) { query[j] = (float)(rng.NextDouble() * 2 - 1); }

        Parallel.For(0, 64, _ =>
        {
            var buf = new (int, float)[10];
            int n = index.Search(query, 10, buf);
            Assert.Equal(10, n);
        });
    }
}
