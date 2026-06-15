using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Runner;

namespace SonnetDB.Parity.Scenarios.FullText;

/// <summary>
/// 全文 parity 场景基类。
/// </summary>
public abstract class FullTextScenarioBase : IScenario
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

        return await RunFullTextAsync(plane.FullText, ctx).ConfigureAwait(false);
    }

    /// <summary>执行当前全文场景。</summary>
    protected abstract Task<ScenarioResult> RunFullTextAsync(IFullTextOps ops, ScenarioContext ctx);

    /// <summary>生成本次 run 独占 index。</summary>
    protected string Index(ScenarioContext ctx, string suffix)
        => ("p133_" + ctx.RunId.Replace("-", "_", StringComparison.Ordinal) + "_" + suffix).ToLowerInvariant();

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

    /// <summary>生成确定性英文日志文档。</summary>
    protected static IReadOnlyList<FullTextDocument> BuildEnglishDocuments(int count)
    {
        var docs = new FullTextDocument[count];
        for (int i = 0; i < count; i++)
        {
            string category = i % 5 == 0 ? "pump" : i % 5 == 1 ? "fan" : i % 5 == 2 ? "sensor" : i % 5 == 3 ? "gateway" : "valve";
            string severity = i % 7 == 0 ? "critical" : i % 3 == 0 ? "warning" : "info";
            string site = i % 2 == 0 ? "north" : "south";
            string body = category switch
            {
                "pump" => $"pump alarm pressure vibration station {site} severity {severity} maintenance ticket {i:D6}",
                "fan" => $"fan airflow normal station {site} severity {severity} ventilation ticket {i:D6}",
                "sensor" => $"temperature sensor drift station {site} severity {severity} calibration ticket {i:D6}",
                "gateway" => $"edge gateway packet loss station {site} severity {severity} network ticket {i:D6}",
                _ => $"valve position stable station {site} severity {severity} actuator ticket {i:D6}",
            };
            docs[i] = new FullTextDocument(
                "doc-" + i.ToString("D8"),
                $"{category} {severity} {site}",
                body,
                category,
                [site, severity]);
        }

        return docs;
    }

    /// <summary>生成确定性中文文档。</summary>
    protected static IReadOnlyList<FullTextDocument> BuildChineseDocuments() =>
    [
        new("cjk-1", "水泵报警", "北站水泵压力报警需要检修", "pump", ["north", "critical"]),
        new("cjk-2", "风机正常", "南站风机运行正常没有报警", "fan", ["south", "info"]),
        new("cjk-3", "水泵维护", "东站水泵震动升高安排维护", "pump", ["east", "warning"]),
        new("cjk-4", "网关丢包", "边缘网关出现网络丢包", "gateway", ["west", "warning"]),
    ];
}
