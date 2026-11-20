using SonnetDB.Ingest;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Core.Tests.Ingest;

public sealed class JsonPointsReaderTests
{
    private static List<Point> ReadAll(JsonPointsReader r)
    {
        var list = new List<Point>();
        while (r.TryRead(out var p)) list.Add(p);
        return list;
    }

    [Fact]
    public void TryRead_TopLevelMeasurementAndTwoPoints_Parses()
    {
        const string json = """
        {
          "m": "cpu",
          "points": [
            {"t": 1, "tags": {"h": "a"}, "fields": {"v": 0.5}},
            {"t": 2, "tags": {"h": "b"}, "fields": {"v": 0.6, "ok": true, "msg": "hi"}}
          ]
        }
        """;
        using var r = new JsonPointsReader(json.AsMemory());
        var pts = ReadAll(r);
        Assert.Equal(2, pts.Count);
        Assert.Equal("cpu", pts[0].Measurement);
        Assert.Equal(1L, pts[0].Timestamp);
        Assert.Equal("a", pts[0].Tags["h"]);
        Assert.Equal(0.5, pts[0].Fields["v"].AsDouble());
        Assert.True(pts[1].Fields["ok"].AsBool());
        Assert.Equal("hi", pts[1].Fields["msg"].AsString());
    }

    [Fact]
    public void TryRead_PerPointMeasurement_OverridesTopLevel()
    {
        const string json = """{"m":"def","points":[{"t":1,"measurement":"override","fields":{"v":1}}]}""";
        using var r = new JsonPointsReader(json.AsMemory());
        var p = Assert.Single(ReadAll(r));
        Assert.Equal("override", p.Measurement);
    }

    [Fact]
    public void TryRead_MeasurementOverride_ForcesAll()
    {
        const string json = """{"m":"x","points":[{"t":1,"fields":{"v":1}}]}""";
        using var r = new JsonPointsReader(json.AsMemory(), measurementOverride: "forced");
        var p = Assert.Single(ReadAll(r));
        Assert.Equal("forced", p.Measurement);
    }

    [Fact]
    public void TryRead_NoFields_Throws()
    {
        const string json = """{"m":"cpu","points":[{"t":1,"tags":{"h":"a"}}]}""";
        using var r = new JsonPointsReader(json.AsMemory());
        Assert.Throws<BulkIngestException>(() => ReadAll(r));
    }

    [Fact]
    public void TryRead_NoPointsArray_ThrowsAtFirstRead()
    {
        const string json = """{"m":"cpu"}""";
        using var r = new JsonPointsReader(json.AsMemory());
        Assert.Throws<BulkIngestException>(() => r.TryRead(out _));
    }

    [Fact]
    public void TryRead_TopLevelNotObject_Throws()
    {
        using var r = new JsonPointsReader("[]".AsMemory());
        Assert.Throws<BulkIngestException>(() => r.TryRead(out _));
    }

    [Fact]
    public void TryRead_PrecisionSeconds_ConvertsToMs()
    {
        const string json = """{"m":"cpu","precision":"s","points":[{"t":1700000000,"fields":{"v":1}}]}""";
        using var r = new JsonPointsReader(json.AsMemory());
        var p = Assert.Single(ReadAll(r));
        Assert.Equal(1700000000_000L, p.Timestamp);
    }

    [Fact]
    public void TryRead_UnknownTopLevelKey_Skipped()
    {
        const string json = """{"unknown":{"a":1},"m":"cpu","points":[{"t":1,"fields":{"v":1}}]}""";
        using var r = new JsonPointsReader(json.AsMemory());
        Assert.Single(ReadAll(r));
    }

    [Fact]
    public void TryRead_IntegerFieldValue_ProducesLongFieldValue()
    {
        const string json = """{"m":"cpu","points":[{"t":1,"fields":{"v":42}}]}""";
        using var r = new JsonPointsReader(json.AsMemory());
        var p = Assert.Single(ReadAll(r));
        Assert.Equal(42L, p.Fields["v"].AsLong());
    }

    [Fact]
    public void TryRead_WhitespaceTagValue_UsesBulkErrorContract()
    {
        const string json = "{\"m\":\"cpu\",\"points\":[{\"t\":1,\"tags\":{\"host\":\"   \"},\"fields\":{\"v\":1}}]}";
        using var reader = new JsonPointsReader(json.AsMemory());

        var error = Assert.Throws<BulkIngestException>(() => reader.TryRead(out _));

        Assert.Contains("tag value", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_AfterBadObject_ContinuesAtNextObject()
    {
        const string json = "{\"m\":\"cpu\",\"points\":["
            + "{\"t\":1,\"tags\":{\"host\":\"\"},\"fields\":{\"v\":1}},"
            + "{\"t\":2,\"tags\":{\"host\":\"ok\"},\"fields\":{\"v\":2}}]}";
        using var reader = new JsonPointsReader(json.AsMemory());

        Assert.Throws<BulkIngestException>(() => reader.TryRead(out _));
        Assert.True(reader.TryRead(out var point));
        Assert.Equal(2L, point.Timestamp);
        Assert.False(reader.TryRead(out _));
    }
}
