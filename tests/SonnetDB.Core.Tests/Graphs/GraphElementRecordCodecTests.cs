using System.Buffers.Binary;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphElementRecordCodecTests
{
    [Fact]
    public void VertexRecord_UnorderedInput_RoundTripsInCanonicalOrder()
    {
        var record = new GraphVertexRecord(
            new GraphElementId(11),
            elementVersion: 3,
            [new LabelId(9), new LabelId(2)],
            [
                new GraphPropertyEntry(8, GraphPropertyValue.FromString("value")),
                new GraphPropertyEntry(1, GraphPropertyValue.FromInt64(-7)),
            ]);

        GraphVertexRecord decoded = GraphElementRecordCodec.DecodeVertex(
            GraphElementRecordCodec.EncodeVertex(record));

        Assert.Equal(new GraphElementId(11), decoded.Id);
        Assert.Equal(3, decoded.ElementVersion);
        Assert.Equal([2, 9], decoded.Labels.Select(static label => label.Value));
        Assert.Equal([1, 8], decoded.Properties.Select(static property => property.PropertyId));
        Assert.Equal(GraphPropertyValue.FromInt64(-7), decoded.Properties[0].Value);
        Assert.Equal(GraphPropertyValue.FromString("value"), decoded.Properties[1].Value);
    }

    [Fact]
    public void EdgeRecord_SelfLoopAndTypedProperties_RoundTrips()
    {
        var record = new GraphEdgeRecord(
            new GraphElementId(21),
            elementVersion: 5,
            new GraphElementId(4),
            new GraphElementId(4),
            new LabelId(6),
            [
                new GraphPropertyEntry(1, GraphPropertyValue.FromBoolean(true)),
                new GraphPropertyEntry(2, GraphPropertyValue.FromBlob([0, 1, 2])),
            ]);

        GraphEdgeRecord decoded = GraphElementRecordCodec.DecodeEdge(
            GraphElementRecordCodec.EncodeEdge(record));

        Assert.Equal(record.Id, decoded.Id);
        Assert.Equal(record.SourceId, decoded.SourceId);
        Assert.Equal(record.TargetId, decoded.TargetId);
        Assert.Equal(record.LabelId, decoded.LabelId);
        Assert.Equal(record.ElementVersion, decoded.ElementVersion);
        Assert.Equal(record.Properties.Select(static property => property.Value),
            decoded.Properties.Select(static property => property.Value));
    }

    [Fact]
    public void VertexRecord_DuplicateInputsAndNonCanonicalPayload_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => new GraphVertexRecord(
            new GraphElementId(1),
            1,
            [new LabelId(2), new LabelId(2)],
            []));
        Assert.Throws<ArgumentException>(() => new GraphVertexRecord(
            new GraphElementId(1),
            1,
            [],
            [
                new GraphPropertyEntry(3, GraphPropertyValue.Null),
                new GraphPropertyEntry(3, GraphPropertyValue.FromInt64(1)),
            ]));

        byte[] payload = new byte[sizeof(int) + sizeof(long) + sizeof(int) + sizeof(int) + (sizeof(int) * 2)];
        BinaryPrimitives.WriteInt32LittleEndian(payload, 1);
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(12), 2);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(16), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(20), 9);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(24), 2);
        byte[] envelope = GraphRecordEnvelopeCodec.Encode(GraphRecordKind.Vertex, 1, payload);

        Assert.Throws<InvalidDataException>(() => GraphElementRecordCodec.DecodeVertex(envelope));
    }

    [Fact]
    public void HighWaterAndRequestMarker_WrongKindOrDigest_AreRejected()
    {
        byte[] highWater = GraphHighWaterCodec.Encode(GraphHighWaterKind.EdgeId, 42);
        Assert.Equal(42, GraphHighWaterCodec.Decode(highWater, GraphHighWaterKind.EdgeId));
        Assert.Equal(
            "01000000020000002A00000000000000",
            Convert.ToHexString(GraphRecordEnvelopeCodec.Decode(highWater).Payload));
        Assert.Equal(highWater, GraphHighWaterCodec.Encode(GraphHighWaterKind.EdgeId, 42));
        Assert.Throws<InvalidDataException>(() =>
            GraphHighWaterCodec.Decode(highWater, GraphHighWaterKind.VertexId));

        byte[] digest = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        Assert.Equal(digest, GraphTransactionRequestCodec.Decode(
            GraphTransactionRequestCodec.Encode(digest)));
        Assert.Throws<ArgumentException>(() => GraphTransactionRequestCodec.Encode([1, 2]));
    }

    [Fact]
    public void VertexRecord_MaximumEmptyStringProperties_DecodesWithLinearAllocation()
    {
        const int propertyCount = 16_384;
        GraphPropertyEntry[] properties = Enumerable.Range(1, propertyCount)
            .Select(static propertyId => new GraphPropertyEntry(
                propertyId,
                GraphPropertyValue.FromString(string.Empty)))
            .ToArray();
        byte[] encoded = GraphElementRecordCodec.EncodeVertex(new GraphVertexRecord(
            new GraphElementId(1),
            1,
            [],
            properties));

        long before = GC.GetAllocatedBytesForCurrentThread();
        GraphVertexRecord decoded = GraphElementRecordCodec.DecodeVertex(encoded);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(propertyCount, decoded.Properties.Count);
        Assert.True(allocated < 32L * 1024 * 1024, $"Graph record decode allocated {allocated} bytes.");
    }

    [Fact]
    public void PropertyScalar_AtIndexKeyLimitEncodesAndOneByteOverIsRejected()
    {
        int maximumTextBytes = GraphKeyCodec.MaxPropertyScalarBytes - 3;
        var record = new GraphVertexRecord(
            new GraphElementId(1),
            1,
            [new LabelId(1)],
            [new GraphPropertyEntry(1, GraphPropertyValue.FromString(new string('a', maximumTextBytes)))]);

        byte[] encoded = GraphElementRecordCodec.EncodeVertex(record);
        GraphVertexRecord decoded = GraphElementRecordCodec.DecodeVertex(encoded);

        Assert.Equal(maximumTextBytes, decoded.Properties[0].Value.AsString().Length);
        Assert.Equal(
            GraphKeyCodec.MaxEncodedKeyBytes,
            GraphKeyCodec.EncodePropertyIndex(
                GraphElementKind.Vertex,
                new LabelId(1),
                1,
                record.Properties[0].Value,
                record.Id).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphVertexRecord(
            new GraphElementId(2),
            1,
            [new LabelId(1)],
            [new GraphPropertyEntry(
                1,
                GraphPropertyValue.FromString(new string('a', maximumTextBytes + 1)))]));
    }

    [Fact]
    public void PropertyScalar_MultiMegabyteTextRejectsWithoutUtf8SizedTemporaryAllocation()
    {
        GraphPropertyValue value = GraphPropertyValue.FromString(new string('a', 2 * 1024 * 1024));
        long before = GC.GetAllocatedBytesForCurrentThread();

        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphVertexRecord(
            new GraphElementId(1),
            1,
            [new LabelId(1)],
            [new GraphPropertyEntry(1, value)]));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 256 * 1024, $"Oversized Graph text validation allocated {allocated:N0} bytes.");
    }
}
