using SonnetDB.Vector.Index.Flat;
using SonnetDB.Vector.Model;

namespace SonnetDB.Core.Tests.Vector.Index.Flat;

public sealed class FlatIndexTests
{
    [Fact]
    public void Ctor_HammingMetric_Throws()
    {
        Assert.Throws<NotSupportedException>(() => new FlatIndex<int>(8, Metric.Hamming));
    }

    [Fact]
    public void Ctor_NonPositiveDimensions_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlatIndex<int>(0, Metric.L2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlatIndex<int>(-1, Metric.L2));
    }

    [Fact]
    public void Add_DimensionMismatch_Throws()
    {
        using var index = new FlatIndex<int>(4, Metric.L2);
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void Add_DuplicateKey_Throws()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        index.Add(1, new float[] { 1f, 2f });
        Assert.Throws<ArgumentException>(() => index.Add(1, new float[] { 3f, 4f }));
    }

    [Fact]
    public void Add_ThenSearch_ReturnsExactMatchAtTop()
    {
        using var index = new FlatIndex<int>(3, Metric.L2);
        index.Add(1, new float[] { 0f, 0f, 0f });
        index.Add(2, new float[] { 1f, 0f, 0f });
        index.Add(3, new float[] { 5f, 5f, 5f });

        Span<(int, float)> buf = stackalloc (int, float)[3];
        int n = index.Search(new float[] { 1f, 0f, 0f }, 3, buf);

        Assert.Equal(3, n);
        Assert.Equal(2, buf[0].Item1); // 距离 0
        Assert.Equal(1, buf[1].Item1); // 距离 1
        Assert.Equal(3, buf[2].Item1); // 距离最远
    }

    [Fact]
    public void Search_OnEmptyIndex_ReturnsZero()
    {
        using var index = new FlatIndex<int>(4, Metric.L2);
        var buf = new (int, float)[5];
        int n = index.Search(new float[4], 5, buf);
        Assert.Equal(0, n);
    }

    [Fact]
    public void Search_TopK_LargerThanCount_ReturnsAllAvailable()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        index.Add(1, new float[] { 0f, 0f });
        index.Add(2, new float[] { 1f, 1f });

        var buf = new (int, float)[5];
        int n = index.Search(new float[] { 0f, 0f }, 5, buf);
        Assert.Equal(2, n);
    }

    [Theory]
    [InlineData(Metric.L2)]
    [InlineData(Metric.Cosine)]
    [InlineData(Metric.DotProduct)]
    [InlineData(Metric.InnerProduct)]
    public void Search_TopK_ReturnsResultsSortedByMetric(Metric metric)
    {
        using var index = new FlatIndex<int>(3, metric);
        index.Add(1, new float[] { 1f, 0f, 0f });
        index.Add(2, new float[] { 0.9f, 0.1f, 0f });
        index.Add(3, new float[] { -1f, 0f, 0f });

        Span<(int Key, float Score)> buf = stackalloc (int, float)[3];
        int n = index.Search(new float[] { 1f, 0f, 0f }, 3, buf);

        Assert.Equal(3, n);
        bool largerBetter = metric.IsLargerBetter();
        for (int i = 1; i < n; i++)
        {
            if (largerBetter)
            {
                Assert.True(buf[i - 1].Score >= buf[i].Score, $"分数应单调递减：{buf[i - 1].Score} >= {buf[i].Score}");
            }
            else
            {
                Assert.True(buf[i - 1].Score <= buf[i].Score, $"分数应单调递增：{buf[i - 1].Score} <= {buf[i].Score}");
            }
        }
        // 与查询完全相同的向量应排在第一。
        Assert.Equal(1, buf[0].Key);
    }

    [Fact]
    public void Remove_ExistingKey_RemovesAndUpdatesCount()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        index.Add(1, new float[] { 1f, 0f });
        index.Add(2, new float[] { 0f, 1f });
        index.Add(3, new float[] { 0f, 0f });

        Assert.True(index.Remove(2));
        Assert.Equal(2, index.Count);
        Assert.False(index.ContainsKey(2));
        Assert.True(index.ContainsKey(1));
        Assert.True(index.ContainsKey(3));

        // 删除后再搜，应仍能正确找到剩余元素。
        Span<(int, float)> buf = stackalloc (int, float)[2];
        int n = index.Search(new float[] { 0f, 0f }, 2, buf);
        Assert.Equal(2, n);
        Assert.Contains(buf[0].Item1, new[] { 1, 3 });
        Assert.Contains(buf[1].Item1, new[] { 1, 3 });
    }

    [Fact]
    public void Remove_NonExistent_ReturnsFalse()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        Assert.False(index.Remove(42));
    }

    [Fact]
    public void AddBatch_InsertsAllVectors()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        var keys = new[] { 10, 20, 30 };
        var vectors = new float[]
        {
            1f, 0f,
            0f, 1f,
            1f, 1f,
        };
        index.AddBatch(keys, vectors);
        Assert.Equal(3, index.Count);
        Assert.True(index.ContainsKey(10));
        Assert.True(index.ContainsKey(20));
        Assert.True(index.ContainsKey(30));
    }

    [Fact]
    public void AddBatch_LengthMismatch_Throws()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        Assert.Throws<ArgumentException>(() =>
            index.AddBatch(new[] { 1, 2 }, new float[] { 1f, 2f, 3f }));
    }

    [Fact]
    public void AddBatch_DuplicateKey_AtomicallyRejects()
    {
        using var index = new FlatIndex<int>(2, Metric.L2);
        index.Add(99, new float[] { 0f, 0f });

        var keys = new[] { 1, 99, 2 };
        var vectors = new float[] { 1f, 0f, 0f, 1f, 1f, 1f };
        Assert.Throws<ArgumentException>(() => index.AddBatch(keys, vectors));

        // 原子性：失败后状态不变。
        Assert.Equal(1, index.Count);
        Assert.False(index.ContainsKey(1));
        Assert.False(index.ContainsKey(2));
    }

    [Fact]
    public void Search_ConcurrentReads_AreSafe()
    {
        using var index = new FlatIndex<int>(8, Metric.L2);
        var rng = new Random(42);
        for (int i = 0; i < 200; i++)
        {
            var v = new float[8];
            for (int j = 0; j < 8; j++) { v[j] = (float)rng.NextDouble(); }
            index.Add(i, v);
        }

        var query = new float[8];
        for (int j = 0; j < 8; j++) { query[j] = (float)rng.NextDouble(); }

        // 单线程基线
        var baseline = new (int, float)[5];
        int baseN = index.Search(query, 5, baseline);

        // 并发读
        Parallel.For(0, 64, _ =>
        {
            Span<(int Key, float Score)> buf = stackalloc (int, float)[5];
            int n = index.Search(query, 5, buf);
            Assert.Equal(baseN, n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(baseline[i].Item1, buf[i].Key);
            }
        });
    }
}
