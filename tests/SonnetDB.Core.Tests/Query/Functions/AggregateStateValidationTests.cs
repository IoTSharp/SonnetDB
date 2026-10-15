using System.Buffers.Binary;
using SonnetDB.Model;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Query.Functions;

/// <summary>聚合原始整数身份与中间状态校验回归。</summary>
public sealed class AggregateStateValidationTests
{
    /// <summary>大整数众数按原始值合并，并保留 Int64 结果与最小值破平合同。</summary>
    [Fact]
    public void Mode_Int64Merge_PreservesAdjacentValuesAndTieOrder()
    {
        const long Lower = 9007199254740992L;
        const long Upper = Lower + 1;
        var left = new ModeAccumulator(FieldType.Int64);
        var right = new ModeAccumulator(FieldType.Int64);
        left.Add(FieldValue.FromLong(Lower));
        right.Add(FieldValue.FromLong(Upper));
        right.Add(FieldValue.FromLong(Upper));
        left.Merge(right);
        Assert.Equal(Upper, Assert.IsType<long>(left.Finalize()));
        Assert.Equal(3L, left.Count);

        left.Add(FieldValue.FromLong(Lower));
        Assert.Equal(Lower, Assert.IsType<long>(left.Finalize()));
        Assert.Equal(Lower, Assert.IsType<long>(left.Finalize()));
    }

    /// <summary>Int64 基数通过完整位模式区分相邻大整数，重复合并继续统计样本数。</summary>
    [Fact]
    public void DistinctCount_Int64Merge_PreservesAdjacentValues()
    {
        var left = new DistinctCountAccumulator(FieldType.Int64);
        var right = new DistinctCountAccumulator(FieldType.Int64);
        left.Add(FieldValue.FromLong(9007199254740992L));
        right.Add(FieldValue.FromLong(9007199254740993L));
        left.Merge(right);
        left.Merge(right);
        Assert.Equal(2L, Assert.IsType<long>(left.Finalize()));
        Assert.Equal(3L, left.Count);
        Assert.False(left.CanMergeLegacyNumericSketch);
        Assert.Throws<InvalidOperationException>(() => left.MergeSketch(new HyperLogLog(), 1));
        Assert.Equal(2L, Assert.IsType<long>(left.Finalize()));
    }

    /// <summary>原始整数累加器拒绝会丢失身份的 Double 调用。</summary>
    [Fact]
    public void Int64Accumulators_DoubleInput_RejectsBeforeMutation()
    {
        var mode = new ModeAccumulator(FieldType.Int64);
        var distinct = new DistinctCountAccumulator(FieldType.Int64);
        Assert.Throws<InvalidOperationException>(() => mode.Add(9007199254740992d));
        Assert.Throws<InvalidOperationException>(() => distinct.Add(9007199254740992d));
        Assert.Equal(0L, mode.Count);
        Assert.Equal(0L, distinct.Count);
    }

    /// <summary>反序列化拒绝未排序的 centroid，避免在错误顺序上求分位数。</summary>
    [Fact]
    public void TDigestDeserialize_UnsortedMeans_RejectsInvalidState()
        => Assert.Throws<InvalidDataException>(() => TDigest.Deserialize(DigestPayload((10, 1), (0, 1))));

    /// <summary>样本权重必须是可计入 Int64 的正整数。</summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(0.5d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(9223372036854775808d)]
    public void TDigestDeserialize_InvalidWeight_RejectsInvalidState(double weight)
        => Assert.Throws<InvalidDataException>(() => TDigest.Deserialize(DigestPayload((1, weight))));

    /// <summary>单项合法但总数溢出的状态按损坏载荷拒绝。</summary>
    [Fact]
    public void TDigestDeserialize_TotalCountOverflow_ThrowsInvalidData()
        => Assert.Throws<InvalidDataException>(() => TDigest.Deserialize(
            DigestPayload((1, 4611686018427387904d), (2, 4611686018427387904d))));

