using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// distinct_count / HyperLogLog 2% 相对误差场景。
/// </summary>
public sealed class DistinctCountHllScenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "distinct_count_hll_2pct_error";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesDistinctCount;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(0.02, 1d);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var measurement = Measurement(ctx, "hll");
        var startMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var points = new List<TsdbPoint>(2_000);
        for (var i = 0; i < 2_000; i++)
        {
            var deviceId = i % 500;
            points.Add(new TsdbPoint(
                measurement,
                startMs + i * 1_000L,
                "device_" + deviceId.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                "cn",
                deviceId));
        }

        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        var result = await ops.DistinctDeviceCountAsync(measurement, ctx.Cancellation).ConfigureAwait(false);
        var scenario = FromResult(result);
        scenario.Metrics["expected_cardinality"] = 500;
        return scenario;
    }
}
