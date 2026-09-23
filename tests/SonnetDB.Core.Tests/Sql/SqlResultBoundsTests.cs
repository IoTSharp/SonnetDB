using SonnetDB.Sql.Execution;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlResultBoundsTests
{
    [Fact]
    public void TakeRows_WhenOverBudget_MarksTruncatedAndKeepsPrefix()
    {
        var result = new SelectExecutionResult(
            ["id"],
            [
                new object?[] { 1L },
                new object?[] { 2L },
                new object?[] { 3L },
            ]);

        SelectExecutionResult preview = result.TakeRows(2);

        Assert.True(preview.Truncated);
        Assert.Equal(2, preview.Rows.Count);
        Assert.Equal(1L, preview.Rows[0][0]);
        Assert.Equal(2L, preview.Rows[1][0]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void SqlResultBounds_WhenNoOverflow_PreservesFullResult()
    {
        var result = new SelectExecutionResult(["id"], [new object?[] { 1L }]);

        Assert.Same(result, SqlResultBounds.Apply(result, 2));
        Assert.Same(result, SqlResultBounds.Apply(result, null));
    }

    [Fact]
    public void SqlResultBounds_LimitsReturningRowsWithoutChangingAffectedCount()
    {
        var result = new InsertExecutionResult("orders", 3)
        {
            Returning = new SelectExecutionResult(
                ["id"],
                [new object?[] { 1L }, new object?[] { 2L }, new object?[] { 3L }]),
        };

        var bounded = Assert.IsType<InsertExecutionResult>(SqlResultBounds.Apply(result, 1));

        Assert.Equal(3, bounded.RowsInserted);
        Assert.NotNull(bounded.Returning);
        Assert.Single(bounded.Returning!.Rows);
        Assert.True(bounded.Returning.Truncated);
    }

}
