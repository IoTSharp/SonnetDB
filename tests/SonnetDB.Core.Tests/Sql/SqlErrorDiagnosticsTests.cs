using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// SQL 解析与执行错误的稳定诊断合同测试。
/// </summary>
public sealed class SqlErrorDiagnosticsTests
{
    [Fact]
    public void Parse_InvalidClause_ExposesStableCodePositionOperationAndHint()
    {
        const string sql = "SELECT * FROM";

        SqlParseException exception = Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));

        Assert.Equal(SqlErrorCodes.Parse, exception.Code);
        Assert.Equal(SqlParseException.DefaultOperation, exception.Operation);
        Assert.Equal(sql.Length, exception.Position);
        Assert.Contains("位置", exception.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(exception.Hint));
        Assert.DoesNotContain(sql, exception.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Lexer_UnclosedString_ProvidesActionableHintWithoutSourceText()
    {
        const string sql = "SELECT 'private-value";

        SqlParseException exception = Assert.Throws<SqlParseException>(() => SqlLexer.Tokenize(sql));

        Assert.Equal(SqlErrorCodes.Parse, exception.Code);
        Assert.Equal(7, exception.Position);
        Assert.Equal("补齐引号、括号或注释后重试。", exception.Hint);
        Assert.DoesNotContain("private-value", exception.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_TableConstraint_UsesStableCodeOperationAndSafeHint()
    {
        var exception = new TableConstraintException(
            TableConstraintException.UniqueViolation,
            "orders",
            "ux_orders_number",
            "table 'orders' 中约束冲突。");

        SqlErrorInfo mapped = SqlErrorMapper.Map(exception, "insert");

        Assert.Equal(TableConstraintException.UniqueViolation, mapped.Code);
        Assert.Equal("insert", mapped.Operation);
        Assert.Null(mapped.Position);
        Assert.Equal("调整冲突键值，或先查询并更新现有行。", mapped.Hint);
        Assert.DoesNotContain("orders", mapped.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_CancelAndTimeout_UsesStableCodesAndCallerOperation()
    {
        SqlErrorInfo cancelled = SqlErrorMapper.Map(
            new OperationCanceledException("request cancelled"),
            "select");
        SqlErrorInfo timedOut = SqlErrorMapper.Map(
            new TimeoutException("budget elapsed"),
            "explain");

        Assert.Equal(SqlErrorCodes.Cancelled, cancelled.Code);
        Assert.Equal("select", cancelled.Operation);
        Assert.Equal(SqlErrorCodes.Timeout, timedOut.Code);
        Assert.Equal("explain", timedOut.Operation);
        Assert.NotNull(cancelled.Hint);
        Assert.NotNull(timedOut.Hint);
    }

    [Fact]
    public void Map_RoutineDeadlineTimeout_UsesTimeoutCode()
    {
        var exception = new RoutineExecutionException(
            RoutineErrorCodes.Cancelled,
            "SQL 执行已超过截止时间。",
            new TimeoutException("deadline elapsed"));

        SqlErrorInfo mapped = SqlErrorMapper.Map(exception, "explain");

        Assert.Equal(SqlErrorCodes.Timeout, mapped.Code);
        Assert.Equal("explain", mapped.Operation);
        Assert.NotNull(mapped.Hint);
    }
}
