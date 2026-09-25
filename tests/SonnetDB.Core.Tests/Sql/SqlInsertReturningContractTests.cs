using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #197：RETURNING 空批次类型与复合键失败原子性。</summary>
public sealed class SqlInsertReturningContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-returning-contract-" + Guid.NewGuid().ToString("N"));

    public SqlInsertReturningContractTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void InsertSelect_EmptySource_RetainsDeclaredReturningSchema()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE source_rows (name STRING, PRIMARY KEY (name))");
        SqlExecutor.Execute(db, "CREATE TABLE target_rows (id INT AUTO_INCREMENT, name STRING NOT NULL DEFAULT 'generated', version INT ROWVERSION, note STRING NULL, PRIMARY KEY (id))");

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO target_rows (name) SELECT name FROM source_rows RETURNING id, name, version, note"));
        Assert.Equal(0, inserted.RowsInserted);
        var returning = Assert.IsType<SelectExecutionResult>(inserted.Returning);
        Assert.Empty(returning.Rows);
        Assert.Equal(["id", "name", "version", "note"], returning.Columns);
        var schema = Assert.IsAssignableFrom<IReadOnlyList<TableColumn>>(returning.ColumnSchema);
        Assert.Equal([TableColumnType.Int64, TableColumnType.String, TableColumnType.Int64, TableColumnType.String],
            schema.Select(column => column.DataType).ToArray());
        Assert.Equal([false, false, false, true], schema.Select(column => column.IsNullable).ToArray());
        Assert.True(schema[0].IsAutoIncrement);
        Assert.True(schema[2].IsRowVersion);
    }

    [Fact]
    public void InsertReturning_DuplicateCompositeKey_RejectsWholeStatement()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE scoped_keys (tenant INT, id INT, name STRING, PRIMARY KEY (tenant, id))");
        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db,
            "INSERT INTO scoped_keys (tenant, id, name) VALUES (1, 1, 'first'), (1, 2, 'second') RETURNING tenant, id, name"));
        Assert.Equal(2, inserted.RowsInserted);
        Assert.Equal([1L, 2L], inserted.Returning!.Rows.Select(row => (long)row[1]!).ToArray());

        var error = Assert.Throws<TableConstraintException>(() => SqlExecutor.Execute(db,
            "INSERT INTO scoped_keys (tenant, id, name) VALUES (2, 1, 'would-write'), (1, 1, 'duplicate') RETURNING tenant, id"));
        Assert.Equal(TableConstraintException.UniqueViolation, error.ErrorCode);
        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT tenant, id FROM scoped_keys ORDER BY tenant, id"));
        Assert.Equal(2, rows.Rows.Count);
        Assert.DoesNotContain(rows.Rows, row => (long)row[0]! == 2L);
    }
}
