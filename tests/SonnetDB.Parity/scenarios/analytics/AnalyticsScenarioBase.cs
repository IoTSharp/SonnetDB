using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.Analytics;

/// <summary>
/// 分析 parity 场景基类：封装能力检查、数据集命名和场景结果构造。
/// </summary>
public abstract class AnalyticsScenarioBase : IScenario
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

        return await RunAnalyticsAsync(plane.Analytics, ctx).ConfigureAwait(false);
    }

    /// <summary>执行当前分析场景。</summary>
    protected abstract Task<ScenarioResult> RunAnalyticsAsync(IAnalyticalOps ops, ScenarioContext ctx);

    /// <summary>生成本次 run 独占数据集名。</summary>
    protected string Dataset(ScenarioContext ctx, string suffix)
        => ("p134_" + ctx.RunId.Replace("-", "_", StringComparison.Ordinal) + "_" + suffix).ToLowerInvariant();

    /// <summary>构造 SQL 结果场景响应。</summary>
    protected static ScenarioResult FromResult(RelationalSqlResult result)
    {
        var scenario = new ScenarioResult
        {
            Pass = true,
            SqlResult = result,
        };
        scenario.Metrics["row_count"] = result.Rows.Count;
        return scenario;
    }

    /// <summary>读取正整数环境变量。</summary>
    protected static int EnvInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    /// <summary>创建确定性分析样本。</summary>
    protected static IReadOnlyList<AnalyticalRow> BuildRows(string dataset, int days, int devices)
    {
        var startMs = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var rows = new List<AnalyticalRow>(days * devices);
        for (var day = 0; day < days; day++)
        {
            for (var device = 0; device < devices; device++)
            {
                double value = device * 10d + day + (device % 3) * 0.25d;
                string region = device % 2 == 0 ? "north" : "south";
                rows.Add(new AnalyticalRow(
                    dataset,
                    startMs + day * 86_400_000L + device * 1_000L,
                    "device_" + device.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
                    region,
                    value));
            }
        }

        return rows;
    }
}
