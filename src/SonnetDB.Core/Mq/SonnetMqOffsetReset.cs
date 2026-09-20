namespace SonnetMQ;

/// <summary>消费者组 offset 重置目标。</summary>
public enum SonnetMqOffsetResetMode : byte
{
    /// <summary>当前 retention 保留的最早 offset。</summary>
    Earliest = 0,

    /// <summary>Topic 当前末尾（下一条新消息）。</summary>
    Latest = 1,

    /// <summary>第一个时间戳大于等于 value 的消息。</summary>
    Time = 2,

    /// <summary>显式 offset；value 会被裁剪到当前 retention 边界。</summary>
    Explicit = 3,
}
