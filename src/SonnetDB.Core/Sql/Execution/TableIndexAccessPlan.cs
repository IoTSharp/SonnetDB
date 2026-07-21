using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 关系表二级索引访问计划：连续等值左前缀后可附带下一列有符号范围。
/// </summary>
/// <param name="Index">选中的二级索引。</param>
/// <param name="EqualityPrefixValues">从索引首列开始连续绑定的等值。</param>
/// <param name="Range">下一列的可选 Int64/DATETIME 范围。</param>
internal sealed record TableIndexAccessPlan(
    TableIndex Index,
    IReadOnlyList<object?> EqualityPrefixValues,
    TableIndexRange? Range)
{
    /// <summary>计划绑定的连续索引列数量。</summary>
    public int MatchedColumnCount => EqualityPrefixValues.Count + (Range is null ? 0 : 1);

    /// <summary>是否绑定了索引全部列的等值键。</summary>
    public bool IsFullEquality => Range is null && EqualityPrefixValues.Count == Index.Columns.Count;
}
