using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证数学标量函数在 SQL 投影、谓词、分组和 UPDATE 路径中的统一行为。
/// </summary>
public sealed class SqlMathScalarFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-math-" + Guid.NewGuid().ToString("N"));

    public SqlMathScalarFunctionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Select_MathFunctions_ReturnDoubleResultsAndPropagateNull()
    {
        using var database = OpenWithValues();

        var result = Query(database,
            "SELECT ceil(value), floor(value), exp(value), power(base_value, exponent), "
            + "ceiling(value), pow(base_value, exponent) FROM math_values ORDER BY id");

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(
            new object?[] { -1d, -2d, Math.Exp(-1.2d), 1024d, -1d, 1024d },
            result.Rows[0]);
        Assert.All(result.Rows[2], Assert.Null);
    }

    [Fact]
    public void MathFunctions_WorkInWhereGroupHavingUpdateAndParameters()
    {
        using var database = OpenWithValues();

        var filtered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: null,
            "SELECT id FROM math_values WHERE power(base_value, exponent) >= ? ORDER BY id",
            new SqlParameters().AddPositional(8),
            controlPlane: null));
        Assert.Equal(new object?[] { 1L, 2L }, filtered.Rows.Select(static row => row[0]).ToArray());

        var grouped = Query(database,
            "SELECT group_key, floor(avg(value)) AS bucket FROM math_values "
            + "WHERE value IS NOT NULL GROUP BY group_key "
            + "HAVING floor(avg(value)) >= 0 ORDER BY group_key");
        Assert.Equal(new object?[] { "a", 0d }, grouped.Rows.Single());

        SqlExecutor.Execute(
            database,
            databaseName: null,
            "UPDATE math_values SET value = ceil(value) WHERE id = ?",
            new SqlParameters().AddPositional(2),
            controlPlane: null);
        var updated = Query(database, "SELECT value FROM math_values WHERE id = 2");
        Assert.Equal(3d, updated.Rows.Single()[0]);
    }

    private Tsdb OpenWithValues()
    {
        var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE math_values (id INT, group_key STRING, value FLOAT, "
            + "base_value FLOAT, exponent FLOAT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO math_values (id, group_key, value, base_value, exponent) VALUES "
            + "(1, 'a', -1.2, 2, 10), (2, 'a', 2.8, 2, 4), "
            + "(3, 'b', NULL, NULL, NULL)");
        return database;
    }

    private static SelectExecutionResult Query(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
