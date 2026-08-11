using System.Buffers.Binary;
using SonnetDB.Storage.Codecs;

namespace SonnetDB.Graphs.Storage;

/// <summary>已规范化的 vertex 持久记录。</summary>
internal sealed class GraphVertexRecord
{
    internal GraphVertexRecord(
        GraphElementId id,
        long elementVersion,
        IEnumerable<LabelId> labels,
        IEnumerable<GraphProperty> properties)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementVersion);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        ElementVersion = elementVersion;
        Labels = GraphElementRecordCodec.NormalizeLabels(labels, nameof(labels));
        Properties = GraphElementRecordCodec.NormalizeProperties(properties, nameof(properties));
    }

    internal GraphElementId Id { get; }

    internal long ElementVersion { get; }

    internal IReadOnlyList<LabelId> Labels { get; }

    internal IReadOnlyList<GraphProperty> Properties { get; }
}

/// <summary>已规范化的 edge 持久记录。</summary>
internal sealed class GraphEdgeRecord
{
    internal GraphEdgeRecord(
        GraphElementId id,
        long elementVersion,
        GraphElementId sourceId,
        GraphElementId targetId,
        LabelId labelId,
        IEnumerable<GraphProperty> properties)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (sourceId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (targetId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetId));
        if (labelId.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(labelId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementVersion);
        ArgumentNullException.ThrowIfNull(properties);
        Id = id;
        ElementVersion = elementVersion;
        SourceId = sourceId;
        TargetId = targetId;
        LabelId = labelId;
        Properties = GraphElementRecordCodec.NormalizeProperties(properties, nameof(properties));
    }

    internal GraphElementId Id { get; }

    internal long ElementVersion { get; }

    internal GraphElementId SourceId { get; }

    internal GraphElementId TargetId { get; }

    internal LabelId LabelId { get; }

    internal IReadOnlyList<GraphProperty> Properties { get; }
}

/// <summary>Graph vertex/edge V1 payload 编解码器。</summary>
internal static class GraphElementRecordCodec
{
    private const int PayloadVersion = 1;
    private const int MaxLabels = 4096;
    private const int MaxProperties = 16384;
    private const int VertexFixedBytes = sizeof(int) + sizeof(long) + sizeof(int) + sizeof(int);
    private const int EdgeFixedBytes = sizeof(int) + (sizeof(long) * 3) + sizeof(int) + sizeof(int);

    internal static byte[] EncodeVertex(GraphVertexRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        int payloadSize = checked(VertexFixedBytes + (record.Labels.Count * sizeof(int)));
        var scalarSizes = new int[record.Properties.Count];
        for (int i = 0; i < record.Properties.Count; i++)
        {
            scalarSizes[i] = SortableScalarCodec.GetGraphSize(record.Properties[i].Value);
            payloadSize = checked(payloadSize + sizeof(int) + scalarSizes[i]);
            EnsurePayloadSize(payloadSize);
        }

        byte[] payload = new byte[payloadSize];
        Span<byte> destination = payload;
        int offset = 0;
        WriteInt32(destination, ref offset, PayloadVersion);
        WriteInt64(destination, ref offset, record.Id.Value);
        WriteInt32(destination, ref offset, record.Labels.Count);
        WriteInt32(destination, ref offset, record.Properties.Count);
        foreach (LabelId label in record.Labels)
            WriteInt32(destination, ref offset, label.Value);
        WriteProperties(destination, ref offset, record.Properties, scalarSizes);
        return GraphRecordEnvelopeCodec.Encode(
            GraphRecordKind.Vertex,
            record.ElementVersion,
            payload);
    }

    internal static GraphVertexRecord DecodeVertex(ReadOnlySpan<byte> encoded)
    {
        GraphRecordEnvelope envelope = GraphRecordEnvelopeCodec.Decode(encoded);
        if (envelope.Kind != GraphRecordKind.Vertex || envelope.ElementVersion <= 0)
            throw new InvalidDataException("Graph vertex record envelope 类别或 element version 无效。");
        ReadOnlySpan<byte> payload = envelope.Payload;
        int offset = 0;
        EnsurePayloadVersion(payload, ref offset);
        GraphElementId id = ReadElementId(payload, ref offset, "vertex");
        int labelCount = ReadBoundedCount(payload, ref offset, MaxLabels, "label");
        int propertyCount = ReadBoundedCount(payload, ref offset, MaxProperties, "property");
        var labels = new LabelId[labelCount];
        int previousLabel = 0;
        for (int i = 0; i < labels.Length; i++)
        {
            int value = ReadInt32(payload, ref offset);
            if (value <= previousLabel)
                throw new InvalidDataException("Graph vertex labels 必须严格递增且不能重复。");
            labels[i] = new LabelId(value);
            previousLabel = value;
        }
        GraphProperty[] properties = ReadProperties(payload, ref offset, propertyCount);
        EnsureComplete(payload, offset);
        return new GraphVertexRecord(id, envelope.ElementVersion, labels, properties);
    }

