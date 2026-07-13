namespace SonnetDB.Diagnostics;

/// <summary>跟踪进程内正在执行的查询，供低优先级后台维护进行保守节流。</summary>
internal static class QueryActivityTracker
{
    private static long s_activeQueries;

    public static long ActiveQueries => Interlocked.Read(ref s_activeQueries);

    public static Scope Enter()
    {
        Interlocked.Increment(ref s_activeQueries);
        return new Scope();
    }

    internal readonly struct Scope : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref s_activeQueries);
    }
}
