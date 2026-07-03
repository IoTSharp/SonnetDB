using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>
/// <see cref="TombstoneTable"/> 的单元测试。
/// </summary>
public sealed class TombstoneTableTests
{
    private static Tombstone MakeTombstone(ulong seriesId, string field, long from, long to, long lsn = 1) =>
        new Tombstone(seriesId, field, from, to, lsn);

    [Fact]
    public void IsCovered_AtExactBoundaries_ReturnsTrue()
    {
        var table = new TombstoneTable();
        table.Add(MakeTombstone(1UL, "f", 100, 200));

        Assert.True(table.IsCovered(1UL, "f", 100));   // from boundary
        Assert.True(table.IsCovered(1UL, "f", 200));   // to boundary
        Assert.True(table.IsCovered(1UL, "f", 150));   // inside
    }

    [Fact]
    public void IsCovered_OutsideBoundaries_ReturnsFalse()
    {
        var table = new TombstoneTable();
        table.Add(MakeTombstone(1UL, "f", 100, 200));

        Assert.False(table.IsCovered(1UL, "f", 99));   // before from
        Assert.False(table.IsCovered(1UL, "f", 201));  // after to
    }

    [Fact]
    public void IsCovered_DifferentSeriesOrField_NotAffected()
    {
        var table = new TombstoneTable();
        table.Add(MakeTombstone(1UL, "f", 100, 200));

        // Different series
        Assert.False(table.IsCovered(2UL, "f", 150));
        // Different field
        Assert.False(table.IsCovered(1UL, "g", 150));
    }

    [Fact]
    public void MultipleNonOverlappingTombstones_AllRangesCovered()
    {
        var table = new TombstoneTable();
        table.Add(MakeTombstone(1UL, "f", 100, 200));
        table.Add(MakeTombstone(1UL, "f", 300, 400));

        Assert.True(table.IsCovered(1UL, "f", 150));
        Assert.True(table.IsCovered(1UL, "f", 350));
        Assert.False(table.IsCovered(1UL, "f", 250));  // gap
    }

    [Fact]
    public void Count_ReflectsAddedTombstones()
    {
        var table = new TombstoneTable();
        Assert.Equal(0, table.Count);

        table.Add(MakeTombstone(1UL, "f", 1, 10));
        Assert.Equal(1, table.Count);

        table.Add(MakeTombstone(2UL, "g", 1, 10));
        Assert.Equal(2, table.Count);
    }

    [Fact]
    public void Add_DuplicateTombstone_IgnoresDuplicate()
    {
        var table = new TombstoneTable();
        var tombstone = MakeTombstone(1UL, "f", 1, 10, 42);

        table.Add(tombstone);
        table.Add(tombstone);
        table.LoadFrom([tombstone]);

        Assert.Equal(1, table.Count);
        Assert.Single(table.All);
    }

    [Fact]
    public void All_ReturnsSnapshot()
    {
        var table = new TombstoneTable();
        var t1 = MakeTombstone(1UL, "f", 1, 10, 1);
        var t2 = MakeTombstone(2UL, "g", 1, 10, 2);

        table.Add(t1);
        table.Add(t2);

        var all = table.All;
        Assert.Equal(2, all.Count);
        Assert.Contains(t1, all);
        Assert.Contains(t2, all);
    }

    [Fact]
    public void LoadFrom_AddsAllTombstones()
    {
        var table = new TombstoneTable();
        var tombstones = new List<Tombstone>
        {
            MakeTombstone(1UL, "f", 1, 10, 1),
            MakeTombstone(2UL, "g", 5, 15, 2),
        };

        table.LoadFrom(tombstones);

        Assert.Equal(2, table.Count);
        Assert.True(table.IsCovered(1UL, "f", 5));
        Assert.True(table.IsCovered(2UL, "g", 10));
    }

