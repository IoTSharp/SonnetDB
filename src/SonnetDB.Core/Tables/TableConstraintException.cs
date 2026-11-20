namespace SonnetDB.Tables;

/// <summary>
/// 关系表约束校验失败异常，携带稳定错误码，便于 ADO.NET 和远程端点映射。
/// </summary>
public sealed class TableConstraintException : InvalidOperationException
{
    /// <summary>唯一约束冲突错误码。</summary>
    public const string UniqueViolation = "table_unique_violation";

    /// <summary>外键约束冲突错误码。</summary>
    public const string ForeignKeyViolation = "table_foreign_key_violation";

    /// <summary>字符串值超过已声明长度上限的错误码。</summary>
    public const string StringLengthViolation = "table_string_length_violation";

    /// <summary>检查约束冲突错误码。</summary>
    public const string CheckViolation = "table_check_violation";

    /// <summary>乐观并发冲突错误码。</summary>
    public const string ConcurrencyConflict = "table_concurrency_conflict";

    /// <summary>可串行化事务的首次读版本已发生变化。</summary>
    public const string SerializationConflict = "table_serialization_conflict";

    /// <summary>当前 schema 演进路径无法安全完成的错误码。</summary>
    public const string SchemaEvolutionUnsupported = "table_schema_evolution_unsupported";

    /// <summary>约束错误码。</summary>
    public string ErrorCode { get; }

    /// <summary>约束名；没有命名约束时可为 <c>null</c>。</summary>
    public string? ConstraintName { get; }

    /// <summary>目标表名。</summary>
    public string TableName { get; }

    /// <summary>失败的 SQL 操作阶段或操作名。</summary>
    public string Operation { get; }

    /// <summary>不包含行内容的可行动提示。</summary>
    public string? Hint { get; }

    /// <summary>schema 迁移预算拒绝的预算类别；普通约束错误为 <c>null</c>。</summary>
    public string? BudgetKind { get; }

    /// <summary>预算拒绝时已经累计的行数；普通约束错误为 <c>null</c>。</summary>
    public long? BudgetRows { get; }

    /// <summary>预算拒绝时已经累计的工作集字节数；普通约束错误为 <c>null</c>。</summary>
    public long? BudgetWorkingBytes { get; }

    /// <summary>预算拒绝时本次尝试继续预留的字节数；普通约束错误为 <c>null</c>。</summary>
    public long? BudgetNextReservationBytes { get; }

    /// <summary>恢复日志预算拒绝时已经累计的日志字节或条目数；其他预算和普通约束错误为 <c>null</c>。</summary>
    public long? BudgetJournalUsed { get; }

    /// <summary>预算拒绝时对应预算的固定上限；普通约束错误为 <c>null</c>。</summary>
    public long? BudgetLimit { get; }

    /// <summary>预算拒绝时迁移涉及的表数量；普通约束错误为 <c>null</c>。</summary>
    public int? BudgetParticipatingTables { get; }

    /// <summary>创建关系表约束异常。</summary>
    public TableConstraintException(string errorCode, string tableName, string? constraintName, string message)
        : this(errorCode, tableName, constraintName, message, operation: "write", hint: null)
    {
    }

    /// <summary>
    /// 创建包含 SQL 诊断上下文的关系表约束异常。
    /// </summary>
    /// <param name="errorCode">稳定约束错误码。</param>
    /// <param name="tableName">目标表名。</param>
    /// <param name="constraintName">约束名；没有命名约束时为 <c>null</c>。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="operation">失败的 SQL 操作阶段或操作名。</param>
    /// <param name="hint">不包含行内容的可行动提示。</param>
    /// <param name="budgetKind">schema 迁移预算类别。</param>
    /// <param name="budgetRows">预算拒绝时已累计的行数。</param>
    /// <param name="budgetWorkingBytes">预算拒绝时已累计的工作集字节数。</param>
    /// <param name="budgetNextReservationBytes">本次尝试继续预留的字节数。</param>
    /// <param name="budgetJournalUsed">恢复日志预算拒绝时已累计的日志字节或条目数。</param>
    /// <param name="budgetLimit">对应预算的固定上限。</param>
    /// <param name="budgetParticipatingTables">迁移涉及的表数量。</param>
    public TableConstraintException(
        string errorCode,
        string tableName,
        string? constraintName,
        string message,
        string operation,
        string? hint,
        string? budgetKind = null,
        long? budgetRows = null,
        long? budgetWorkingBytes = null,
        long? budgetNextReservationBytes = null,
        long? budgetJournalUsed = null,
        long? budgetLimit = null,
        int? budgetParticipatingTables = null)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ErrorCode = errorCode;
        TableName = tableName;
        ConstraintName = constraintName;
        Operation = operation;
        Hint = string.IsNullOrWhiteSpace(hint)
            ? DefaultHint(errorCode)
            : hint;
        BudgetKind = budgetKind;
        BudgetRows = budgetRows;
        BudgetWorkingBytes = budgetWorkingBytes;
        BudgetNextReservationBytes = budgetNextReservationBytes;
        BudgetJournalUsed = budgetJournalUsed;
        BudgetLimit = budgetLimit;
        BudgetParticipatingTables = budgetParticipatingTables;
    }

    private static string DefaultHint(string errorCode)
        => errorCode switch
        {
            UniqueViolation => "调整冲突键值，或先查询并更新现有行。",
            ForeignKeyViolation => "确认引用行存在，或先处理依赖行。",
            CheckViolation => "检查写入值是否满足表上的 CHECK 条件。",
            ConcurrencyConflict => "重新读取当前行版本后重试写入。",
            SerializationConflict => "整个事务已回滚；从 BEGIN 开始重新读取并重试整个事务。",
            SchemaEvolutionUnsupported => "在事务外创建目标表，回填并校验行、索引和外键后切换应用访问。",
            _ => "检查表约束和当前写入值后重试。",
        };
}
