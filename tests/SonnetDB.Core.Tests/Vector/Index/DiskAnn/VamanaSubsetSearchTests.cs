using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Index.DiskAnn;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.DiskAnn;

/// <summary>
/// M12.4 — VamanaIndex.SearchSubset 单元测试：
/// 验证候选集投影路径在精度、边界、tombstone 行为上与 FlatIndex.SearchSubset 一致。
/// </summary>
public sealed class VamanaSubsetSearchTests
{
    private static VamanaOptions DeterministicOptions(int seed = 42)
        => new()
        {
            MaxDegree = 16,
            SearchListSize = 64,
            Alpha = 1.2f,
            BeamWidth = 4,
            Seed = seed,
        };

    private static float[] RandomVector(Random rng, int dim)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++) { v[i] = (float)(rng.NextDouble() * 2 - 1); }
        return v;
    }

    [Fact]
    public void SearchSubset_EmptyCandidateSet_ReturnsZero()
    {
        using var idx = new VamanaIndex<int>(8, Metric.L2, DeterministicOptions());
        var rng = new Random(1);
        for (int i = 0; i < 20; i++) { idx.Add(i, RandomVector(rng, 8)); }

        Span<(int Key, float Score)> buf = stackalloc (int, float)[5];
        int written = idx.SearchSubset(RandomVector(rng, 8), 5, Array.Empty<int>(), buf);
        Assert.Equal(0, written);
    }

    [Fact]
    public void SearchSubset_UnknownKeysIgnored_ReturnsZero()
    {
        using var idx = new VamanaIndex<int>(8, Metric.L2, DeterministicOptions());
        var rng = new Random(1);
        for (int i = 0; i < 20; i++) { idx.Add(i, RandomVector(rng, 8)); }

        Span<(int Key, float Score)> buf = stackalloc (int, float)[3];
        int written = idx.SearchSubset(RandomVector(rng, 8), 3, new[] { 999, 1000 }, buf);
        Assert.Equal(0, written);
    }

    [Fact]
    public void SearchSubset_TombstonedRows_AreSkipped()
    {
        using var idx = new VamanaIndex<int>(8, Metric.L2, DeterministicOptions());
        var rng = new Random(7);
        for (int i = 0; i < 20; i++) { idx.Add(i, RandomVector(rng, 8)); }
        idx.Remove(0);
        idx.Remove(1);

        Span<(int Key, float Score)> buf = stackalloc (int, float)[5];
        int written = idx.SearchSubset(RandomVector(rng, 8), 5, new[] { 0, 1, 2, 3, 4 }, buf);
        Assert.Equal(3, written);
        for (int i = 0; i < written; i++)
        {
            Assert.NotEqual(0, buf[i].Key);
            Assert.NotEqual(1, buf[i].Key);
        }
    }

    [Theory]
    [InlineData(Metric.L2)]
    [InlineData(Metric.Cosine)]
    [InlineData(Metric.InnerProduct)]
    public void SearchSubset_Result_MatchesGroundTruthOnSubset(Metric metric)
    {
        const int N = 200;
        const int Dim = 16;
        const int TopK = 5;

        using var idx = new VamanaIndex<int>(Dim, metric, DeterministicOptions());
        var rng = new Random(123);
        var vectors = new float[N][];
        for (int i = 0; i < N; i++)
        {
            vectors[i] = RandomVector(rng, Dim);
            idx.Add(i, vectors[i]);
        }

        // 选定一个候选子集（偶数键）。
        var candidates = Enumerable.Range(0, N).Where(i => i % 2 == 0).ToArray();
        var query = RandomVector(rng, Dim);

        // ground truth：在候选子集上做精确 Top-K。
        var truth = candidates
            .Select(i => (Key: i, Score: Distance.Compute(query, vectors[i], metric)))
            .OrderBy(t => metric.IsLargerBetter() ? -t.Score : t.Score)
            .Take(TopK)
            .Select(t => t.Key)
            .ToHashSet();

        Span<(int Key, float Score)> buf = stackalloc (int, float)[TopK];
        int written = idx.SearchSubset(query, TopK, candidates, buf);
        Assert.Equal(TopK, written);

        var got = new HashSet<int>();
        for (int i = 0; i < written; i++) { got.Add(buf[i].Key); }

        // SearchSubset 在候选集上是精确扫描，召回率必须 100%。
        Assert.Equal(truth, got);
    }

    [Fact]
    public void SearchSubset_BufferTooSmall_Throws()
    {
        using var idx = new VamanaIndex<int>(8, Metric.L2, DeterministicOptions());
        idx.Add(1, new float[8]);
        var buf = new (int Key, float Score)[1];
        Assert.Throws<ArgumentException>(() => idx.SearchSubset(new float[8], 5, new[] { 1 }, buf));
    }
}
