namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>SELECT</c> 语句的执行结果：列名 + 行数据。
/// </summary>
/// <remarks>
/// 行内每个值的运行时类型对应该列的语义：
/// <list type="bullet">
///   <item><description><c>time</c> 列：<see cref="long"/>（Unix 毫秒时间戳）。</description></item>
///   <item><description>Tag 列：<see cref="string"/>。</description></item>
///   <item><description>Field 列：<see cref="double"/> / <see cref="long"/> / <see cref="bool"/> / <see cref="string"/>，与列声明类型一致；
///     缺失时为 <c>null</c>（仅在 outer-join 路径出现，v1 raw 模式按 inner-join 不会产生 null）。</description></item>
///   <item><description>聚合结果列：除 <c>count</c> 为 <see cref="long"/> 外，其余为 <see cref="double"/>。</description></item>
/// </list>
/// </remarks>
/// <param name="Columns">输出列名（按 SELECT 列表中的声明顺序，含别名）。</param>
/// <param name="Rows">数据行（每行长度等于 <see cref="Columns"/> 数量）。</param>
public sealed record SelectExecutionResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows)
{
    /// <summary>结果是否为不完整的有界预览。</summary>
    public bool Truncated { get; init; }

    /// <summary>关系表 RETURNING 投影的声明列 schema；普通 SELECT 未提供时为 null。</summary>
    public IReadOnlyList<SonnetDB.Tables.TableColumn>? ColumnSchema { get; init; }

    /// <summary>普通 SELECT 的逐列声明信息；未知投影类型保留为 null。</summary>
    public IReadOnlyList<SelectColumnInfo>? ColumnInfo { get; init; }

    /// <summary>
    /// 返回限制行数后的结果预览。原结果超过限制时，返回结果的
    /// <see cref="Truncated"/> 为 <see langword="true"/>；否则保留原结果实例。
    /// </summary>
    /// <param name="maxRows">允许返回的最大行数。</param>
    /// <returns>有界结果预览。</returns>
    public SelectExecutionResult TakeRows(int maxRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        if (Rows.Count <= maxRows)
            return this;

        return this with
        {
            Rows = Rows.Take(maxRows).ToArray(),
            Truncated = true,
        };
    }
}
