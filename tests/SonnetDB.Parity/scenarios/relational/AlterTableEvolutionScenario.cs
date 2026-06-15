using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// <c>ALTER TABLE</c> 演进场景：新增列、重命名列、删除列后验证数据保持一致。
/// </summary>
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
                enabled {IntType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO rel_devices (id, name, enabled) VALUES (1, 'pump', 1), (2, 'fan', 0)", ct)
            .ConfigureAwait(false);

        await ops.ExecuteAsync($"ALTER TABLE rel_devices ADD COLUMN site {StringType(ops)} NOT NULL DEFAULT 'north'", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices RENAME COLUMN name TO display_name", ct).ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices DROP COLUMN enabled", ct).ConfigureAwait(false);
        await ops.ExecuteAsync("ALTER TABLE rel_devices RENAME TO rel_assets", ct).ConfigureAwait(false);

        var result = await ops.QueryAsync("""
            SELECT id, display_name, site
            FROM rel_assets
            ORDER BY id
            """, ct).ConfigureAwait(false);

        return ScenarioFromRows(result, Row(1L, "pump", "north"), Row(2L, "fan", "north"));
    }
}
