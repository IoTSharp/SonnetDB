using SonnetDB.Vector.Index.Ivf;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.Ivf;

public sealed class IvfFlatIndexTests
{
    private static IvfOptions Opts(int nList = 4, int nProbe = 4, int seed = 42) =>
        new() { NList = nList, NProbe = nProbe, MaxIterations = 25, Seed = seed };

    [Fact]
    public void Ctor_HammingMetric_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new IvfFlatIndex<int>(8, Metric.Hamming));
    }

    [Fact]
    public void Ctor_NonPositiveDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IvfFlatIndex<int>(0, Metric.L2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new IvfFlatIndex<int>(-1, Metric.L2));
    }

    [Fact]
    public void Add_DimensionMismatch_Throws()
    {
        using var index = new IvfFlatIndex<int>(4, Metric.L2, Opts());
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        using var index = new IvfFlatIndex<int>(2, Metric.L2, Opts());
        index.Add(1, new float[] { 1f, 2f });
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 3f, 4f }));
    }

    [Fact]
    public void Search_DimensionMismatch_Throws()
    {
        using var index = new IvfFlatIndex<int>(4, Metric.L2, Opts());
        index.Add(1, new float[] { 1f, 2f, 3f, 4f });
        var buf = new (int, float)[1];
        Assert.Throws<ArgumentException>(() => index.Search(new float[] { 1f, 2f }, 1, buf));
    }

    [Fact]
    public void Search_OnEmptyIndex_ReturnsZero()
    {
        using var index = new IvfFlatIndex<int>(4, Metric.L2, Opts());
        var buf = new (int, float)[5];
        int n = index.Search(new float[4], 5, buf);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Add_ThenSearch_ReturnsExactMatchAtTop()
    {
        const int N = 32;
        const int Dim = 4;
        using var index = new IvfFlatIndex<int>(Dim, Metric.L2, Opts(nList: 4, nProbe: 4));
        var rng = new Random(7);
        for (int i = 0; i < N; i++)
        {
            var v = new float[Dim];
            for (int j = 0; j < Dim; j++) { v[j] = (float)(rng.NextDouble() * 10); }
            index.Add(i, v);
        }
        // 添加一个明确目标
        var target = new float[] { 100f, 200f, 300f, 400f };
        index.Add(999, target);

        var buf = new (int, float)[3];
        int n = index.Search(target, 3, buf);
        Assert.Equal(3, n);
        Assert.Equal(999, buf[0].Item1);
    }

    [Theory]
    [InlineData(Metric.L2)]
    [InlineData(Metric.Cosine)]
    [InlineData(Metric.DotProduct)]
    [InlineData(Metric.InnerProduct)]
    public void Search_TopK_ReturnsResultsSortedByMetric(Metric metric)
    {
        const int N = 64;
        const int Dim = 8;
        var rng = new Random(123);
        using var index = new IvfFlatIndex<int>(Dim, metric, Opts(nList: 8, nProbe: 8));
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
            for (int i = 1; i < n; i++) { Assert.True(buf[i - 1].Item2 >= buf[i].Item2); }
        }
        else
        {
            for (int i = 1; i < n; i++) { Assert.True(buf[i - 1].Item2 <= buf[i].Item2); }
        }
    }

    [Fact]
    public void Remove_RemovesKey_AndExcludesFromSearch()
    {
        const int N = 16;
        const int Dim = 4;
        using var index = new IvfFlatIndex<int>(Dim, Metric.L2, Opts(nList: 4, nProbe: 4));
        var rng = new Random(11);
        for (int i = 0; i < N; i++)
        {
            var v = new float[Dim];
            for (int j = 0; j < Dim; j++) { v[j] = (float)(rng.NextDouble() * 5); }
            index.Add(i, v);
        }
        var target = new float[] { 100f, 100f, 100f, 100f };
        index.Add(999, target);

        Assert.Equal(N + 1, index.Count);
        Assert.True(index.Remove(999));
        Assert.Equal(N, index.Count);
        Assert.False(index.Remove(999));

        var buf = new (int, float)[5];
        int n = index.Search(target, 5, buf);
        for (int i = 0; i < n; i++) { Assert.NotEqual(999, buf[i].Item1); }
    }
}
