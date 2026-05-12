using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>
/// <see cref="SegmentIndex"/> Block 级跳跃索引（MaxTimestamp 前缀最大值二分）专项测试。
/// 验证：
/// <list type="number">
///   <item><description>非重叠 block 序列在窄/宽时间窗下命中集合正确，且优于线性下界；</description></item>
///   <item><description>压缩产生的重叠 block（MaxTimestamp 非单调）下结果集仍与朴素 O(n) 一致；</description></item>
///   <item><description>边界等值（from == MaxTs / to == MinTs / from == to）下结果集正确；</description></item>
///   <item><description>多 field 单 series 时间窗剪枝（GetBlocks(seriesId, from, to)）行为正确；</description></item>
///   <item><description>随机 fuzz 与朴素实现对拍。</description></item>
/// </list>
/// </summary>
public sealed class SegmentIndexSkippingTests
{
    private const ulong SeriesA = 0x1111UL;
    private const ulong SeriesB = 0x2222UL;
    private const string FieldX = "x";
    private const string FieldY = "y";

    private static BlockDescriptor MakeBlock(
        int index, ulong seriesId, string field, long minTs, long maxTs)
    {
        // 仅填充本测试关心的字段；其余保留默认值不影响 SegmentIndex 行为。
        return new BlockDescriptor
        {
            Index = index,
            SeriesId = seriesId,
            FieldName = field,
            MinTimestamp = minTs,
            MaxTimestamp = maxTs,
            Count = (int)(maxTs - minTs + 1),
            FieldType = FieldType.Float64,
            TimestampEncoding = BlockEncoding.None,
            ValueEncoding = BlockEncoding.None,
        };
    }

    /// <summary>
    /// 朴素参考实现：扫描整张 (sid, field) 桶，返回所有时间窗相交的 block。
    /// 用于 fuzz 测试与新实现对拍。
    /// </summary>
    private static List<BlockDescriptor> NaiveOverlap(
        IEnumerable<BlockDescriptor> blocks, ulong sid, string field, long from, long to)
    {
        var r = new List<BlockDescriptor>();
        foreach (var b in blocks)
        {
            if (b.SeriesId != sid || b.FieldName != field) continue;
            if (b.MinTimestamp <= to && b.MaxTimestamp >= from) r.Add(b);
        }
        r.Sort(static (a, b) => a.MinTimestamp.CompareTo(b.MinTimestamp));
        return r;
    }

    private static List<BlockDescriptor> NaiveOverlapBySeries(
        IEnumerable<BlockDescriptor> blocks, ulong sid, long from, long to)
    {
        var r = new List<BlockDescriptor>();
        foreach (var b in blocks)
        {
            if (b.SeriesId != sid) continue;
            if (b.MinTimestamp <= to && b.MaxTimestamp >= from) r.Add(b);
        }
        r.Sort(static (a, b) => a.MinTimestamp.CompareTo(b.MinTimestamp));
        return r;
    }

