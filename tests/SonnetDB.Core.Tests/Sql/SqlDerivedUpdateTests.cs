using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlDerivedUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-derived-update-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("UPDATE target AS t SET value=s.value FROM (VALUES(1,6001),(2,6002)) AS s(id,value) WHERE t.id=s.id")]
    [InlineData("UPDATE target AS t JOIN (SELECT 1 AS id,6001 AS value UNION ALL SELECT 2 AS id,6002 AS value) AS s ON t.id=s.id SET value=s.value WHERE 1=1")]
    public void Update_DerivedIssueExamples_UpdatesBothRows(string sql)
    {
        using var db = Open();
        Create(db);
        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, sql + " RETURNING id,value"));
        Assert.Equal(2, result.RowsAffected);
        Assert.Equal(new object?[] { new object?[] { 1L, 6001L }, new object?[] { 2L, 6002L } }, result.Returning!.Rows);
    }

    [Fact]
    public void Update_ParameterizedTenThousandRows_IsOneCorrectBatch()
    {
        using var db = Open();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        SqlExecutor.Execute(db, "CREATE TABLE target(id INT,value INT,PRIMARY KEY(id))");
        var insert = new StringBuilder("INSERT INTO target(id,value) VALUES");
        var update = new StringBuilder("UPDATE target AS t SET value=s.value FROM (VALUES");
        var parameters = new SqlParameters();
        for (int i = 1; i <= 10_000; i++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (i > 1) { insert.Append(','); update.Append(','); }
            insert.Append('(').Append(i).Append(",0)");
            update.Append("(@id").Append(i).Append(",@v").Append(i).Append(')');
            parameters.AddNamed("id" + i, i).AddNamed("v" + i, i + 6000);
        }
        SqlExecutor.Execute(db, insert.ToString());
        update.Append(") AS s(id,value) WHERE t.id=s.id");
        var bound = SqlParameterBinder.Bind(SqlParser.Parse(update.ToString()), parameters);
        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.ExecuteStatement(db, null, bound, null, null,
            new SqlExecutionOptions { CancellationToken = cancellation.Token, DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(90) }));
        Assert.Equal(10_000, result.RowsAffected);
        var rows = Select(db, "SELECT id,value FROM target ORDER BY id").Rows;
        Assert.Equal(10_000, rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Assert.Equal((long)i + 1, rows[i][0]);
            Assert.Equal((long)i + 6001, rows[i][1]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_ContinuousOverlay_SeesInsertUpdateDeleteSourceAndSelf(bool commit)
    {
        using var db = Open();
        Create(db);
        SqlExecutor.Execute(db, "CREATE TABLE source(id INT,value INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO source(id,value) VALUES(1,30),(2,40)");
        var tx = new SqlTransactionContext();
        Execute(db, tx, "UPDATE target SET value=11 WHERE id=1");
        Execute(db, tx, "DELETE FROM target WHERE id=2");
        Execute(db, tx, "INSERT INTO target(id,value) VALUES(3,13)");
        Execute(db, tx, "UPDATE source SET value=31 WHERE id=1");
        Execute(db, tx, "INSERT INTO source(id,value) VALUES(3,33)");
        Execute(db, tx, "UPDATE target AS t JOIN (SELECT id,value FROM source) AS s ON t.id=s.id SET value=t.value+s.value WHERE t.id>0");
        Execute(db, tx, "UPDATE target AS t SET value=t.value+s.value FROM (VALUES(1,5),(3,7)) AS s(id,value) WHERE t.id=s.id");
        Execute(db, tx, "UPDATE target AS t JOIN target AS s ON t.id=s.id SET value=s.value+1 WHERE t.id>0");
        var selected = Assert.IsType<SelectExecutionResult>(Execute(db, tx, "SELECT id,value FROM target ORDER BY id"));
        Assert.Equal(new object?[] { new object?[] { 1L, 48L }, new object?[] { 3L, 54L } }, selected.Rows);
        Execute(db, tx, commit ? "COMMIT" : "ROLLBACK");
        Assert.Equal(commit
            ? new object?[] { new object?[] { 1L, 48L }, new object?[] { 3L, 54L } }
            : new object?[] { new object?[] { 1L, 10L }, new object?[] { 2L, 20L } },
            Select(db, "SELECT id,value FROM target ORDER BY id").Rows);
    }

    [Fact]
    public void Update_LateDecimalFailure_KeepsEarlierTransactionWorkAndCanContinue()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE target(id INT,value DECIMAL(5,2),PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO target(id,value) VALUES(1,'1.25'),(2,'2.25')");
        var tx = new SqlTransactionContext();
        Execute(db, tx, "UPDATE target SET value='3.25' WHERE id=1");
        Assert.ThrowsAny<Exception>(() => Execute(db, tx,
            "UPDATE target AS t SET value=s.value FROM (VALUES(1,'4.25'),(2,'1.234')) AS s(id,value) WHERE t.id=s.id"));
        Assert.Equal(3.25m, Assert.IsType<SelectExecutionResult>(Execute(db, tx, "SELECT value FROM target WHERE id=1")).Rows[0][0]);
        Execute(db, tx, "UPDATE target AS t SET value=s.value FROM (VALUES(2,'5.25')) AS s(id,value) WHERE t.id=s.id");
        Execute(db, tx, "COMMIT");
        Assert.Equal(new object?[] { new object?[] { 3.25m }, new object?[] { 5.25m } }, Select(db, "SELECT value FROM target ORDER BY id").Rows);
    }

    [Theory]
    [InlineData("UPDATE target AS t JOIN (SELECT id,value FROM target WHERE id<0) AS s ON t.id=s.missing SET value=s.value WHERE t.id>0")]
    [InlineData("UPDATE target AS t JOIN (SELECT id,value FROM target WHERE id<0) AS s ON t.id=s.id SET value=s.missing WHERE t.id>0")]
    [InlineData("UPDATE target AS t SET value=s.value FROM (SELECT id,value FROM target WHERE id<0) AS s WHERE t.id=s.missing")]
    public void Update_EmptyDerivedSource_StillRejectsUnknownColumns(string sql)
    {
        using var db = Open();
        Create(db);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, sql));
        Assert.Equal(10L, Select(db, "SELECT value FROM target WHERE id=1").Rows[0][0]);
    }

    [Fact]
    public void Update_DerivedParameters_PreservesDecimalTimeUtcNullAndQuotedNames()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE target(id INT,amount DECIMAL,clock TIME,stamp DATETIME,note STRING,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO target(id) VALUES(1),(2)");
        decimal exact = 12345678901234567890.12345678m;
        var clock = TimeSpan.FromTicks(123456789);
        var stamp = new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc);
        var parameters = new SqlParameters().AddNamed("amount", exact).AddNamed("clock", clock).AddNamed("stamp", stamp);
        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, null, """
            UPDATE target AS t SET amount=s."Money",clock=s."Clock",stamp=s."Stamp",note=s."a""b"
            FROM (VALUES(1,@amount,@clock,@stamp,NULL),(2,NULL,@clock,@stamp,'O''Brien')) AS s(id,"Money","Clock","Stamp","a""b")
            WHERE t.id=s.id RETURNING amount,clock,stamp,note
            """, parameters, null));
        Assert.Equal(2, result.RowsAffected);
        var first = result.Returning!.Rows[0];
        Assert.Equal(exact, first[0]); Assert.Equal(clock.Ticks, Assert.IsType<TimeOnly>(first[1]).Ticks); Assert.Equal(stamp, first[2]); Assert.Null(first[3]);
        Assert.Null(result.Returning.Rows[1][0]); Assert.Equal("O'Brien", result.Returning.Rows[1][3]);
        var selected = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, null,
            "UPDATE target AS t JOIN (SELECT 1 AS id,@amount AS money) AS s ON t.id=s.id SET amount=s.money WHERE t.id=1 RETURNING amount", parameters, null));
        Assert.Equal(exact, Assert.Single(selected.Returning!.Rows)[0]);
    }

    [Fact]
    public void Update_DuplicateDerivedMatchesAndBufferedSourceDelete_PreservesFirstMatch()
    {
        using var db = Open();
        Create(db);
        var duplicate = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE target AS t JOIN (VALUES(1,50),(1,60)) AS s(id,value) ON t.id=s.id SET value=s.value WHERE t.id=1 RETURNING value"));
        Assert.Equal(1, duplicate.RowsAffected);
        Assert.Equal(50L, Assert.Single(duplicate.Returning!.Rows)[0]);
        SqlExecutor.Execute(db, "CREATE TABLE source(id INT,value INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO source(id,value) VALUES(1,100),(2,200)");
        var tx = new SqlTransactionContext();
        Execute(db, tx, "DELETE FROM source WHERE id=1");
        var update = Assert.IsType<RowsAffectedExecutionResult>(Execute(db, tx,
            "UPDATE target AS t JOIN source AS s ON t.id=s.id SET value=s.value WHERE t.id>0"));
        Assert.Equal(1, update.RowsAffected);
        Execute(db, tx, "COMMIT");
        Assert.Equal(new object?[] { new object?[] { 1L, 50L }, new object?[] { 2L, 200L } }, Select(db, "SELECT id,value FROM target ORDER BY id").Rows);
    }

    [Fact]
    public void Select_DerivedColumnAliasesAndNullFirstRow_PreservesDeclarations()
    {
        using var db = Open();
        Create(db);
        var result = Select(db, """
            SELECT s."Money",s."Clock",s."a.b" FROM target AS t
            JOIN (VALUES(1,NULL,CAST('00:00:12.3456789' AS TIME),'one'),(2,CAST('1.2300' AS DECIMAL),CAST('00:00:12.3456789' AS TIME),'two')) AS s(id,"Money","Clock","a.b")
            ON t.id=s.id ORDER BY t.id
            """);
        Assert.Null(result.Rows[0][0]);
        Assert.Equal(1.2300m, result.Rows[1][0]);
        Assert.Equal(SonnetDB.Tables.TableColumnType.Decimal, result.ColumnInfo![0].DataType);
        Assert.Equal(SonnetDB.Tables.TableColumnType.Time, result.ColumnInfo[1].DataType);
        Assert.Equal(123456789, Assert.IsType<TimeOnly>(result.Rows[0][1]).Ticks);
        var alias = Select(db, """
            SELECT s."a.b" FROM target AS t JOIN (SELECT 1 AS id,'quoted' AS "a.b") AS s ON t.id=s.id
            """);
        Assert.Equal("quoted", Assert.Single(alias.Rows)[0]);
        var renamed = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE target AS t SET value=s.value FROM (SELECT 1 AS original,99 AS old) AS s(id,value) WHERE t.id=s.id RETURNING value"));
        Assert.Equal(99L, Assert.Single(renamed.Returning!.Rows)[0]);
    }

    [Theory]
    [InlineData("UPDATE target AS t SET value=s.value FROM (VALUES(1,2),(3)) AS s(id,value) WHERE t.id=s.id")]
    [InlineData("UPDATE target AS t SET value=s.value FROM (VALUES(1,2)) AS s(id,ID) WHERE t.id=s.id")]
    [InlineData("UPDATE target AS t SET value=s.value FROM (SELECT 1 AS id,2 AS value) AS s(id) WHERE t.id=s.id")]
    [InlineData("UPDATE target AS t SET value=s.\"value\" FROM (VALUES(1,2)) AS s(id,\"Value\") WHERE t.id=s.id")]
    public void Update_InvalidDerivedDeclaration_RejectsBeforeWriting(string sql)
    {
        using var db = Open();
        Create(db);
        Assert.ThrowsAny<Exception>(() => SqlExecutor.Execute(db, sql));
        Assert.Equal(10L, Select(db, "SELECT value FROM target WHERE id=1").Rows[0][0]);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Update_ValuesAliasShadowsCatalog_InDerivedMultiJoinUsesValues(bool indexedLookup)
    {
        using var db = Open();
        Create(db);
        CreateAliasShadowSources(db, indexedLookup);
        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db,
            "UPDATE target AS t SET value=d.value FROM (" + AliasShadowSourceSql + ") AS d WHERE t.id=d.id RETURNING value"));
        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(50L, Assert.Single(result.Returning!.Rows)[0]);
        Assert.Equal(999L, Assert.Single(Select(db, "SELECT value FROM v").Rows)[0]);
        var plan = RelationalSelectExecutor.ExplainJoinOperators(db,
            Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(AliasShadowSourceSql)));
        Assert.Equal("right_input_requires_runtime_materialization", plan.JoinOrderFallbackReason);
    }

    [Fact]
    public void Select_ValuesAliasShadowsCatalog_WithIndexablePeerPreservesIndexedRoute()
    {
        using var db = Open();
        CreateAliasShadowSources(db, indexedLookup: true);
        var metrics = new RelationalSelectExecutionMetrics();
        var result = RelationalSelectExecutor.ExecutePreservingJoinOrder(db,
            Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(SqlParser.Parse(AliasShadowSourceSql)), metrics);
        Assert.Equal(50L, Assert.Single(result.Rows)[1]);
        Assert.Equal("index_nested_loop", metrics.LastJoinOperator);
        Assert.Equal("primary", metrics.LastJoinIndexName);
        Assert.Equal(999L, Assert.Single(Select(db, "SELECT value FROM v").Rows)[0]);
    }

    private const string AliasShadowSourceSql = """
        SELECT a.id,v.value FROM source AS a
        JOIN (VALUES(1,50)) AS v(id,value) ON a.id=v.id
        JOIN lookup AS b ON a.id=b.id
        """;

    private static void CreateAliasShadowSources(Tsdb db, bool indexedLookup)
    {
        SqlExecutor.Execute(db, "CREATE TABLE source(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE TABLE v(id INT,value INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO source(id) VALUES(1)");
        SqlExecutor.Execute(db, "INSERT INTO v(id,value) VALUES(1,999)");
        var sql = new StringBuilder("INSERT INTO lookup(id) VALUES");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int count = indexedLookup ? 64 : 1;
        for (int i = 1; i <= count && watch.Elapsed < TimeSpan.FromSeconds(5); i++)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (i > 1) sql.Append(',');
            sql.Append('(').Append(i).Append(')');
        }
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        SqlExecutor.Execute(db, sql.ToString());
    }

    private static void Create(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE target(id INT,value INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(db, "INSERT INTO target(id,value) VALUES(1,10),(2,20)");
    }
    private static object? Execute(Tsdb db, SqlTransactionContext tx, string sql)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, tx,
            new SqlExecutionOptions { DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(30) });
    private static SelectExecutionResult Select(Tsdb db, string sql) => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
