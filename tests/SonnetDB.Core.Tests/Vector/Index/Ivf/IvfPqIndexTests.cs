using SonnetDB.Vector.Index.Ivf;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.Ivf;

public sealed class IvfPqIndexTests
{
    private static IvfPqOptions Opts(int nList = 16, int nProbe = 8, int m = 4, int seed = 42) =>
        new() { NList = nList, NProbe = nProbe, M = m, NBits = 8, MaxIterations = 15, Seed = seed };

    [Fact]
    public void Ctor_HammingMetric_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new IvfPqIndex<int>(8, Metric.Hamming));
    }

    [Fact]
    public void Ctor_DimNotDivisibleByM_Throws()
    {
        // dim=10, M=4 → 10 % 4 != 0
        Assert.Throws<ArgumentException>(() => new IvfPqIndex<int>(10, Metric.L2, Opts(m: 4)));
    }

    [Fact]
    public void Ctor_NonPositiveDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IvfPqIndex<int>(0, Metric.L2));
    }

    [Fact]
    public void Add_DimensionMismatch_Throws()
    {
        using var index = new IvfPqIndex<int>(8, Metric.L2, Opts());
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        using var index = new IvfPqIndex<int>(8, Metric.L2, Opts());
        var v = new float[8];
        index.Add(1, v);
        Assert.Throws<ArgumentException>(() => index.Add(1, v));
    }

    [Fact]
    public void Search_OnEmptyIndex_ReturnsZero()
    {
        using var index = new IvfPqIndex<int>(8, Metric.L2, Opts());
        var buf = new (int, float)[5];
        int n = index.Search(new float[8], 5, buf);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Add_ThenSearch_ReturnsApproximateNearestNeighbor()
    {
        const int N = 300;
        const int Dim = 16;
        var rng = new Random(7);
        using var index = new IvfPqIndex<int>(Dim, Metric.L2, Opts(nList: 16, nProbe: 16, m: 4));
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

        // L2 距离单调非降
        for (int i = 1; i < n; i++) { Assert.True(buf[i - 1].Item2 <= buf[i].Item2); }
    }
}
