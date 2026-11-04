namespace SonnetDB.Exceptions;

/// <summary>调用方指定的 payload 预算不足；在读取、复制或编码超额内容前拒绝。</summary>
public sealed class PayloadLimitExceededException : InvalidOperationException
{
    /// <summary>创建指定上限的预算异常。</summary>
    /// <param name="maximumBytes">允许的最大字节数。</param>
    public PayloadLimitExceededException(long maximumBytes)
        : base($"Payload 超过调用方指定的 {maximumBytes} 字节预算。")
    {
    }
}
