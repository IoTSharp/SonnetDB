using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #173：显式 CAST 的解析、投影和错误合同。</summary>
public sealed class SqlCastTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-cast-" + Guid.NewGuid().ToString("N"));

    public SqlCastTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_Cast_StoresTargetType()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT CAST(value AS INT), CAST(value AS DATETIME) FROM cpu"));

        var first = Assert.IsType<CastExpression>(statement.Projections[0].Expression);
        var second = Assert.IsType<CastExpression>(statement.Projections[1].Expression);
        Assert.Equal(SqlDataType.Int64, first.TargetType);
        Assert.Equal(SqlDataType.DateTime, second.TargetType);
    }

    [Fact]
    public void Execute_Cast_LiteralAndFieldValues_ReturnsTypedResults()
    {
        using var db = OpenDatabase();
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, value, text_value) VALUES "
            + "(0, 'a', 1.9, '42')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT CAST(value AS INT), CAST(text_value AS INT), "
            + "CAST('true' AS BOOL), CAST('abc' AS BLOB), "
            + "CAST('{\"ok\":true}' AS JSON) FROM cpu"));

        var row = Assert.Single(result.Rows);
        Assert.Equal(1L, row[0]);
        Assert.Equal(42L, row[1]);
        Assert.Equal(true, row[2]);
        Assert.Equal(Encoding.UTF8.GetBytes("abc"), Assert.IsType<byte[]>(row[3]));
        Assert.Equal("{\"ok\":true}", row[4]);
    }

    [Fact]
    public void Execute_Cast_NullAndDateTime_UseStableNullAndUtcSemantics()
    {
        using var db = OpenDatabase();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT CAST(NULL AS INT), CAST('2026-01-02T03:04:05Z' AS DATETIME)"));

        var row = Assert.Single(result.Rows);
        Assert.Null(row[0]);
        var timestamp = Assert.IsType<DateTime>(row[1]);
        Assert.Equal(DateTimeKind.Utc, timestamp.Kind);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), timestamp);
    }

    [Fact]
    public void Execute_Cast_UnsupportedTargetAndInvalidValue_FailClosed()
    {
        using var db = OpenDatabase();

        var unsupported = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db, "SELECT CAST(value AS VECTOR) FROM cpu"));
        Assert.Contains("CAST 当前不支持目标类型 Vector", unsupported.Message, StringComparison.Ordinal);

        var invalid = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db, "SELECT CAST('not-a-number' AS INT) FROM cpu"));
        Assert.Contains("不是有效的 Int64", invalid.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("embedding VECTOR(3)", "关系表 MVP 暂不支持 VECTOR")]
    [InlineData("position GEOPOINT", "关系表 MVP 暂不支持 GEOPOINT 类型")]
    public void RelationTable_VectorAndGeoPointColumns_RejectWithExplicitBoundary(
        string columnDefinition,
        string expectedMessage)
    {
        var exception = Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            $"CREATE TABLE relation_types (id INT, {columnDefinition}, PRIMARY KEY (id))"));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TableCast_ColumnProjectionAndPredicate_UseConvertedValue()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE values_table (id INT, value FLOAT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO values_table (id, value) VALUES (1, 1.9), (2, 2.1)");

        var projected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT id, CAST(value AS INT) AS rounded FROM values_table ORDER BY id"));
        Assert.Equal(new object?[] { 1L, 1L }, projected.Rows[0]);
        Assert.Equal(new object?[] { 2L, 2L }, projected.Rows[1]);

        var filtered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT id FROM values_table WHERE CAST(value AS INT) = 2"));
        Assert.Equal(new object?[] { 2L }, Assert.Single(filtered.Rows));
    }

    [Theory]
    [InlineData("VECTOR", "Vector")]
    [InlineData("GEOPOINT", "GeoPoint")]
    public void SelectCast_UnsupportedTarget_ThrowsStableError(string target, string displayName)
    {
        using var db = OpenDatabase();
        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db, $"SELECT CAST(1 AS {target}) FROM cpu"));
        Assert.Contains($"CAST 当前不支持目标类型 {displayName}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Cast_InvalidBoolean_ReportsDeterministicError()
    {
        using var db = OpenDatabase();
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db, "SELECT CAST('maybe' AS BOOL) FROM cpu"));
        Assert.Contains("不是有效的 BOOL", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_Cast_ParameterValue_IsConverted()
    {
        using var db = OpenDatabase();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            null,
            "SELECT CAST(@value AS INT)",
            new SqlParameters().AddNamed("value", "7")));
        Assert.Equal(7L, Assert.Single(result.Rows)[0]);
    }

    private Tsdb OpenDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT, text_value FIELD STRING)");
        return db;
    }
}
