using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// 外键级联场景：父表删除后子表应随 ON DELETE CASCADE 删除。
/// </summary>
public sealed class FkCascadeConstraintScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "fk_cascade_constraint";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.SqlForeignKey | Capability.SqlCascadeDelete;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["orders", "customers"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await DropTablesAsync(ops, ct, "orders", "customers").ConfigureAwait(false);

        await ops.ExecuteAsync($"""
            CREATE TABLE customers (
                id {IntType(ops)},
                name {TextType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE orders (
                id {IntType(ops)},
                customer_id {IntType(ops)},
                total FLOAT,
                PRIMARY KEY (id),
                FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("""
            INSERT INTO customers (id, name) VALUES (1, 'alice'), (2, 'bob')
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("""
            INSERT INTO orders (id, customer_id, total) VALUES (10, 1, 12.5), (11, 1, 18.0), (20, 2, 7.0)
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("DELETE FROM customers WHERE id = 1", ct).ConfigureAwait(false);

        var result = await ops.QueryAsync("""
            SELECT id, customer_id
            FROM orders
            ORDER BY id
            """, ct).ConfigureAwait(false);

        return FromSql(
            result,
            result.Rows.Count == 1
            && Equals(result.Rows[0].Values[0], 20L)
            && Equals(result.Rows[0].Values[1], 2L));
    }
}
