using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Object;

/// <summary>
/// 对象桶 parity 场景基类：封装能力检查、bucket 命名和指标结果构造。
/// </summary>
public abstract class ObjectScenarioBase : IScenario
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract Capability Required { get; }

    /// <summary>本场景的准确度容差合同。</summary>
    public virtual DiffTolerance Tolerance => DiffTolerance.Strict;

    /// <inheritdoc />
    public async Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentNullException.ThrowIfNull(ctx);

        if ((plane.Capabilities & Required) != Required)
        {
            return new ScenarioResult
            {
                Pass = true,
                GapReason = $"backend '{plane.BackendName}' lacks required capabilities: {Required & ~plane.Capabilities}",
            };
        }

        return await RunObjectAsync(plane.Objects, ctx).ConfigureAwait(false);
    }

    /// <summary>执行当前对象桶场景。</summary>
    protected abstract Task<ScenarioResult> RunObjectAsync(IObjectOps ops, ScenarioContext ctx);

    /// <summary>生成本次 run 独占 bucket。</summary>
    protected string Bucket(ScenarioContext ctx, string suffix)
        => ("p131-" + ctx.RunId.Replace("_", "-", StringComparison.Ordinal) + "-" + suffix).ToLowerInvariant();

    /// <summary>构造确定性内容。</summary>
    protected static byte[] Payload(int sizeBytes, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[sizeBytes];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>构造单行 SQL 结果。</summary>
    protected static ScenarioResult MetricRow(params object?[] values)
    {
        var result = new RelationalSqlResult(
            Enumerable.Range(0, values.Length).Select(i => "c" + i).ToArray(),
            [new RelationalSqlRow(values)],
            -1);
        var scenario = new ScenarioResult { Pass = true, SqlResult = result };
        scenario.Metrics["row_count"] = 1;
        return scenario;
    }
}
