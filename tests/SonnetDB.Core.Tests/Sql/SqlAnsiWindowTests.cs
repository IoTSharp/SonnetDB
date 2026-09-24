using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #172：ANSI OVER 窗口规格的 bounded 合同。</summary>
public sealed class SqlAnsiWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-ansi-window-" + Guid.NewGuid().ToString("N"));

    public SqlAnsiWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_Over_StoresEmptyAndOrderedSpecifications()
    {
        var empty = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT row_number() OVER () FROM cpu"));
        var emptyWindow = Assert.IsType<FunctionCallExpression>(empty.Projections[0].Expression).Over;
        Assert.NotNull(emptyWindow);
        Assert.Empty(emptyWindow!.PartitionBy);
        Assert.Empty(emptyWindow.OrderBy);

        var ordered = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT running_sum(value) OVER (ORDER BY time ASC) FROM cpu"));
        var orderedWindow = Assert.IsType<FunctionCallExpression>(ordered.Projections[0].Expression).Over;
        Assert.NotNull(orderedWindow);
        Assert.Empty(orderedWindow!.PartitionBy);
        var order = Assert.Single(orderedWindow.OrderBy);
        Assert.Equal("time", Assert.IsType<IdentifierExpression>(order.Expression).Name);
        Assert.Equal(SortDirection.Ascending, order.Direction);
    }

    [Fact]
    public void Execute_RowNumberOver_ProducesPerSeriesOrdinal()
    {
        using var db = OpenDatabase();
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, value) VALUES "
            + "(0, 'a', 10), (1, 'a', 20), (2, 'a', 30), "
            + "(0, 'b', 40), (1, 'b', 50)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT host, time, row_number() OVER (ORDER BY time ASC) AS rn FROM cpu ORDER BY time ASC"));

        Assert.Equal([1L, 1L, 2L, 2L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[2])).ToArray());
    }

    [Fact]
    public void Execute_ExistingWindowFunctionWithOverOrderByTime_PreservesSemantics()
    {
        using var db = OpenDatabase();
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, value) VALUES (0, 'a', 2), (1, 'a', 3), (2, 'a', 5)");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT time, running_sum(value) OVER (ORDER BY time ASC) AS total FROM cpu"));

        Assert.Equal([2d, 5d, 10d], result.Rows.Select(row => Assert.IsType<double>(row[1])).ToArray());
    }

    [Fact]
    public void Execute_UnsupportedOverShapes_FailClosed()
    {
        using var db = OpenDatabase();
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, value) VALUES (0, 'a', 1)");

        var partition = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db,
            "SELECT row_number() OVER (PARTITION BY host) FROM cpu"));
        Assert.Contains("PARTITION BY 尚未支持", partition.Message, StringComparison.Ordinal);

        var descending = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db,
            "SELECT row_number() OVER (ORDER BY time DESC) FROM cpu"));
        Assert.Contains("ORDER BY time ASC", descending.Message, StringComparison.Ordinal);

        var frame = Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "SELECT row_number() OVER (ORDER BY time ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM cpu"));
        Assert.Contains("OVER 目前仅支持", frame.Message, StringComparison.Ordinal);

        var aggregate = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "SELECT sum(value) OVER () FROM cpu"));
        Assert.Contains("不是窗口函数", aggregate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_RowNumberWithoutOver_IsRejected()
    {
        using var db = OpenDatabase();
        var error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "SELECT row_number() FROM cpu"));
        Assert.Contains("必须带 OVER", error.Message, StringComparison.Ordinal);
    }

    private Tsdb OpenDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");
        return db;
    }
}
