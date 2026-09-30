using SonnetDB.Ingest;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Core.Tests.Ingest;

public sealed class BulkValuesReaderTests
{
    private static BulkValuesColumnRole Resolver(string col) => col switch
    {
        "host" => BulkValuesColumnRole.Tag,
        "value" or "ok" or "msg" => BulkValuesColumnRole.Field,
        _ => throw new BulkIngestException($"未知列 {col}"),
    };

    [Fact]
    public void TryRead_HeaderAndRows_Parses()
    {
        const string sql = "INSERT INTO cpu(host, value, time) VALUES "
            + "('a', 0.5, 1700000000000),"
            + "('b', 0.6, 1700000000001);";
        var r = new BulkValuesReader(sql, Resolver);
        Assert.Equal("cpu", r.Measurement);
        Assert.Equal(new[] { "host", "value", "time" }, r.Columns);

        Assert.True(r.TryRead(out var p1));
        Assert.Equal("a", p1.Tags["host"]);
        Assert.Equal(0.5, p1.Fields["value"].AsDouble());
        Assert.Equal(1700000000000L, p1.Timestamp);

        Assert.True(r.TryRead(out var p2));
        Assert.Equal("b", p2.Tags["host"]);
        Assert.Equal(1700000000001L, p2.Timestamp);

        Assert.False(r.TryRead(out _));
    }

    [Fact]
    public void TryRead_AllFieldKinds_Parsed()
    {
        const string sql = "INSERT INTO cpu(host, value, ok, msg, time) VALUES ('a', 1.5, true, 'hi''o', 10)";
        var r = new BulkValuesReader(sql, Resolver);
        Assert.True(r.TryRead(out var p));
        Assert.Equal(1.5, p.Fields["value"].AsDouble());
        Assert.True(p.Fields["ok"].AsBool());
        Assert.Equal("hi'o", p.Fields["msg"].AsString());
        Assert.Equal(10L, p.Timestamp);
    }

    [Fact]
    public void TryRead_UnknownString_DefaultsToFieldUnlessExplicitlyTagged()
    {
        const string sql = "INSERT INTO cpu(host TAG, request_id, time) VALUES ('server', 'unique-001', 1)";
        var reader = new BulkValuesReader(sql, static _ => BulkValuesColumnRole.Auto);

        Assert.True(reader.TryRead(out var point));
        Assert.Equal("server", point.Tags["host"]);
        Assert.Equal("unique-001", point.Fields["request_id"].AsString());
    }

    [Fact]
    public void Ctor_ExistingRoleConflictsWithHint_Throws()
    {
        const string sql = "INSERT INTO cpu(host FIELD, value) VALUES ('server', 1)";

        var error = Assert.Throws<BulkIngestException>(() => new BulkValuesReader(sql, Resolver));
        Assert.Contains("schema", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_QuotedIdentifierMeasurement_Supported()
    {
        const string sql = "INSERT INTO \"my m\"(host, value, time) VALUES ('a', 1, 1)";
        var r = new BulkValuesReader(sql, Resolver);
        Assert.Equal("my m", r.Measurement);
        Assert.True(r.TryRead(out _));
    }

    [Fact]
    public void Ctor_DuplicateColumn_Throws()
    {
        const string sql = "INSERT INTO cpu(host, host, time) VALUES ('a', 'b', 1)";
        Assert.Throws<BulkIngestException>(() => new BulkValuesReader(sql, Resolver));
    }

    [Fact]
    public void TryRead_MissingFieldValue_Throws()
    {
        // 列声明只有 host + time → 没有 field 列
        const string sql = "INSERT INTO cpu(host, time) VALUES ('a', 1)";
        var r = new BulkValuesReader(sql, Resolver);
        Assert.Throws<BulkIngestException>(() => r.TryRead(out _));
    }

    [Fact]
    public void TryRead_TagNotString_Throws()
    {
        const string sql = "INSERT INTO cpu(host, value, time) VALUES (1, 1.0, 1)";
        var r = new BulkValuesReader(sql, Resolver);
        Assert.Throws<BulkIngestException>(() => r.TryRead(out _));
    }

    [Fact]
    public void Ctor_MeasurementOverride_TakesEffect()
    {
        const string sql = "INSERT INTO ignored(host, value, time) VALUES ('a', 1, 1)";
        var r = new BulkValuesReader(sql, Resolver, measurementOverride: "forced");
        Assert.Equal("forced", r.Measurement);
        Assert.True(r.TryRead(out var p));
        Assert.Equal("forced", p.Measurement);
    }

    [Fact]
    public void TryRead_NoSemicolon_StillTerminatesAfterLastRow()
    {
        const string sql = "INSERT INTO cpu(host, value, time) VALUES ('a',1,1),('b',2,2)";
        var r = new BulkValuesReader(sql, Resolver);
        Assert.True(r.TryRead(out _));
        Assert.True(r.TryRead(out _));
        Assert.False(r.TryRead(out _));
    }
}
