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

    /// <summary>检查约束冲突错误码。</summary>
    public const string CheckViolation = "table_check_violation";

    /// <summary>乐观并发冲突错误码。</summary>
    public const string ConcurrencyConflict = "table_concurrency_conflict";

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
    public TableConstraintException(
        string errorCode,
        string tableName,
        string? constraintName,
        string message,
        string operation,
        string? hint)
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
    }

    private static string DefaultHint(string errorCode)
        => errorCode switch
        {
            UniqueViolation => "调整冲突键值，或先查询并更新现有行。",
            ForeignKeyViolation => "确认引用行存在，或先处理依赖行。",
            CheckViolation => "检查写入值是否满足表上的 CHECK 条件。",
            ConcurrencyConflict => "重新读取当前行版本后重试写入。",
            _ => "检查表约束和当前写入值后重试。",
        };
}
