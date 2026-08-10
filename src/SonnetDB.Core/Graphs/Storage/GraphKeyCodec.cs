using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Storage.Codecs;

namespace SonnetDB.Graphs.Storage;

/// <summary>
/// 原生图 V1 key 编解码器。完整 key 带 magic、版本和 CRC；扫描前缀只包含稳定头与已绑定字段。
/// </summary>
internal static class GraphKeyCodec
{
    internal const int MaxEncodedKeyBytes = 64 * 1024;
    internal const int MaxPropertyScalarBytes = MaxEncodedKeyBytes
        - GraphStorageFormat.KeyHeaderSize
        - GraphStorageFormat.KeyCrcSize
        - sizeof(int)
        - sizeof(int)
        - sizeof(long);

    public static byte[] EncodeVertexRecord(GraphElementId vertexId)
    {
        byte[] key = Allocate(GraphKeyKind.VertexRecord, sizeof(long));
        WriteElementId(key.AsSpan(GraphStorageFormat.KeyHeaderSize), vertexId);
        return Complete(key);
    }

    public static byte[] EncodeEdgeRecord(GraphElementId edgeId)
    {
        byte[] key = Allocate(GraphKeyKind.EdgeRecord, sizeof(long));
        WriteElementId(key.AsSpan(GraphStorageFormat.KeyHeaderSize), edgeId);
        return Complete(key);
    }

    public static byte[] EncodeOutgoingAdjacency(
        GraphElementId sourceId,
        LabelId edgeTypeId,
        GraphElementId targetId,
        GraphElementId edgeId)
        => EncodeAdjacency(GraphKeyKind.OutgoingAdjacency, sourceId, edgeTypeId, targetId, edgeId);

    public static byte[] EncodeIncomingAdjacency(
        GraphElementId targetId,
        LabelId edgeTypeId,
        GraphElementId sourceId,
        GraphElementId edgeId)
        => EncodeAdjacency(GraphKeyKind.IncomingAdjacency, targetId, edgeTypeId, sourceId, edgeId);

    public static byte[] EncodeLabelMembership(
        GraphElementKind elementKind,
        LabelId labelId,
        GraphElementId elementId)
    {
        GraphKeyKind kind = elementKind switch
        {
            GraphElementKind.Vertex => GraphKeyKind.VertexLabel,
            GraphElementKind.Edge => GraphKeyKind.EdgeLabel,
            _ => throw new ArgumentOutOfRangeException(nameof(elementKind)),
        };
        byte[] key = Allocate(kind, sizeof(int) + sizeof(long));
        Span<byte> payload = key.AsSpan(GraphStorageFormat.KeyHeaderSize);
        WriteLabelId(payload, labelId);
        WriteElementId(payload[sizeof(int)..], elementId);
        return Complete(key);
    }

