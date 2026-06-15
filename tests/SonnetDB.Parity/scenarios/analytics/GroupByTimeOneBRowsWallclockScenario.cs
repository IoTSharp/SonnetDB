using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Analytics;

/// <summary>
/// 大规模时间桶聚合墙钟时间场景。默认数据集保持本地可跑，环境变量可放大到 long-soak。
/// </summary>
public sealed class GroupByTimeOneBRowsWallclockScenario : AnalyticsScenarioBase
{
    /// <inheritdoc />
    public override string Name => "groupby_time_1b_rows_wallclock";

    /// <inheritdoc />
    public override Capability Required => Capability.Analytics | Capability.AnalyticsGroupByTime;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(1e-9, 1e-9);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunAnalyticsAsync(IAnalyticalOps ops, ScenarioContext ctx)
    {
        var dataset = Dataset(ctx, "groupby");
        int days = EnvInt("PARITY_ANALYTICS_GROUPBY_DAYS", 120);
        int devices = EnvInt("PARITY_ANALYTICS_GROUPBY_DEVICES", 8);
        var rows = BuildRows(dataset, days, devices);
        await ops.IngestAsync(rows, ctx.Cancellation).ConfigureAwait(false);

        var started = DateTimeOffset.UtcNow;
        var result = await ops.GroupByTimeAverageAsync(dataset, TimeSpan.FromDays(1), ctx.Cancellation).ConfigureAwait(false);
        var scenario = FromResult(result);
        scenario.Metrics["input_rows"] = rows.Count;
        scenario.Metrics["wallclock_ms"] = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        scenario.Metrics["performance_gating"] = "warning_only";
        return scenario;
    }
}
