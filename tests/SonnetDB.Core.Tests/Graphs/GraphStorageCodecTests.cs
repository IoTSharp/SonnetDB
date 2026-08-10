using System.Buffers.Binary;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphStorageCodecTests
{
    [Fact]
    public void GraphStorageFormat_AllKeyFamiliesAndEnvelope_HaveFrozenV1Bytes()
    {
        var id1 = new GraphElementId(1);
        var id2 = new GraphElementId(2);
        var id4 = new GraphElementId(4);
        var label3 = new LabelId(3);
        Guid requestId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        (string Expected, byte[] Actual)[] vectors =
        [
            ("53444247011000000000000000016650CBE0", GraphKeyCodec.EncodeVertexRecord(id1)),
            ("53444247011100000000000000029F15B96E", GraphKeyCodec.EncodeEdgeRecord(id2)),
            ("534442470120000000000000000100000003000000000000000200000000000000045B7EC9F0",
                GraphKeyCodec.EncodeOutgoingAdjacency(id1, label3, id2, id4)),
            ("5344424701210000000000000002000000030000000000000001000000000000000459195DD1",
                GraphKeyCodec.EncodeIncomingAdjacency(id2, label3, id1, id4)),
            ("53444247013000000003000000000000000198DB251B",
                GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Vertex, label3, id1)),
            ("534442470131000000030000000000000002A753BA5F",
                GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Edge, label3, id2)),
            ("5344424701400000000300000005017FFFFFFFFFFFFFFF00000000000000012A492BE7",
                GraphKeyCodec.EncodePropertyIndex(
                    GraphElementKind.Vertex,
                    label3,
                    5,
                    GraphPropertyValue.FromInt64(-1),
                    id1)),
            ("53444247014100000003000000050461000000000000000000022458C3B1",
                GraphKeyCodec.EncodePropertyIndex(
                    GraphElementKind.Edge,
                    label3,
                    5,
                    GraphPropertyValue.FromString("a"),
                    id2)),
            ("5344424701420000000300000005030110DA0C53",
                GraphKeyCodec.EncodeUniqueProperty(
                    GraphElementKind.Vertex,
                    label3,
                    5,
                    GraphPropertyValue.FromBoolean(true))),
            ("534442470143000000030000000500516AE8C6",
                GraphKeyCodec.EncodeUniqueProperty(
                    GraphElementKind.Edge,
                    label3,
                    5,
                    GraphPropertyValue.Null)),
            ("53444247015004F9FC7C83", GraphKeyCodec.EncodeMetadata(4)),
            ("53444247015100112233445566778899AABBCCDDEEFF7032AFA6",
                GraphKeyCodec.EncodeTransactionRequest(requestId)),
            ("5344424752454331010000001C00010007000000000000000200000001024953C03F",
                GraphRecordEnvelopeCodec.Encode(GraphRecordKind.Vertex, 7, [1, 2])),
            ("5344424752454331010000001C00040000000000000000001000000001000000010000002A00000000000000B18F55D9",
                GraphUniquePropertyOwnerCodec.Encode(GraphElementKind.Vertex, new GraphElementId(42))),
        ];
        Assert.Equal(GraphStorageFormat.StorageFormatVersion, GraphDefinition.CurrentRecordFormatVersion);
        Assert.Equal((byte)GraphStorageFormat.StorageFormatVersion, GraphStorageFormat.KeyFormatVersion);
        foreach ((string expected, byte[] actual) in vectors)
            Assert.Equal(expected, Convert.ToHexString(actual));
    }

    [Fact]
    public void GraphKeyCodec_AllFamilies_RoundTrip()
    {
        var source = new GraphElementId(10);
        var target = new GraphElementId(20);
        var edge = new GraphElementId(30);
        var label = new LabelId(7);

        GraphStorageKey vertexKey = GraphKeyCodec.Decode(GraphKeyCodec.EncodeVertexRecord(source));
        GraphStorageKey edgeKey = GraphKeyCodec.Decode(GraphKeyCodec.EncodeEdgeRecord(edge));
        GraphStorageKey outgoing = GraphKeyCodec.Decode(
            GraphKeyCodec.EncodeOutgoingAdjacency(source, label, target, edge));
        GraphStorageKey incoming = GraphKeyCodec.Decode(
            GraphKeyCodec.EncodeIncomingAdjacency(target, label, source, edge));
        GraphStorageKey membership = GraphKeyCodec.Decode(
            GraphKeyCodec.EncodeLabelMembership(GraphElementKind.Vertex, label, source));
        GraphStorageKey property = GraphKeyCodec.Decode(
            GraphKeyCodec.EncodePropertyIndex(
                GraphElementKind.Vertex,
                label,
                9,
                GraphPropertyValue.FromString("a\0b"),
                source));
        GraphStorageKey unique = GraphKeyCodec.Decode(
            GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                label,
                9,
                GraphPropertyValue.FromInt64(-1)));
        GraphStorageKey metadata = GraphKeyCodec.Decode(GraphKeyCodec.EncodeMetadata(1));
        Guid requestId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        GraphStorageKey request = GraphKeyCodec.Decode(
            GraphKeyCodec.EncodeTransactionRequest(requestId));

        Assert.Equal(source, vertexKey.ElementId);
        Assert.Equal(edge, edgeKey.ElementId);
        Assert.Equal((source, target, edge, label),
            (outgoing.SourceId, outgoing.TargetId, outgoing.EdgeId, outgoing.LabelId));
        Assert.Equal((source, target, edge, label),
            (incoming.SourceId, incoming.TargetId, incoming.EdgeId, incoming.LabelId));
        Assert.Equal((source, label), (membership.ElementId, membership.LabelId));
        Assert.Equal((source, label, 9, GraphPropertyValue.FromString("a\0b")),
            (property.ElementId, property.LabelId, property.PropertyId, property.PropertyValue));
        Assert.Equal(GraphPropertyValue.FromInt64(-1), unique.PropertyValue);
        Assert.Equal(1, metadata.MetadataKind);
        Assert.Equal(requestId, request.TransactionRequestId);
    }

    [Fact]
    public void GraphKeyCodec_AdjacencyOrderFollowsNeighborAndEdgeIds()
    {
        var source = new GraphElementId(1);
        var label = new LabelId(1);
        byte[] first = GraphKeyCodec.EncodeOutgoingAdjacency(
            source, label, new GraphElementId(2), new GraphElementId(100));
        byte[] second = GraphKeyCodec.EncodeOutgoingAdjacency(
            source, label, new GraphElementId(10), new GraphElementId(1));

        Assert.True(KvKeyComparer.Instance.Compare(first, second) < 0);
        Assert.True(first.AsSpan().StartsWith(GraphKeyCodec.OutgoingPrefix(source)));
    }

    [Fact]
    public void GraphKeyCodec_CorruptCrcAndFutureVersion_AreRejected()
    {
        byte[] corrupt = GraphKeyCodec.EncodeVertexRecord(new GraphElementId(1));
        corrupt[6] ^= 0x40;
        Assert.Throws<InvalidDataException>(() => GraphKeyCodec.Decode(corrupt));

        byte[] future = GraphKeyCodec.EncodeVertexRecord(new GraphElementId(1));
        future[4] = 2;
        Assert.Throws<InvalidDataException>(() => GraphKeyCodec.Decode(future));
    }

    [Fact]
    public void GraphRecordEnvelopeCodec_RoundTripsAndRejectsDamage()
    {
        byte[] encoded = GraphRecordEnvelopeCodec.Encode(GraphRecordKind.Edge, 42, [1, 2, 3]);
        GraphRecordEnvelope decoded = GraphRecordEnvelopeCodec.Decode(encoded);

        Assert.Equal(GraphRecordKind.Edge, decoded.Kind);
        Assert.Equal(42, decoded.ElementVersion);
        Assert.Equal([1, 2, 3], decoded.Payload);

        encoded[30] ^= 0x80;
        Assert.Throws<InvalidDataException>(() => GraphRecordEnvelopeCodec.Decode(encoded));
        Assert.Throws<InvalidDataException>(() => GraphRecordEnvelopeCodec.Decode(encoded[..10]));
    }

    [Fact]
    public void GraphRecordEnvelopeCodec_FutureVersion_IsRejectedExplicitly()
    {
        byte[] encoded = GraphRecordEnvelopeCodec.Encode(GraphRecordKind.Vertex, 1, []);
        encoded[8] = 2;

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => GraphRecordEnvelopeCodec.Decode(encoded));
        Assert.Contains("显式迁移", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GraphUniquePropertyOwnerCodec_RoundTripsAndRejectsWrongKindOrDamage()
    {
        var ownerId = new GraphElementId(42);
        byte[] encoded = GraphUniquePropertyOwnerCodec.Encode(GraphElementKind.Vertex, ownerId);

        Assert.Equal(
            ownerId,
            GraphUniquePropertyOwnerCodec.Decode(encoded, GraphElementKind.Vertex));
        GraphRecordEnvelope frozen = GraphRecordEnvelopeCodec.Decode(encoded);
        Assert.Equal(GraphRecordKind.UniquePropertyOwner, frozen.Kind);
        Assert.Equal(0, frozen.ElementVersion);
        Assert.Equal(16, frozen.Payload.Length);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frozen.Payload));
        Assert.Equal((byte)GraphElementKind.Vertex, frozen.Payload[4]);
        Assert.Equal([0, 0, 0], frozen.Payload[5..8]);
        Assert.Equal(ownerId.Value, BinaryPrimitives.ReadInt64LittleEndian(frozen.Payload.AsSpan(8)));
        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode(encoded, GraphElementKind.Edge));
        byte[] highWater = GraphHighWaterCodec.Encode(GraphHighWaterKind.VertexId, ownerId.Value);
        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode(highWater, GraphElementKind.Vertex));
        Assert.Throws<InvalidDataException>(() =>
            GraphHighWaterCodec.Decode(encoded, GraphHighWaterKind.VertexId));

        byte[] corruptCrc = encoded.ToArray();
        corruptCrc[^1] ^= 0x80;
        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode(corruptCrc, GraphElementKind.Vertex));
        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode([.. encoded, 0], GraphElementKind.Vertex));

        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode(
                MutateOwnerPayload(encoded, static payload =>
                    BinaryPrimitives.WriteInt32LittleEndian(payload, 2)),
                GraphElementKind.Vertex));
        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode(
                MutateOwnerPayload(encoded, static payload => payload[5] = 1),
                GraphElementKind.Vertex));
        Assert.Throws<InvalidDataException>(() =>
            GraphUniquePropertyOwnerCodec.Decode(
                MutateOwnerPayload(encoded, static payload =>
                    BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(8), 0)),
                GraphElementKind.Vertex));
    }

    private static byte[] MutateOwnerPayload(byte[] encoded, Action<byte[]> mutation)
    {
        GraphRecordEnvelope envelope = GraphRecordEnvelopeCodec.Decode(encoded);
        mutation(envelope.Payload);
        return GraphRecordEnvelopeCodec.Encode(
            GraphRecordKind.UniquePropertyOwner,
            0,
            envelope.Payload);
    }
}