    public static byte[] EncodePropertyIndex(
        GraphElementKind elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value,
        GraphElementId elementId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        GraphKeyKind kind = elementKind switch
        {
            GraphElementKind.Vertex => GraphKeyKind.VertexPropertyIndex,
            GraphElementKind.Edge => GraphKeyKind.EdgePropertyIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(elementKind)),
        };
        byte[] scalar = EncodePropertyScalar(value);
        byte[] key = Allocate(kind, sizeof(int) + sizeof(int) + scalar.Length + sizeof(long));
        Span<byte> payload = key.AsSpan(GraphStorageFormat.KeyHeaderSize);
        WriteLabelId(payload, labelId);
        BinaryPrimitives.WriteInt32BigEndian(payload[sizeof(int)..], propertyId);
        scalar.CopyTo(payload[(sizeof(int) * 2)..]);
        WriteElementId(payload[(sizeof(int) * 2 + scalar.Length)..], elementId);
        return Complete(key);
    }

    public static byte[] EncodeUniqueProperty(
        GraphElementKind elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        GraphKeyKind kind = elementKind switch
        {
            GraphElementKind.Vertex => GraphKeyKind.VertexUniqueProperty,
            GraphElementKind.Edge => GraphKeyKind.EdgeUniqueProperty,
            _ => throw new ArgumentOutOfRangeException(nameof(elementKind)),
        };
        byte[] scalar = EncodePropertyScalar(value);
        byte[] key = Allocate(kind, sizeof(int) + sizeof(int) + scalar.Length);
        Span<byte> payload = key.AsSpan(GraphStorageFormat.KeyHeaderSize);
        WriteLabelId(payload, labelId);
        BinaryPrimitives.WriteInt32BigEndian(payload[sizeof(int)..], propertyId);
        scalar.CopyTo(payload[(sizeof(int) * 2)..]);
        return Complete(key);
    }

    public static byte[] EncodeMetadata(byte metadataKind)
    {
        if (metadataKind == 0)
            throw new ArgumentOutOfRangeException(nameof(metadataKind));
        byte[] key = Allocate(GraphKeyKind.Metadata, 1);
        key[GraphStorageFormat.KeyHeaderSize] = metadataKind;
        return Complete(key);
    }

    public static byte[] EncodeTransactionRequest(Guid requestId)
    {
        if (requestId == Guid.Empty)
            throw new ArgumentException("Graph transaction request ID 不能为空。", nameof(requestId));
        byte[] key = Allocate(GraphKeyKind.TransactionRequest, 16);
        if (!requestId.TryWriteBytes(
            key.AsSpan(GraphStorageFormat.KeyHeaderSize, 16),
            bigEndian: true,
            out int bytesWritten)
            || bytesWritten != 16)
        {
            throw new InvalidOperationException("Graph transaction request ID 编码失败。");
        }
        return Complete(key);
    }

    public static byte[] VertexRecordPrefix() => Prefix(GraphKeyKind.VertexRecord, 0);

    public static byte[] EdgeRecordPrefix() => Prefix(GraphKeyKind.EdgeRecord, 0);

    public static byte[] OutgoingPrefix(GraphElementId sourceId)
    {
        byte[] prefix = Prefix(GraphKeyKind.OutgoingAdjacency, sizeof(long));
        WriteElementId(prefix.AsSpan(GraphStorageFormat.KeyHeaderSize), sourceId);
        return prefix;
    }

    public static byte[] IncomingPrefix(GraphElementId targetId)
    {
        byte[] prefix = Prefix(GraphKeyKind.IncomingAdjacency, sizeof(long));
        WriteElementId(prefix.AsSpan(GraphStorageFormat.KeyHeaderSize), targetId);
        return prefix;
    }

    public static byte[] FamilyPrefix(GraphKeyKind kind)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        return Prefix(kind, 0);
    }

    public static byte[] PropertyIndexPrefix(
        GraphElementKind elementKind,
        LabelId labelId,
        int propertyId,
        GraphPropertyValue value)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(propertyId);
        GraphKeyKind kind = elementKind switch
        {
            GraphElementKind.Vertex => GraphKeyKind.VertexPropertyIndex,
            GraphElementKind.Edge => GraphKeyKind.EdgePropertyIndex,
            _ => throw new ArgumentOutOfRangeException(nameof(elementKind)),
        };
        byte[] scalar = EncodePropertyScalar(value);
        byte[] prefix = Prefix(kind, sizeof(int) + sizeof(int) + scalar.Length);
        Span<byte> payload = prefix.AsSpan(GraphStorageFormat.KeyHeaderSize);
        WriteLabelId(payload, labelId);
        BinaryPrimitives.WriteInt32BigEndian(payload[sizeof(int)..], propertyId);
        scalar.CopyTo(payload[(sizeof(int) * 2)..]);
        return prefix;
    }

    public static GraphStorageKey Decode(ReadOnlySpan<byte> key)
    {
        ValidateHeaderAndCrc(key, out GraphKeyKind kind);
        ReadOnlySpan<byte> payload = key.Slice(
            GraphStorageFormat.KeyHeaderSize,
            key.Length - GraphStorageFormat.KeyHeaderSize - GraphStorageFormat.KeyCrcSize);
        int offset = 0;

        switch (kind)
        {
            case GraphKeyKind.VertexRecord:
            case GraphKeyKind.EdgeRecord:
            {
                GraphElementId id = ReadElementId(payload, ref offset);
                EnsureComplete(payload, offset);
                return new GraphStorageKey(kind, id, default, default, default, default, 0, default, 0);
            }
            case GraphKeyKind.OutgoingAdjacency:
            case GraphKeyKind.IncomingAdjacency:
            {
                GraphElementId anchor = ReadElementId(payload, ref offset);
                LabelId label = ReadLabelId(payload, ref offset);
                GraphElementId neighbor = ReadElementId(payload, ref offset);
                GraphElementId edge = ReadElementId(payload, ref offset);
                EnsureComplete(payload, offset);
                return kind == GraphKeyKind.OutgoingAdjacency
                    ? new GraphStorageKey(kind, edge, anchor, neighbor, edge, label, 0, default, 0)
                    : new GraphStorageKey(kind, edge, neighbor, anchor, edge, label, 0, default, 0);
            }
            case GraphKeyKind.VertexLabel:
            case GraphKeyKind.EdgeLabel:
            {
                LabelId label = ReadLabelId(payload, ref offset);
                GraphElementId element = ReadElementId(payload, ref offset);
                EnsureComplete(payload, offset);
                return new GraphStorageKey(kind, element, default, default, default, label, 0, default, 0);
            }
            case GraphKeyKind.VertexPropertyIndex:
            case GraphKeyKind.EdgePropertyIndex:
            {
                LabelId label = ReadLabelId(payload, ref offset);
                int propertyId = ReadPropertyId(payload, ref offset);
                GraphPropertyValue value = SortableScalarCodec.DecodeGraph(payload[offset..], out int consumed);
                offset += consumed;
                GraphElementId element = ReadElementId(payload, ref offset);
                EnsureComplete(payload, offset);
                return new GraphStorageKey(kind, element, default, default, default, label, propertyId, value, 0);
            }
            case GraphKeyKind.VertexUniqueProperty:
            case GraphKeyKind.EdgeUniqueProperty:
            {
                LabelId label = ReadLabelId(payload, ref offset);
                int propertyId = ReadPropertyId(payload, ref offset);
                GraphPropertyValue value = SortableScalarCodec.DecodeGraph(payload[offset..], out int consumed);
                offset += consumed;
                EnsureComplete(payload, offset);
                return new GraphStorageKey(kind, default, default, default, default, label, propertyId, value, 0);
            }
            case GraphKeyKind.Metadata:
                if (payload.Length != 1 || payload[0] == 0)
                    throw new InvalidDataException("Graph V1 metadata key 无效。");
                return new GraphStorageKey(kind, default, default, default, default, default, 0, default, payload[0]);
            case GraphKeyKind.TransactionRequest:
                if (payload.Length != 16)
                    throw new InvalidDataException("Graph V1 transaction request key 长度无效。");
                Guid requestId = new(payload, bigEndian: true);
                if (requestId == Guid.Empty)
                    throw new InvalidDataException("Graph V1 transaction request ID 不能为空。");
                return new GraphStorageKey(
                    kind,
                    default,
                    default,
                    default,
                    default,
                    default,
                    0,
                    default,
                    0,
                    requestId);
            default:
                throw new InvalidDataException($"Graph V1 key family {(byte)kind} 未知。");
        }
    }

    private static byte[] EncodeAdjacency(
        GraphKeyKind kind,
        GraphElementId anchorId,
        LabelId edgeTypeId,
        GraphElementId neighborId,
        GraphElementId edgeId)
    {
        byte[] key = Allocate(kind, sizeof(long) + sizeof(int) + sizeof(long) + sizeof(long));
        Span<byte> payload = key.AsSpan(GraphStorageFormat.KeyHeaderSize);
        int offset = 0;
        WriteElementId(payload[offset..], anchorId);
        offset += sizeof(long);
        WriteLabelId(payload[offset..], edgeTypeId);
        offset += sizeof(int);
        WriteElementId(payload[offset..], neighborId);
        offset += sizeof(long);
        WriteElementId(payload[offset..], edgeId);
        return Complete(key);
    }

    private static byte[] Allocate(GraphKeyKind kind, int payloadSize)
    {
        int keySize = checked(
            GraphStorageFormat.KeyHeaderSize + payloadSize + GraphStorageFormat.KeyCrcSize);
        if (keySize > MaxEncodedKeyBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadSize),
                $"Graph V1 key 编码后不能超过 {MaxEncodedKeyBytes} 字节。");
        }
        byte[] key = new byte[keySize];
        WriteHeader(key, kind);
        return key;
    }

    private static byte[] EncodePropertyScalar(GraphPropertyValue value)
    {
        int scalarSize = SortableScalarCodec.GetGraphSize(value);
        if (scalarSize > MaxPropertyScalarBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"Graph V1 property scalar 编码后不能超过 {MaxPropertyScalarBytes} 字节，以保证完整索引 key 不超过 {MaxEncodedKeyBytes} 字节。");
        }
        return SortableScalarCodec.EncodeGraph(value);
    }

    private static byte[] Prefix(GraphKeyKind kind, int payloadSize)
    {
        byte[] prefix = new byte[checked(GraphStorageFormat.KeyHeaderSize + payloadSize)];
        WriteHeader(prefix, kind);
        return prefix;
    }

    private static void WriteHeader(Span<byte> destination, GraphKeyKind kind)
    {
        GraphStorageFormat.KeyMagic.CopyTo(destination);
        destination[4] = GraphStorageFormat.KeyFormatVersion;
        destination[5] = (byte)kind;
    }

    private static byte[] Complete(byte[] key)
    {
        uint crc = Crc32.HashToUInt32(key.AsSpan(0, key.Length - GraphStorageFormat.KeyCrcSize));
        BinaryPrimitives.WriteUInt32LittleEndian(key.AsSpan(key.Length - GraphStorageFormat.KeyCrcSize), crc);
        return key;
    }

    private static void ValidateHeaderAndCrc(ReadOnlySpan<byte> key, out GraphKeyKind kind)
    {
        if (key.Length < GraphStorageFormat.KeyHeaderSize + GraphStorageFormat.KeyCrcSize)
            throw new InvalidDataException("Graph V1 key 被截断。");
        if (!key[..4].SequenceEqual(GraphStorageFormat.KeyMagic))
            throw new InvalidDataException("Graph V1 key magic 无效。");
        if (key[4] != GraphStorageFormat.KeyFormatVersion)
            throw new InvalidDataException($"Graph key format version {key[4]} 不受支持；需要显式迁移。 ");

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(key[^GraphStorageFormat.KeyCrcSize..]);
        uint actual = Crc32.HashToUInt32(key[..^GraphStorageFormat.KeyCrcSize]);
        if (expected != actual)
            throw new InvalidDataException("Graph V1 key CRC32 不匹配。");
        kind = (GraphKeyKind)key[5];
    }

    private static void WriteElementId(Span<byte> destination, GraphElementId id)
        => BinaryPrimitives.WriteInt64BigEndian(destination, id.Value);

    private static void WriteLabelId(Span<byte> destination, LabelId id)
        => BinaryPrimitives.WriteInt32BigEndian(destination, id.Value);

    private static GraphElementId ReadElementId(ReadOnlySpan<byte> payload, ref int offset)
    {
        EnsureRemaining(payload, offset, sizeof(long));
        long value = BinaryPrimitives.ReadInt64BigEndian(payload[offset..]);
        offset += sizeof(long);
        try
        {
            return new GraphElementId(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Graph V1 key 包含无效元素 ID。", exception);
        }
    }

    private static LabelId ReadLabelId(ReadOnlySpan<byte> payload, ref int offset)
    {
        EnsureRemaining(payload, offset, sizeof(int));
        int value = BinaryPrimitives.ReadInt32BigEndian(payload[offset..]);
        offset += sizeof(int);
        try
        {
            return new LabelId(value);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Graph V1 key 包含无效 label ID。", exception);
        }
    }

    private static int ReadPropertyId(ReadOnlySpan<byte> payload, ref int offset)
    {
        EnsureRemaining(payload, offset, sizeof(int));
        int value = BinaryPrimitives.ReadInt32BigEndian(payload[offset..]);
        offset += sizeof(int);
        if (value <= 0)
            throw new InvalidDataException("Graph V1 key 包含无效 property ID。");
        return value;
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> payload, int offset, int count)
    {
        if (payload.Length - offset < count)
            throw new InvalidDataException("Graph V1 key payload 被截断。");
    }

    private static void EnsureComplete(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset != payload.Length)
            throw new InvalidDataException("Graph V1 key 包含尾随数据。");
    }
}

/// <summary>解码后的 Graph V1 key 字段。</summary>
internal readonly record struct GraphStorageKey(
    GraphKeyKind Kind,
    GraphElementId ElementId,
    GraphElementId SourceId,
    GraphElementId TargetId,
    GraphElementId EdgeId,
    LabelId LabelId,
    int PropertyId,
    GraphPropertyValue PropertyValue,
    byte MetadataKind,
    Guid TransactionRequestId = default);
