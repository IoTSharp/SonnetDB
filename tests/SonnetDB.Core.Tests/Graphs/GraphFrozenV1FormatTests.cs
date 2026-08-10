using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Storage.Codecs;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphFrozenV1FormatTests
{
    [Fact]
    public void VertexRecord_WithCanonicalizedCollections_HasFrozenV1BytesAndRoundTrips()
    {
        const string ExpectedHex =
            "5344424752454331010000001C000100090000000000000033000000" +
            "01000000080706050403020102000000020000000200000007000000" +
            "03000000017FFFFFFFFFFFFFFE0B000000047600FF0000DC6B742B";
        var record = new GraphVertexRecord(
            new GraphElementId(0x0102_0304_0506_0708),
            9,
            [new LabelId(7), new LabelId(2)],
            [
                new GraphPropertyEntry(11, GraphPropertyValue.FromString("v\0")),
                new GraphPropertyEntry(3, GraphPropertyValue.FromInt64(-2)),
            ]);

        Assert.Equal(ExpectedHex, Convert.ToHexString(GraphElementRecordCodec.EncodeVertex(record)));

        GraphVertexRecord decoded = GraphElementRecordCodec.DecodeVertex(
            Convert.FromHexString(ExpectedHex));
        Assert.Equal(record.Id, decoded.Id);
        Assert.Equal(record.ElementVersion, decoded.ElementVersion);
        Assert.Equal([2, 7], decoded.Labels.Select(static label => label.Value));
        Assert.Equal([3, 11], decoded.Properties.Select(static property => property.PropertyId));
        Assert.Equal(GraphPropertyValue.FromInt64(-2), decoded.Properties[0].Value);
        Assert.Equal(GraphPropertyValue.FromString("v\0"), decoded.Properties[1].Value);
        Assert.Equal(ExpectedHex, Convert.ToHexString(GraphElementRecordCodec.EncodeVertex(decoded)));
    }

    [Fact]
    public void EdgeRecord_WithBooleanProperty_HasFrozenV1BytesAndRoundTrips()
    {
        const string ExpectedHex =
            "5344424752454331010000001C00020006000000000000002A000000" +
            "01000000110000000000000003000000000000000500000000000000" +
            "0700000001000000020000000300E68FC269";
        var record = new GraphEdgeRecord(
            new GraphElementId(17),
            6,
            new GraphElementId(3),
            new GraphElementId(5),
            new LabelId(7),
            [new GraphPropertyEntry(2, GraphPropertyValue.FromBoolean(false))]);

        Assert.Equal(ExpectedHex, Convert.ToHexString(GraphElementRecordCodec.EncodeEdge(record)));

        GraphEdgeRecord decoded = GraphElementRecordCodec.DecodeEdge(
            Convert.FromHexString(ExpectedHex));
        Assert.Equal(record.Id, decoded.Id);
        Assert.Equal(record.ElementVersion, decoded.ElementVersion);
        Assert.Equal(record.SourceId, decoded.SourceId);
        Assert.Equal(record.TargetId, decoded.TargetId);
        Assert.Equal(record.LabelId, decoded.LabelId);
        GraphPropertyEntry property = Assert.Single(decoded.Properties);
        Assert.Equal(2, property.PropertyId);
        Assert.Equal(GraphPropertyValue.FromBoolean(false), property.Value);
        Assert.Equal(ExpectedHex, Convert.ToHexString(GraphElementRecordCodec.EncodeEdge(decoded)));
    }

    [Fact]
    public void SortableScalar_AllKinds_HaveFrozenV1BytesAndRoundTrip()
    {
        (string Hex, GraphPropertyValue Value)[] vectors =
        [
            ("00", GraphPropertyValue.Null),
            ("017FFFFFFFFFFFFFFE", GraphPropertyValue.FromInt64(-2)),
            ("023FD6FFFFFFFFFFFF", GraphPropertyValue.FromFloat64(-12.5)),
            ("0301", GraphPropertyValue.FromBoolean(true)),
            ("044100FFE4B8AD0000", GraphPropertyValue.FromString("A\0中")),
            ("057FFFFFFFFFFFFFFF", GraphPropertyValue.FromDateTime(
                DateTimeOffset.UnixEpoch.AddMilliseconds(-1))),
            ("0600FF01FF0000", GraphPropertyValue.FromBlob([0, 1, 255])),
            ("075B302C2278225D0000", GraphPropertyValue.FromJson("[0,\"x\"]")),
        ];
        Assert.Equal(Enum.GetValues<GraphPropertyKind>().Length, vectors.Length);

        foreach ((string expectedHex, GraphPropertyValue expected) in vectors)
        {
            Assert.Equal(expectedHex, Convert.ToHexString(SortableScalarCodec.EncodeGraph(expected)));

            byte[] frozen = Convert.FromHexString(expectedHex);
            GraphPropertyValue decoded = SortableScalarCodec.DecodeGraph(frozen, out int consumed);
            Assert.Equal(frozen.Length, consumed);
            Assert.Equal(expected, decoded);
            Assert.Equal(expectedHex, Convert.ToHexString(SortableScalarCodec.EncodeGraph(decoded)));
        }
    }

    [Fact]
    public void MetadataValues_AllDiscriminators_HaveFrozenV1BytesAndRoundTrip()
    {
        const long Value = 0x0102_0304_0506_0708;
        (GraphHighWaterKind Kind, string Hex)[] highWaterVectors =
        [
            (GraphHighWaterKind.VertexId,
                "5344424752454331010000001C000300000000000000000010000000010000000100000008070605040302015B6A2FC4"),
            (GraphHighWaterKind.EdgeId,
                "5344424752454331010000001C00030000000000000000001000000001000000020000000807060504030201ABB8B1B3"),
            (GraphHighWaterKind.LabelId,
                "5344424752454331010000001C00030000000000000000001000000001000000030000000807060504030201C4F41428"),
            (GraphHighWaterKind.PropertyId,
                "5344424752454331010000001C000300000000000000000010000000010000000400000008070605040302014B1D8C5C"),
        ];
        Assert.Equal(Enum.GetValues<GraphHighWaterKind>().Length, highWaterVectors.Length);
        foreach ((GraphHighWaterKind kind, string expectedHex) in highWaterVectors)
        {
            Assert.Equal(expectedHex, Convert.ToHexString(GraphHighWaterCodec.Encode(kind, Value)));
            Assert.Equal(Value, GraphHighWaterCodec.Decode(Convert.FromHexString(expectedHex), kind));
        }

        byte[] digest = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        const string RequestHex =
            "5344424752454331010000001C00030000000000000000002400000001000000" +
            "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F42342FBE";
        Assert.Equal(RequestHex, Convert.ToHexString(GraphTransactionRequestCodec.Encode(digest)));
        Assert.Equal(digest, GraphTransactionRequestCodec.Decode(Convert.FromHexString(RequestHex)));

        (GraphElementKind Kind, string Hex)[] ownerVectors =
        [
            (GraphElementKind.Vertex,
                "5344424752454331010000001C0004000000000000000000100000000100000001000000080706050403020163C30D71"),
            (GraphElementKind.Edge,
                "5344424752454331010000001C0004000000000000000000100000000100000002000000080706050403020193119306"),
        ];
        Assert.Equal(Enum.GetValues<GraphElementKind>().Length, ownerVectors.Length);
        foreach ((GraphElementKind kind, string expectedHex) in ownerVectors)
        {
            var ownerId = new GraphElementId(Value);
            Assert.Equal(
                expectedHex,
                Convert.ToHexString(GraphUniquePropertyOwnerCodec.Encode(kind, ownerId)));
            Assert.Equal(
                ownerId,
                GraphUniquePropertyOwnerCodec.Decode(Convert.FromHexString(expectedHex), kind));
        }
    }
}
