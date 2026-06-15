using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// p95 分位数场景：SonnetDB t-digest / InfluxDB estimate_tdigest / PromQL quantile 对齐。
/// </summary>
public sealed class PercentileP95Scenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "percentile_p95_tdigest_vs_quantile";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesQuantile;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(0.02, 2d);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var measurement = Measurement(ctx, "p95");
        var startMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var points = new List<TsdbPoint>(1_000);
        for (var i = 0; i < 1_000; i++)
        {
            var value = (i % 100) + (i % 7) * 0.01d;
            points.Add(new TsdbPoint(measurement, startMs + i * 1_000L, "device_000", "cn", value));
        }

        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.PercentileP95Async(measurement, ctx.Cancellation).ConfigureAwait(false));
    }
}
