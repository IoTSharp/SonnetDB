using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 date_diff 与日期格式化函数在关系 SQL 投影和过滤中的合同。
/// </summary>
public sealed class SqlDateFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-date-functions-" + Guid.NewGuid().ToString("N"));

    public SqlDateFunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不覆盖原始断言结果。 */ }
    }

    [Fact]
    public void DateFunctions_SelectAndWhere_ReturnStableResults()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE events (id INT, occurred_at DATETIME, PRIMARY KEY (id))");
        var first = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var second = first.AddDays(2).AddHours(3);
        SqlExecutor.Execute(database,
            $"INSERT INTO events (id, occurred_at) VALUES "
            + $"(1, {new DateTimeOffset(first).ToUnixTimeMilliseconds()}), "
            + $"(2, {new DateTimeOffset(second).ToUnixTimeMilliseconds()})");

        var projected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT id,
                   date_diff('day', occurred_at, date_add(occurred_at, 2, 'day')) AS days,
                   date_format(occurred_at, 'yyyy-MM-dd HH:mm:ss') AS formatted,
                   strftime('%Y-%m-%d', occurred_at) AS compact
            FROM events
            ORDER BY id
            """));

        Assert.Equal(new object?[] { 1L, 2L, "2026-01-02 03:04:05", "2026-01-02" }, projected.Rows[0]);
        Assert.Equal(new object?[] { 2L, 2L, "2026-01-04 06:04:05", "2026-01-04" }, projected.Rows[1]);

        var filtered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id FROM events WHERE date_diff('day', occurred_at, date_add(occurred_at, 1, 'day')) = 1 ORDER BY id"));
        Assert.Equal(new object?[] { 1L }, filtered.Rows[0]);
        Assert.Equal(new object?[] { 2L }, filtered.Rows[1]);
    }

    [Fact]
    public void DateFunctions_InvalidArguments_ReturnStableErrors()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE events (id INT, occurred_at DATETIME, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO events (id, occurred_at) VALUES (1, 1767323045000)");

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
            "SELECT date_diff('fortnight', occurred_at, occurred_at) FROM events"));
        Assert.Contains("不支持日期分量", exception.Message, StringComparison.Ordinal);

        exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
            "SELECT date_format(occurred_at, '%Q') FROM events"));
        Assert.Contains("不支持日期格式标记", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("year", "2024-01-01T00:00:00.0000000Z")]
    [InlineData("quarter", "2024-01-01T00:00:00.0000000Z")]
    [InlineData("month", "2024-02-01T00:00:00.0000000Z")]
    [InlineData("week", "2024-02-26T00:00:00.0000000Z")]
    [InlineData(" DAY ", "2024-02-29T00:00:00.0000000Z")]
    [InlineData("hour", "2024-02-29T12:00:00.0000000Z")]
    [InlineData("minute", "2024-02-29T12:34:00.0000000Z")]
    [InlineData("second", "2024-02-29T12:34:56.0000000Z")]
    [InlineData("millisecond", "2024-02-29T12:34:56.1230000Z")]
    public void DateTrunc_Units_ReturnDateTime2WithTruncatedTicks(string unit, string expected)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            $"SELECT DaTe_TrUnC('{unit}', CAST('2024-02-29T12:34:56.1234567Z' AS DATETIME2))"));
        var value = Assert.IsType<DateTime>(Assert.Single(Assert.Single(result.Rows)));
        Assert.Equal(DateTime.ParseExact(expected, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(TableColumnType.DateTime2, Assert.Single(result.ColumnInfo!).DataType);
    }

    [Fact]
    public void DateTrunc_OffsetAndUnixBoundaries_UseUtcCalendar()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT date_trunc('day', CAST('2024-03-01T03:00:00.0000001+08:00' AS DATETIMEOFFSET)),
                   date_trunc('week', -1),
                   date_trunc('week', -62135596800000),
                   date_trunc('millisecond', 253402300799999),
                   date_trunc('quarter', CAST('2024-12-31T23:59:59.999Z' AS DATETIME))
            """));
        var row = Assert.Single(result.Rows);
        var offset = Assert.IsType<DateTimeOffset>(row[0]);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero), offset);
        Assert.Equal(TimeSpan.Zero, offset.Offset);
        Assert.Equal(-259200000L, row[1]);
        Assert.Equal(-62135596800000L, row[2]);
        Assert.Equal(253402300799999L, row[3]);
        Assert.Equal(new DateTime(2024, 10, 1, 0, 0, 0, DateTimeKind.Utc), row[4]);
        Assert.Equal(new TableColumnType?[] { TableColumnType.DateTimeOffset, TableColumnType.Int64,
            TableColumnType.Int64, TableColumnType.Int64, TableColumnType.DateTime },
            result.ColumnInfo!.Select(column => column.DataType));
    }

    [Fact]
    public void DateTrunc_RelationalWhereGroupHavingAndOrder_UseNativeDateType()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE events (id INT, occurred_at DATETIME2, PRIMARY KEY (id))");
        const string groupSql = """
            SELECT date_trunc('day', occurred_at) AS bucket, count(*) AS total
            FROM events
            WHERE date_trunc('day', occurred_at) >= CAST('2024-02-28T00:00:00Z' AS DATETIME2)
            GROUP BY date_trunc('day', occurred_at)
            HAVING date_trunc('day', max(occurred_at)) >= CAST('2024-02-28T00:00:00Z' AS DATETIME2)
            ORDER BY bucket DESC
            """;
        var empty = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, groupSql));
        Assert.Empty(empty.Rows);
        Assert.Equal(TableColumnType.DateTime2, empty.ColumnInfo![0].DataType);

        SqlExecutor.Execute(database, """
            INSERT INTO events (id, occurred_at) VALUES
              (1, CAST('2024-02-28T23:59:59.9999999Z' AS DATETIME2)),
              (2, CAST('2024-02-29T00:00:00.0000001Z' AS DATETIME2)),
              (3, CAST('2024-02-29T23:59:59.9999999Z' AS DATETIME2)),
              (4, NULL)
            """);
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, groupSql));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Utc), 2L }, result.Rows[0]);
        Assert.Equal(new object?[] { new DateTime(2024, 2, 28, 0, 0, 0, DateTimeKind.Utc), 1L }, result.Rows[1]);

        var ordered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT id FROM events WHERE occurred_at IS NOT NULL
            ORDER BY date_trunc('day', occurred_at) DESC, id ASC
            """));
        Assert.Equal(new object?[] { 2L, 3L, 1L }, ordered.Rows.Select(row => row[0]));
    }

    [Fact]
    public void DateTrunc_NullArgumentsReturnNull_AndTypoIsUnregistered()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT date_trunc(NULL, 0), date_trunc('invalid unit', NULL)"));
        Assert.Equal(new object?[] { null, null }, Assert.Single(result.Rows));
        Assert.Equal(TableColumnType.Int64, result.ColumnInfo![0].DataType);
        Assert.Equal(FunctionKind.Unknown, FunctionRegistry.GetFunctionKind("data_trunc"));
        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT data_trunc('day', 0)"));
        Assert.Contains("data_trunc", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("date_trunc('day')", "2 个参数")]
    [InlineData("date_trunc('day', 0, 1)", "2 个参数")]
    [InlineData("date_trunc('fortnight', 0)", "不支持日期分量")]
    [InlineData("date_trunc('', 0)", "非空字符串")]
    [InlineData("date_trunc(1, 0)", "非空字符串")]
    [InlineData("date_trunc('day', 1.5)", "Unix 毫秒整数")]
    [InlineData("date_trunc('day', '2024-02-29')", "Unix 毫秒整数")]
    [InlineData("date_trunc('day', -62135596800001)", "超出可表示的日期范围")]
    [InlineData("date_trunc('day', 253402300800000)", "超出可表示的日期范围")]
    public void DateTrunc_InvalidArguments_ReturnStableErrors(string expression, string message)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, "SELECT " + expression));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }
}
