using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Analytics;

/// <summary>
/// p50 / p95 / p99 聚合精度对齐场景。
/// </summary>
public sealed class PercentileAccuracyP50P95P99Scenario : AnalyticsScenarioBase
{
    /// <inheritdoc />
    public override string Name => "percentile_accuracy_p50_p95_p99";

    /// <inheritdoc />
    public override Capability Required => Capability.Analytics | Capability.AccuracyPercentile;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(0.02, 2d);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunAnalyticsAsync(IAnalyticalOps ops, ScenarioContext ctx)
    {
        var dataset = Dataset(ctx, "percentile");
        var rows = BuildRows(dataset, 50, 20);
        await ops.IngestAsync(rows, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.PercentilesAsync(dataset, ctx.Cancellation).ConfigureAwait(false));
    }
}
