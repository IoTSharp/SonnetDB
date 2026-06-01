using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExecutorTableTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorTableTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-table-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ParseCreateTable_WithPrimaryKey_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            "CREATE TABLE devices (id INT NOT NULL, name STRING, enabled BOOL, PRIMARY KEY (id))"));

        Assert.Equal("devices", stmt.Name);
        Assert.Equal(3, stmt.Columns.Count);
        Assert.Equal(SqlDataType.Int64, stmt.Columns[0].DataType);
        Assert.Equal(ColumnNullability.NotNull, stmt.Columns[0].Nullability);
        Assert.Equal(["id"], stmt.PrimaryKey);
    }

    [Fact]
    public void ParseCreateIndex_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateTableIndexStatement>(SqlParser.Parse(
            "CREATE UNIQUE INDEX ux_devices_serial ON devices (tenant, serial)"));

        Assert.Equal("ux_devices_serial", stmt.IndexName);
        Assert.Equal("devices", stmt.TableName);
        Assert.True(stmt.IsUnique);
        Assert.Equal(["tenant", "serial"], stmt.Columns);
    }

    [Fact]
    public void CreateShowDescribeTable_PersistsAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db,
                "CREATE TABLE devices (id INT, name STRING NOT NULL, metadata JSON NULL, PRIMARY KEY (id))");
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var show = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, "SHOW TABLES"));
            Assert.Equal(new[] { "name" }, show.Columns);
            Assert.Equal("devices", show.Rows.Single()[0]);

            var describe = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "DESCRIBE TABLE devices"));
            Assert.Equal(new[] { "column_name", "data_type", "is_nullable", "is_primary_key", "ordinal" }, describe.Columns);
            Assert.Equal(new object?[] { "id", "int64", false, true, 0L }, describe.Rows[0]);
            Assert.Equal(new object?[] { "metadata", "json", true, false, 2L }, describe.Rows[2]);
        }
    }

    [Fact]
    public void InsertSelectUpdateDelete_TableRows_WorkEndToEnd()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE TABLE devices (id INT, name STRING NOT NULL, enabled BOOL, temp FLOAT NULL, PRIMARY KEY (id))");

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name, enabled, temp) VALUES (1, 'pump', TRUE, 12.5), (2, 'fan', FALSE, NULL)"));
        Assert.Equal(2, inserted.RowsInserted);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, name, enabled, temp FROM devices WHERE id = 1"));
        Assert.Equal(new[] { "id", "name", "enabled", "temp" }, selected.Columns);
        Assert.Equal(new object?[] { 1L, "pump", true, 12.5 }, selected.Rows.Single());

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE devices SET name = 'pump-2', temp = 13.25 WHERE id = 1"));
        Assert.Equal(1, updated.RowsAffected);

        var afterUpdate = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT name, temp FROM devices WHERE id = 1"));
        Assert.Equal(new object?[] { "pump-2", 13.25 }, afterUpdate.Rows.Single());

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM devices WHERE id = 2"));
        Assert.Equal(1, deleted.TombstonesAdded);

        var all = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices ORDER BY id"));
        Assert.Equal(1L, all.Rows.Single()[0]);
    }

    [Fact]
    public void Insert_DuplicatePrimaryKey_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'a')");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'b')"));
    }

    [Fact]
    public void Insert_MissingNotNullColumn_Throws()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING NOT NULL, PRIMARY KEY (id))");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id) VALUES (1)"));
    }

    [Fact]
    public void Select_WithNonPrimaryKeyPredicate_ScansAndFilters()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, name, enabled) VALUES (1, 'a', TRUE), (2, 'b', FALSE), (3, 'c', TRUE)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE enabled = TRUE AND id > 1 ORDER BY id DESC"));

        Assert.Equal([3L], result.Rows.Select(r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void Delete_WithPrimaryKeyAndExtraPredicate_RespectsAllPredicates()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name, enabled) VALUES (1, 'pump', TRUE)");

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM devices WHERE id = 1 AND enabled = FALSE"));
        Assert.Equal(0, deleted.TombstonesAdded);

        var remaining = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE id = 1"));
        Assert.Single(remaining.Rows);
    }

    [Fact]
    public void DropTable_RemovesSchemaAndRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'a')");

        var dropped = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP TABLE devices"));
        Assert.Equal(1, dropped.RowsAffected);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW TABLES")).Rows);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT * FROM devices"));
    }

    [Fact]
    public void CreateIndex_PersistsAndSelectUsesIndex()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, name STRING, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant, name) VALUES (1, 'north', 'pump'), (2, 'south', 'fan'), (3, 'north', 'meter')");
            SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");

            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT id, name FROM devices WHERE tenant = 'north' ORDER BY id"));
            Assert.Equal([1L, 3L], result.Rows.Select(static r => (long)r[0]!).ToArray());

            var indexes = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SHOW INDEXES ON devices"));
            Assert.Equal(new[] { "index_name", "is_unique", "columns", "created_utc" }, indexes.Columns);
            Assert.Equal("idx_devices_tenant", indexes.Rows.Single()[0]);

            var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "EXPLAIN SELECT id FROM devices WHERE tenant = 'north'"));
            var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
            Assert.Equal("secondary_index", values["access_path"]);
            Assert.Equal("idx_devices_tenant", values["index_name"]);
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened,
                "SELECT id FROM devices WHERE tenant = 'south'"));
            Assert.Equal(2L, result.Rows.Single()[0]);
        }
    }

    [Fact]
    public void UniqueIndex_RejectsDuplicateAndLeavesRowsUnchanged()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, serial STRING, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_devices_serial ON devices (serial)");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, serial, name) VALUES (1, 'A-1', 'pump')");

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO devices (id, serial, name) VALUES (2, 'A-1', 'fan')"));

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, name FROM devices WHERE serial = 'A-1'"));
        Assert.Equal(new object?[] { 1L, "pump" }, rows.Rows.Single());
    }

    [Fact]
    public void UpdateAndDelete_MaintainSecondaryIndexes()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, enabled BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant, enabled) VALUES (1, 'north', TRUE), (2, 'south', TRUE)");

        SqlExecutor.Execute(db, "UPDATE devices SET tenant = 'south' WHERE id = 1");

        var north = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'north'"));
        Assert.Empty(north.Rows);

        var south = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'south' ORDER BY id"));
        Assert.Equal([1L, 2L], south.Rows.Select(static r => (long)r[0]!).ToArray());

        SqlExecutor.Execute(db, "DELETE FROM devices WHERE tenant = 'south' AND id = 1");
        var afterDelete = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'south' ORDER BY id"));
        Assert.Equal([2L], afterDelete.Rows.Select(static r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void MultipleIndexes_OnDifferentColumns_DoNotCrossContaminate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, tenant STRING, site STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_site ON devices (site)");
        SqlExecutor.Execute(db,
            "INSERT INTO devices (id, tenant, site) VALUES (1, 'north', 'a'), (2, 'south', 'north'), (3, 'north', 'b')");

        var tenant = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE tenant = 'north' ORDER BY id"));
        Assert.Equal([1L, 3L], tenant.Rows.Select(static r => (long)r[0]!).ToArray());

        var site = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices WHERE site = 'north' ORDER BY id"));
        Assert.Equal([2L], site.Rows.Select(static r => (long)r[0]!).ToArray());
    }


    [Fact]
    public void ExecuteScript_CommitAndRollback_LightTransaction()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        var commitResults = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (1, 'pump');
            INSERT INTO devices (id, name) VALUES (2, 'fan');
            COMMIT;
            """);
        Assert.IsType<RowsAffectedExecutionResult>(commitResults[^1]);

        var committed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices ORDER BY id"));
        Assert.Equal([1L, 2L], committed.Rows.Select(static r => (long)r[0]!).ToArray());

        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (3, 'meter');
            ROLLBACK;
            """);

        var afterRollback = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM devices ORDER BY id"));
        Assert.Equal([1L, 2L], afterRollback.Rows.Select(static r => (long)r[0]!).ToArray());
    }

    [Fact]
    public void ExecuteScript_CommitFailure_RollsBackBatch()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, serial STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_devices_serial ON devices (serial)");

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, serial) VALUES (1, 'A-1');
            INSERT INTO devices (id, serial) VALUES (2, 'A-1');
            COMMIT;
            """));

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices"));
        Assert.Empty(rows.Rows);
    }

    [Fact]
    public void ExecuteScript_CrossTableTransaction_IsRejectedWithoutWrites()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE sites (id INT, name STRING, PRIMARY KEY (id))");

        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO devices (id, name) VALUES (1, 'pump');
            INSERT INTO sites (id, name) VALUES (1, 'north');
            COMMIT;
            """));

        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM devices")).Rows);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM sites")).Rows);
    }
}
