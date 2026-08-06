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

/// <summary>
/// 单表 <c>EXISTS</c> 的物理候选访问计划，供运行时和 EXPLAIN 共用同一判定。
/// </summary>
/// <param name="AccessPath">稳定的访问路径名称。</param>
/// <param name="IndexName">命中的索引名；主键使用 <c>primary</c>。</param>
/// <param name="UsesPrimaryKey">是否执行主键点查。</param>
/// <param name="IndexPlan">可选二级索引访问计划。</param>
/// <param name="PredicateCovered">访问约束是否完整覆盖 WHERE；为真时可把候选上限安全压到一行。</param>
/// <param name="HasResidualPredicate">是否仍需对候选执行完整 WHERE 残余复检。</param>
/// <param name="FallbackReason">未使用常规索引计划时的可解释原因。</param>
internal sealed record TableExistsAccessPlan(
    string AccessPath,
    string? IndexName,
    bool UsesPrimaryKey,
    TableIndexAccessPlan? IndexPlan,
    bool PredicateCovered,
    bool HasResidualPredicate,
    string? FallbackReason = null);

/// <summary>
/// 单表 <c>EXISTS</c> 已加载的候选行及其实际访问计划。
/// </summary>
/// <param name="Plan">实际执行的访问计划。</param>
/// <param name="Rows">等待残余谓词复检的候选行。</param>
internal sealed record TableExistsCandidateRows(
    TableExistsAccessPlan Plan,
    IReadOnlyList<TableRow> Rows);
