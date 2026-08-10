using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Graphs.Storage;

/// <summary>Graph V1 record 类别。</summary>
internal enum GraphRecordKind : byte
{
    Vertex = 1,
    Edge = 2,
    Metadata = 3,
    UniquePropertyOwner = 4,
}

/// <summary>已验证且拥有 payload 的 Graph V1 record envelope。</summary>
internal sealed record GraphRecordEnvelope(
    GraphRecordKind Kind,
    long ElementVersion,
    byte[] Payload);

/// <summary>
/// Graph V1 record 公共 envelope 编解码器。具体 vertex/edge payload 在后续存储层中扩展，
/// envelope 的 magic、版本、element version、长度和 CRC 保持稳定。
/// </summary>
internal static class GraphRecordEnvelopeCodec
{
    private const int HeaderSize = 28;
    private const int FooterSize = sizeof(uint);
    internal const int MaxEncodedRecordBytes = 16 * 1024 * 1024;
    internal const int MaxPayloadBytes = MaxEncodedRecordBytes - HeaderSize - FooterSize;

    public static byte[] Encode(
        GraphRecordKind kind,
        long elementVersion,
        ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentOutOfRangeException.ThrowIfNegative(elementVersion);
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Graph record payload 不能超过 {MaxPayloadBytes} 字节。");

        byte[] record = new byte[checked(HeaderSize + payload.Length + FooterSize)];
        Span<byte> header = record.AsSpan(0, HeaderSize);
        GraphStorageFormat.RecordMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], GraphStorageFormat.RecordFormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], HeaderSize);
        header[14] = (byte)kind;
        header[15] = 0;
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], elementVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], payload.Length);
        payload.CopyTo(record.AsSpan(HeaderSize));

        uint crc = Crc32.HashToUInt32(record.AsSpan(0, record.Length - FooterSize));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(record.Length - FooterSize), crc);
        return record;
    }

    public static GraphRecordEnvelope Decode(ReadOnlySpan<byte> record)
    {
        if (record.Length < HeaderSize + FooterSize)
            throw new InvalidDataException("Graph record envelope 被截断。");
        if (!record[..8].SequenceEqual(GraphStorageFormat.RecordMagic))
            throw new InvalidDataException("Graph record envelope magic 无效。");

        int version = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
        if (version != GraphStorageFormat.RecordFormatVersion)
        {
            throw new InvalidDataException(
                $"Graph record format version {version} 不受支持；需要显式迁移或拒绝打开。");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(record[12..]) != HeaderSize || record[15] != 0)
            throw new InvalidDataException("Graph record envelope header 无效。");

        var kind = (GraphRecordKind)record[14];
        if (!Enum.IsDefined(kind))
            throw new InvalidDataException($"Graph record kind {(byte)kind} 未知。");
        long elementVersion = BinaryPrimitives.ReadInt64LittleEndian(record[16..]);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(record[24..]);
        if (elementVersion < 0 || payloadLength < 0 || payloadLength > MaxPayloadBytes)
            throw new InvalidDataException("Graph record envelope counter 无效。");
        if (record.Length != HeaderSize + payloadLength + FooterSize)
            throw new InvalidDataException("Graph record envelope 长度与 payload 声明不一致。");

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(record[^FooterSize..]);
        uint actual = Crc32.HashToUInt32(record[..^FooterSize]);
        if (expected != actual)
            throw new InvalidDataException("Graph record envelope CRC32 不匹配。");

        return new GraphRecordEnvelope(
            kind,
            elementVersion,
            record.Slice(HeaderSize, payloadLength).ToArray());
    }
}
