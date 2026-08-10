using System.Buffers.Binary;

namespace SonnetDB.Graphs.Storage;

/// <summary>Graph V1 unique property 到内部 element ID 映射的 value 编解码器。</summary>
internal static class GraphUniquePropertyOwnerCodec
{
    private const int PayloadVersion = 1;
    private const int PayloadSize = sizeof(int) + sizeof(byte) + 3 + sizeof(long);

    internal static byte[] Encode(GraphElementKind elementKind, GraphElementId ownerId)
    {
        if (!Enum.IsDefined(elementKind))
            throw new ArgumentOutOfRangeException(nameof(elementKind));
        if (ownerId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerId));

        Span<byte> payload = stackalloc byte[PayloadSize];
        payload.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(payload, PayloadVersion);
        payload[4] = (byte)elementKind;
        BinaryPrimitives.WriteInt64LittleEndian(payload[8..], ownerId.Value);
        return GraphRecordEnvelopeCodec.Encode(GraphRecordKind.UniquePropertyOwner, 0, payload);
    }

    internal static GraphElementId Decode(
        ReadOnlySpan<byte> encoded,
        GraphElementKind expectedElementKind)
    {
        if (!Enum.IsDefined(expectedElementKind))
            throw new ArgumentOutOfRangeException(nameof(expectedElementKind));

        GraphRecordEnvelope envelope = GraphRecordEnvelopeCodec.Decode(encoded);
        ReadOnlySpan<byte> payload = envelope.Payload;
        if (envelope.Kind != GraphRecordKind.UniquePropertyOwner
            || envelope.ElementVersion != 0
            || payload.Length != PayloadSize
            || BinaryPrimitives.ReadInt32LittleEndian(payload) != PayloadVersion
            || payload[4] != (byte)expectedElementKind
            || payload[5] != 0
            || payload[6] != 0
            || payload[7] != 0)
        {
            throw new InvalidDataException("Graph unique property owner header 无效。");
        }

        long ownerId = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        if (ownerId <= 0)
            throw new InvalidDataException("Graph unique property owner ID 无效。");
        return new GraphElementId(ownerId);
    }
}
