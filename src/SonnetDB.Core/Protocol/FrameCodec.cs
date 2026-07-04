using System.Buffers;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>
/// 帧信封编解码：帧头 + payload 的写入与增量解析。
/// payload 内容的编解码由各 service 的 codec（如 <see cref="MqFrameCodec"/>）负责。
/// </summary>
public static class FrameCodec
{
    /// <summary>
    /// 写入一个完整帧（帧头 + payload）。
    /// </summary>
    /// <param name="writer">目标缓冲写入器。</param>
    /// <param name="header">帧头（其 PayloadLength 必须等于 <paramref name="payload"/> 长度）。</param>
    /// <param name="payload">帧体字节。</param>
    public static void WriteFrame(IBufferWriter<byte> writer, in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (header.PayloadLength != (uint)payload.Length)
            throw new ArgumentException("FrameHeader.PayloadLength 与 payload 实际长度不一致。", nameof(header));

        Span<byte> headerSpan = writer.GetSpan(FrameHeader.Size);
        header.Write(headerSpan);
        writer.Advance(FrameHeader.Size);
        if (!payload.IsEmpty)
            writer.Write(payload);
    }

    /// <summary>
    /// 写入一个错误帧（Flags = Response|Error，payload = varstr code + varstr message）。
    /// </summary>
    /// <param name="writer">目标缓冲写入器。</param>
    /// <param name="service">回显的 service。</param>
    /// <param name="op">回显的 opcode。</param>
    /// <param name="streamId">回显的 stream id。</param>
    /// <param name="code">机器可读错误码（如 bad_request / forbidden / bad_frame）。</param>
    /// <param name="message">人类可读错误消息。</param>
    public static void WriteErrorFrame(IBufferWriter<byte> writer, byte service, byte op, uint streamId, string code, string message)
    {
        int payloadLength = SpanWriter.MeasureVarString(code) + SpanWriter.MeasureVarString(message);
        var header = new FrameHeader(
            payloadLength: (uint)payloadLength,
            version: FrameHeader.CurrentVersion,
            service: service,
            op: op,
            flags: (byte)(FrameFlags.Response | FrameFlags.Error),
            streamId: streamId);

        Span<byte> span = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(span);
        var payloadWriter = new SpanWriter(span.Slice(FrameHeader.Size, payloadLength));
        payloadWriter.WriteVarString(code);
        payloadWriter.WriteVarString(message);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>
    /// 解码错误帧 payload（varstr code + varstr message）。
    /// </summary>
    /// <param name="payload">错误帧 payload。</param>
    /// <returns>(code, message) 二元组。</returns>
    public static (string Code, string Message) ReadErrorPayload(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        string code = reader.ReadVarString();
        string message = reader.ReadVarString();
        return (code, message);
    }

    /// <summary>
    /// 尝试从字节序列头部解析一个完整帧。成功时把 <paramref name="buffer"/> 前移到帧尾、
    /// 输出帧头与 payload 切片（指向原缓冲，零拷贝，AdvanceTo 之前有效）；
    /// 数据不足一个完整帧时返回 false 且不消费任何字节。
    /// </summary>
    /// <param name="buffer">输入缓冲序列（就地前移）。</param>
    /// <param name="header">解析出的帧头。</param>
    /// <param name="payload">帧体切片。</param>
    /// <returns>解析出完整帧时为 true。</returns>
    /// <exception cref="FrameFormatException">声明的 payload 长度超过 <see cref="FrameHeader.MaxFramePayloadBytes"/> 时抛出（在分配/切片之前）。</exception>
    public static bool TryReadFrame(ref ReadOnlySequence<byte> buffer, out FrameHeader header, out ReadOnlySequence<byte> payload)
    {
        header = default;
        payload = default;

        if (buffer.Length < FrameHeader.Size)
            return false;

        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        buffer.Slice(0, FrameHeader.Size).CopyTo(headerBytes);
        if (!FrameHeader.TryRead(headerBytes, out header))
            return false;

        if (header.PayloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new FrameFormatException($"帧 payload 长度 {header.PayloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");

        long totalLength = FrameHeader.Size + (long)header.PayloadLength;
        if (buffer.Length < totalLength)
            return false;

        payload = buffer.Slice(FrameHeader.Size, header.PayloadLength);
        buffer = buffer.Slice(totalLength);
        return true;
    }
}
