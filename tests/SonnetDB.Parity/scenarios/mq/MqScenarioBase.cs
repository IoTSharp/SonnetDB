using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Mq;

/// <summary>
/// MQ parity 场景基类：封装能力检查、topic 命名和指标结果构造。
/// </summary>
public abstract class MqScenarioBase : IScenario
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

        return await RunMqAsync(plane.Mq, ctx).ConfigureAwait(false);
    }

    /// <summary>执行当前 MQ 场景。</summary>
    protected abstract Task<ScenarioResult> RunMqAsync(IMqOps ops, ScenarioContext ctx);

    /// <summary>生成本次 run 独占 topic。</summary>
    protected string Topic(ScenarioContext ctx, string suffix)
        => ("p132." + ctx.RunId.Replace("-", ".", StringComparison.Ordinal) + "." + suffix).ToLowerInvariant();

    /// <summary>读取正整数环境变量。</summary>
    protected static int EnvInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
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
