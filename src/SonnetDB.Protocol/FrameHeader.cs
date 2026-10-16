using System.Buffers.Binary;

namespace SonnetDB.Protocol;

/// <summary>
/// 通用二进制帧的固定 12 字节帧头（little-endian）。
/// 布局：u32 PayloadLength | u8 Version | u8 Service | u8 Op | u8 Flags | u32 StreamId。
/// PayloadLength 不含帧头本身。
/// </summary>
public readonly struct FrameHeader
{
    /// <summary>帧头固定字节数。</summary>
    public const int Size = 12;

    /// <summary>当前协议版本。</summary>
    public const byte CurrentVersion = 1;

    /// <summary>
    /// 单帧 payload 上限：132 MiB。覆盖 MQ 128 MiB payload 上限 + 名字/headers 余量，
    /// 解码前先校验，防止畸形长度触发大分配。
    /// </summary>
    public const uint MaxFramePayloadBytes = 132 * 1024 * 1024;

    /// <summary>payload 字节数（不含帧头）。</summary>
    public uint PayloadLength { get; }

    /// <summary>协议版本。</summary>
    public byte Version { get; }

    /// <summary>目标 service（见 <see cref="FrameService"/>）。</summary>
    public byte Service { get; }

    /// <summary>service 内 opcode。</summary>
    public byte Op { get; }

    /// <summary>标志位（见 <see cref="FrameFlags"/>）。</summary>
    public byte Flags { get; }

    /// <summary>客户端选定的关联 id，响应帧原样回显。</summary>
    public uint StreamId { get; }

    /// <summary>
    /// 初始化帧头。
    /// </summary>
    public FrameHeader(uint payloadLength, byte version, byte service, byte op, byte flags, uint streamId)
    {
        PayloadLength = payloadLength;
        Version = version;
        Service = service;
        Op = op;
        Flags = flags;
        StreamId = streamId;
    }

    /// <summary>是否响应帧。</summary>
    public bool IsResponse => (Flags & (byte)FrameFlags.Response) != 0;

    /// <summary>是否错误帧。</summary>
    public bool IsError => (Flags & (byte)FrameFlags.Error) != 0;

    /// <summary>
    /// 把帧头写入目标缓冲区（至少 <see cref="Size"/> 字节）。
    /// </summary>
    /// <param name="destination">目标缓冲区。</param>
    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, PayloadLength);
        destination[4] = Version;
        destination[5] = Service;
        destination[6] = Op;
        destination[7] = Flags;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], StreamId);
    }

    /// <summary>
    /// 尝试从缓冲区解析帧头；数据不足 <see cref="Size"/> 字节时返回 false。
    /// 不做语义校验（版本/service/长度上限由调用方判定）。
    /// </summary>
    /// <param name="source">源缓冲区。</param>
    /// <param name="header">解析结果。</param>
    /// <returns>数据足够时为 true。</returns>
    public static bool TryRead(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        if (source.Length < Size)
        {
            header = default;
            return false;
        }

        header = new FrameHeader(
            payloadLength: BinaryPrimitives.ReadUInt32LittleEndian(source),
            version: source[4],
            service: source[5],
            op: source[6],
            flags: source[7],
            streamId: BinaryPrimitives.ReadUInt32LittleEndian(source[8..]));
        return true;
    }
}
