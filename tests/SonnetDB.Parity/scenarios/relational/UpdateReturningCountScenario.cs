using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// 更新计数场景：Postgres 走 <c>UPDATE ... RETURNING</c>，SonnetDB 走 ADO.NET 受影响行数。
/// </summary>
public sealed class UpdateReturningCountScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "update_returning_count";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.SqlUpdateCount;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["rel_update_count"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await ops.ExecuteAsync($"""
            CREATE TABLE rel_update_count (
                id {IntType(ops)},
                enabled {IntType(ops)},
                touched {IntType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("""
            INSERT INTO rel_update_count (id, enabled, touched)
            VALUES (1, 1, 0), (2, 1, 0), (3, 0, 0)
            """, ct).ConfigureAwait(false);

        long updatedRows;
        if (ops.Dialect == RelationalDialect.Postgres)
        {
            var returned = await ops.QueryAsync("""
                UPDATE rel_update_count
                SET touched = 1
                WHERE enabled = 1
                RETURNING id
                """, ct).ConfigureAwait(false);
            updatedRows = returned.Rows.Count;
        }
        else
        {
            updatedRows = await ops.ExecuteAsync("""
                UPDATE rel_update_count
                SET touched = 1
                WHERE enabled = 1
                """, ct).ConfigureAwait(false);
        }

        var after = await ops.QueryAsync("""
            SELECT count(*) AS touched_count
            FROM rel_update_count
            WHERE touched = 1
            """, ct).ConfigureAwait(false);
        var touchedRows = after.Rows.Single().Values[0];

        var result = new RelationalSqlResult(
            ["metric", "value"],
            [Row("returned_or_affected", updatedRows), Row("touched_rows", touchedRows)],
            -1);

        var scenarioResult = ScenarioFromRows(result, Row("returned_or_affected", 2L), Row("touched_rows", 2L));
        scenarioResult.Metrics["sonnetdb_returning_gap"] = ops.Dialect == RelationalDialect.SonnetDb
            ? "UPDATE RETURNING not advertised; verified count via ExecuteNonQuery"
            : null;
        return scenarioResult;
    }
}
