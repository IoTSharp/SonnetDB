namespace SonnetDB.Exceptions;

/// <summary>时序写入因已记录的 Flush 故障被拒绝，数据库需要重开并恢复 WAL。</summary>
public sealed class TimeSeriesWriteRecoveryRequiredException : InvalidOperationException
{
    /// <summary>写入已进入恢复保护时在 SQL 入口返回的稳定错误码。</summary>
    public const string ErrorCode = "sql_write_recovery_required";

    /// <summary>写入保护发生前可能已有部分写入时的稳定 SQL 错误码。</summary>
    public const string PartialErrorCode = "sql_write_recovery_required_partial";

    /// <summary>本次操作是否可能已写入前缀。</summary>
    public bool MayHaveWritten { get; }

    /// <summary>记录触发保护的原始 Flush 异常。</summary>
    /// <param name="failure">触发写入保护的异常。</param>
    /// <param name="mayHaveWritten">本次操作是否可能已写入前缀。</param>
    public TimeSeriesWriteRecoveryRequiredException(Exception failure, bool mayHaveWritten = false)
        : base("此前的 Flush 发布失败，后续 Flush 和 checkpoint 已被停止；请关闭并重新打开数据库以从 WAL 恢复。", failure)
    {
        MayHaveWritten = mayHaveWritten;
    }
}
