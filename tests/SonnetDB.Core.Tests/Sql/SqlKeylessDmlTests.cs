using System.Buffers.Binary;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#243/#253/#255：无显式主键的稳定行身份和原子 DML。</summary>
public sealed class SqlKeylessDmlTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-keyless-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(99, 3)]
    public void Delete_DuplicateRows_UsesStableIdentityAndBounds(int maximum, int deleted)
    {
        string[] remaining;
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE logs(message STRING NULL, value INT NULL)");
            Assert.Equal(4, Assert.IsType<InsertExecutionResult>(Execute(db,
                "INSERT INTO logs(message,value) VALUES('event',1),('event',1),('event',1),(NULL,2)")).RowsInserted);
            var store = db.Tables.Open("logs");
            Assert.Empty(store.Schema.PrimaryKey);
            Assert.Equal(2, store.Schema.Columns.Count);
            Assert.Equal(4, store.Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Distinct().Count());
            long scans = store.FullScanCount;
            var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db, null,
                "DELETE FROM logs WHERE message=@message LIMIT @maximum RETURNING *",
                new SqlParameters().AddNamed("message", "event").AddNamed("maximum", maximum)));
            Assert.Equal(deleted, result.SeriesAffected);
            Assert.Equal(deleted, result.Returning!.Rows.Count);
            Assert.Equal(new[] { "message", "value" }, result.Returning.Columns);
            if (maximum == 0) Assert.Equal(scans, store.FullScanCount);
            Assert.Equal(4 - deleted, store.RowCount);
            Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE message IS NULL"));
            remaining = store.Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order().ToArray();
            Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(db.Tables.SchemaPath).AsSpan(8)));
        }
        using var reopened = Open();
        Assert.Equal(remaining, reopened.Tables.Open("logs").Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order().ToArray());
        Assert.Equal(4L - deleted, Scalar(reopened, "SELECT COUNT(*) FROM logs"));
    }

    [Fact]
    public void Update_DuplicatesAndNulls_PreservesIdentityAndIndexes()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(message STRING NULL,value INT NULL)");
        Execute(db, "CREATE INDEX ix_logs_value ON logs(value)");
        Execute(db, "INSERT INTO logs(message,value) VALUES('event',1),('event',1),(NULL,2)");
        var ids = db.Tables.Open("logs").Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order().ToArray();
        var result = Assert.IsType<RowsAffectedExecutionResult>(Execute(db,
            "UPDATE logs SET value=value+4 WHERE message='event' RETURNING message,value"));
        Assert.Equal(2, result.RowsAffected);
        Assert.Equal(2, result.Returning!.Rows.Count);
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=5"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=1"));
        var covered = Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT value FROM logs WHERE value=5"));
        Assert.Equal(2, covered.Rows.Count);
        Assert.All(covered.Rows, row => Assert.Equal(5L, row[0]));
        Assert.Equal(ids, db.Tables.Open("logs").Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order().ToArray());
        Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(Execute(db, "DELETE FROM logs WHERE message IS NULL LIMIT 1")).SeriesAffected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transaction_InsertUpdateDeleteDuplicates_PreservesOverlayCommitRollback(bool commit)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE logs(value INT NULL)");
            Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
            var tx = new SqlTransactionContext();
            Assert.Equal(2, Assert.IsType<InsertExecutionResult>(Execute(db, "INSERT INTO logs(value) VALUES(1),(1)", tx)).RowsInserted);
            Assert.Equal(4L, Scalar(db, "SELECT COUNT(*) FROM logs", tx));
            Assert.Equal(4, Assert.IsType<RowsAffectedExecutionResult>(Execute(db, "UPDATE logs SET value=2 WHERE value=1", tx)).RowsAffected);
            Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(Execute(db, "DELETE FROM logs WHERE value=2 LIMIT 1", tx)).SeriesAffected);
            Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(Execute(db, "DELETE FROM logs WHERE value=2 LIMIT 1", tx)).SeriesAffected);
            Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=2", tx));
            Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=1"));
            Execute(db, commit ? "COMMIT" : "ROLLBACK", tx);
        }
        using var reopened = Open();
        Assert.Equal(2L, Scalar(reopened, $"SELECT COUNT(*) FROM logs WHERE value={(commit ? 2 : 1)}"));
    }

    [Fact]
    public void Constraints_UniqueCheckAndForeignKeyFailures_AreAtomic()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE parents(id INT,PRIMARY KEY(id))");
        Execute(db, "INSERT INTO parents(id) VALUES(1),(2)");
        Execute(db, "CREATE TABLE logs(code INT NULL,parentId INT NULL,value INT,CHECK(value>=0),FOREIGN KEY(parentId) REFERENCES parents(id))");
        Execute(db, "CREATE UNIQUE INDEX uq_logs_code ON logs(code)");
        Execute(db, "INSERT INTO logs(code,parentId,value) VALUES(1,1,0),(2,2,0),(NULL,NULL,0),(NULL,NULL,0)");
        foreach (string sql in new[]
        {
            "INSERT INTO logs(code,parentId,value) VALUES(3,1,0),(3,1,0)",
            "INSERT INTO logs(code,parentId,value) VALUES(3,1,0),(4,99,0)",
            "INSERT INTO logs(code,parentId,value) VALUES(3,1,0),(4,1,-1)",
            "UPDATE logs SET code=9 WHERE code IS NOT NULL",
            "UPDATE logs SET parentId=99 WHERE code IS NOT NULL",
            "UPDATE logs SET value=-1 WHERE code IS NOT NULL",
            "DELETE FROM parents WHERE id>0 LIMIT 2",
        })
            Assert.ThrowsAny<Exception>(() => Execute(db, sql));
        Assert.Equal(4L, Scalar(db, "SELECT COUNT(*) FROM logs"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE code=1"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE code=2"));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM parents"));
    }

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    public void ForeignKey_DeleteAction_HandlesDuplicateKeylessChildren(string action)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE parents(id INT,PRIMARY KEY(id))");
        Execute(db, $"CREATE TABLE logs(parentId INT NULL,FOREIGN KEY(parentId) REFERENCES parents(id) ON DELETE {action})");
        Execute(db, "INSERT INTO parents(id) VALUES(1),(2)");
        Execute(db, "INSERT INTO logs(parentId) VALUES(1),(1),(2)");
        Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(Execute(db, "DELETE FROM parents WHERE id=1 LIMIT 1")).SeriesAffected);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE parentId=2"));
        Assert.Equal(action == "CASCADE" ? 1L : 3L, Scalar(db, "SELECT COUNT(*) FROM logs"));
        Assert.Equal(action == "CASCADE" ? 0L : 2L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE parentId IS NULL"));
    }

    [Fact]
    public void Transaction_ConcurrentUpdate_RejectsLostUpdateWithoutMergingDuplicates()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE logs(value INT)");
        Execute(db, "INSERT INTO logs(value) VALUES(1),(1)");
        var first = new SqlTransactionContext();
        var second = new SqlTransactionContext();
        Execute(db, "UPDATE logs SET value=2 WHERE value=1", first);
        Execute(db, "DELETE FROM logs WHERE value=1 LIMIT 1", second);
        Execute(db, "COMMIT", second);
        Assert.Throws<TableConstraintException>(() => Execute(db, "COMMIT", first));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=1"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM logs WHERE value=2"));
    }

    [Fact]
    public void Ado_SchemaAndReturning_ExposeOnlyDeclaredColumns()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE logs(message STRING NULL)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO logs(message) VALUES(@message),(@message) RETURNING *";
        command.Parameters.AddWithValue("@message", "event");
        using (var reader = command.ExecuteReader())
        {
            Assert.Equal(1, reader.FieldCount);
            Assert.Equal("message", reader.GetName(0));
            Assert.True(reader.Read());
            Assert.Equal("event", reader.GetString(0));
            Assert.True(reader.Read());
            Assert.False(reader.Read());
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name='logs'";
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("message", reader.GetString(0));
            Assert.False(reader.Read());
        }
        var columns = connection.GetSchema("Columns", [null, null, "logs", null]);
        Assert.Single(columns.Rows.Cast<System.Data.DataRow>());
        Assert.Equal("message", columns.Rows[0]["COLUMN_NAME"]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? tx = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, tx, SqlExecutionOptions.Default);
    private static object? Scalar(Tsdb db, string sql, SqlTransactionContext? tx = null)
        => Assert.Single(Assert.IsType<SelectExecutionResult>(Execute(db, sql, tx)).Rows)[0];
}
