using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Analytics;

/// <summary>
/// 7 点移动平均窗口对齐场景：SonnetDB 使用 moving_average，ClickHouse 使用 SQL window。
/// </summary>
public sealed class WindowAvg7DayScenario : AnalyticsScenarioBase
{
    /// <inheritdoc />
    public override string Name => "window_avg_7day";

    /// <inheritdoc />
    public override Capability Required => Capability.Analytics | Capability.SqlWindowFunction;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunAnalyticsAsync(IAnalyticalOps ops, ScenarioContext ctx)
    {
        var dataset = Dataset(ctx, "window");
        var rows = BuildRows(dataset, 28, 1);
        await ops.IngestAsync(rows, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.WindowAverage7DayAsync(dataset, ctx.Cancellation).ConfigureAwait(false));
    }
}
