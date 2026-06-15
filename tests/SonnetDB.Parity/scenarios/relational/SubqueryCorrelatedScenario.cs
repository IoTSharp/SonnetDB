using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// 相关子查询场景：验证 <c>EXISTS</c> 中引用外层表列的过滤语义。
/// </summary>
public sealed class SubqueryCorrelatedScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "subquery_correlated";

    /// <inheritdoc />
    public override Capability Required =>
        Capability.Relational | Capability.SqlSubquery | Capability.SqlCorrelatedSubquery;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["rel_orders", "rel_customers"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await ops.ExecuteAsync($"""
            CREATE TABLE rel_customers (
                id {IntType(ops)},
                name {StringType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE rel_orders (
                id {IntType(ops)},
                customer_id {IntType(ops)},
                total {IntType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO rel_customers (id, name) VALUES (1, 'alice'), (2, 'bob'), (3, 'cora')", ct)
            .ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO rel_orders (id, customer_id, total) VALUES (10, 1, 50), (20, 1, 200), (30, 2, 80)", ct)
            .ConfigureAwait(false);

        var result = await ops.QueryAsync("""
            SELECT c.name
            FROM rel_customers c
            WHERE EXISTS (
                SELECT 1
                FROM rel_orders o
                WHERE o.customer_id = c.id AND o.total >= 100
            )
            ORDER BY c.name
            """, ct).ConfigureAwait(false);

        return ScenarioFromRows(result, Row("alice"));
    }
}
