using System.Diagnostics;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Relational;

/// <summary>
/// TPC-C lite 场景：小型库存扣减 + 订单写入事务循环。
/// </summary>
public sealed class TpccLiteScenario : RelationalScenarioBase
{
    /// <inheritdoc />
    public override string Name => "tpcc_lite";

    /// <inheritdoc />
    public override Capability Required => Capability.Relational | Capability.RelationalTpccLite;

    /// <inheritdoc />
    protected override IReadOnlyList<string> TablesToDrop => ["order_lines", "orders", "stock", "districts", "warehouses"];

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunRelationalAsync(IRelationalOps ops, ScenarioContext ctx)
    {
        var ct = ctx.Cancellation;
        var warehouseCount = EnvInt("PARITY_TPCC_WAREHOUSES", 5);
        var duration = TimeSpan.FromSeconds(EnvInt("PARITY_TPCC_SECONDS", 1800));
        if (Environment.GetEnvironmentVariable("PARITY_TPCC_FULL") != "1")
        {
            var skipped = ScenarioSkipped("long profile disabled; set PARITY_TPCC_FULL=1 to run 5 warehouses for 30 minutes");
            skipped.Metrics["warehouses"] = warehouseCount;
            skipped.Metrics["duration_seconds"] = (long)duration.TotalSeconds;
            return skipped;
        }

        await DropTablesAsync(ops, ct, TablesToDrop.ToArray()).ConfigureAwait(false);
        await CreateSchemaAsync(ops, ct).ConfigureAwait(false);
        await SeedAsync(ops, warehouseCount, ct).ConfigureAwait(false);

        var stopwatch = Stopwatch.StartNew();
        var txCount = 0;
        var orderId = 1L;
        while (stopwatch.Elapsed < duration)
        {
            var warehouseId = txCount % warehouseCount + 1;
            var itemId = txCount % 10 + 1;
            await ops.ExecuteAsync($"""
                UPDATE stock SET quantity = quantity - 1
                WHERE warehouse_id = {warehouseId} AND item_id = {itemId}
                """, ct).ConfigureAwait(false);
            await ops.ExecuteAsync($"""
                INSERT INTO orders (id, warehouse_id, district_id, customer_id, total)
                VALUES ({orderId}, {warehouseId}, 1, {1000 + txCount}, 1.0)
                """, ct).ConfigureAwait(false);
            await ops.ExecuteAsync($"""
                INSERT INTO order_lines (order_id, line_no, item_id, quantity)
                VALUES ({orderId}, 1, {itemId}, 1)
                """, ct).ConfigureAwait(false);
            txCount++;
            orderId++;
        }

        var result = await ops.QueryAsync("""
            SELECT count(*) AS order_count, sum(quantity) AS stock_sum
            FROM stock
            """, ct).ConfigureAwait(false);

        result = new RelationalSqlResult(
            ["metric", "value"],
            [
                new RelationalSqlRow(["transactions", (long)txCount]),
                new RelationalSqlRow(["warehouses", (long)warehouseCount]),
                new RelationalSqlRow(["stock_sum", result.Rows[0].Values[1]]),
            ],
            -1);

        var scenarioResult = FromSql(result, txCount > 0);
        scenarioResult.Metrics["transactions"] = txCount;
        scenarioResult.Metrics["duration_ms"] = stopwatch.ElapsedMilliseconds;
        scenarioResult.Metrics["warehouses"] = warehouseCount;
        return scenarioResult;
    }

    private static async Task CreateSchemaAsync(IRelationalOps ops, CancellationToken ct)
    {
        await ops.ExecuteAsync($"""
            CREATE TABLE warehouses (
                id {IntType(ops)},
                name {TextType(ops)},
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE districts (
                id {IntType(ops)},
                warehouse_id {IntType(ops)},
                next_order_id {IntType(ops)},
                PRIMARY KEY (id, warehouse_id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE stock (
                warehouse_id {IntType(ops)},
                item_id {IntType(ops)},
                quantity {IntType(ops)},
                PRIMARY KEY (warehouse_id, item_id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE orders (
                id {IntType(ops)},
                warehouse_id {IntType(ops)},
                district_id {IntType(ops)},
                customer_id {IntType(ops)},
                total FLOAT,
                PRIMARY KEY (id)
            )
            """, ct).ConfigureAwait(false);
        await ops.ExecuteAsync($"""
            CREATE TABLE order_lines (
                order_id {IntType(ops)},
                line_no {IntType(ops)},
                item_id {IntType(ops)},
                quantity {IntType(ops)},
                PRIMARY KEY (order_id, line_no)
            )
            """, ct).ConfigureAwait(false);
    }

    private static async Task SeedAsync(IRelationalOps ops, int warehouseCount, CancellationToken ct)
    {
        for (var w = 1; w <= warehouseCount; w++)
        {
            await ops.ExecuteAsync($"INSERT INTO warehouses (id, name) VALUES ({w}, 'w-{w}')", ct).ConfigureAwait(false);
            await ops.ExecuteAsync($"INSERT INTO districts (id, warehouse_id, next_order_id) VALUES (1, {w}, 1)", ct).ConfigureAwait(false);
            for (var item = 1; item <= 10; item++)
            {
                await ops.ExecuteAsync(
                    $"INSERT INTO stock (warehouse_id, item_id, quantity) VALUES ({w}, {item}, 1000)",
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static int EnvInt(string name, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
