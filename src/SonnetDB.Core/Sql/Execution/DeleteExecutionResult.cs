namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>DELETE FROM measurement WHERE ...</c> 执行结果。
/// </summary>
/// <param name="Measurement">被操作的 measurement 名称。</param>
/// <param name="SeriesAffected">命中 WHERE 中 tag 过滤的 series 数量。</param>
/// <param name="TombstonesAdded">向 WAL/内存追加的墓碑总数（= SeriesAffected × Field 列数）。</param>
public sealed record DeleteExecutionResult(
    string Measurement,
    int SeriesAffected,
    int TombstonesAdded)
{
    /// <summary>
    /// 关系表 <c>DELETE ... RETURNING</c> 产生的结果集；时序与普通删除为 <see langword="null"/>。
    /// </summary>
    public SelectExecutionResult? Returning { get; init; }
}
