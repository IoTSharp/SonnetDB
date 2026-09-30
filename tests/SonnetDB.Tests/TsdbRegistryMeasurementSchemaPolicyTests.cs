using Microsoft.Extensions.Configuration;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证 Server 配置的 measurement 策略适用于新建和重载的数据库。</summary>
public sealed class TsdbRegistryMeasurementSchemaPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-registry-measurement-policy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Registry_ConfiguredMeasurementModes_ApplyAfterCreateAndReload()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SonnetDBServer:MeasurementSchema:Mode"] = "CreateOnly",
            ["SonnetDBServer:MeasurementSchema:MeasurementModes:hot"] = "CreateAndEvolve",
            ["SonnetDBServer:MeasurementSchema:MeasurementModes:cold"] = "Disabled",
        }).Build();
        var policy = ServerOptionsBinder.Bind(configuration).MeasurementSchema.ToPolicy();

        using (var registry = new TsdbRegistry(_root, null, null, null, policy))
        {
            Assert.True(registry.TryCreate("configured", out Tsdb db));
            db.Write(MakePoint("hot", 1, "value"));
            db.Write(MakePoint("hot", 2, "later"));
            db.Write(MakePoint("normal", 1, "value"));
            Assert.Throws<InvalidOperationException>(() => db.Write(MakePoint("normal", 2, "later")));
            Assert.Throws<InvalidOperationException>(() => db.Write(MakePoint("cold", 1, "value")));
            Assert.NotNull(db.Measurements.TryGet("hot")!.TryGetColumn("later"));
            Assert.Null(db.Measurements.TryGet("normal")!.TryGetColumn("later"));
        }

        using (var registry = new TsdbRegistry(_root, null, null, null, policy))
        {
            registry.LoadExisting();
            Assert.True(registry.TryGet("configured", out Tsdb db));
            Assert.Throws<InvalidOperationException>(() => db.Write(MakePoint("normal", 3, "reloaded")));
            db.Write(MakePoint("hot", 3, "reloaded"));
            Assert.NotNull(db.Measurements.TryGet("hot")!.TryGetColumn("reloaded"));
        }
    }

    private static Point MakePoint(string measurement, long timestamp, string field)
        => SonnetDB.Model.Point.Create(measurement, timestamp, fields: new Dictionary<string, FieldValue>
        {
            [field] = FieldValue.FromLong(timestamp),
        });

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }
}
