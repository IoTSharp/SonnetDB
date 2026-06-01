namespace SonnetDB.Tables;

/// <summary>
/// 轻事务批处理中的单行关系表变更。
/// </summary>
/// <param name="PrimaryKeyValues">目标主键值；插入新行时可为 null。</param>
/// <param name="NewValues">新行值；为 null 表示删除目标行。</param>
public sealed record TableRowMutation(
    IReadOnlyList<object?>? PrimaryKeyValues,
    IReadOnlyList<object?>? NewValues);
