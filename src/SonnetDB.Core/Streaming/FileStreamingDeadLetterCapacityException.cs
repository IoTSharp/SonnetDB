namespace SonnetDB.Exceptions;

/// <summary>死信容量不足时的原子拒绝；原未确认批次和位点不变。</summary>
public sealed class FileStreamingDeadLetterCapacityException : Exception
{
    /// <summary>创建持久死信容量不足异常。</summary>
    public FileStreamingDeadLetterCapacityException()
        : base("持久死信目录容量不足；原批次、检查点和事件保持不变。")
    {
    }
}
