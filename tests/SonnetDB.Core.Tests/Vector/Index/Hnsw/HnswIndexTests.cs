using SonnetDB.Vector.Index.Hnsw;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.Hnsw;

public sealed class HnswIndexTests
{
    private static HnswOptions DeterministicOptions(int seed = 42, int? m = null, int? efC = null, int? efS = null)
        => new()
        {
            M = m ?? 16,
            EfConstruction = efC ?? 200,
            EfSearch = efS ?? 50,
            Seed = seed,
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
        using var index = new HnswIndex<int>(3, Metric.L2, DeterministicOptions());
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
