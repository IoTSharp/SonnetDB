namespace SonnetDB.Sql.Execution;

/// <summary>
/// 对传输边界应用 SQL 结果行数预算。
/// </summary>
public static class SqlResultBounds
{
    /// <summary>
    /// 限制查询结果或 RETURNING 结果的行数。空预算保持完整结果语义。
    /// </summary>
    /// <param name="result">SQL 执行结果。</param>
    /// <param name="maxRows">最大返回行数；为空表示不限制。</param>
    /// <returns>带有截断状态的结果对象。</returns>
    public static object? Apply(object? result, int? maxRows)
    {
        if (result is null || maxRows is null)
            return result;

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows.Value);
        return result switch
        {
            SelectExecutionResult select => select.TakeRows(maxRows.Value),
            InsertExecutionResult insert when insert.Returning is { } returning =>
                insert with { Returning = returning.TakeRows(maxRows.Value) },
            DeleteExecutionResult delete when delete.Returning is { } returning =>
                delete with { Returning = returning.TakeRows(maxRows.Value) },
            RowsAffectedExecutionResult affected when affected.Returning is { } returning =>
                affected with { Returning = returning.TakeRows(maxRows.Value) },
            _ => result,
        };
    }
}