    internal static byte[] EncodeEdge(GraphEdgeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        int payloadSize = EdgeFixedBytes;
        var scalarSizes = new int[record.Properties.Count];
        for (int i = 0; i < record.Properties.Count; i++)
        {
            scalarSizes[i] = SortableScalarCodec.GetGraphSize(record.Properties[i].Value);
            payloadSize = checked(payloadSize + sizeof(int) + scalarSizes[i]);
            EnsurePayloadSize(payloadSize);
        }

        byte[] payload = new byte[payloadSize];
        Span<byte> destination = payload;
        int offset = 0;
        WriteInt32(destination, ref offset, PayloadVersion);
        WriteInt64(destination, ref offset, record.Id.Value);
        WriteInt64(destination, ref offset, record.SourceId.Value);
        WriteInt64(destination, ref offset, record.TargetId.Value);
        WriteInt32(destination, ref offset, record.LabelId.Value);
        WriteInt32(destination, ref offset, record.Properties.Count);
        WriteProperties(destination, ref offset, record.Properties, scalarSizes);
        return GraphRecordEnvelopeCodec.Encode(
            GraphRecordKind.Edge,
            record.ElementVersion,
            payload);
    }

    internal static GraphEdgeRecord DecodeEdge(ReadOnlySpan<byte> encoded)
    {
        GraphRecordEnvelope envelope = GraphRecordEnvelopeCodec.Decode(encoded);
        if (envelope.Kind != GraphRecordKind.Edge || envelope.ElementVersion <= 0)
            throw new InvalidDataException("Graph edge record envelope 类别或 element version 无效。");
        ReadOnlySpan<byte> payload = envelope.Payload;
        int offset = 0;
        EnsurePayloadVersion(payload, ref offset);
        GraphElementId id = ReadElementId(payload, ref offset, "edge");
        GraphElementId sourceId = ReadElementId(payload, ref offset, "edge source");
        GraphElementId targetId = ReadElementId(payload, ref offset, "edge target");
        int labelValue = ReadInt32(payload, ref offset);
        if (labelValue <= 0)
            throw new InvalidDataException("Graph edge label ID 无效。");
        int propertyCount = ReadBoundedCount(payload, ref offset, MaxProperties, "property");
        GraphProperty[] properties = ReadProperties(payload, ref offset, propertyCount);
        EnsureComplete(payload, offset);
        return new GraphEdgeRecord(
            id,
            envelope.ElementVersion,
            sourceId,
            targetId,
            new LabelId(labelValue),
            properties);
    }

