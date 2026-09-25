using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #195：关系表 INSERT SELECT 的投影、事务与生成列合同。</summary>
public sealed class SqlInsertSelectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-insert-select-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void InsertSelect_DecimalProjection_PreservesExactValueAndReturning()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, amount DECIMAL(20,6), PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT AUTO_INCREMENT, amount DECIMAL(20,6), PRIMARY KEY (id))");
        Execute(db, "INSERT INTO source_rows (id, amount) VALUES (1, '9007199254740993.125000')");

        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO target_rows (amount) SELECT amount FROM source_rows RETURNING id, amount"));

        Assert.Equal(1, inserted.RowsInserted);
        Assert.Equal(new object?[] { 1L, 9007199254740993.125000m }, Assert.Single(inserted.Returning!.Rows));
        var selected = Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT amount FROM target_rows"));
        Assert.Equal(9007199254740993.125000m, Assert.Single(selected.Rows)[0]);
    }

    [Fact]
    public void InsertSelect_InTransaction_ReadsBufferedSourceAndRollsBack()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, value INT, PRIMARY KEY (id))");
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 42)", transaction);

        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO target_rows (id, value) SELECT id, value FROM source_rows RETURNING id, value",
            transaction));

        Assert.Equal(new object?[] { 1L, 42L }, Assert.Single(inserted.Returning!.Rows));
        Execute(db, "ROLLBACK", transaction);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT * FROM target_rows")).Rows);
    }

    [Fact]
    public void InsertSelect_MismatchedProjection_LeavesTargetUnchanged()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 42)");

        Assert.Throws<InvalidOperationException>(() => Execute(db,
            "INSERT INTO target_rows (id, value) SELECT id FROM source_rows"));
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT * FROM target_rows")).Rows);
    }

    [Fact]
    public void InsertSelect_OnConflictDoNothing_ReturnsOnlyInsertedRows()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO source_rows (id, value) VALUES (1, 99), (2, 20)");
        Execute(db, "INSERT INTO target_rows (id, value) VALUES (1, 10)");

        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO target_rows (id, value) SELECT id, value FROM source_rows ORDER BY id "
            + "ON CONFLICT (id) DO NOTHING RETURNING id, value"));

        Assert.Equal(1, inserted.RowsInserted);
        Assert.Equal(new object?[] { 2L, 20L }, Assert.Single(inserted.Returning!.Rows));
        var selected = Assert.IsType<SelectExecutionResult>(Execute(db,
            "SELECT id, value FROM target_rows ORDER BY id"));
        Assert.Equal(new object?[] { 1L, 10L }, selected.Rows[0]);
        Assert.Equal(new object?[] { 2L, 20L }, selected.Rows[1]);
    }

    [Fact]
    public void InsertSelect_EmptySource_ReturnsEmptyResultAndZeroAffected()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, PRIMARY KEY (id))");

        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO target_rows (id) SELECT id FROM source_rows RETURNING id"));

        Assert.Equal(0, inserted.RowsInserted);
        Assert.Empty(inserted.Returning!.Rows);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT id FROM target_rows")).Rows);
    }

    [Fact]
    public void InsertSelect_DuplicateCompositeKey_RollsBackWholeStatement()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (site INT, id INT, value INT, PRIMARY KEY (site, id))");
        Execute(db, "CREATE TABLE target_rows (site INT, id INT, value INT, PRIMARY KEY (site, id))");
        Execute(db, "INSERT INTO source_rows (site, id, value) VALUES (1, 1, 11), (1, 2, 12)");
        Execute(db, "INSERT INTO target_rows (site, id, value) VALUES (1, 2, 99)");

        var error = Assert.Throws<TableConstraintException>(() => Execute(db,
            "INSERT INTO target_rows (site, id, value) SELECT site, id, value FROM source_rows ORDER BY id"));
        Assert.Equal(TableConstraintException.UniqueViolation, error.ErrorCode);

        var selected = Assert.IsType<SelectExecutionResult>(Execute(db,
            "SELECT site, id, value FROM target_rows"));
        Assert.Equal(new object?[] { 1L, 2L, 99L }, Assert.Single(selected.Rows));
    }

    [Fact]
    public void InsertSelect_SameTable_UsesSourceSnapshot()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE rows_to_copy (id INT, value STRING, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO rows_to_copy (id, value) VALUES (1, 'first'), (2, 'second')");

        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO rows_to_copy (id, value) "
            + "SELECT id + 10, value FROM rows_to_copy ORDER BY id RETURNING id, value"));

        Assert.Equal(2, inserted.RowsInserted);
        Assert.Equal(new object?[] { 11L, "first" }, inserted.Returning!.Rows[0]);
        Assert.Equal(new object?[] { 12L, "second" }, inserted.Returning.Rows[1]);
        Assert.Equal(4, Assert.IsType<SelectExecutionResult>(Execute(db,
            "SELECT id FROM rows_to_copy")).Rows.Count);
    }

    [Fact]
    public void InsertSelect_ParameterizedJoinAndAggregate_BindsSourceQuery()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, group_id INT, value INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE groups (id INT, enabled BOOL, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (group_id INT, total INT, PRIMARY KEY (group_id))");
        Execute(db, "INSERT INTO source_rows (id, group_id, value) VALUES (1, 7, 10), (2, 7, 20), (3, 8, 30)");
        Execute(db, "INSERT INTO groups (id, enabled) VALUES (7, true), (8, false)");

        var statement = SqlParameterBinder.Bind(SqlParser.Parse(
            "INSERT INTO target_rows (group_id, total) "
            + "SELECT s.group_id, SUM(s.value) FROM source_rows AS s "
            + "INNER JOIN groups AS g ON s.group_id = g.id "
            + "WHERE g.enabled = @enabled GROUP BY s.group_id RETURNING group_id, total"),
            new SqlParameters().AddNamed("enabled", true));
        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.ExecuteStatement(
            db, null, statement, null, null, SqlExecutionOptions.Default));

        Assert.Equal(new object?[] { 7L, 30L }, Assert.Single(inserted.Returning!.Rows));
    }

    [Fact]
    public void InsertSelect_SourceRowLimit_StopsBeforeFullSourceMaterialization()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, PRIMARY KEY (id))");
        string values = string.Join(", ", Enumerable.Range(1, 20).Select(static id => $"({id})"));
        Execute(db, $"INSERT INTO source_rows (id) VALUES {values}");
        int decoded = 0;
        db.Tables.Open("source_rows").RowDecodedTestHook = _ => decoded++;

        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO target_rows (id) SELECT id FROM source_rows",
            options: new SqlExecutionOptions { MaxTriggerTransitionRows = 2 }));

        Assert.Equal(RoutineErrorCodes.TransitionLimit, error.Code);
        Assert.InRange(decoded, 1, 3);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT * FROM target_rows")).Rows);
    }

    [Fact]
    public void InsertSelect_SourceByteLimit_StopsBeforeFullSourceMaterialization()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, payload STRING, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, payload STRING, PRIMARY KEY (id))");
        string payload = new('x', 1_000);
        string values = string.Join(", ", Enumerable.Range(1, 10)
            .Select(id => $"({id}, '{payload}')"));
        Execute(db, $"INSERT INTO source_rows (id, payload) VALUES {values}");
        int decoded = 0;
        db.Tables.Open("source_rows").RowDecodedTestHook = _ => decoded++;

        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO target_rows (id, payload) SELECT id, payload FROM source_rows",
            options: new SqlExecutionOptions { MaxTriggerTransitionBytes = 2_500 }));

        Assert.Equal(RoutineErrorCodes.TransitionLimit, error.Code);
        Assert.InRange(decoded, 1, 2);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT * FROM target_rows")).Rows);
    }

    [Fact]
    public void InsertSelect_JoinAggregateIntermediateLimit_RejectsBeforeTargetWrite()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE lookup_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT AUTO_INCREMENT, total INT, PRIMARY KEY (id))");
        string values = string.Join(", ", Enumerable.Range(1, 30).Select(static id => $"({id})"));
        Execute(db, $"INSERT INTO source_rows (id) VALUES {values}");
        Execute(db, $"INSERT INTO lookup_rows (id) VALUES {values}");

        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO target_rows (total) "
            + "SELECT COUNT(*) FROM source_rows AS s INNER JOIN lookup_rows AS l ON s.id = l.id",
            options: new SqlExecutionOptions { MaxTriggerTransitionBytes = 1_024 }));

        Assert.Equal(RoutineErrorCodes.TransitionLimit, error.Code);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT * FROM target_rows")).Rows);
    }

    [Fact]
    public void InsertSelect_SortedTopOne_RetainsOnlySelectedSourceRow()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, PRIMARY KEY (id))");
        string values = string.Join(", ", Enumerable.Range(1, 20).Select(static id => $"({id})"));
        Execute(db, $"INSERT INTO source_rows (id) VALUES {values}");

        var inserted = Assert.IsType<InsertExecutionResult>(Execute(db,
            "INSERT INTO target_rows (id) SELECT id FROM source_rows ORDER BY id DESC LIMIT 1",
            options: new SqlExecutionOptions { MaxTriggerTransitionRows = 2 }));

        Assert.Equal(1, inserted.RowsInserted);
        Assert.Equal(20L, Assert.Single(Assert.IsType<SelectExecutionResult>(
            Execute(db, "SELECT id FROM target_rows")).Rows)[0]);
    }

    [Fact]
    public void InsertSelect_SortedSpillOutput_RejectsOversizedSourceBeforeWrite()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source_rows (id INT, payload STRING, PRIMARY KEY (id))");
        Execute(db, "CREATE TABLE target_rows (id INT, payload STRING, PRIMARY KEY (id))");
        string payload = new('x', 1_000);
        string values = string.Join(", ", Enumerable.Range(1, 10)
            .Select(id => $"({id}, '{payload}')"));
        Execute(db, $"INSERT INTO source_rows (id, payload) VALUES {values}");

        var error = Assert.Throws<RoutineExecutionException>(() => Execute(db,
            "INSERT INTO target_rows (id, payload) "
            + "SELECT id, payload FROM source_rows ORDER BY id DESC LIMIT 3",
            options: new SqlExecutionOptions { MaxTriggerTransitionBytes = 2_500 }));

        Assert.Equal(RoutineErrorCodes.TransitionLimit, error.Code);
        Assert.Empty(Assert.IsType<SelectExecutionResult>(Execute(db, "SELECT * FROM target_rows")).Rows);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static object? Execute(
        Tsdb db,
        string sql,
        SqlTransactionContext? transaction = null,
        SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            options ?? SqlExecutionOptions.Default);
}
