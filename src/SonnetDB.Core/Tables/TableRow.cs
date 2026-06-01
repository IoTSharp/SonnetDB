namespace SonnetDB.Tables;

/// <summary>
/// 关系表行快照。
/// </summary>
/// <param name="Values">按 schema 列顺序排列的值。</param>
public sealed record TableRow(IReadOnlyList<object?> Values);
