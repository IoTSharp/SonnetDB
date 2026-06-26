using SonnetDB.Vector.Compression;
using SonnetDB.Vector.Index.Flat;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.Flat;

public sealed class QuantizedFlatIndexTests
{
    [Fact]
    public void Ctor_UntrainedQuantizer_Throws()
    {
        var sq = new ScalarQuantizer8(8);
        Assert.Throws<ArgumentException>(() => new QuantizedFlatIndex<int>(sq));
    }

    [Fact]
    public void Ctor_NonL2Metric_Throws()
    {
        ScalarQuantizer8 sq = TrainSq8(8, seed: 1);
        Assert.Throws<NotSupportedException>(() => new QuantizedFlatIndex<int>(sq, Metric.Cosine));
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        ScalarQuantizer8 sq = TrainSq8(8, seed: 2);
        using var idx = new QuantizedFlatIndex<int>(sq);
        idx.Add(1, new float[8]);
        Assert.Throws<ArgumentException>(() => idx.Add(1, new float[8]));
    }

    [Fact]
    public void Add_DimensionMismatch_Throws()
    {
        ScalarQuantizer8 sq = TrainSq8(8, seed: 3);
        using var idx = new QuantizedFlatIndex<int>(sq);
        Assert.Throws<ArgumentException>(() => idx.Add(1, new float[7]));
    }

    [Fact]
    public void Search_OnEmptyIndex_ReturnsZero()
    {
        ScalarQuantizer8 sq = TrainSq8(8, seed: 4);
        using var idx = new QuantizedFlatIndex<int>(sq);
        var results = new (int Key, float Score)[5];
        Assert.Equal(0, idx.Search(new float[8], 5, results));
    }

    [Fact]
    public void Remove_ExistingKey_ReducesCount()
    {
        ScalarQuantizer8 sq = TrainSq8(8, seed: 5);
        using var idx = new QuantizedFlatIndex<int>(sq);
        idx.Add(1, new float[8]);
        idx.Add(2, new float[8]);
        Assert.True(idx.Remove(1));
        Assert.Equal(1, idx.Count);
        Assert.False(idx.ContainsKey(1));
        Assert.True(idx.ContainsKey(2));
    }

    [Fact]
    public void Search_Sq8_TopKMatchesRawFlatHighOverlap()
    {
        const int dim = 32;
        const int n = 400;
        const int topK = 10;

        float[] data = GenerateRandomData(seed: 11, n, dim);
        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        using var qIdx = new QuantizedFlatIndex<int>(sq);
        using var rawIdx = new FlatIndex<int>(dim, Metric.L2);
        for (int i = 0; i < n; i++)
        {
            qIdx.Add(i, data.AsSpan(i * dim, dim));
            rawIdx.Add(i, data.AsSpan(i * dim, dim));
        }

        // 用前 20 条作为查询，统计 Top-K 重合率（SQ8 误差小，期望 recall ≥ 0.8）。
        const int queries = 20;
        int totalHits = 0;
        var qBuf = new (int Key, float Score)[topK];
        var rBuf = new (int Key, float Score)[topK];
        for (int q = 0; q < queries; q++)
        {
            ReadOnlySpan<float> query = data.AsSpan(q * dim, dim);
            int qN = qIdx.Search(query, topK, qBuf);
            int rN = rawIdx.Search(query, topK, rBuf);
            Assert.Equal(topK, qN);
            Assert.Equal(topK, rN);

            var rSet = new HashSet<int>();
            for (int i = 0; i < rN; i++)
            {
                rSet.Add(rBuf[i].Key);
            }
            for (int i = 0; i < qN; i++)
            {
                if (rSet.Contains(qBuf[i].Key))
                {
                    totalHits++;
                }
            }
        }

        double recall = totalHits / (double)(queries * topK);
        Assert.True(recall >= 0.8, $"SQ8 vs raw recall@{topK} = {recall:F3} 期望 ≥ 0.8");
    }

    [Fact]
    public void Search_Sq8_QueryItselfRanksFirst()
    {
        const int dim = 24;
        const int n = 200;
        const int topK = 5;

        float[] data = GenerateRandomData(seed: 13, n, dim);
        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);

        using var idx = new QuantizedFlatIndex<int>(sq);
        idx.AddBatch(Enumerable.Range(0, n).ToList(), data);

        var buf = new (int Key, float Score)[topK];
        // 直接查询样本本身：top1 应大概率是自己（SQ8 量化误差极小）。
        int hits = 0;
        for (int q = 0; q < 50; q++)
        {
            int wrote = idx.Search(data.AsSpan(q * dim, dim), topK, buf);
            Assert.Equal(topK, wrote);
            if (buf[0].Key == q)
            {
                hits++;
            }
        }
        Assert.True(hits >= 45, $"SQ8 自查询 top1 命中 {hits}/50");
    }

    [Fact]
    public void Snapshot_PreservesCodesAndKeys()
    {
        const int dim = 8;
        ScalarQuantizer8 sq = TrainSq8(dim, seed: 21);
        using var idx = new QuantizedFlatIndex<int>(sq);
        idx.Add(10, new float[dim]);
        idx.Add(20, new float[dim]);

        idx.Snapshot(out List<int> keys, out byte[] codes);
        Assert.Equal(new[] { 10, 20 }, keys);
        Assert.Equal(2 * sq.CodeBytes, codes.Length);
    }

    private static ScalarQuantizer8 TrainSq8(int dim, int seed)
    {
        const int n = 200;
        float[] data = GenerateRandomData(seed, n, dim);
        var sq = new ScalarQuantizer8(dim);
        sq.Train(data, n);
        return sq;
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
