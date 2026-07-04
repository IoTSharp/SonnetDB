namespace SonnetDB.Protocol;

/// <summary>
/// 线上帧数据结构畸形（截断、长度越界、超出防御上限等）时抛出。
/// 与逻辑错误区分：处理端把它映射为 <c>bad_frame</c> / <c>bad_request</c> 错误帧而非 500。
/// </summary>
public sealed class FrameFormatException : Exception
{
    /// <summary>
    /// 初始化 <see cref="FrameFormatException"/>。
    /// </summary>
    /// <param name="message">描述畸形原因的消息。</param>
    public FrameFormatException(string message)
        : base(message)
    {
    }
}
