using SonnetDB.Vector.Index.DiskAnn;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.DiskAnn;

public sealed class VamanaIndexTests
{
    private static VamanaOptions DeterministicOptions(int seed = 42, int? r = null, int? l = null)
        => new()
        {
            MaxDegree = r ?? 16,
            SearchListSize = l ?? 64,
            Alpha = 1.2f,
            BeamWidth = 4,
            Seed = seed,
        };

    [Fact]
    public void Ctor_HammingMetric_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new VamanaIndex<int>(8, Metric.Hamming));
    }

    [Fact]
    public void Ctor_NonPositiveDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VamanaIndex<int>(0, Metric.L2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VamanaIndex<int>(-1, Metric.L2));
    }

    [Fact]
    public void Ctor_NullOptions_UsesDefault()
    {
        using var index = new VamanaIndex<int>(4, Metric.L2);
        Assert.Equal(VamanaOptions.Default.MaxDegree, index.Options.MaxDegree);
        Assert.Equal(VamanaOptions.Default.SearchListSize, index.Options.SearchListSize);
        Assert.Equal(VamanaOptions.Default.Alpha, index.Options.Alpha);
    }

    [Fact]
    public void Add_DimensionMismatch_Throws()
    {
        using var index = new VamanaIndex<int>(4, Metric.L2, DeterministicOptions());
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        using var index = new VamanaIndex<int>(2, Metric.L2, DeterministicOptions());
        index.Add(1, new float[] { 1f, 2f });
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 3f, 4f }));
    }

    [Fact]
    public void Search_DimensionMismatch_Throws()
    {
        using var index = new VamanaIndex<int>(4, Metric.L2, DeterministicOptions());
        index.Add(1, new float[] { 1f, 2f, 3f, 4f });
        var buf = new (int, float)[1];
        Assert.Throws<ArgumentException>(() => index.Search(new float[] { 1f, 2f }, 1, buf));
    }

    [Fact]
    public void Search_OnEmptyIndex_ReturnsZero()
    {
        using var index = new VamanaIndex<int>(4, Metric.L2, DeterministicOptions());
        var buf = new (int, float)[5];
        int n = index.Search(new float[4], 5, buf);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Add_ThenSearch_ReturnsExactMatchAtTop()
    {
        using var index = new VamanaIndex<int>(3, Metric.L2, DeterministicOptions());
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
        using var index = new VamanaIndex<int>(Dim, metric, DeterministicOptions());
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
        using var index = new VamanaIndex<int>(3, Metric.L2, DeterministicOptions());
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

    [Fact]
    public void Concurrent_Reads_AreSafe()
    {
        const int N = 200;
        const int Dim = 16;
        var rng = new Random(7);
        using var index = new VamanaIndex<int>(Dim, Metric.L2, DeterministicOptions());
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
