using System.Text.Json;
using SonnetDB.Graphs;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphPropertyValueTests
{
    [Fact]
    public void GraphElementId_WithNonPositiveValue_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphElementId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphElementId(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LabelId(0));
    }

    [Fact]
    public void FromBlob_InputAndOutputAreCopied()
    {
        byte[] source = [1, 2, 3];
        GraphPropertyValue value = GraphPropertyValue.FromBlob(source);
        source[0] = 9;

        byte[] first = value.AsBlob();
        first[1] = 9;

        Assert.Equal([1, 2, 3], value.AsBlob());
    }

    [Fact]
    public void FromJson_InvalidJson_Throws()
        => Assert.ThrowsAny<JsonException>(() => GraphPropertyValue.FromJson("{"));

    [Fact]
    public void TextFactories_UnpairedSurrogate_ThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => GraphPropertyValue.FromString("\uD800"));
        Assert.Throws<ArgumentException>(() => GraphPropertyValue.FromJson("\"\uD800\""));
    }

    [Fact]
    public void TypedFactories_AllKindsRoundTrip()
    {
        DateTimeOffset timestamp = DateTimeOffset.FromUnixTimeMilliseconds(-1234);
        GraphPropertyValue[] values =
        [
            GraphPropertyValue.Null,
            GraphPropertyValue.FromInt64(long.MinValue),
            GraphPropertyValue.FromFloat64(-12.5),
            GraphPropertyValue.FromBoolean(true),
            GraphPropertyValue.FromString("sonnet"),
            GraphPropertyValue.FromDateTime(timestamp),
            GraphPropertyValue.FromBlob([0, 1, 255]),
            GraphPropertyValue.FromJson("{\"ok\":true}"),
        ];

        Assert.Equal(GraphPropertyKind.Null, values[0].Kind);
        Assert.Equal(long.MinValue, values[1].AsInt64());
        Assert.Equal(-12.5, values[2].AsFloat64());
        Assert.True(values[3].AsBoolean());
        Assert.Equal("sonnet", values[4].AsString());
        Assert.Equal(timestamp, values[5].AsDateTime());
        Assert.Equal([0, 1, 255], values[6].AsBlob());
        Assert.Equal("{\"ok\":true}", values[7].AsJson());
    }

    [Fact]
    public void AsInt64_ForDifferentKind_Throws()
        => Assert.Throws<InvalidOperationException>(() => GraphPropertyValue.FromBoolean(true).AsInt64());
}
