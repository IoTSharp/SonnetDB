using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #178：SQL 集合运算的解析与执行合同。</summary>
public sealed class SqlSetOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-set-operation-" + Guid.NewGuid().ToString("N"));

    public SqlSetOperationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_UnionAllIntersectExcept_CapturesOperationKinds()
    {
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(
            "SELECT id FROM left_rows UNION ALL SELECT id FROM right_rows INTERSECT SELECT id FROM right_rows EXCEPT SELECT id FROM left_rows"));

        Assert.Equal(
            [
                SonnetDB.Sql.Ast.SqlSetOperationKind.UnionAll,
                SonnetDB.Sql.Ast.SqlSetOperationKind.Intersect,
                SonnetDB.Sql.Ast.SqlSetOperationKind.Except,
            ],
            statement.SetOperationList.Select(operation => operation.Kind).ToArray());
    }

    [Fact]
    public void Execute_UnionAll_PreservesDuplicates_WhileIntersectAndExceptDeduplicate()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateRows(db);

        var unionAll = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM left_rows UNION ALL SELECT id FROM right_rows ORDER BY id"));
        Assert.Equal([1L, 2L, 2L, 3L], unionAll.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());

        var intersect = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM left_rows INTERSECT SELECT id FROM right_rows ORDER BY id"));
        Assert.Equal([2L], intersect.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());

        var except = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM left_rows EXCEPT SELECT id FROM right_rows ORDER BY id"));
        Assert.Equal([1L], except.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_SetOperation_WithMismatchedColumns_ReportsStableError()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateRows(db);

        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "SELECT id FROM left_rows UNION SELECT id, id FROM right_rows"));
        Assert.Contains("集合运算分支列数不一致", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_IntersectBeforeUnionAndExcept_UsesStandardPrecedence()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateRows(db);
        SqlExecutor.Execute(db, "CREATE TABLE third_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO third_rows (id) VALUES (3)");

        var union = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM left_rows UNION SELECT id FROM right_rows "
            + "INTERSECT SELECT id FROM third_rows ORDER BY id"));
        Assert.Equal([1L, 2L, 3L], union.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());

        var except = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM left_rows EXCEPT SELECT id FROM right_rows "
            + "INTERSECT SELECT id FROM third_rows ORDER BY id"));
        Assert.Equal([1L, 2L], except.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_UnionAll_WithoutOrderBy_PreservesBranchAndRowOrder()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateRows(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM right_rows UNION ALL SELECT id FROM left_rows LIMIT 3"));

        Assert.Equal([2L, 3L, 1L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
        Assert.Equal("id", Assert.Single(result.Columns));
    }

    [Fact]
    public void Execute_IntersectAndExcept_WithNullDuplicatesAndMultipleColumns_UseSetEquality()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE set_left (id INT, a INT NULL, b STRING NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE set_right (id INT, a INT NULL, b STRING NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO set_left (id, a, b) VALUES (1, NULL, 'x'), (2, NULL, 'x'), (3, 1, NULL), (4, 2, 'y')");
        SqlExecutor.Execute(db, "INSERT INTO set_right (id, a, b) VALUES (10, NULL, 'x'), (11, NULL, 'x'), (12, 1, NULL), (13, 3, 'z')");

        var intersect = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT a AS first_value, b FROM set_left INTERSECT SELECT a, b FROM set_right"));
        Assert.Equal(["first_value", "b"], intersect.Columns);
        Assert.Collection(intersect.Rows,
            row => { Assert.Null(row[0]); Assert.Equal("x", row[1]); },
            row => { Assert.Equal(1L, row[0]); Assert.Null(row[1]); });

        var except = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT a, b FROM set_left EXCEPT SELECT a, b FROM set_right"));
        Assert.Collection(except.Rows,
            row => { Assert.Equal(2L, row[0]); Assert.Equal("y", row[1]); });
    }

    [Fact]
    public void Execute_SetOperation_WithDifferentDeclaredTypesEvenWhenEmpty_ReportsColumn()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE set_ints (id INT, value INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE set_texts (id INT, value STRING NULL, PRIMARY KEY (id))");

        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "SELECT value FROM set_ints UNION ALL SELECT value FROM set_texts"));

        Assert.Contains("第 1 列的分支类型不一致", error.Message, StringComparison.Ordinal);
        Assert.Contains("CAST", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_SetOperation_OverMemoryBudget_RejectsAndReleasesReservation()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(
            "SELECT value FROM first_rows UNION ALL SELECT value FROM second_rows"));
        using (SqlQueryResources.EnterRoot(db,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 256 }))
        {
            var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteUnion(
                statement,
                branch => new SelectExecutionResult(["value"], [[new string('x', 100)]])));
            Assert.Contains("集合运算保留行", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Fact]
    public void Execute_Intersect_WithCloseDecimalValues_KeepsExactComparison()
    {
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(
            "SELECT value FROM first_rows INTERSECT SELECT value FROM second_rows"));

        var result = SqlExecutor.ExecuteUnion(statement, branch => new SelectExecutionResult(
            ["value"],
            branch.Measurement == "first_rows"
                ? [[1.000000000000000000000000001m]]
                : [[1.000000000000000000000000002m]]));

        Assert.Empty(result.Rows);
    }

    [Fact]
    public void Execute_UnionAll_WithDifferentRuntimeTypes_ReportsColumn()
    {
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(
            "SELECT value FROM first_rows UNION ALL SELECT value FROM second_rows"));

        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteUnion(
            statement,
            branch => new SelectExecutionResult(["value"],
                branch.Measurement == "first_rows" ? [[1L]] : [["one"]])));

        Assert.Contains("第 1 列的分支类型不一致", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_UnionAll_WithManyRowsOverMemoryBudget_RejectsAndReleasesReservation()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(
            "SELECT value FROM first_rows UNION ALL SELECT value FROM second_rows"));
        var manyRows = Enumerable.Range(0, 20)
            .Select(index => (IReadOnlyList<object?>)new object?[] { (long)index })
            .ToArray();

        using (SqlQueryResources.EnterRoot(db,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 256 }))
        {
            var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteUnion(
                statement, _ => new SelectExecutionResult(["value"], manyRows)));
            Assert.Contains("集合运算保留行", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    private static void CreateRows(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE left_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE right_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO left_rows (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "INSERT INTO right_rows (id) VALUES (2), (3)");
    }
}
