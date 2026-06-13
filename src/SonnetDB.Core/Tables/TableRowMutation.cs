namespace SonnetDB.Tables;

/// <summary>
/// 轻事务批处理中的单行关系表变更。
/// </summary>
/// <param name="PrimaryKeyValues">目标主键值；插入新行时可为 null。</param>
/// <param name="NewValues">新行值；为 null 表示删除目标行。</param>
/// <param name="ExpectedRowVersion">乐观并发期望版本；表未声明 ROWVERSION 时为 null。</param>
public sealed record TableRowMutation(
    IReadOnlyList<object?>? PrimaryKeyValues,
    IReadOnlyList<object?>? NewValues,
    long? ExpectedRowVersion = null);
