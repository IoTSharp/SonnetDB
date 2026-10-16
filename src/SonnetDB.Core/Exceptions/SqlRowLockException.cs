namespace SonnetDB.Exceptions;

/// <summary>关系行锁申请因冲突、死锁或等待超时而失败。</summary>
public sealed class SqlRowLockException : Exception
{
    /// <summary>NOWAIT 申请的目标正由其它事务占有或排队等待。</summary>
    public const string NotAvailable = "sql_lock_not_available";

    /// <summary>等待会构成行锁依赖环。</summary>
    public const string Deadlock = "sql_lock_deadlock";

    /// <summary>普通行锁等待超过本次请求的时间预算。</summary>
    public const string WaitTimeout = "sql_lock_timeout";

    /// <summary>行锁、等待者数量或键内存预算超过管理器的保护上限。</summary>
    public const string LimitExceeded = "sql_lock_limit_exceeded";

    /// <summary>创建不包含行键或 SQL 参数的稳定行锁异常。</summary>
    /// <param name="code">稳定错误码。</param>
    /// <param name="message">不包含行内容的说明。</param>
    public SqlRowLockException(string code, string message) : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
    }

    /// <summary>稳定、与传输无关的错误码。</summary>
    public string Code { get; }
}
