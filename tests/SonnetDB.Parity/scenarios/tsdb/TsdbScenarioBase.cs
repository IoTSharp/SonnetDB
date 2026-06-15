using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Tsdb;

/// <summary>
/// TSDB parity 场景基类：封装能力检查、run 专属 measurement 命名和场景结果构造。
/// </summary>
public abstract class TsdbScenarioBase : IScenario
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

        return await RunTimeSeriesAsync(plane.TimeSeries, ctx).ConfigureAwait(false);
    }

    /// <summary>执行当前 TSDB 场景。</summary>
    /// <param name="ops">时序操作集合。</param>
    /// <param name="ctx">场景上下文。</param>
    /// <returns>场景结果。</returns>
    protected abstract Task<ScenarioResult> RunTimeSeriesAsync(ITimeSeriesOps ops, ScenarioContext ctx);

    /// <summary>生成本次 run 独占的 measurement 名。</summary>
    /// <param name="ctx">场景上下文。</param>
    /// <param name="suffix">稳定后缀。</param>
    /// <returns>measurement 名。</returns>
    protected string Measurement(ScenarioContext ctx, string suffix)
        => ("p129_" + ctx.RunId.Replace("-", "_", StringComparison.Ordinal) + "_" + suffix).ToLowerInvariant();

    /// <summary>构造 SQL 结果场景响应。</summary>
    /// <param name="result">规范化结果集。</param>
    /// <returns>场景结果。</returns>
    protected static ScenarioResult FromResult(RelationalSqlResult result)
    {
        var scenarioResult = new ScenarioResult
        {
            Pass = true,
            SqlResult = result,
        };
        scenarioResult.Metrics["row_count"] = result.Rows.Count;
        return scenarioResult;
    }

    /// <summary>读取正整数环境变量。</summary>
    /// <param name="key">变量名。</param>
    /// <param name="fallback">默认值。</param>
    /// <returns>解析后的值。</returns>
    protected static int EnvInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    /// <summary>创建连续秒级样本。</summary>
    protected static IReadOnlyList<TsdbPoint> BuildLinearSeries(string measurement, int count, double start = 0d, double step = 1d)
    {
        var startMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var points = new TsdbPoint[count];
        for (var i = 0; i < points.Length; i++)
            points[i] = new TsdbPoint(measurement, startMs + i * 1_000L, "device_000", "cn", start + step * i);
        return points;
    }
}
