using System.Text.Json;
using System.Text;
using System.Numerics;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #198：显式 Int64/STRING 边界及远程数值解析。</summary>
public sealed class SqlInt64BoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-int64-boundary-" + Guid.NewGuid().ToString("N"));

    public SqlInt64BoundaryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("18446744073709551616")]
    public void SqlLiteral_OutsideInt64_RejectsRatherThanRounding(string literal)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT {literal}"));
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    public void RemoteInteger_OutsideInt64_RejectsRatherThanRounding(string literal)
    {
        using var doc = JsonDocument.Parse(literal);
        var error = Assert.Throws<InvalidDataException>(() => RemoteExecutionResult.ReadScalar(doc.RootElement));
        Assert.Contains("Int64", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteMeta_WithoutColumnTypes_StillReadsLegacyStream()
    {
        using var response = new HttpResponseMessage();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"type\":\"meta\",\"columns\":[\"id\"]}\n[9007199254740993]\n"
            + "{\"type\":\"end\",\"rowCount\":1,\"recordsAffected\":-1}\n"));
        using var result = await RemoteExecutionResult.CreateAsync(response, stream, CancellationToken.None);
        Assert.True(result.ReadNextRow());
        Assert.Equal(9007199254740993L, result.GetValue(0));
        Assert.Equal(typeof(long), result.GetFieldType(0));
        Assert.False(result.ReadNextRow());
    }

    [Fact]
    public void CoreParameter_BigInteger_RejectsWithSameCode()
    {
        var error = Assert.Throws<SndbParameterTypeException>(() => SqlParameterBinder.ToLiteral(BigInteger.One));
        Assert.Equal(SndbParameterTypeException.BigIntegerUnsupportedCode, error.Code);
        Assert.Equal(error.Code, SqlErrorMapper.Map(error, "bind").Code);
    }

    [Fact]
    public void Query_Int64Boundary_UsesExactNumericOrderAndStringEscape()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE numbers (value INT, exact_text STRING, PRIMARY KEY (value))");
        SqlExecutor.Execute(db, "INSERT INTO numbers (value, exact_text) VALUES "
            + "(-9223372036854775808, '9223372036854775808'), "
            + "(9007199254740993, '9007199254740993'), "
            + "(9223372036854775807, '9223372036854775807')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT value, exact_text FROM numbers WHERE value > 9007199254740992 ORDER BY value"));
        Assert.Equal([9007199254740993L, long.MaxValue], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
        Assert.Equal("9223372036854775807", result.Rows[1][1]);
        var stringResult = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT value FROM numbers WHERE exact_text = '9223372036854775808'"));
        Assert.Equal(long.MinValue, Assert.Single(stringResult.Rows)[0]);
        var aggregate = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT MIN(value), MAX(value), COUNT(*) FROM numbers"));
        var aggregateRow = Assert.Single(aggregate.Rows);
        Assert.Equal(long.MinValue, aggregateRow[0]);
        Assert.Equal(long.MaxValue, aggregateRow[1]);
        Assert.Equal(3L, aggregateRow[2]);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "SELECT CAST('9223372036854775808' AS INT)"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "SELECT 9223372036854775807 + 1"));
    }

    [Fact]
    public void Parameters_Int64AndNull_KeepInsertUpdateWhereAndCountSemantics()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE parameter_numbers (id INT, value INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, null,
            "INSERT INTO parameter_numbers (id, value) VALUES (@id, @value)",
            new SqlParameters().AddNamed("id", long.MinValue).AddNamed("value", null), null);
        SqlExecutor.Execute(db, null,
            "INSERT INTO parameter_numbers (id, value) VALUES (@id, @value)",
            new SqlParameters().AddNamed("id", long.MaxValue).AddNamed("value", long.MaxValue), null);
        SqlExecutor.Execute(db, null,
            "UPDATE parameter_numbers SET value = @value WHERE id = @id",
            new SqlParameters().AddNamed("value", long.MinValue).AddNamed("id", long.MaxValue), null);

        var filtered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null,
            "SELECT value FROM parameter_numbers WHERE id = @id",
            new SqlParameters().AddNamed("id", long.MaxValue), null));
        Assert.Equal(long.MinValue, Assert.Single(filtered.Rows)[0]);

        var counted = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT COUNT(value), COUNT(*) FROM parameter_numbers"));
        Assert.Equal(1L, Assert.Single(counted.Rows)[0]);
        Assert.Equal(2L, counted.Rows[0][1]);
    }
}
