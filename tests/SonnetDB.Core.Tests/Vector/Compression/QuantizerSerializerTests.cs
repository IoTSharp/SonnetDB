using SonnetDB.Vector.Compression;

namespace SonnetDB.Core.Tests.Vector.Compression;

public sealed class QuantizerSerializerTests
{
    [Fact]
    public void Write_NullQuantizer_RoundTripsToNull()
    {
        using var ms = new MemoryStream();
        QuantizerSerializer.Write(null, ms);
        ms.Position = 0;

        IVectorQuantizer? loaded = QuantizerSerializer.Read(ms);
        Assert.Null(loaded);
    }

    [Fact]
    public void Write_UntrainedQuantizer_Throws()
    {
        var sq = new ScalarQuantizer8(8);
        using var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => QuantizerSerializer.Write(sq, ms));
    }

    [Fact]
    public void Sq8_RoundTrip_PreservesEncoding()
    {
        const int dim = 32;
        const int n = 500;
        float[] data = GenerateRandomData(seed: 1, n, dim);

        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        IVectorQuantizer loaded = WriteThenRead(sq);
        Assert.IsType<ScalarQuantizer8>(loaded);
        Assert.True(loaded.IsTrained);
        AssertEncodeMatches(sq, loaded, data, n, dim);
    }

    [Fact]
    public void Pq_RoundTrip_PreservesEncoding()
    {
        const int dim = 32;
        const int m = 8;
        const int n = 600;
        float[] data = GenerateRandomData(seed: 2, n, dim);

        var pq = new ProductQuantizer(dim, m);
        pq.Train(data, n);

        IVectorQuantizer loaded = WriteThenRead(pq);
        Assert.IsType<ProductQuantizer>(loaded);
        AssertEncodeMatches(pq, loaded, data, n, dim);
    }

    [Fact]
    public void Opq_RoundTrip_PreservesEncoding()
    {
        const int dim = 16;
        const int m = 4;
        const int n = 400;
        float[] data = GenerateRandomData(seed: 3, n, dim);

        var opq = new OptimizedProductQuantizer(dim, m);
        opq.Train(data, n);

        IVectorQuantizer loaded = WriteThenRead(opq);
        Assert.IsType<OptimizedProductQuantizer>(loaded);
        AssertEncodeMatches(opq, loaded, data, n, dim);
    }

    [Fact]
    public void Rq_RoundTrip_PreservesEncoding()
    {
        const int dim = 24;
        const int levels = 3;
        const int n = 500;
        float[] data = GenerateRandomData(seed: 4, n, dim);

        var rq = new ResidualQuantizer(dim, levels);
        rq.Train(data, n);

        IVectorQuantizer loaded = WriteThenRead(rq);
        Assert.IsType<ResidualQuantizer>(loaded);
        AssertEncodeMatches(rq, loaded, data, n, dim);
    }

    private static IVectorQuantizer WriteThenRead(IVectorQuantizer q)
    {
        using var ms = new MemoryStream();
        QuantizerSerializer.Write(q, ms);
        ms.Position = 0;
        IVectorQuantizer? loaded = QuantizerSerializer.Read(ms);
        Assert.NotNull(loaded);
        Assert.Equal(q.Kind, loaded!.Kind);
        Assert.Equal(q.Dimensions, loaded.Dimensions);
        Assert.Equal(q.CodeBytes, loaded.CodeBytes);
        return loaded;
    }

    private static void AssertEncodeMatches(
        IVectorQuantizer expected,
        IVectorQuantizer actual,
        float[] data,
        int n,
        int dim)
    {
        byte[] e = new byte[expected.CodeBytes];
        byte[] a = new byte[actual.CodeBytes];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<float> v = data.AsSpan(i * dim, dim);
            expected.Encode(v, e);
            actual.Encode(v, a);
            Assert.True(e.AsSpan().SequenceEqual(a), $"行 {i} 编码不一致");
        }
    }

    private static float[] GenerateRandomData(int seed, int n, int dim)
    {
        var rng = new Random(seed);
        float[] data = new float[n * dim];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        }
        return data;
    }
}
