using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlCteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-cte-sql-" + Guid.NewGuid().ToString("N"));

    public SqlCteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖断言结果。
        }
    }

    [Fact]
    public void Parse_WithMultipleCtes_CapturesDefinitionsInOrder()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "WITH base AS (SELECT id FROM devices), filtered AS (SELECT id FROM base WHERE id > 1) SELECT id FROM filtered"));

        Assert.Equal(["base", "filtered"], statement.CommonTableExpressions.Select(cte => cte.Name));
        Assert.Equal("devices", statement.CommonTableExpressions[0].Query.Measurement);
        Assert.Equal("base", statement.CommonTableExpressions[1].Query.Measurement);
    }

    [Fact]
    public void Execute_SingleCteFromSource_ReturnsFilteredRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH filtered AS (
                SELECT id, region FROM devices WHERE region = 'west'
            )
            SELECT id FROM filtered ORDER BY id
            """));

        Assert.Equal(["id"], result.Columns);
        Assert.Equal([1L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_MultipleCtesAndInSubquery_UsesEarlierCte()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH west AS (
                SELECT id FROM devices WHERE region = 'west'
            ), selected AS (
                SELECT id FROM west WHERE id > 1
            )
            SELECT id FROM devices WHERE id IN (SELECT id FROM selected) ORDER BY id
            """));

        Assert.Equal([3L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_CteInCorrelatedExistsSubquery_ReturnsMatchingRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH west AS (
                SELECT id FROM devices WHERE region = 'west'
            )
            SELECT d.id FROM devices AS d
            WHERE EXISTS (SELECT 1 FROM west AS w WHERE w.id = d.id)
            ORDER BY d.id
            """));

        Assert.Equal([1L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_CteColumnList_ReportsExplicitBoundary()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(db, """
            WITH filtered (device_id) AS (SELECT id FROM devices)
            SELECT device_id FROM filtered
            """));

        Assert.Contains("CTE 输出列名列表", exception.Message, StringComparison.Ordinal);
    }

    private static void CreateDevices(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, region STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, region) VALUES (1, 'west'), (2, 'east'), (3, 'west')");
    }
}
