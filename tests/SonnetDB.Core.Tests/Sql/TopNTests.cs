using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// <see cref="TopN"/> 有界 Top-N 选择工具（#214）的单元测试：验证与全量稳定排序 + 分页等价。
/// </summary>
public sealed class TopNTests
{
    private static readonly IComparer<int> Ascending = Comparer<int>.Default;

    private static int[] Reference(IReadOnlyList<int> rows, int offset, int? fetch)
    {
        // 参考实现：全量稳定排序（OrderBy 稳定）+ Skip/Take。
        var sorted = rows.Select((v, i) => (v, i))
            .OrderBy(t => t.v)
            .ThenBy(t => t.i)
            .Select(t => t.v)
            .ToArray();
        if (offset < 0) offset = 0;
        if (offset >= sorted.Length) return [];
        int take = fetch ?? (sorted.Length - offset);
        if (take <= 0) return [];
        return sorted.Skip(offset).Take(Math.Min(take, sorted.Length - offset)).ToArray();
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    [InlineData(5, 10)]
    [InlineData(0, null)]
    [InlineData(3, null)]
    [InlineData(100, 10)] // offset 超出范围
    [InlineData(0, 0)]    // fetch=0
    public void OrderByThenPaginate_MatchesFullSortAndSlice(int offset, int? fetch)
    {
        var rows = new[] { 7, 1, 5, 3, 9, 2, 8, 4, 6, 0, 5, 3 };
        var actual = TopN.OrderByThenPaginate(rows, Ascending, offset, fetch);
        var expected = Reference(rows, offset, fetch);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OrderByThenPaginate_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TopN.OrderByThenPaginate(Array.Empty<int>(), Ascending, 0, 10));
    }

    [Fact]
    public void OrderByThenPaginate_FetchExceedsCount_ReturnsAllSorted()
    {
        var rows = new[] { 3, 1, 2 };
        var actual = TopN.OrderByThenPaginate(rows, Ascending, 0, 100);
        Assert.Equal(new[] { 1, 2, 3 }, actual);
    }

    [Fact]
    public void OrderByThenPaginate_IsStable_ForEqualKeys()
    {
        // 用 (key, tag) 对，仅按 key 比较；等 key 必须保留输入顺序（稳定）。
        var rows = new[] { (k: 1, tag: "a"), (k: 1, tag: "b"), (k: 0, tag: "c"), (k: 1, tag: "d") };
        var cmp = Comparer<(int k, string tag)>.Create((x, y) => x.k.CompareTo(y.k));

        var top = TopN.OrderByThenPaginate(rows, cmp, offset: 0, fetch: 3);

        // 排序序：k=0(c), 然后 k=1 三个按输入顺序 a,b,d；取前 3 = c,a,b。
        Assert.Equal(new[] { "c", "a", "b" }, top.Select(t => t.tag).ToArray());
    }

    [Fact]
    public void OrderByThenPaginate_TopNPath_LargeInputSmallFetch()
    {
        // 大输入 + 小 fetch：走有界堆路径，仍须与全量排序等价。
        var rnd = new Random(12345);
        var rows = Enumerable.Range(0, 10_000).Select(_ => rnd.Next(0, 1000)).ToArray();

        var actual = TopN.OrderByThenPaginate(rows, Ascending, offset: 3, fetch: 7);
        var expected = Reference(rows, 3, 7);

        Assert.Equal(expected, actual);
    }
}