    /// <summary>合法载荷 round-trip 保持计数和分位结果。</summary>
    [Fact]
    public void TDigestDeserialize_ValidRoundTrip_PreservesCountAndQuantiles()
    {
        var digest = new TDigest();
        for (int i = 0; i < 1000; i++)
            digest.Add(i % 19);
        TDigest restored = TDigest.Deserialize(digest.Serialize());
        Assert.Equal(digest.Count, restored.Count);
        Assert.Equal(digest.Quantile(0.5), restored.Quantile(0.5));
        Assert.Equal(digest.Quantile(0.99), restored.Quantile(0.99));
    }

    /// <summary>哈希剩余位数之外的寄存器状态不可恢复。</summary>
    [Theory]
    [InlineData(52)]
    [InlineData(64)]
    [InlineData(255)]
    public void HyperLogLogDeserialize_InvalidRank_RejectsInvalidState(int rank)
    {
        byte[] payload = new HyperLogLog().Serialize();
        payload[^1] = (byte)rank;
        Assert.Throws<InvalidDataException>(() => HyperLogLog.Deserialize(payload));
    }

    /// <summary>最大合法寄存器与正常整数 sketch 均保持 round-trip。</summary>
    [Fact]
    public void HyperLogLogDeserialize_ValidRanks_PreservesSnapshot()
    {
        var hll = new HyperLogLog();
        hll.Add(9007199254740992L);
        hll.Add(9007199254740993L);
        hll.AddHash(0);
        byte[] payload = hll.Serialize();
        Assert.Equal(51, payload[12]);
        HyperLogLog restored = HyperLogLog.Deserialize(payload);
        Assert.Equal(payload, restored.Serialize());
        Assert.Equal(hll.Estimate(), restored.Estimate());
    }

    /// <summary>非法桶宽在创建时拒绝。</summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Histogram_InvalidWidth_RejectsBeforeCreatingState(double width)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new HistogramAccumulator(width));

    /// <summary>无法表示的桶不改变已累加的计数或结果。</summary>
    [Theory]
    [InlineData(double.MaxValue, 10d)]
    [InlineData(1d, double.Epsilon)]
    [InlineData(9007199254740992d, 1d)]
    [InlineData(double.MaxValue, double.MaxValue)]
    public void Histogram_UnrepresentableBucket_RejectsWithoutMutation(double value, double width)
    {
        var histogram = new HistogramAccumulator(width);
        histogram.Add(0);
        string before = Assert.IsType<string>(histogram.Finalize());
        Assert.Throws<InvalidOperationException>(() => histogram.Add(value));
        Assert.Equal(1L, histogram.Count);
        Assert.Equal(before, histogram.Finalize());
    }

    /// <summary>负桶和正常合并仍保持准确计数。</summary>
    [Fact]
    public void Histogram_ValidNegativeBucketsAndMerge_PreservesCounts()
    {
        var left = new HistogramAccumulator(10);
        var right = new HistogramAccumulator(10);
        left.Add(-1);
        right.Add(-10);
        right.Add(0);
        left.Merge(right);
        Assert.Equal(3L, left.Count);
        Assert.Equal("{\"[-10,0)\":2,\"[0,10)\":1}", left.Finalize());
    }

    /// <summary>普通小数相除和桶边界的浮点舍入不作为非法输入。</summary>
    [Theory]
    [InlineData(-99.9d, 0.3d)]
    [InlineData(-98.7d, 0.7d)]
    [InlineData(0.3d, 0.1d)]
    public void Histogram_FractionalBoundaryRounding_PreservesExistingBucketRule(double value, double width)
    {
        var histogram = new HistogramAccumulator(width);
        histogram.Add(value);
        Assert.Equal(1L, histogram.Count);
        Assert.NotNull(histogram.Finalize());
    }

    private static byte[] DigestPayload(params (double Mean, double Weight)[] centroids)
    {
        byte[] bytes = new byte[8 + centroids.Length * 16];
        "TD01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), centroids.Length);
        for (int i = 0; i < centroids.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(8 + i * 16), centroids[i].Mean);
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(16 + i * 16), centroids[i].Weight);
        }
        return bytes;
    }
}