    internal static LabelId[] NormalizeLabels(IEnumerable<LabelId> labels, string parameterName)
    {
        var bounded = new List<LabelId>();
        foreach (LabelId label in labels)
        {
            if (bounded.Count == MaxLabels)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Graph element labels 不能超过 {MaxLabels} 个。");
            }
            bounded.Add(label);
        }
        LabelId[] result = [.. bounded];
        Array.Sort(result, static (left, right) => left.CompareTo(right));
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i].Value <= 0 || i > 0 && result[i] == result[i - 1])
                throw new ArgumentException("Graph labels 不能包含默认值或重复 ID。", parameterName);
        }
        return result;
    }

    internal static GraphProperty[] NormalizeProperties(
        IEnumerable<GraphProperty> properties,
        string parameterName)
    {
        var bounded = new List<GraphProperty>();
        long encodedBytes = 0;
        foreach (GraphProperty property in properties)
        {
            if (bounded.Count == MaxProperties)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Graph element properties 不能超过 {MaxProperties} 个。");
            }
            int scalarSize = SortableScalarCodec.GetGraphSize(property.Value);
            if (scalarSize > GraphKeyCodec.MaxPropertyScalarBytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Graph property scalar 编码后不能超过 {GraphKeyCodec.MaxPropertyScalarBytes} 字节，以保证属性索引 key 可持久化。");
            }
            encodedBytes = checked(encodedBytes + sizeof(int) + scalarSize);
            if (encodedBytes > GraphRecordEnvelopeCodec.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    parameterName,
                    $"Graph element properties 编码后不能超过 {GraphRecordEnvelopeCodec.MaxPayloadBytes} 字节。");
            }
            bounded.Add(property);
        }
        GraphProperty[] result = [.. bounded];
        Array.Sort(result, static (left, right) => left.PropertyId.CompareTo(right.PropertyId));
        for (int i = 0; i < result.Length; i++)
        {
            if (result[i].PropertyId <= 0
                || i > 0 && result[i].PropertyId == result[i - 1].PropertyId)
            {
                throw new ArgumentException("Graph properties 必须使用正数且不重复的 property ID。", parameterName);
            }
        }
        return result;
    }

    private static void WriteProperties(
        Span<byte> destination,
        ref int offset,
        IReadOnlyList<GraphProperty> properties,
        IReadOnlyList<int> scalarSizes)
    {
        for (int i = 0; i < properties.Count; i++)
        {
            WriteInt32(destination, ref offset, properties[i].PropertyId);
            int written = SortableScalarCodec.WriteGraph(destination[offset..], properties[i].Value);
            if (written != scalarSizes[i])
                throw new InvalidDataException("Graph property scalar 编码长度不一致。");
            offset += written;
        }
    }

    private static void EnsurePayloadSize(int payloadSize)
    {
        if (payloadSize > GraphRecordEnvelopeCodec.MaxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payloadSize),
                $"Graph element payload 不能超过 {GraphRecordEnvelopeCodec.MaxPayloadBytes} 字节。");
        }
    }

    private static GraphProperty[] ReadProperties(
        ReadOnlySpan<byte> payload,
        ref int offset,
        int count)
    {
        var properties = new GraphProperty[count];
        int previousPropertyId = 0;
        for (int i = 0; i < properties.Length; i++)
        {
            int propertyId = ReadInt32(payload, ref offset);
            if (propertyId <= previousPropertyId)
                throw new InvalidDataException("Graph properties 必须严格递增且不能重复。");
            GraphPropertyValue value;
            int consumed;
            try
            {
                value = SortableScalarCodec.DecodeGraph(payload[offset..], out consumed);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                throw new InvalidDataException("Graph property scalar 无效。", exception);
            }
            if (consumed <= 0 || consumed > payload.Length - offset)
                throw new InvalidDataException("Graph property scalar 长度无效。");
            offset += consumed;
            properties[i] = new GraphProperty(propertyId, value);
            previousPropertyId = propertyId;
        }
        return properties;
    }

    private static GraphElementId ReadElementId(
        ReadOnlySpan<byte> payload,
        ref int offset,
        string fieldName)
    {
        long value = ReadInt64(payload, ref offset);
        if (value <= 0)
            throw new InvalidDataException($"Graph {fieldName} ID 无效。");
        return new GraphElementId(value);
    }

    private static int ReadBoundedCount(
        ReadOnlySpan<byte> payload,
        ref int offset,
        int maximum,
        string fieldName)
    {
        int count = ReadInt32(payload, ref offset);
        if (count < 0 || count > maximum)
            throw new InvalidDataException($"Graph {fieldName} count 无效。");
        return count;
    }

    private static void EnsurePayloadVersion(ReadOnlySpan<byte> payload, ref int offset)
    {
        int version = ReadInt32(payload, ref offset);
        if (version != PayloadVersion)
            throw new InvalidDataException($"Graph element payload version {version} 不受支持。");
    }

    private static void EnsureComplete(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset != payload.Length)
            throw new InvalidDataException("Graph element payload 包含尾随数据。");
    }

    private static void WriteInt32(Span<byte> destination, ref int offset, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..], value);
        offset += sizeof(int);
    }

    private static void WriteInt64(Span<byte> destination, ref int offset, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination[offset..], value);
        offset += sizeof(long);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureRemaining(source, offset, sizeof(int));
        int value = BinaryPrimitives.ReadInt32LittleEndian(source[offset..]);
        offset += sizeof(int);
        return value;
    }

    private static long ReadInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        EnsureRemaining(source, offset, sizeof(long));
        long value = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
        offset += sizeof(long);
        return value;
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> source, int offset, int count)
    {
        if (offset < 0 || count < 0 || source.Length - offset < count)
            throw new InvalidDataException("Graph element payload 被截断。");
    }
}
