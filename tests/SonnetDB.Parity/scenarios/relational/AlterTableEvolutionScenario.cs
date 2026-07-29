using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// <c>ALTER TABLE</c> 演进场景：验证新增、类型转换、空值约束、默认值、重命名和删除列
/// 在 SonnetDB 与 PostgreSQL 之间保持一致。
/// </summary>
/// <remarks>
/// 空值约束使用两后端共同支持的 <c>SET NOT NULL</c> / <c>DROP NOT NULL</c>；当前
/// <see cref="IRelationalOps"/> 没有物理重启操作，因此本场景不把独立会话误当作重启验证。
/// </remarks>
public sealed class AlterTableEvolutionScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "alter_table_evolution";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.SqlAlterTable;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["rel_assets", "rel_devices"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await ops.ExecuteAsync($"""
            CREATE TABLE rel_devices (
                id {IntType(ops)},
                name {StringType(ops)},
                reading {IntType(ops)},
                tier {StringType(ops)} NULL DEFAULT 'baseline',
                enabled {IntType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO rel_devices (id, name, reading, enabled) VALUES (1, 'pump', 5, 1), (2, 'fan', 7, 0)", ct)
            .ConfigureAwait(false);

        await ops.ExecuteAsync($"ALTER TABLE rel_devices ADD COLUMN site {StringType(ops)} NOT NULL DEFAULT 'north'", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices ALTER COLUMN reading TYPE FLOAT", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices ALTER COLUMN site DROP NOT NULL", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices ALTER COLUMN site SET DEFAULT 'west'", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices RENAME COLUMN name TO display_name", ct).ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices DROP COLUMN enabled", ct).ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO rel_devices (id, display_name, reading) VALUES (3, 'valve', 9)", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices ALTER COLUMN site SET NOT NULL", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices ALTER COLUMN site DROP DEFAULT", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices ALTER COLUMN site DROP NOT NULL", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO rel_devices (id, display_name, reading) VALUES (4, 'meter', 11)", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices RENAME TO rel_assets", ct).ConfigureAwait(false);

        var result = await ops.QueryAsync("""
            SELECT id, display_name, reading, site, tier
            FROM rel_assets
            ORDER BY id
            """, ct).ConfigureAwait(false);

        return ScenarioFromRows(
            result,
            Row(1L, "pump", 5d, "north", "baseline"),
            Row(2L, "fan", 7d, "north", "baseline"),
            Row(3L, "valve", 9d, "west", "baseline"),
            Row(4L, "meter", 11d, null, "baseline"));
    }
}
