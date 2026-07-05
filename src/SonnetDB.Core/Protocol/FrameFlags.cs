namespace SonnetDB.Protocol;

/// <summary>
/// 帧头 Flags 位。v1 中保留位（bit3~bit7）必须为 0；请求帧的 <see cref="Response"/>/<see cref="Push"/> 位必须为 0。
/// </summary>
[Flags]
public enum FrameFlags : byte
{
    /// <summary>无标志（请求帧）。</summary>
    None = 0,

    /// <summary>响应帧。</summary>
    Response = 1,

    /// <summary>错误帧（隐含 <see cref="Response"/>；payload = varstr code + varstr message）。</summary>
    Error = 2,

    /// <summary>
    /// 服务端推送帧（#236，仅服务端→客户端）。用于订阅推送的消息投递帧，非对某请求的直接响应，
    /// 故与 <see cref="Response"/> 区分；streamId 回显订阅时的 streamId。请求帧不得设置该位。
    /// </summary>
    Push = 4,
}
