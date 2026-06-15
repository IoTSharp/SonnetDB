using System.Data;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// READ COMMITTED 场景：一个会话提交前另一个会话不可见，提交后可读。
/// </summary>
public sealed class IsolationReadCommittedScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "isolation_read_committed";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.SqlReadCommitted;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["inventory"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        await DropTablesAsync(ops, ct, "inventory").ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE inventory (
                id {IntType(ops)},
                qty {IntType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync("INSERT INTO inventory (id, qty) VALUES (1, 10)", ct).ConfigureAwait(false);

        await using var writer = await ops.OpenSessionAsync(ct).ConfigureAwait(false);
        await using var reader = await ops.OpenSessionAsync(ct).ConfigureAwait(false);
        await using var tx = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

        await writer.ExecuteAsync("UPDATE inventory SET qty = 20 WHERE id = 1", ct).ConfigureAwait(false);
        var beforeCommit = await reader.QueryAsync("SELECT qty FROM inventory WHERE id = 1", ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        var afterCommit = await reader.QueryAsync("SELECT qty FROM inventory WHERE id = 1", ct).ConfigureAwait(false);

        var result = new RelationalSqlResult(
            ["phase", "qty"],
            [
                new RelationalSqlRow(["before_commit", beforeCommit.Rows.Single().Values[0]]),
                new RelationalSqlRow(["after_commit", afterCommit.Rows.Single().Values[0]]),
            ],
            -1);

        return FromSql(
            result,
            Equals(beforeCommit.Rows.Single().Values[0], 10L)
            && Equals(afterCommit.Rows.Single().Values[0], 20L));
    }
}
