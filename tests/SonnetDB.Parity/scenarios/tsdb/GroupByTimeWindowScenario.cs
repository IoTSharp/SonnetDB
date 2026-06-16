using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// <c>GROUP BY time</c> / Flux aggregateWindow / PromQL range window 的平均值对齐场景。
/// </summary>
public sealed class GroupByTimeWindowScenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "groupby_time_window";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesGroupByTime;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(1e-9, 1e-9) { TimeBucketMs = 60_000L };

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var measurement = Measurement(ctx, "groupby");
        var points = BuildLinearSeries(measurement, 120, start: 0d, step: 1d);
        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.GroupByTimeAverageAsync(measurement, TimeSpan.FromMinutes(1), ctx.Cancellation).ConfigureAwait(false));
    }
}
