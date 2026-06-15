using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// rate / irate 一致性场景。单调计数器每秒固定增长，三种后端均应得到相同速率。
/// </summary>
public sealed class RateIrateConsistencyScenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "rate_irate_consistency";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesRateIrate;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(1e-6, 1e-6);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var measurement = Measurement(ctx, "rate");
        var points = BuildLinearSeries(measurement, 30, start: 100d, step: 5d);
        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.RateIrateAsync(measurement, ctx.Cancellation).ConfigureAwait(false));
    }
}
