using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
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
}
