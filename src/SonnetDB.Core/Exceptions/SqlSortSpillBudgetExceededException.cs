namespace SonnetDB.Exceptions;

/// <summary>有序临时文件的活跃字节数超过当前 SQL 查询的额度。</summary>
public sealed class SqlSortSpillBudgetExceededException : IOException
{
    /// <summary>创建排序临时文件额度超限错误。</summary>
    /// <param name="limitBytes">当前 SQL 查询的活跃临时字节额度。</param>
    public SqlSortSpillBudgetExceededException(long limitBytes)
        : base($"SQL 排序临时文件超过 {limitBytes} 字节额度。")
    {
        LimitBytes = limitBytes;
    }

    /// <summary>触发拒绝的活跃字节额度。</summary>
    public long LimitBytes { get; }
}
