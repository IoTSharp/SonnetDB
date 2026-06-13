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

    /// <summary>乐观并发冲突错误码。</summary>
    public const string ConcurrencyConflict = "table_concurrency_conflict";

    /// <summary>约束错误码。</summary>
    public string ErrorCode { get; }

    /// <summary>约束名；没有命名约束时可为 <c>null</c>。</summary>
    public string? ConstraintName { get; }

    /// <summary>目标表名。</summary>
    public string TableName { get; }

    /// <summary>创建关系表约束异常。</summary>
    public TableConstraintException(string errorCode, string tableName, string? constraintName, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ErrorCode = errorCode;
        TableName = tableName;
        ConstraintName = constraintName;
    }
}
