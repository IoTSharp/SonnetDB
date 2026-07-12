namespace SonnetDB.Copilot;

/// <summary>
/// 跟踪当前正在执行的 Copilot 会话请求数量。
/// </summary>
internal sealed class CopilotInFlightTracker
{
    private long _count;

    /// <summary>当前正在执行的会话请求数。</summary>
    public long Count => Interlocked.Read(ref _count);

    /// <summary>
    /// 登记一个在飞会话，并返回用于离开计数的租约。
    /// </summary>
    /// <returns>释放时自动递减计数的租约。</returns>
    public IDisposable Enter()
    {
        Interlocked.Increment(ref _count);
        return new Lease(this);
    }

    private sealed class Lease(CopilotInFlightTracker owner) : IDisposable
    {
        private CopilotInFlightTracker? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null)
                Interlocked.Decrement(ref current._count);
        }
    }
}
