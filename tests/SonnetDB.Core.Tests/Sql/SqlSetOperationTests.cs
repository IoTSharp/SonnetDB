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

    private static void CreateRows(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE left_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE right_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO left_rows (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "INSERT INTO right_rows (id) VALUES (2), (3)");
    }
}
