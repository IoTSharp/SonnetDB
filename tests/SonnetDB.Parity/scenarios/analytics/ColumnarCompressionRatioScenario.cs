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
        var result = await ops.CompressionRatioAsync(dataset, ctx.Cancellation).ConfigureAwait(false);
        var scenario = FromResult(result);

        // 本场景虽然 cross-backend 容差是 warning_only，但 SonnetDB 一侧必须返回
        // 一个有限正数压缩率；否则说明 IngestAsync/CompressionRatioAsync 未对齐
        // （例如 _analyticsLogicalBytes 未填、或物理目录路径错位），不能记作通过。
        if (scenario.Pass)
        {
            var values = result.Rows[0].Values;
            if (values.Count == 0 || values[0] is not double ratio || !double.IsFinite(ratio) || ratio <= 0d)
            {
                scenario.Pass = false;
                scenario.GapReason = $"compression_ratio expected positive finite double, got {(values.Count == 0 ? "<empty>" : values[0])}";
            }
            else
            {
                scenario.Metrics["compression_ratio"] = ratio;
            }
        }

        scenario.Metrics["input_rows"] = rows.Count;
        scenario.Metrics["performance_gating"] = "warning_only";
        return scenario;
    }
}
