using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// Holt-Winters 预测召回场景。VictoriaMetrics 无内置同口径 forecast，按能力缺口 SKIP。
/// </summary>
public sealed class HoltWintersForecastRecallScenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "holt_winters_forecast_recall";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesHoltWinters;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(0.20, 5d);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var measurement = Measurement(ctx, "holt");
        var startMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var points = new List<TsdbPoint>(80);
        for (var i = 0; i < 80; i++)
        {
            var seasonal = Math.Sin(i / 10d * Math.PI * 2d) * 3d;
            points.Add(new TsdbPoint(measurement, startMs + i * 1_000L, "device_000", "cn", 100d + i * 0.5d + seasonal));
        }

        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.HoltWintersForecastAsync(measurement, 6, ctx.Cancellation).ConfigureAwait(false));
    }
}
