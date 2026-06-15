using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// TSDB 写入吞吐冒烟场景：默认轻量规模，可通过 <c>PARITY_TSDB_INGEST_POINTS=1000000</c>
/// 跑完整 1M 点。
/// </summary>
public sealed class IngestOneMillionPointsScenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "ingest_1m_points";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesRemoteWrite;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var count = EnvInt("PARITY_TSDB_INGEST_POINTS", 10_000);
        var measurement = Measurement(ctx, "ingest");
        var points = BuildLinearSeries(measurement, count, start: 1d, step: 1d);

        var started = DateTimeOffset.UtcNow;
        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - started;
        var result = await ops.CountAsync(measurement, ctx.Cancellation).ConfigureAwait(false);

        var scenario = FromResult(result);
        scenario.Pass = result.Rows.Count == 1 && Convert.ToInt64(result.Rows[0].Values[0], System.Globalization.CultureInfo.InvariantCulture) == count;
        scenario.Metrics["points"] = count;
        scenario.Metrics["elapsed_ms"] = elapsed.TotalMilliseconds;
        scenario.Metrics["points_per_second"] = elapsed.TotalSeconds <= 0 ? 0 : count / elapsed.TotalSeconds;
        return scenario;
    }
}
