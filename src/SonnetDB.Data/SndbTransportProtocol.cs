namespace SonnetDB.Data;

/// <summary>
/// 远程 SonnetDB 连接的线传输选择（M28 P5b #241）。仅影响远程模式，嵌入式模式无关。
/// </summary>
public enum SndbTransportProtocol
{
    /// <summary>
    /// 自动：运行时惰性探测服务端是否支持二进制帧协议；支持则对数据面操作优先走帧，
    /// 不支持或传输级失败则回落 REST/JSON。
    /// </summary>
    Auto = 0,

    /// <summary>
    /// 强制走二进制帧协议（HTTP/2 帧端点 <c>/v1/frame</c>）。帧不支持的操作仍回落 REST；
    /// 但帧端点本身不可用时直接报错，不静默回落。
    /// </summary>
    FrameHttp2 = 1,

    /// <summary>强制走 REST/JSON（保持 #241 之前的行为）。</summary>
    Rest = 2,
}
