using SonnetDB.Ingest;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Core.Tests.Ingest;

public sealed class LineProtocolReaderTests
{
    private static List<Point> ReadAll(LineProtocolReader r)
    {
        var list = new List<Point>();
        while (r.TryRead(out var p)) list.Add(p);
        return list;
    }

    [Fact]
    public void TryRead_SingleLineWithTagsAndDoubleField_Parses()
    {
        var r = new LineProtocolReader("cpu,host=srv1 value=0.5 1700000000000".AsMemory());
        var points = ReadAll(r);
        var p = Assert.Single(points);
        Assert.Equal("cpu", p.Measurement);
        Assert.Equal(1700000000000L, p.Timestamp);
        Assert.Equal("srv1", p.Tags["host"]);
        Assert.Equal(0.5, p.Fields["value"].AsDouble());
    }

    [Fact]
    public void TryRead_IntegerSuffixIBoolStringFields_Parses()
    {
        const string lp = "m,h=a a=1i,b=t,c=\"hi\\\"there\" 1700000000000";
        var r = new LineProtocolReader(lp.AsMemory());
        var p = Assert.Single(ReadAll(r));
        Assert.Equal(1L, p.Fields["a"].AsLong());
        Assert.True(p.Fields["b"].AsBool());
        Assert.Equal("hi\"there", p.Fields["c"].AsString());
    }

    [Fact]
    public void TryRead_EscapedSpaceInMeasurementAndTagValue_Decoded()
    {
        // 注：SonnetDB Point 不允许 tag value 含逗号/等号等保留字符，但允许空格。
        const string lp = "m\\ x,h=a\\ b value=1 1700000000000";
        var r = new LineProtocolReader(lp.AsMemory());
        var p = Assert.Single(ReadAll(r));
        Assert.Equal("m x", p.Measurement);
        Assert.Equal("a b", p.Tags["h"]);
    }

    [Fact]
    public void TryRead_NoTimestamp_UsesDefault()
    {
        var r = new LineProtocolReader("cpu value=1".AsMemory()) { DefaultTimestampMs = 12345L };
        var p = Assert.Single(ReadAll(r));
        Assert.Equal(12345L, p.Timestamp);
    }

    [Fact]
    public void TryRead_NanoPrecision_ConvertsToMs()
    {
        // 1700000000000_000_000 ns = 1700000000000 ms
        var r = new LineProtocolReader("cpu value=1 1700000000000000000".AsMemory(), TimePrecision.Nanoseconds);
        var p = Assert.Single(ReadAll(r));
        Assert.Equal(1700000000000L, p.Timestamp);
    }

    [Fact]
    public void TryRead_SkipsBlankAndCommentLines()
    {
        const string lp = "\n# comment\ncpu value=1 1\n  \n";
        var r = new LineProtocolReader(lp.AsMemory());
        var p = Assert.Single(ReadAll(r));
        Assert.Equal("cpu", p.Measurement);
    }

    [Fact]
    public void TryRead_MultipleLines_ProducesAllPoints()
    {
        const string lp = "cpu value=1 1\ncpu value=2 2\ncpu value=3 3";
        var pts = ReadAll(new LineProtocolReader(lp.AsMemory()));
        Assert.Equal(3, pts.Count);
        Assert.Equal(new long[] { 1, 2, 3 }, pts.ConvertAll(p => p.Timestamp).ToArray());
    }

    [Fact]
    public void TryRead_MissingFields_Throws()
    {
        var r = new LineProtocolReader("cpu,h=a 1700000000000".AsMemory());
        Assert.Throws<BulkIngestException>(() => ReadAll(r));
    }

    [Fact]
    public void TryRead_MeasurementOverride_AppliedToAllPoints()
    {
        const string lp = "ignored value=1 1\nalso_ignored value=2 2";
        var r = new LineProtocolReader(lp.AsMemory(), measurementOverride: "forced");
        foreach (var p in ReadAll(r))
            Assert.Equal("forced", p.Measurement);
    }

    [Fact]
    public void TryRead_WhitespaceTagValue_UsesBulkErrorContract()
    {
        var reader = new LineProtocolReader("cpu,host=\\ \\ \\  value=1 1".AsMemory());

        var error = Assert.Throws<BulkIngestException>(() => reader.TryRead(out _));

        Assert.Contains("tag value", error.Message, StringComparison.Ordinal);
    }
}
