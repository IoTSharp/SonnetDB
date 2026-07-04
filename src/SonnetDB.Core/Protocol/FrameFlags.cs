namespace SonnetDB.Protocol;

/// <summary>
/// 帧头 Flags 位。v1 中保留位（bit2~bit7）必须为 0；请求帧的 <see cref="Response"/> 位必须为 0。
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
}
