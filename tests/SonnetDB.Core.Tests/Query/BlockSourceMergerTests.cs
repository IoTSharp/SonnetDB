using SonnetDB.Model;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// <see cref="BlockSourceMerger"/> 单元测试：验证惰性 N 路流式有序合并的正确性、稳定性与「按需解码」语义。
/// </summary>
public sealed class BlockSourceMergerTests
{
    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private static DataPoint Dp(long ts) => new(ts, FieldValue.FromDouble(ts));

    private static DataPoint[] MakePoints(params long[] timestamps)
        => timestamps.Select(ts => Dp(ts)).ToArray();

    private static ReadOnlyMemory<DataPoint> Mem(params long[] timestamps)
        => new(MakePoints(timestamps));

    /// <summary>把一批已排序点包成惰性 block 源（LowerBound = 首点时间戳，空块用 long.MaxValue）。</summary>
    private static BlockSourceMerger.LazyBlock Block(params long[] timestamps)
    {
        var pts = MakePoints(timestamps);
        long lb = pts.Length > 0 ? pts[0].Timestamp : long.MaxValue;
        return new BlockSourceMerger.LazyBlock(lb, () => pts);
    }

    private static List<DataPoint> Run(
        IReadOnlyList<ReadOnlyMemory<DataPoint>> mem,
        IReadOnlyList<BlockSourceMerger.LazyBlock> blocks)
        => BlockSourceMerger.Merge(mem, blocks).ToList();

    private static readonly IReadOnlyList<ReadOnlyMemory<DataPoint>> NoMem = Array.Empty<ReadOnlyMemory<DataPoint>>();
    private static readonly IReadOnlyList<BlockSourceMerger.LazyBlock> NoBlocks = Array.Empty<BlockSourceMerger.LazyBlock>();

    // ── 基本场景 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = Run(NoMem, NoBlocks);
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_OnlyMemTable_ReturnsInOrder()
    {
        var result = Run(new[] { Mem(1L, 3L, 5L) }, NoBlocks);

        Assert.Equal(3, result.Count);
        Assert.Equal(1L, result[0].Timestamp);
        Assert.Equal(3L, result[1].Timestamp);
        Assert.Equal(5L, result[2].Timestamp);
    }

    [Fact]
    public void Merge_OnlySegments_ReturnsInOrder()
    {
        var result = Run(NoMem, new[] { Block(2L, 4L, 6L) });

        Assert.Equal(3, result.Count);
        Assert.Equal(2L, result[0].Timestamp);
        Assert.Equal(4L, result[1].Timestamp);
        Assert.Equal(6L, result[2].Timestamp);
    }

    [Fact]
    public void Merge_MemTableAndSegments_Interleaved()
    {
        var result = Run(
            new[] { Mem(1L, 4L, 7L) },
            new[] { Block(2L, 5L, 8L), Block(3L, 6L, 9L) });

        Assert.Equal(9, result.Count);
        for (int i = 0; i < result.Count; i++)
            Assert.Equal((long)(i + 1), result[i].Timestamp);
    }

