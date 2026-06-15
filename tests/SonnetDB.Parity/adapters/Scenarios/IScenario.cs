using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios;

/// <summary>
/// 单个 Parity 场景：对一个 <see cref="IDataPlane"/> 后端执行一段固定的操作序列并返回结果。
/// 同一个场景实例会被 runner 在 SonnetDB 与某个竞品后端各跑一遍，再交由
/// <see cref="Runner.ResultDiffer"/> 做容差判定。
/// </summary>
public interface IScenario
{
    /// <summary>场景稳定标识（用于报告与去重），如 <c>relational_hello_world</c>。</summary>
    string Name { get; }

    /// <summary>本场景依赖的能力位；后端 <see cref="IDataPlane.Capabilities"/> 不满足时被 SKIP。</summary>
    Capability Required { get; }

    /// <summary>
    /// 在给定后端上执行本场景。
    /// </summary>
    /// <param name="plane">目标后端的数据面。</param>
    /// <param name="ctx">运行上下文（run id / 报告目录 / 取消令牌）。</param>
    /// <returns>场景执行结果，含通过标志、行数据与指标。</returns>
    Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx);
}

/// <summary>
/// 场景运行上下文：贯穿一次 Parity run 的元数据。
/// </summary>
public sealed class ScenarioContext
{
    /// <summary>本次 run 的标识（用于报告子目录命名）。</summary>
    public required string RunId { get; init; }

    /// <summary>报告输出目录（JSON / Markdown 写入此处）。</summary>
    public required string ReportDirectory { get; init; }

    /// <summary>取消令牌。</summary>
    public CancellationToken Cancellation { get; init; }
}

/// <summary>
/// 场景在单个后端上的执行结果。
/// </summary>
public sealed class ScenarioResult
{
    /// <summary>该后端自身的内部一致性是否通过（与跨后端 diff 无关）。</summary>
    public bool Pass { get; set; }

    /// <summary>场景产出的行集合（供跨后端 <see cref="Runner.ResultDiffer"/> 比对）。</summary>
    public IReadOnlyList<RelationalRow> Rows { get; set; } = [];

    /// <summary>通用 SQL 场景产出的规范化结果集；旧冒烟场景可继续只填 <see cref="Rows"/>。</summary>
    public RelationalSqlResult? SqlResult { get; set; }

    /// <summary>附带的数值 / 文本指标（写入报告）。</summary>
    public Dictionary<string, object?> Metrics { get; } = new();

    /// <summary>当后端不支持或不可达而被 SKIP 时，记录原因；通过时为 null。</summary>
    public string? GapReason { get; set; }
}
