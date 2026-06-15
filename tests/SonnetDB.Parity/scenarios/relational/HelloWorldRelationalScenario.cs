using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// 关系型冒烟场景：建表 → 插入两行 → 按 id 升序读全表 → 清理。
/// 用于验证 Parity 骨架（适配器 / runner / reporter / differ）端到端跑通。
/// </summary>
public sealed class HelloWorldRelationalScenario : IScenario
{
    /// <summary>本场景期望读回的行（顺序敏感）。</summary>
    public static readonly IReadOnlyList<RelationalRow> Expected =
    [
        new RelationalRow(1, "pump"),
        new RelationalRow(2, "fan"),
    ];

    /// <inheritdoc />
    public string Name => "relational_hello_world";

    /// <inheritdoc />
    public Capability Required => Capability.Relational;

    /// <inheritdoc />
    public async Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx)
    {
        ArgumentNullException.ThrowIfNull(plane);
        ArgumentNullException.ThrowIfNull(ctx);

        var ct = ctx.Cancellation;
        var ops = plane.Relational;
        var result = new ScenarioResult();
        try
        {
            await ops.EnsureDeviceTableAsync(ct).ConfigureAwait(false);
            await ops.InsertDevicesAsync(Expected, ct).ConfigureAwait(false);
            var rows = await ops.SelectDevicesOrderByIdAsync(ct).ConfigureAwait(false);
            result.Rows = rows;
            result.Pass = rows.SequenceEqual(Expected);
            result.Metrics["row_count"] = rows.Count;
        }
        finally
        {
            try { await ops.DropDeviceTableAsync(ct).ConfigureAwait(false); }
            catch { /* best-effort cleanup */ }
        }

        return result;
    }
}
