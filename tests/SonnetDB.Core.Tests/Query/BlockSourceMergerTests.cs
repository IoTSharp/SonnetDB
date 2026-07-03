using SonnetDB.Model;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.Core.Tests.Query;

/// <summary>
/// <see cref="BlockSourceMerger"/> 单元测试：验证 N 路有序合并的正确性。
/// </summary>
public sealed class BlockSourceMergerTests
{
    // ── 辅助方法 ──────────────────────────────────────────────────────────────

    private static DataPoint Dp(long ts) => new(ts, FieldValue.FromDouble(ts));

    private static DataPoint[] MakePoints(params long[] timestamps)
        => timestamps.Select(ts => Dp(ts)).ToArray();

    // ── 基本场景 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var result = BlockSourceMerger.Merge((ReadOnlyMemory<DataPoint>?)null, Array.Empty<DataPoint[]>()).ToList();
        Assert.Empty(result);
    }

    [Fact]
    public void Merge_OnlyMemTable_ReturnsInOrder()
    {
        var mem = new ReadOnlyMemory<DataPoint>(MakePoints(1L, 3L, 5L));
        var result = BlockSourceMerger.Merge(mem, Array.Empty<DataPoint[]>()).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(1L, result[0].Timestamp);
        Assert.Equal(3L, result[1].Timestamp);
        Assert.Equal(5L, result[2].Timestamp);
    }

    [Fact]
    public void Merge_OnlySegments_ReturnsInOrder()
    {
        var seg1 = MakePoints(2L, 4L, 6L);
        var result = BlockSourceMerger.Merge((ReadOnlyMemory<DataPoint>?)null, new[] { seg1 }).ToList();

        Assert.Equal(3, result.Count);
        Assert.Equal(2L, result[0].Timestamp);
        Assert.Equal(4L, result[1].Timestamp);
        Assert.Equal(6L, result[2].Timestamp);
    }

    [Fact]
    public void Merge_MemTableAndSegments_Interleaved()
    {
        var mem = new ReadOnlyMemory<DataPoint>(MakePoints(1L, 4L, 7L));
        var seg1 = MakePoints(2L, 5L, 8L);
        var seg2 = MakePoints(3L, 6L, 9L);

        var result = BlockSourceMerger.Merge(mem, new[] { seg1, seg2 }).ToList();

        Assert.Equal(9, result.Count);
        for (int i = 0; i < result.Count; i++)
            Assert.Equal((long)(i + 1), result[i].Timestamp);
    }

    [Fact]
    public void Merge_EmptyMemTable_HandlesNull()
    {
        var seg1 = MakePoints(10L, 20L, 30L);
        var result = BlockSourceMerger.Merge((ReadOnlyMemory<DataPoint>?)null, new[] { seg1 }).ToList();
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Merge_EmptySegmentSlice_SkipsEmpty()
    {
        var mem = new ReadOnlyMemory<DataPoint>(MakePoints(1L, 2L));
        var empty = Array.Empty<DataPoint>();
        var result = BlockSourceMerger.Merge(mem, new[] { empty }).ToList();
        Assert.Equal(2, result.Count);
    }

    // ── 同 ts 多源全部 yield ────────────────────────────────────────────────

    [Fact]
    public void Merge_SameTimestampMultipleSources_AllYielded()
    {
        var mem = new ReadOnlyMemory<DataPoint>(MakePoints(100L));
        var seg1 = MakePoints(100L);
        var seg2 = MakePoints(100L);

        var result = BlockSourceMerger.Merge(mem, new[] { seg1, seg2 }).ToList();

        // 全部三条都应输出
        Assert.Equal(3, result.Count);
        Assert.All(result, dp => Assert.Equal(100L, dp.Timestamp));
    }

    // ── N=4 路，每路 100 点随机时间戳 ──────────────────────────────────────

    [Fact]
    public void Merge_FourSources_100PointsEach_StrictlyOrdered()
    {
        var rng = new Random(42);

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
        var mem = new ReadOnlyMemory<DataPoint>(MakeRandom(4));

        var result = BlockSourceMerger.Merge(mem, new[] { seg1, seg2, seg3 }).ToList();

        Assert.Equal(400, result.Count);

        // 验证升序（允许相等，但不允许后面的小于前面的）
        for (int i = 1; i < result.Count; i++)
            Assert.True(result[i].Timestamp >= result[i - 1].Timestamp,
                $"result[{i}].Timestamp ({result[i].Timestamp}) < result[{i - 1}].Timestamp ({result[i - 1].Timestamp})");
    }

    // ── 稳定性：同 ts 时段优先于 MemTable ──────────────────────────────────

    [Fact]
    public void Merge_SameTimestamp_SegmentBeforeMemTable()
    {
        // seg 先，MemTable 后（InputIndex：seg=0, mem=1）
        var segPt = new DataPoint(100L, FieldValue.FromLong(999L));  // 用特殊值区分
        var memPt = new DataPoint(100L, FieldValue.FromDouble(0.5));

        var seg1 = new[] { segPt };
        var mem = new ReadOnlyMemory<DataPoint>(new[] { memPt });

        var result = BlockSourceMerger.Merge(mem, new[] { seg1 }).ToList();

        Assert.Equal(2, result.Count);
        // 段的点（InputIndex=0）应该先于 MemTable（InputIndex=1）
        Assert.Equal(segPt, result[0]);
        Assert.Equal(memPt, result[1]);
    }

    // ── 总数守恒 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_TotalCount_EqualsSum()
    {
        var seg1 = MakePoints(1L, 3L, 5L);
        var seg2 = MakePoints(2L, 4L, 6L);
        var mem = new ReadOnlyMemory<DataPoint>(MakePoints(7L, 8L, 9L));

        var result = BlockSourceMerger.Merge(mem, new[] { seg1, seg2 }).ToList();

        Assert.Equal(9, result.Count);
    }
}
