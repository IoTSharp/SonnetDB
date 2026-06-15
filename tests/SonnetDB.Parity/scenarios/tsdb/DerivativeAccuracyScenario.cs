using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// derivative 准确度场景。数据为严格线性序列，PromQL deriv、Flux derivative 与 SonnetDB
/// 行级 derivative 应落在同一斜率。
/// </summary>
public sealed class DerivativeAccuracyScenario : TsdbScenarioBase
{
    /// <inheritdoc />
    public override string Name => "derivative_accuracy";

    /// <inheritdoc />
    public override Capability Required => Capability.TimeSeries | Capability.TimeSeriesDerivative;

    /// <inheritdoc />
    public override DiffTolerance Tolerance => new(1e-6, 1e-6);

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx)
    {
        var measurement = Measurement(ctx, "derivative");
        var points = BuildLinearSeries(measurement, 30, start: 10d, step: 2d);
        await ops.IngestAsync(points, ctx.Cancellation).ConfigureAwait(false);
        return FromResult(await ops.DerivativeAsync(measurement, ctx.Cancellation).ConfigureAwait(false));
    }
}
