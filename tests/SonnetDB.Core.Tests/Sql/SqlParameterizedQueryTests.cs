using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// SQL 参数化查询（#213）：占位符解析、参数绑定与执行的 Core 层测试。
/// </summary>
public sealed class SqlParameterizedQueryTests : IDisposable
{
    private readonly string _root;

    public SqlParameterizedQueryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-param-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    // ── 解析：占位符产出 ParameterExpression ─────────────────────────────────

    [Fact]
    public void Parse_PositionalPlaceholder_ProducesParameterExpression()
    {
        SqlParser.ClearParseCache();
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu WHERE host = ? AND value > ?");
        var and = Assert.IsType<BinaryExpression>(stmt.Where);

        var right = Assert.IsType<BinaryExpression>(and.Right);
        var p1 = Assert.IsType<ParameterExpression>(right.Right);
        Assert.Equal(1, p1.Ordinal);
        Assert.Null(p1.Name);

        var left = Assert.IsType<BinaryExpression>(and.Left);
        var p0 = Assert.IsType<ParameterExpression>(left.Right);
        Assert.Equal(0, p0.Ordinal);
    }

    [Fact]
    public void Parse_NamedPlaceholder_CapturesName()
    {
        SqlParser.ClearParseCache();
        var stmt = (SelectStatement)SqlParser.Parse("SELECT * FROM cpu WHERE host = @h");
        var cmp = Assert.IsType<BinaryExpression>(stmt.Where);
        var param = Assert.IsType<ParameterExpression>(cmp.Right);
        Assert.Equal("h", param.Name);
    }

    [Fact]
    public void Parse_SamePlaceholderSql_HitsCacheRegardlessOfValues()
    {
        SqlParser.ClearParseCache();
        var a = SqlParser.Parse("SELECT * FROM cpu WHERE host = ?");
        var b = SqlParser.Parse("SELECT * FROM cpu WHERE host = ?");
        // 带占位符的 AST 与参数值无关，可被缓存复用。
        Assert.Same(a, b);
    }

    // ── 绑定 + 执行 ──────────────────────────────────────────────────────────

    private Tsdb OpenTable()
    {
        var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, active BOOL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, name, active) VALUES (1, 'pump', TRUE), (2, 'fan', FALSE), (3, 'valve', TRUE)");
        return db;
    }

    private static SelectExecutionResult Query(Tsdb db, string sql, SqlParameters p)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, databaseName: null, sql, p, controlPlane: null));

    [Fact]
    public void Execute_PositionalParameters_BindAndFilter()
    {
        using var db = OpenTable();
        var p = new SqlParameters().AddPositional("pump").AddPositional(true);

        var r = Query(db, "SELECT id FROM devices WHERE name = ? AND active = ?", p);

        Assert.Single(r.Rows);
        Assert.Equal(1L, r.Rows[0][0]);
    }

    [Fact]
    public void Execute_NamedParameters_BindAndFilter()
    {
        using var db = OpenTable();
        var p = new SqlParameters().AddNamed("active", true);

        var r = Query(db, "SELECT id FROM devices WHERE active = @active", p);

        Assert.Equal([1L, 3L], r.Rows.Select(row => (long)row[0]!).OrderBy(x => x));
    }

    [Fact]
    public void Execute_ParameterInInsert_BindsValues()
    {
        using var db = OpenTable();
        var p = new SqlParameters().AddPositional(4).AddPositional("meter").AddPositional(false);

        SqlExecutor.Execute(db, databaseName: null,
            "INSERT INTO devices (id, name, active) VALUES (?, ?, ?)", p, controlPlane: null);

        var r = Query(db, "SELECT name FROM devices WHERE id = ?", new SqlParameters().AddPositional(4));
        Assert.Equal("meter", r.Rows.Single()[0]);
    }

    [Fact]
    public void Execute_NullParameter_BindsSqlNull()
    {
        using var db = OpenTable();
        // name = NULL 按三值逻辑为 UNKNOWN，无行匹配（验证 null 绑定为 SQL NULL 而非字符串 "null"）。
        var r = Query(db, "SELECT id FROM devices WHERE name = ?", new SqlParameters().AddPositional(null));
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Execute_MissingParameterValue_Throws()
    {
        using var db = OpenTable();
        // 只提供一个值，但 SQL 有两个占位符。
        var p = new SqlParameters().AddPositional("pump");
        Assert.ThrowsAny<Exception>(() =>
            SqlExecutor.Execute(db, databaseName: null,
                "SELECT id FROM devices WHERE name = ? AND active = ?", p, controlPlane: null));
    }

    [Fact]
    public void Execute_StringParameter_IsNotInjectable()
    {
        using var db = OpenTable();
        // 恶意值作为参数值绑定为字符串字面量，不会被解释为 SQL——不匹配任何 name，返回空。
        var p = new SqlParameters().AddPositional("pump' OR '1'='1");
        var r = Query(db, "SELECT id FROM devices WHERE name = ?", p);
        Assert.Empty(r.Rows);
    }
}
