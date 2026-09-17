namespace SonnetDB.Exceptions;

/// <summary>跨表事务无法确定提交或回滚状态，需要重启恢复后核对业务幂等键。</summary>
public sealed class TableTransactionRecoveryException : IOException
{
    /// <summary>创建需要事务恢复的存储异常。</summary>
    /// <param name="message">不确定的提交或回滚阶段说明。</param>
    /// <param name="innerException">底层 I/O 失败。</param>
    public TableTransactionRecoveryException(string message, Exception innerException)
        : base(message, innerException) { }
}
