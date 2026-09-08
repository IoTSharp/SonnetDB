using SonnetDB.Engine;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

/// <summary>Measurement 原生批次身份和重开幂等合同。</summary>
public sealed class TsdbMeasurementBatchIdempotencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public TsdbMeasurementBatchIdempotencyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root, BackgroundFlush = new() { Enabled = false } };

    [Fact]
    public void WriteMany_WithBatchIdentity_ReplayAndReopenAreIdempotent()
    {
        var points = Enumerable.Range(0, 3).Select(i => Point.Create(
            "cpu", i, new Dictionary<string, string> { ["host"] = "a" },
            new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromLong(i) })).ToArray();

        using (var db = Tsdb.Open(Options()))
        {
            Assert.Equal(3, db.WriteMany(points, "capture-001"));
            Assert.Equal(0, db.WriteMany(points, "capture-001"));
        }

        using var reopened = Tsdb.Open(Options());
        Assert.Equal(0, reopened.WriteMany(points, "capture-001"));
        var series = reopened.Catalog.Snapshot().Single();
        Assert.Equal(3, reopened.Query.Execute(new SonnetDB.Query.PointQuery(
            series.Id, "usage", new SonnetDB.Query.TimeRange(0, long.MaxValue))).Count());
    }

    [Fact]
    public void WriteMany_WithSameBatchIdentityAndDifferentPayload_Rejects()
    {
        using var db = Tsdb.Open(Options());
        var first = Point.Create("cpu", 1, fields: new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromLong(1) });
        var second = Point.Create("cpu", 1, fields: new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromLong(2) });
        db.WriteMany(new[] { first }, "capture-002");
        Assert.Throws<InvalidOperationException>(() => db.WriteMany(new[] { second }, "capture-002"));
    }
}