    [Fact]
    public void LoadFrom_EmptyList_NoChange()
    {
        var table = new TombstoneTable();
        table.Add(MakeTombstone(1UL, "f", 1, 10));

        table.LoadFrom([]);

        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void RemoveAll_RemovesSpecifiedTombstones()
    {
        var table = new TombstoneTable();
        var t1 = MakeTombstone(1UL, "f", 1, 10, 1);
        var t2 = MakeTombstone(1UL, "f", 20, 30, 2);
        var t3 = MakeTombstone(2UL, "g", 1, 10, 3);

        table.Add(t1);
        table.Add(t2);
        table.Add(t3);

        table.RemoveAll([t1, t3]);

        Assert.Equal(1, table.Count);
        Assert.False(table.IsCovered(1UL, "f", 5));    // t1 removed
        Assert.True(table.IsCovered(1UL, "f", 25));    // t2 still there
        Assert.False(table.IsCovered(2UL, "g", 5));    // t3 removed
    }

    [Fact]
    public void GetForSeriesField_ReturnsCorrectList()
    {
        var table = new TombstoneTable();
        var t1 = MakeTombstone(1UL, "f", 1, 10, 1);
        var t2 = MakeTombstone(1UL, "f", 20, 30, 2);
        var t3 = MakeTombstone(2UL, "g", 1, 10, 3);

        table.Add(t1);
        table.Add(t2);
        table.Add(t3);

        var list = table.GetForSeriesField(1UL, "f");
        Assert.Equal(2, list.Count);
        Assert.Contains(t1, list);
        Assert.Contains(t2, list);

        var listG = table.GetForSeriesField(2UL, "g");
        Assert.Single(listG);

        var listEmpty = table.GetForSeriesField(99UL, "nothere");
        Assert.Empty(listEmpty);
    }

    [Fact]
    public void GetForSeriesField_RepeatedCallsWithoutMutation_ReturnSameInstance()
    {
        // C8：per-key 快照免拷贝——未发生写操作时重复调用应返回同一不可变数组实例，
        // 而非每次 ToArray 新分配。
        var table = new TombstoneTable();
        table.Add(MakeTombstone(1UL, "f", 1, 10, 1));
        table.Add(MakeTombstone(1UL, "f", 20, 30, 2));

        var first = table.GetForSeriesField(1UL, "f");
        var second = table.GetForSeriesField(1UL, "f");

        Assert.Same(first, second);
        Assert.Equal(2, first.Count);
    }

    [Fact]
    public void GetForSeriesField_AfterMutation_ReturnsFreshSnapshot()
    {
        // 写操作后必须发布新的 per-key 快照，旧引用保持不变（不可变），新引用反映变更。
        var table = new TombstoneTable();
        var t1 = MakeTombstone(1UL, "f", 1, 10, 1);
        table.Add(t1);

        var before = table.GetForSeriesField(1UL, "f");
        Assert.Single(before);

        table.Add(MakeTombstone(1UL, "f", 20, 30, 2));
        var after = table.GetForSeriesField(1UL, "f");

        Assert.NotSame(before, after);
        Assert.Single(before);      // 旧快照不可变，不受后续写影响
        Assert.Equal(2, after.Count);

        table.RemoveAll([t1]);
        var afterRemove = table.GetForSeriesField(1UL, "f");
        Assert.Single(afterRemove);
        Assert.DoesNotContain(t1, afterRemove);
        Assert.False(table.IsCovered(1UL, "f", 5)); // t1 覆盖窗 [1,10] 已移除
        Assert.True(table.IsCovered(1UL, "f", 25)); // 剩余 [20,30] 仍覆盖
    }

    [Fact]
    public async Task Concurrent_WritersAndReaders_NoThrow()
    {
        var table = new TombstoneTable();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var writers = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    table.Add(MakeTombstone((ulong)(i % 5), $"f{i % 3}", i * 10, i * 10 + 100, i));
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        var readers = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    _ = table.IsCovered((ulong)(i % 5), $"f{i % 3}", i * 10 + 50);
                    _ = table.All.Count;
                    _ = table.GetForSeriesField((ulong)(i % 5), $"f{i % 3}").Count;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }
        })).ToArray();

        await Task.WhenAll([.. writers, .. readers]);
        Assert.Empty(exceptions);
    }
}
