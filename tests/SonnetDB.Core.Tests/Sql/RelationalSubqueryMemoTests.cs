using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class RelationalSubqueryMemoTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-subquery-memo-{Guid.NewGuid():N}");

    [Fact]
    public void SelectProjection_NonCorrelatedSubquery_ExecutesOnce()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id, (SELECT value FROM memo_singleton) AS value
            FROM memo_outer o
            ORDER BY o.id
            """);

        Assert.Equal(["constant", "constant", "constant"],
            result.Rows.Select(static row => (string)row[1]!));
        Assert.Equal(1, metrics.SubqueryExecutionCount);
        Assert.Equal(2, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void SelectProjection_CorrelatedSubquery_ExecutesPerOuterRow()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id, (SELECT l.label FROM memo_lookup l WHERE l.id = o.id) AS label
            FROM memo_outer o
            ORDER BY o.id
            """);

        Assert.Equal(["gamma", "alpha", "beta"],
            result.Rows.Select(static row => (string)row[1]!));
        Assert.Equal(3, metrics.SubqueryExecutionCount);
        Assert.Equal(0, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void OrderBy_NonCorrelatedSubquery_ExecutesOnce()
    {
        using var db = CreateDatabase();

        const string sql = """
            SELECT o.id
            FROM memo_outer o
            ORDER BY o.id DESC, (SELECT value FROM memo_singleton)
            """;
        var dispatched = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
        var (result, metrics) = Execute(db, sql);

        Assert.Equal([3L, 2L, 1L], dispatched.Rows.Select(static row => (long)row[0]!));
        Assert.Equal([3L, 2L, 1L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(1, metrics.SubqueryExecutionCount);
        Assert.Equal(2, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void OrderBy_CorrelatedSubquery_ExecutesPerOuterRow()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id
            FROM memo_outer o
            ORDER BY (SELECT l.label FROM memo_lookup l WHERE l.id = o.id)
            """);

        Assert.Equal([2L, 3L, 1L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(3, metrics.SubqueryExecutionCount);
        Assert.Equal(0, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void JoinOn_NonCorrelatedSubquery_ExecutesOnceAcrossCandidates()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id, l.label
            FROM memo_outer o
            JOIN memo_lookup l ON (SELECT value FROM memo_singleton) = 'constant'
                AND o.id = l.id
            ORDER BY o.id
            """);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(1, metrics.SubqueryExecutionCount);
        Assert.Equal(8, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void JoinOn_CorrelatedSubquery_ExecutesPerCandidate()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id, l.label
            FROM memo_outer o
            JOIN memo_lookup l
                ON (SELECT x.id FROM memo_lookup x WHERE x.id = o.id) = l.id
            ORDER BY o.id
            """);

        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(9, metrics.SubqueryExecutionCount);
        Assert.Equal(0, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void FunctionArgument_NonCorrelatedSubquery_ExecutesOnce()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id, upper((SELECT value FROM memo_singleton)) AS value
            FROM memo_outer o
            ORDER BY o.id
            """);

        Assert.Equal(["CONSTANT", "CONSTANT", "CONSTANT"],
            result.Rows.Select(static row => (string)row[1]!));
        Assert.Equal(1, metrics.SubqueryExecutionCount);
        Assert.Equal(2, metrics.SubqueryCacheHitCount);
    }

    [Fact]
    public void FunctionArgument_CorrelatedSubquery_ExecutesPerOuterRow()
    {
        using var db = CreateDatabase();

        var (result, metrics) = Execute(db, """
            SELECT o.id, upper((SELECT l.label FROM memo_lookup l WHERE l.id = o.id)) AS label
            FROM memo_outer o
            ORDER BY o.id
            """);

        Assert.Equal(["GAMMA", "ALPHA", "BETA"],
            result.Rows.Select(static row => (string)row[1]!));
        Assert.Equal(3, metrics.SubqueryExecutionCount);
        Assert.Equal(0, metrics.SubqueryCacheHitCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private Tsdb CreateDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE memo_outer (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE memo_lookup (id INT, label STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE memo_singleton (id INT, value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO memo_outer (id) VALUES (1), (2), (3)");
        SqlExecutor.Execute(db, """
            INSERT INTO memo_lookup (id, label) VALUES (1, 'gamma'), (2, 'alpha'), (3, 'beta')
            """);
        SqlExecutor.Execute(db, "INSERT INTO memo_singleton (id, value) VALUES (1, 'constant')");
        return db;
    }

    private static (SelectExecutionResult Result, RelationalSelectExecutionMetrics Metrics) Execute(
        Tsdb db,
        string sql)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        var metrics = new RelationalSelectExecutionMetrics();
        return (RelationalSelectExecutor.Execute(db, statement, metrics), metrics);
    }
}
