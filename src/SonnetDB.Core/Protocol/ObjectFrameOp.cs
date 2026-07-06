namespace SonnetDB.Protocol;

/// <summary>
/// object service（<see cref="FrameService.Object"/>）的 opcode（M28 P5b #240）。
/// </summary>
public enum ObjectFrameOp : byte
{
    /// <summary>读取对象内容（响应为 meta → data × N → end 流式分块帧序列）。</summary>
    Get = 1,

    /// <summary>写入对象（内容原始字节直传，零 Base64）。</summary>
    Put = 2,
}
