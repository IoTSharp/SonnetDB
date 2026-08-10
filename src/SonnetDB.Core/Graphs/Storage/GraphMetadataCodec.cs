using System.Buffers.Binary;

namespace SonnetDB.Graphs.Storage;

/// <summary>Graph V1 high-water metadata 编解码器。</summary>
internal static class GraphHighWaterCodec
{
    private const int PayloadVersion = 1;
    private const int PayloadSize = sizeof(int) + sizeof(byte) + 3 + sizeof(long);

    internal static byte[] Encode(GraphHighWaterKind kind, long value)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(payload, PayloadVersion);
        payload[4] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(payload[8..], value);
        return GraphRecordEnvelopeCodec.Encode(GraphRecordKind.Metadata, 0, payload);
    }

    internal static long Decode(ReadOnlySpan<byte> encoded, GraphHighWaterKind expectedKind)
    {
        if (!Enum.IsDefined(expectedKind))
            throw new ArgumentOutOfRangeException(nameof(expectedKind));
        GraphRecordEnvelope envelope = GraphRecordEnvelopeCodec.Decode(encoded);
        ReadOnlySpan<byte> payload = envelope.Payload;
        if (envelope.Kind != GraphRecordKind.Metadata
            || envelope.ElementVersion != 0
            || payload.Length != PayloadSize
            || BinaryPrimitives.ReadInt32LittleEndian(payload) != PayloadVersion
            || payload[4] != (byte)expectedKind
            || payload[5] != 0
            || payload[6] != 0
            || payload[7] != 0)
        {
            throw new InvalidDataException("Graph high-water metadata header 无效。");
        }
        long value = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        if (value < 0)
            throw new InvalidDataException("Graph high-water metadata 不能为负数。");
        return value;
    }
}

/// <summary>Graph 幂等 transaction request marker 编解码器。</summary>
internal static class GraphTransactionRequestCodec
{
    private const int PayloadVersion = 1;
    private const int DigestBytes = 32;

    internal static byte[] Encode(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != DigestBytes)
            throw new ArgumentException("Graph transaction digest 必须为 32 字节。", nameof(digest));
        byte[] payload = new byte[sizeof(int) + DigestBytes];
        BinaryPrimitives.WriteInt32LittleEndian(payload, PayloadVersion);
        digest.CopyTo(payload.AsSpan(sizeof(int)));
        return GraphRecordEnvelopeCodec.Encode(GraphRecordKind.Metadata, 0, payload);
    }

    internal static byte[] Decode(ReadOnlySpan<byte> encoded)
    {
        GraphRecordEnvelope envelope = GraphRecordEnvelopeCodec.Decode(encoded);
        if (envelope.Kind != GraphRecordKind.Metadata
            || envelope.ElementVersion != 0
            || envelope.Payload.Length != sizeof(int) + DigestBytes
            || BinaryPrimitives.ReadInt32LittleEndian(envelope.Payload) != PayloadVersion)
        {
            throw new InvalidDataException("Graph transaction request marker 无效。");
        }
        return envelope.Payload.AsSpan(sizeof(int), DigestBytes).ToArray();
    }
}
