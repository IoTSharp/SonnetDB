using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证常用字符串标量函数在关系表投影和过滤路径上的一致执行语义。
/// </summary>
public sealed class SqlStringFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-string-" + Guid.NewGuid().ToString("N"));

    public SqlStringFunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* 测试清理失败不覆盖原始断言结果。 */ }
    }

    [Fact]
    public void StringFunctions_SelectAndWhere_UseNullAwareSemantics()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE strings (id INT, value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO strings (id, value) VALUES "
            + "(1, '  Hello  '), (2, 'shell'), (3, NULL)");

        var projected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, """
            SELECT id,
                   trim(value) AS trimmed,
                   substring(value, 2, 3) AS middle,
                   length(value) AS length,
                   replace(value, 'l', 'L') AS replaced
            FROM strings
            ORDER BY id
            """));

        Assert.Equal(new object?[] { 1L, "Hello", " He", 9L, "  HeLLo  " }, projected.Rows[0]);
        Assert.Equal(new object?[] { 2L, "shell", "hel", 5L, "sheLL" }, projected.Rows[1]);
        Assert.Equal(new object?[] { 3L, null, null, null, null }, projected.Rows[2]);

        var filtered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT id FROM strings WHERE contains(value, 'ell') OR starts_with(value, '  He') ORDER BY id"));
        Assert.Equal(new object?[] { 1L }, filtered.Rows[0]);
        Assert.Equal(new object?[] { 2L }, filtered.Rows[1]);
    }

    [Fact]
    public void StringFunctions_InvalidArguments_FailBeforeScanning()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE TABLE invalid_strings (id INT, value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO invalid_strings (id, value) VALUES (1, 'x')");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT substring(value, 0, 1) FROM invalid_strings"));
        Assert.Contains("start 参数必须大于等于 1", exception.Message, StringComparison.Ordinal);

        exception = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "SELECT left(value, -1) FROM invalid_strings"));
        Assert.Contains("count 参数不能为负数", exception.Message, StringComparison.Ordinal);
    }
}
