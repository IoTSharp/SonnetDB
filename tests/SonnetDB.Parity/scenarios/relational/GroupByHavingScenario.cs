using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// 关系表 <c>GROUP BY ... HAVING</c> 场景。
/// </summary>
public sealed class GroupByHavingScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "groupby_having";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.SqlGroupBy | Capability.SqlHaving;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["rel_sales"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await ops.ExecuteAsync($"""
            CREATE TABLE rel_sales (
                id {IntType(ops)},
                region {StringType(ops)},
                amount {IntType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("""
            INSERT INTO rel_sales (id, region, amount)
            VALUES (1, 'north', 70), (2, 'north', 50), (3, 'south', 20), (4, 'west', 200)
            """, ct).ConfigureAwait(false);

        var result = await ops.QueryAsync("""
            SELECT region, count(*) AS order_count, sum(amount) AS total_amount
            FROM rel_sales
            GROUP BY region
            HAVING sum(amount) >= 100
            ORDER BY region
            """, ct).ConfigureAwait(false);

        return ScenarioFromRows(result, Row("north", 2L, 120L), Row("west", 1L, 200L));
    }
}
