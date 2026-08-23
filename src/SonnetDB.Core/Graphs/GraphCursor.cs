namespace SonnetDB.Graphs;

/// <summary>Graph 流式读取的分页和结果预算。</summary>
public sealed record GraphCursorOptions
{
    /// <summary>每次读取最多返回的结果数。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>底层每页 key/value payload 的最大字节数。</summary>
    public int MaxPageBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>游标生命周期内允许返回的最大结果数。</summary>
    public int MaxResults { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(PageSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxResults);
    }
}

/// <summary>
/// 固定 Graph 读快照上的前向结果游标。
/// </summary>
/// <typeparam name="T">每条结果的类型。</typeparam>
public sealed class GraphCursor<T> : IGraphPullCursor<T> where T : class
{
    private readonly object _sync = new();
    private IGraphCursorSource<T>? _source;
    private readonly int _maximumResults;
    private int _readInProgress;
    private int _returned;
    private bool _exhausted;
    private bool _faulted;

    internal GraphCursor(IGraphCursorSource<T> source, int maximumResults)
    {
        _source = source;
        _maximumResults = maximumResults;
        SnapshotSequence = source.SnapshotSequence;
    }

    /// <summary>游标所属稳定读快照的单调序列号。</summary>
    public long SnapshotSequence { get; }

    /// <summary>是否已经到达范围末尾或结果预算。</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_sync)
                return _exhausted;
        }
    }

    /// <summary>读取下一页结果。</summary>
    /// <param name="cancellationToken">取消令牌；取消后游标不可继续使用。</param>
    /// <returns>独立拥有的不可变结果页；空页表示读取结束。</returns>
    public IReadOnlyList<T> ReadNextPage(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
            throw new InvalidOperationException("同一个 Graph cursor 不能并发读取。");

        try
        {
            lock (_sync)
            {
                if (_exhausted)
                    return Array.Empty<T>();
                if (_faulted)
                    throw new InvalidOperationException("Graph cursor 已因读取故障终止。");
                ObjectDisposedException.ThrowIf(_source is null, this);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<T> page = _source.ReadNextPage(cancellationToken);
                    int remaining = _maximumResults - _returned;
                    if (page.Count > remaining)
                        page = page.Take(remaining).ToArray();
                    _returned += page.Count;
                    if (_returned >= _maximumResults || _source.IsExhausted)
                    {
                        _exhausted = true;
                        ReleaseSourceLocked();
                    }
                    return page;
                }
                catch
                {
                    _faulted = true;
                    ReleaseSourceLocked();
                    throw;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    /// <summary>释放游标及其稳定读快照租约。</summary>
    public void Dispose()
    {
        lock (_sync)
            ReleaseSourceLocked();
    }

    private void ReleaseSourceLocked()
    {
        IGraphCursorSource<T>? source = _source;
        _source = null;
        source?.Dispose();
    }
}

internal interface IGraphCursorSource<T> : IDisposable where T : class
{
    long SnapshotSequence { get; }

    bool IsExhausted { get; }

    IReadOnlyList<T> ReadNextPage(CancellationToken cancellationToken);
}

/// <summary>原生和关系映射 Graph 执行器共享的分页 pull cursor 合同。</summary>
internal interface IGraphPullCursor<T> : IDisposable where T : class
{
    bool IsExhausted { get; }

    IReadOnlyList<T> ReadNextPage(CancellationToken cancellationToken = default);
}
