using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlTableTruncateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-table-truncate-" + Guid.NewGuid().ToString("N"));

    public SqlTableTruncateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_TruncateTable_ReturnsDedicatedAst()
    {
        var statement = Assert.IsType<TruncateTableStatement>(SqlParser.Parse("TRUNCATE TABLE devices"));
        Assert.Equal("devices", statement.TableName);
    }

    [Fact]
    public void DeleteWhereTrue_UsesGenerationAndDoesNotReviveRowsAfterCrash()
    {
        var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_name ON devices (name)");
        var store = db.Tables.Open("devices");
        store.InsertMany(Enumerable.Range(1, 500)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, $"device-{id:D4}" })
            .ToArray());
        Assert.Equal(500, store.RowCount);

        var deleted = Assert.IsType<DeleteExecutionResult>(
            SqlExecutor.Execute(db, "DELETE FROM devices WHERE TRUE"));
        Assert.Equal(500, deleted.SeriesAffected);
        Assert.Equal(0, store.RowCount);
        Assert.Equal(1, store.Generation);

        SqlExecutor.Execute(db, "INSERT INTO devices (id, name) VALUES (1, 'new-device')");
        db.CrashSimulationCloseWal();

        using var reopened = Open();
        var rows = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "SELECT id, name FROM devices ORDER BY id"));
        var row = Assert.Single(rows.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal("new-device", row[1]);
        Assert.Equal(1, reopened.Tables.Open("devices").Generation);
    }

    [Fact]
    public void PredicateBulkDelete_UsesBatchTombstonesAndPreservesResult()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");
        var store = db.Tables.Open("devices");
        store.InsertMany(Enumerable.Range(1, 300)
            .Select(static id => (IReadOnlyList<object?>)new object?[] { (long)id, $"device-{id}" })
            .ToArray());

        var deleted = Assert.IsType<DeleteExecutionResult>(
            SqlExecutor.Execute(db, "DELETE FROM devices WHERE id >= 1"));

        Assert.Equal(300, deleted.SeriesAffected);
        Assert.Equal(0, store.RowCount);
        Assert.Empty(store.Scan());
    }

    [Fact]
    public void TruncateTable_ReturnsRowsAndRejectsInboundForeignKey()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE parents (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE children (id INT, parent_id INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES parents (id))");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO children (id, parent_id) VALUES (1, 1)");

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "TRUNCATE TABLE parents"));
        Assert.Contains("外键", error.Message, StringComparison.Ordinal);

        var childResult = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "TRUNCATE TABLE children"));
        Assert.Equal(1, childResult.RowsAffected);
        Assert.Equal("truncate_generation", childResult.Operation);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        Kv = SonnetDB.Kv.KvOptions.Default with
        {
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
    });
}
