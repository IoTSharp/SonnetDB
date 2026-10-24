using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// #343：关系约束、级联动作和索引最终状态的一致性回归。
///
/// 这些用例只固定当前 SQL/Core 已支持的行为；Server/HTTP 入口和真实进程强杀
/// 仍需在各自测试项目中单独取得证据，不能从嵌入式结果外推。
/// </summary>
public sealed class SqlConstraintConsistencyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-constraint-consistency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SetNull_MultipleForeignKeysToSameParent_NullsEveryReferencingColumn()
    {
        using (var database = Open())
        {
            CreateDualReferenceFixture(database);
            Execute(database, "DELETE FROM parents WHERE id = 1");

            var child = Rows(database,
                "SELECT id, parent_a, parent_b FROM children ORDER BY id");
            Assert.Equal(2, child.Count);
            Assert.Equal(new object?[] { 10L, null, null }, child[0]);
            Assert.Equal(new object?[] { 20L, 2L, null }, child[1]);
        }

        // The parent delete and both child updates are a single TableManager batch;
        // reopening verifies that the merged SET NULL mutation and its indexes were
        // durably published rather than only visible in an in-memory overlay.
        using var reopened = Open();
        var childAfterReopen = Rows(reopened,
            "SELECT id, parent_a, parent_b FROM children ORDER BY id");
        Assert.Equal(new object?[] { 10L, null, null }, childAfterReopen[0]);
        Assert.Equal(new object?[] { 20L, 2L, null }, childAfterReopen[1]);
        Assert.Empty(Rows(reopened, "SELECT id FROM parents WHERE id = 1"));
    }

    [Fact]
    public void SetNull_MultipleForeignKeysInExplicitTransaction_MergesIntoOneFinalRow()
    {
        using var database = Open();
        CreateDualReferenceFixture(database);

        var transaction = Assert.IsType<SqlTransactionContext>(Execute(database, "BEGIN"));
        Execute(database, "DELETE FROM parents WHERE id = 1", transaction);
        Execute(database, "COMMIT", transaction);

        Assert.Equal(new object?[] { 10L, null, null },
            Assert.Single(Rows(database, "SELECT id, parent_a, parent_b FROM children WHERE id = 10")));
        Assert.Empty(Rows(database, "SELECT id FROM parents WHERE id = 1"));
    }

    [Fact]
    public void ConstraintAndIndexFailure_RollsBackWholeStatementAndLeavesBaseRowsAligned()
    {
        using (var database = Open())
        {
            Execute(database,
                "CREATE TABLE inventory (id INT, sku STRING NOT NULL, quantity INT, PRIMARY KEY (id), CHECK (quantity >= 0))");
            Execute(database, "CREATE UNIQUE INDEX ux_inventory_sku ON inventory (sku)");
            Execute(database, "CREATE INDEX ix_inventory_quantity ON inventory (quantity)");
            Execute(database,
                "INSERT INTO inventory (id, sku, quantity) VALUES (1, 'pump', 4), (2, 'fan', 7)");

            Assert.ThrowsAny<Exception>(() => Execute(database,
                "INSERT INTO inventory (id, sku, quantity) VALUES (3, 'pump', 8), (4, 'meter', -1)"));
            Assert.ThrowsAny<Exception>(() => Execute(database,
                "UPDATE inventory SET sku = 'pump' WHERE id = 2"));

            Assert.Equal(
                new object?[] { 1L, "pump", 4L },
                Assert.Single(Rows(database, "SELECT id, sku, quantity FROM inventory WHERE id = 1")));
            Assert.Equal(
                new object?[] { 2L, "fan", 7L },
                Assert.Single(Rows(database, "SELECT id, sku, quantity FROM inventory WHERE id = 2")));
            Assert.Equal(1L, Assert.Single(Rows(database,
                "SELECT id FROM inventory WHERE sku = 'pump'"))[0]);
            Assert.Equal(2L, Assert.Single(Rows(database,
                "SELECT id FROM inventory WHERE quantity = 7"))[0]);

            Execute(database, "UPDATE inventory SET sku = 'compressor', quantity = 9 WHERE id = 2");
            Execute(database, "DELETE FROM inventory WHERE id = 1");
            AssertInventoryConsistency(database);
        }

        using var reopened = Open();
        AssertInventoryConsistency(reopened);
        Assert.Equal(new object?[] { 2L, "compressor", 9L },
            Assert.Single(Rows(reopened, "SELECT id, sku, quantity FROM inventory")));
    }

    private static void CreateDualReferenceFixture(Tsdb database)
    {
        Execute(database, "CREATE TABLE parents (id INT, PRIMARY KEY (id))");
        Execute(database, """
            CREATE TABLE children (
                id INT,
                parent_a INT NULL,
                parent_b INT NULL,
                PRIMARY KEY (id),
                CONSTRAINT fk_children_a FOREIGN KEY (parent_a)
                    REFERENCES parents (id) ON DELETE SET NULL,
                CONSTRAINT fk_children_b FOREIGN KEY (parent_b)
                    REFERENCES parents (id) ON DELETE SET NULL
            )
            """);
        Execute(database, "INSERT INTO parents (id) VALUES (1), (2)");
        Execute(database,
            "INSERT INTO children (id, parent_a, parent_b) VALUES (10, 1, 1), (20, 2, 1)");
    }

    private static void AssertInventoryConsistency(Tsdb database)
    {
        var rows = Rows(database, "SELECT id, sku, quantity FROM inventory ORDER BY id");
        Assert.Equal(1, rows.Count);
        Assert.Equal(new object?[] { 2L, "compressor", 9L }, rows[0]);

        // Every indexed lookup must resolve to the same primary row as the base scan.
        Assert.Equal(2L, Assert.Single(Rows(database,
            "SELECT id FROM inventory WHERE sku = 'compressor'"))[0]);
        Assert.Equal(2L, Assert.Single(Rows(database,
            "SELECT id FROM inventory WHERE quantity = 9"))[0]);
        Assert.Empty(Rows(database, "SELECT id FROM inventory WHERE sku = 'pump'"));
        Assert.Empty(Rows(database, "SELECT id FROM inventory WHERE quantity = 4"));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    private static object? Execute(
        Tsdb database,
        string sql,
        SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse(sql), null, transaction);

    private static IReadOnlyList<IReadOnlyList<object?>> Rows(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(Execute(database, sql)).Rows;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