    [Fact]
    public void GetBlocks_NonOverlappingBlocks_NarrowWindow_ReturnsOnlyHits()
    {
        // 5 个相邻不重叠 block：[0,99] [100,199] [200,299] [300,399] [400,499]
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0,  99),
            MakeBlock(1, SeriesA, FieldX, 100, 199),
            MakeBlock(2, SeriesA, FieldX, 200, 299),
            MakeBlock(3, SeriesA, FieldX, 300, 399),
            MakeBlock(4, SeriesA, FieldX, 400, 499),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        // 窄窗 [250, 320] → 命中 block2、block3
        var hits = idx.GetBlocks(SeriesA, FieldX, 250, 320);
        Assert.Equal(2, hits.Count);
        Assert.Equal(200, hits[0].MinTimestamp);
        Assert.Equal(300, hits[1].MinTimestamp);
    }

    [Fact]
    public void GetBlocks_NonOverlappingBlocks_FullCover_ReturnsAll()
    {
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0,  99),
            MakeBlock(1, SeriesA, FieldX, 100, 199),
            MakeBlock(2, SeriesA, FieldX, 200, 299),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, FieldX, long.MinValue, long.MaxValue);
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public void GetBlocks_QueryEntirelyBeforeAllBlocks_ReturnsEmpty()
    {
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX, 100, 199),
            MakeBlock(1, SeriesA, FieldX, 200, 299),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, FieldX, 0, 50);
        Assert.Empty(hits);
    }

    [Fact]
    public void GetBlocks_QueryEntirelyAfterAllBlocks_ReturnsEmpty()
    {
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX, 100, 199),
            MakeBlock(1, SeriesA, FieldX, 200, 299),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, FieldX, 1_000, 2_000);
        Assert.Empty(hits);
    }

    [Theory]
    // from == 某 block 的 MaxTimestamp，应命中该 block
    [InlineData(199L, 199L, 1)]
    // to == 某 block 的 MinTimestamp，应命中该 block
    [InlineData(200L, 200L, 1)]
    // 单点查询正好落在两 block 之间的边界（恰好等于左 block 的 MaxTs）
    [InlineData(99L, 99L, 1)]
    public void GetBlocks_BoundaryEqualities_AreInclusive(long from, long to, int expectedCount)
    {
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0,  99),
            MakeBlock(1, SeriesA, FieldX, 100, 199),
            MakeBlock(2, SeriesA, FieldX, 200, 299),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, FieldX, from, to);
        Assert.Equal(expectedCount, hits.Count);
    }

    [Fact]
    public void GetBlocks_OverlappingBlocks_CompactionScenario_StillCorrect()
    {
        // 模拟压缩产生的重叠 block：按 MinTimestamp 升序，但 MaxTimestamp 非单调。
        // block0 跨度极大 [0, 1000]，block1 [100, 200]，block2 [300, 400]。
        // 当查询 [150, 180] 时，朴素逻辑命中 block0 + block1；新实现也必须命中两者。
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0, 1000),
            MakeBlock(1, SeriesA, FieldX, 100,  200),
            MakeBlock(2, SeriesA, FieldX, 300,  400),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, FieldX, 150, 180);
        var naive = NaiveOverlap(blocks, SeriesA, FieldX, 150, 180);
        Assert.Equal(naive.Select(b => b.Index).ToArray(),
                     hits.Select(b => b.Index).ToArray());
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void GetBlocks_OverlappingBlocks_QueryFitsInsideShortInnerBlock()
    {
        // 同上，但查询窗口 [350, 370] 严格落在 block2 内部；block0 (0..1000) 也覆盖该窗口。
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0, 1000),
            MakeBlock(1, SeriesA, FieldX, 100,  200),
            MakeBlock(2, SeriesA, FieldX, 300,  400),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, FieldX, 350, 370);
        var naive = NaiveOverlap(blocks, SeriesA, FieldX, 350, 370);
        Assert.Equal(naive.Select(b => b.Index).ToArray(),
                     hits.Select(b => b.Index).ToArray());
        Assert.Equal(2, hits.Count);  // block0 + block2
    }

    [Fact]
    public void GetBlocks_BySeriesAndTimeRange_MultipleFields_ReturnsOnlyOverlapping()
    {
        // 同 series 下两 field：
        //   x: [0, 99] [200, 299]
        //   y: [100, 199] [300, 399]
        // 查询 [120, 250] → x:[200,299] + y:[100,199]
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0,  99),
            MakeBlock(1, SeriesA, FieldX, 200, 299),
            MakeBlock(2, SeriesA, FieldY, 100, 199),
            MakeBlock(3, SeriesA, FieldY, 300, 399),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hits = idx.GetBlocks(SeriesA, 120, 250);
        var naive = NaiveOverlapBySeries(blocks, SeriesA, 120, 250);
        Assert.Equal(naive.Count, hits.Count);
        // 以 (MinTimestamp, FieldName) 多重集合方式比较，避免 List.Sort 非稳定带来的顺序差异。
        var expectedKeys = naive.Select(b => (b.MinTimestamp, b.FieldName))
            .OrderBy(t => t.MinTimestamp).ThenBy(t => t.FieldName, StringComparer.Ordinal).ToList();
        var actualKeys = hits.Select(b => (b.MinTimestamp, b.FieldName))
            .OrderBy(t => t.MinTimestamp).ThenBy(t => t.FieldName, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedKeys, actualKeys);
    }

    [Fact]
    public void GetBlocks_UnknownSeriesField_ReturnsEmpty_NoAlloc()
    {
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "empty.SDBSEG", Array.Empty<BlockDescriptor>());

        Assert.Same(Array.Empty<BlockDescriptor>(), idx.GetBlocks(SeriesA, FieldX, 0, 100));
        Assert.Same(Array.Empty<BlockDescriptor>(), idx.GetBlocks(SeriesA, 0, 100));
    }

    [Fact]
    public void GetBlocks_OtherSeriesNotIncluded()
    {
        var blocks = new List<BlockDescriptor>
        {
            MakeBlock(0, SeriesA, FieldX,   0,  99),
            MakeBlock(1, SeriesB, FieldX, 100, 199),
        };
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fake.SDBSEG", blocks);

        var hitsA = idx.GetBlocks(SeriesA, FieldX, 0, long.MaxValue);
        Assert.Single(hitsA);
        Assert.Equal(SeriesA, hitsA[0].SeriesId);

        var hitsB = idx.GetBlocks(SeriesB, FieldX, 0, long.MaxValue);
        Assert.Single(hitsB);
        Assert.Equal(SeriesB, hitsB[0].SeriesId);
    }

    [Fact]
    public void GetBlocks_Fuzz_AgreesWithNaiveImplementation()
    {
        const int blockCount = 64;
        const int queryCount = 300;
        var rng = new Random(20251231);

        // 生成 64 个随机 block：MinTimestamp ∈ [0, 10000)，长度 ∈ [1, 500]，
        // 随机分配到两个 series × 两个 field 中，形成可能的"压缩重叠"分布。
        var blocks = new List<BlockDescriptor>(blockCount);
        for (int i = 0; i < blockCount; i++)
        {
            long min = rng.Next(0, 10_000);
            long max = min + rng.Next(1, 500);
            ulong sid = rng.Next(2) == 0 ? SeriesA : SeriesB;
            string field = rng.Next(2) == 0 ? FieldX : FieldY;
            blocks.Add(MakeBlock(i, sid, field, min, max));
        }
        var idx = SegmentIndex.BuildFromBlocksForTesting(1L, "fuzz.SDBSEG", blocks);

        var sids = new[] { SeriesA, SeriesB };
        var fields = new[] { FieldX, FieldY };

        for (int q = 0; q < queryCount; q++)
        {
            ulong sid = sids[rng.Next(2)];
            string field = fields[rng.Next(2)];
            long a = rng.Next(-200, 11_000);
            long b = rng.Next(-200, 11_000);
            long from = Math.Min(a, b);
            long to = Math.Max(a, b);

            // 单 field 路径
            var hits = idx.GetBlocks(sid, field, from, to);
            var naive = NaiveOverlap(blocks, sid, field, from, to);
            Assert.Equal(naive.Count, hits.Count);
            // 单 field 路径：MinTimestamp 互不相同的概率高，但仍以 (Index) 多重集合比较以避免排序非稳定影响。
            var expectedSet = naive.Select(b => b.Index).OrderBy(i => i).ToArray();
            var actualSet = hits.Select(b => b.Index).OrderBy(i => i).ToArray();
            Assert.Equal(expectedSet, actualSet);

            // 多 field（按 series）路径
            var hits2 = idx.GetBlocks(sid, from, to);
            var naive2 = NaiveOverlapBySeries(blocks, sid, from, to);
            Assert.Equal(naive2.Count, hits2.Count);
            // List.Sort 非稳定：MinTimestamp 相同的两 block 顺序可能不同，按 Index 对集合做比较。
            var expectedSet2 = naive2.Select(b => b.Index).OrderBy(i => i).ToArray();
            var actualSet2 = hits2.Select(b => b.Index).OrderBy(i => i).ToArray();
            Assert.Equal(expectedSet2, actualSet2);
        }
    }
}
