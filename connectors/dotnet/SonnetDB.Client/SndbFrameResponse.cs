using SonnetDB.Protocol;

namespace SonnetDB.Client;

/// <summary>
/// 远程多帧响应中的一个独立拥有帧。
/// </summary>
/// <param name="Header">响应帧头。</param>
/// <param name="Payload">已经从 HTTP 响应流复制出的帧体。</param>
public readonly record struct SndbFrameResponse(FrameHeader Header, ReadOnlyMemory<byte> Payload);
