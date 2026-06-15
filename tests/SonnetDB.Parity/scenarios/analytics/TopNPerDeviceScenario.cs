using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Analytics;

/// <summary>
/// 按设备聚合 Top-N 场景。
/// </summary>
public sealed class TopNPerDeviceScenario : AnalyticsScenarioBase
{
    /// <inheritdoc />
    public override string Name => "topn_per_device";

    /// <inheritdoc />
    public override Capability Required => Capability.Analytics | Capability.AnalyticsTopN;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunAnalyticsAsync(IAnalyticalOps ops, ScenarioContext ctx)
    {
        var dataset = Dataset(ctx, "topn");
        var rows = BuildRows(dataset, 14, 12);
        await ops.IngestAsync(rows, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.TopNPerDeviceAsync(dataset, 5, ctx.Cancellation).ConfigureAwait(false));
    }
}
