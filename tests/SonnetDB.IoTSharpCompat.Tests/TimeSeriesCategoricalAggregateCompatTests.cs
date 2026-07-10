namespace SonnetDB.IoTSharpCompat.Tests;

using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

/// <summary>
/// IoTSharp 字符串遥测在 SonnetDB storage profile 下的 selector 聚合兼容验收。
/// </summary>
public sealed class TimeSeriesCategoricalAggregateCompatTests : IDisposable
{
    private readonly string _root;

    public TimeSeriesCategoricalAggregateCompatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-iotsharp-selector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void StringTelemetry_FirstBySixtySecondBucket_ReturnsIoTSharpStatusValues()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT ST_DeviceVal (deviceId TAG, str_val FIELD STRING)");
        SqlExecutor.Execute(db,
            "INSERT INTO ST_DeviceVal (time, deviceId, str_val) VALUES " +
            "(50000, 'xx', 'running'), (10000, 'xx', 'idle'), " +
            "(90000, 'xx', 'alarm'), (70000, 'xx', 'running')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT time, first(str_val) FROM ST_DeviceVal " +
            "WHERE deviceId='xx' AND time >= 0 AND time <= 119999 GROUP BY time(60s)"));

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal([0L, "idle"], result.Rows[0]);
        Assert.Equal([60000L, "running"], result.Rows[1]);
    }
}
