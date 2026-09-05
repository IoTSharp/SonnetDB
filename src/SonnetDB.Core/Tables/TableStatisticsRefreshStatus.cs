namespace SonnetDB.Tables;

/// <summary>关系表自动统计维护的可观测状态；不表示统计本身是否新鲜。</summary>
/// <param name="State">idle、queued、running、completed、deferred、failed 或 cancelled。</param>
/// <param name="ErrorCode">最近一次未成功刷新的稳定原因；成功时为空。</param>
public sealed record TableStatisticsRefreshStatus(string State, string? ErrorCode);

/// <summary>同一数据库最多接纳一个自动统计任务；繁忙时由后续查询重新尝试。</summary>
internal sealed class TableStatisticsRefreshBudget
{
    private int _active;

    internal bool TryAcquire() => Interlocked.CompareExchange(ref _active, 1, 0) == 0;

    internal void Release() => Volatile.Write(ref _active, 0);
}
