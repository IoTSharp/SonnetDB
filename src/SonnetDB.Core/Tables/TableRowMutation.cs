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
    long? ExpectedRowVersion = null)
{
    // SQL 事务保留最初读取的规范行编码，未声明 ROWVERSION 的表同样不能静默丢失更新。
    internal byte[]? ExpectedRowState { get; init; }

    // 无显式主键时使用物理行身份；NULL 主键值仍表示 INSERT，空主键值列表表示既有行变更。
    internal byte[]? EncodedRowIdentity { get; init; }

    // ON DELETE SET NULL 可能需要合并同一子行上的多个外键动作。
    // 该标记仅用于 TableManager 的级联展开，不属于持久化或公共 SQL 合同。
    internal bool IsGeneratedCascadeSetNull { get; init; }
}
