namespace SonnetDB.Tables;

/// <summary>
/// 二级索引有符号整数范围的单侧边界。
/// </summary>
/// <param name="Value">逻辑有符号值；DATETIME 使用 Unix 毫秒。</param>
/// <param name="Inclusive">是否包含边界值。</param>
internal readonly record struct TableIndexRangeBound(long Value, bool Inclusive);

/// <summary>
/// 二级索引下一列的逻辑范围约束。
/// </summary>
/// <param name="Column">承载范围条件的索引列。</param>
/// <param name="Lower">可选下界。</param>
/// <param name="Upper">可选上界。</param>
internal sealed record TableIndexRange(
    TableColumn Column,
    TableIndexRangeBound? Lower,
    TableIndexRangeBound? Upper);

/// <summary>
/// 二级索引 key 的半开物理扫描区间。
/// </summary>
/// <param name="StartInclusive">起始 key，包含。</param>
/// <param name="EndExclusive">结束 key，不包含。</param>
internal readonly record struct TableIndexKeyRange(byte[] StartInclusive, byte[] EndExclusive);
