using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Analytics;

/// <summary>
/// 列式压缩率指标场景。该场景用于报告指标，不作为吞吐或压缩性能红绿门槛。
/// </summary>
public sealed class ColumnarCompressionRatioScenario : AnalyticsScenarioBase
{
    /// <inheritdoc />
    public override string Name => "columnar_compression_ratio";

    /// <inheritdoc />
    public override Capability Required => Capability.Analytics | Capability.AnalyticsCompressionRatio;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(10d, double.MaxValue);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunAnalyticsAsync(IAnalyticalOps ops, ScenarioContext ctx)
    {
        var dataset = Dataset(ctx, "compression");
        var rows = BuildRows(dataset, 30, 16);
        await ops.IngestAsync(rows, ctx.Cancellation).ConfigureAwait(false);
        var scenario = FromResult(await ops.CompressionRatioAsync(dataset, ctx.Cancellation).ConfigureAwait(false));
        scenario.Metrics["input_rows"] = rows.Count;
        scenario.Metrics["performance_gating"] = "warning_only";
        return scenario;
    }
}