    [Fact]
    public void Merge_EmptyMemList_HandlesSegmentsOnly()
    {
        var result = Run(NoMem, new[] { Block(10L, 20L, 30L) });
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_EmptySegmentBlock_SkipsEmpty()
    {
        var result = Run(new[] { Mem(1L, 2L) }, new[] { Block() });
        Assert.Equal(2, result.Count);
        Assert.Equal(1L, result[0].Timestamp);
        Assert.Equal(2L, result[1].Timestamp);
    }

    // ── 同 ts 多源全部 yield ────────────────────────────────────────────────

    [Fact]
    public void Merge_SameTimestampMultipleSources_AllYielded()
    {
        var result = Run(
            new[] { Mem(100L) },
            new[] { Block(100L), Block(100L) });

        Assert.Equal(3, result.Count);
        Assert.All(result, dp => Assert.Equal(100L, dp.Timestamp));
    }

    // ── N=4 路，每路 100 点随机时间戳 ──────────────────────────────────────

    [Fact]
    public void Merge_FourSources_100PointsEach_StrictlyOrdered()
    {
        DataPoint[] MakeRandom(int seed)
        {
            var r = new Random(seed);
            var pts = new long[100];
            for (int i = 0; i < 100; i++)
                pts[i] = r.NextInt64(0, 10_000L);
            Array.Sort(pts);
            return pts.Select(ts => Dp(ts)).ToArray();
        }

        var seg1 = MakeRandom(1);
        var seg2 = MakeRandom(2);
        var seg3 = MakeRandom(3);
        var mem = MakeRandom(4);

        long Lb(DataPoint[] p) => p.Length > 0 ? p[0].Timestamp : long.MaxValue;

        var result = Run(
            new[] { new ReadOnlyMemory<DataPoint>(mem) },
            new[]
            {
                new BlockSourceMerger.LazyBlock(Lb(seg1), () => seg1),
                new BlockSourceMerger.LazyBlock(Lb(seg2), () => seg2),
                new BlockSourceMerger.LazyBlock(Lb(seg3), () => seg3),
            });

        Assert.Equal(400, result.Count);
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].Timestamp >= result[i - 1].Timestamp,
                $"result[{i}].Timestamp ({result[i].Timestamp}) < result[{i - 1}].Timestamp ({result[i - 1].Timestamp})");
    }

    // ── 稳定性：同 ts 时段优先于 MemTable ──────────────────────────────────

    [Fact]
    public void Merge_SameTimestamp_SegmentBeforeMemTable()
    {
        var segPt = new DataPoint(100L, FieldValue.FromLong(999L));  // 用特殊值区分
        var memPt = new DataPoint(100L, FieldValue.FromDouble(0.5));

        var seg1 = new[] { segPt };
        var result = Run(
            new[] { new ReadOnlyMemory<DataPoint>(new[] { memPt }) },
            new[] { new BlockSourceMerger.LazyBlock(100L, () => seg1) });

        Assert.Equal(2, result.Count);
        // 段的点（Rank 更小）应先于 MemTable
        Assert.Equal(segPt, result[0]);
        Assert.Equal(memPt, result[1]);
    }

    [Fact]
    public void Merge_SameTimestamp_EarlierSegmentBeforeLaterSegment()
    {
        var a = new DataPoint(50L, FieldValue.FromLong(1L));
        var b = new DataPoint(50L, FieldValue.FromLong(2L));

        var result = Run(
            NoMem,
            new[]
            {
                new BlockSourceMerger.LazyBlock(50L, () => new[] { a }),
                new BlockSourceMerger.LazyBlock(50L, () => new[] { b }),
            });

        Assert.Equal(2, result.Count);
        Assert.Equal(a, result[0]);
        Assert.Equal(b, result[1]);
    }

    // ── 总数守恒 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_TotalCount_EqualsSum()
    {
        var result = Run(
            new[] { Mem(7L, 8L, 9L) },
            new[] { Block(1L, 3L, 5L), Block(2L, 4L, 6L) });

        Assert.Equal(9, result.Count);
    }

    // ── 惰性解码语义 ──────────────────────────────────────────────────────────

    [Fact]
    public void Merge_DoesNotDecodeBlocksBeyondLimit_WhenEnumerationStopsEarly()
    {
        // 三个不相交的 block；只取前 2 个点（都来自 block1），block2/block3 不应被解码。
        int decoded2 = 0, decoded3 = 0;

        var block1 = new BlockSourceMerger.LazyBlock(0L, () => MakePoints(0L, 1L, 2L));
        var block2 = new BlockSourceMerger.LazyBlock(
            100L, () => { decoded2++; return MakePoints(100L, 101L); });
        var block3 = new BlockSourceMerger.LazyBlock(
            200L, () => { decoded3++; return MakePoints(200L, 201L); });

        var taken = BlockSourceMerger.Merge(NoMem, new[] { block1, block2, block3 })
            .Take(2)
            .ToList();

        Assert.Equal(2, taken.Count);
        Assert.Equal(0L, taken[0].Timestamp);
        Assert.Equal(1L, taken[1].Timestamp);
        Assert.Equal(0, decoded2);
        Assert.Equal(0, decoded3);
    }

    [Fact]
    public void Merge_BoundsDecodeWorkingSet_ToOverlapDepth()
    {
        // 10 个互不重叠、时间递增的 block，每个 2 点。全量消费时，任意时刻已解码但未耗尽的 block 数
        // （overlap depth）应为 1——验证不是「先全部解码」。
        int liveDecoded = 0;
        int maxLive = 0;
        var blocks = new List<BlockSourceMerger.LazyBlock>();
        for (int k = 0; k < 10; k++)
        {
            long baseTs = k * 100L;
            blocks.Add(new BlockSourceMerger.LazyBlock(baseTs, () =>
            {
                liveDecoded++;
                if (liveDecoded > maxLive) maxLive = liveDecoded;
                return MakePoints(baseTs, baseTs + 1L);
            }));
        }

        var result = new List<DataPoint>();
        foreach (var dp in BlockSourceMerger.Merge(NoMem, blocks))
        {
            result.Add(dp);
            // 每产出一个点，模拟「上游消费完上一个 chunk 后其内存可回收」——这里只统计解码调用峰值。
            liveDecoded = 0;
        }

        Assert.Equal(20, result.Count);
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].Timestamp >= result[i - 1].Timestamp);
        // 不相交 → 峰值单块解码。
        Assert.Equal(1, maxLive);
    }

    [Fact]
    public void Merge_OverlappingBlocks_MergeCorrectly()
    {
        // 段内压缩可能产出时间区间重叠的 block（MinTimestamp 升序但区间交叠）。
        var result = Run(
            NoMem,
            new[]
            {
                Block(0L, 10L, 20L),   // [0,20]
                Block(5L, 15L, 25L),   // [5,25] 与上一块重叠
            });

        Assert.Equal(6, result.Count);
        long[] expected = { 0L, 5L, 10L, 15L, 20L, 25L };
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], result[i].Timestamp);
    }
}
