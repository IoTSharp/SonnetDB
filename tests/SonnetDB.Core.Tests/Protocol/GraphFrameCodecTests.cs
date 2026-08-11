using System.Buffers;
using SonnetDB.Graphs;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

public sealed class GraphFrameCodecTests
{
    [Fact]
    public void ExpandRequest_AllOptions_RoundTrips()
    {
        var writer = new ArrayBufferWriter<byte>();
        GraphFrameCodec.EncodeExpandRequest(
            writer,
            42,
            "demo",
            "code",
            new GraphElementId(7),
            GraphDirection.Incoming,
            new LabelId(3),
            pageSize: 17,
            maxResults: 99);

        ReadOnlyMemory<byte> payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Graph, header.Service);
        Assert.Equal((byte)GraphFrameOp.Expand, header.Op);
        Assert.Equal(42u, header.StreamId);
        Assert.False(header.IsResponse);
        GraphExpandFrameRequest request = GraphFrameCodec.DecodeExpandRequest(payload.Span);
        Assert.Equal("demo", request.Database);
        Assert.Equal("code", request.Graph);
        Assert.Equal(new GraphElementId(7), request.VertexId);
        Assert.Equal(GraphDirection.Incoming, request.Direction);
        Assert.Equal(new LabelId(3), request.EdgeLabelId);
        Assert.Equal(17, request.PageSize);
        Assert.Equal(99, request.MaxResults);
    }

    [Fact]
    public void ExpandResponse_MetaRowEnd_RoundTripsTypedProperties()
    {
        var edge = new GraphEdge(
            new GraphElementId(10),
            2,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(3),
            [new GraphProperty(4, GraphPropertyValue.FromString("calls"))]);
        var expansion = new GraphExpansion(
            new GraphElementId(1),
            new GraphElementId(2),
            GraphDirection.Outgoing,
            edge);
        var writer = new ArrayBufferWriter<byte>();
        GraphFrameCodec.EncodeExpandMetaFrame(writer, 9, 123);
        GraphFrameCodec.EncodeExpandRowFrame(writer, 9, expansion);
        GraphFrameCodec.EncodeExpandEndFrame(writer, 9, 1);

        IReadOnlyList<(FrameHeader Header, byte[] Payload)> frames = ParseFrames(writer.WrittenMemory);
        Assert.Equal(3, frames.Count);
        Assert.All(frames, static frame =>
        {
            Assert.Equal((byte)FrameService.Graph, frame.Header.Service);
            Assert.Equal((byte)GraphFrameOp.Expand, frame.Header.Op);
            Assert.True(frame.Header.IsResponse);
            Assert.Equal(9u, frame.Header.StreamId);
        });
        Assert.Equal(123, GraphFrameCodec.DecodeExpandMetaFrame(frames[0].Payload));
        GraphExpansion decoded = GraphFrameCodec.DecodeExpandRowFrame(frames[1].Payload);
        Assert.Equal(expansion.AnchorId, decoded.AnchorId);
        Assert.Equal(expansion.NeighborId, decoded.NeighborId);
        Assert.Equal("calls", Assert.Single(decoded.Edge.Properties).Value.AsString());
        Assert.Equal(1, GraphFrameCodec.DecodeExpandEndFrame(frames[2].Payload));
    }

    [Fact]
    public void ExpandRequest_InvalidBudgetOrTrailingBytes_IsRejected()
    {
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentOutOfRangeException>(() => GraphFrameCodec.EncodeExpandRequest(
            writer,
            1,
            "db",
            "graph",
            new GraphElementId(1),
            pageSize: 0));

        GraphFrameCodec.EncodeExpandRequest(
            writer,
            1,
            "db",
            "graph",
            new GraphElementId(1));
        byte[] payload = ParseSingleFrame(writer, out _).ToArray();
        Assert.Throws<FrameFormatException>(() => GraphFrameCodec.DecodeExpandRequest([.. payload, 0]));
        payload.AsSpan(9, sizeof(long)).Clear();
        Assert.Throws<FrameFormatException>(() => GraphFrameCodec.DecodeExpandRequest(payload));

        var edge = new GraphEdge(
            new GraphElementId(2),
            1,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(3),
            []);
        writer = new ArrayBufferWriter<byte>();
        GraphFrameCodec.EncodeExpandRowFrame(
            writer,
            1,
            new GraphExpansion(new GraphElementId(1), new GraphElementId(2), GraphDirection.Outgoing, edge));
        byte[] rowPayload = ParseSingleFrame(writer, out _).ToArray();
        rowPayload.AsSpan(1, sizeof(long)).Clear();
        Assert.Throws<FrameFormatException>(() => GraphFrameCodec.DecodeExpandRowFrame(rowPayload));
    }

    private static ReadOnlyMemory<byte> ParseSingleFrame(
        ArrayBufferWriter<byte> writer,
        out FrameHeader header)
    {
        var sequence = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref sequence, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, sequence.Length);
        return payload.ToArray();
    }

    private static IReadOnlyList<(FrameHeader Header, byte[] Payload)> ParseFrames(
        ReadOnlyMemory<byte> encoded)
    {
        var result = new List<(FrameHeader, byte[])>();
        var sequence = new ReadOnlySequence<byte>(encoded);
        while (FrameCodec.TryReadFrame(ref sequence, out FrameHeader header, out ReadOnlySequence<byte> payload))
            result.Add((header, payload.ToArray()));
        Assert.Equal(0, sequence.Length);
        return result;
    }
}
