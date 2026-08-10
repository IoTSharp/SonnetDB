namespace SonnetDB.Kv;

/// <summary>
/// 在单个 KV 稳定读快照上按 key 字节序升序读取的前向范围游标。
/// </summary>
/// <remarks>
/// 游标不持有 keyspace 锁。每页中的 key/value 都是该页独立拥有的副本，
/// 在读取后续页或释放游标、快照后仍保持有效。取消会终止游标，不能继续读取。
/// 按字节预算截页时，底层枚举器可能在返回页之外预取并保留下一条 source-owned 记录；
/// 因此活跃游标的读取层驻留上限是返回页 payload 加一条记录，而不是 keyspace 总量。
/// </remarks>
public sealed class KvRangeCursor : IDisposable
{
    private const int InitialPageCapacity = 256;
    private readonly object _sync = new();
    private readonly int _pageSize;
    private readonly int _maxPageBytes;
    private KvReadSnapshotState? _state;
    private IEnumerator<KeyValuePair<byte[], KvValueEntry>>? _enumerator;
    private int _readInProgress;
    private bool _hasPendingEntry;
    private bool _exhausted;
    private bool _canceled;
    private bool _faulted;
    private bool _disposed;

    /// <summary>测试读取一页时阻塞在不持有 keyspace 锁的位置。</summary>
    internal Action? PageReadTestHook { get; set; }

    /// <summary>测试每复制一条结果后观察当前页条目数。</summary>
    internal Action<int>? EntryCopiedTestHook { get; set; }

    internal KvRangeCursor(KvReadSnapshotState state, KvRangeScanOptions options)
    {
        _state = state;
        _pageSize = options.PageSize;
        _maxPageBytes = options.MaxPageBytes;
        _enumerator = state.CreateEnumerator(options);
        SnapshotSequence = state.Sequence;
        ReadTimestampUtc = state.ReadTimestampUtc;
    }

    /// <summary>游标所属快照的单调版本号。</summary>
    public long SnapshotSequence { get; }

    /// <summary>游标所属快照用于判断 TTL 可见性的固定 UTC 时刻。</summary>
    public DateTimeOffset ReadTimestampUtc { get; }

    /// <summary>每页最多返回的条目数。</summary>
    public int PageSize => _pageSize;

    /// <summary>每页最多复制的 key/value payload 字节数。</summary>
    public int MaxPageBytes => _maxPageBytes;

    /// <summary>是否已经确认读取到范围末尾。</summary>
    public bool IsExhausted
    {
        get
        {
            lock (_sync)
                return _exhausted;
        }
    }

    /// <summary>
    /// 读取下一页。返回空集合表示范围已经读完；读完后重复调用仍返回空集合。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；取消会终止游标并释放其快照租约。</param>
    /// <returns>
    /// 最多包含 <see cref="PageSize"/> 条且 payload 不超过 <see cref="MaxPageBytes"/> 的独立结果页。
    /// </returns>
    /// <exception cref="OperationCanceledException">读取被取消。</exception>
    /// <exception cref="InvalidOperationException">
    /// 游标已取消、读取故障、正在被另一个调用方读取，或单条记录超过页字节预算。
    /// </exception>
    /// <exception cref="ObjectDisposedException">游标已经释放。</exception>
    public IReadOnlyList<KvEntry> ReadNextPage(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _readInProgress, 1) != 0)
            throw new InvalidOperationException("同一个 KV range cursor 不能并发读取。");

        try
        {
            lock (_sync)
            {
                ThrowIfUnavailableLocked();
                if (_exhausted)
                    return Array.Empty<KvEntry>();

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PageReadTestHook?.Invoke();
                    IEnumerator<KeyValuePair<byte[], KvValueEntry>> enumerator = _enumerator
                        ?? throw new InvalidOperationException("KV range cursor 的枚举状态不可用。");
                    var page = new List<KvEntry>(Math.Min(_pageSize, InitialPageCapacity));
                    long pagePayloadBytes = 0;
                    while (page.Count < _pageSize)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!_hasPendingEntry && !enumerator.MoveNext())
                        {
                            _exhausted = true;
                            ReleaseEnumerationLocked();
                            break;
                        }
                        _hasPendingEntry = false;

                        cancellationToken.ThrowIfCancellationRequested();
                        KeyValuePair<byte[], KvValueEntry> current = enumerator.Current;
                        if (current.Value.IsExpired(ReadTimestampUtc))
                            continue;

                        long entryPayloadBytes = checked(
                            (long)current.Key.Length + current.Value.Value.Length);
                        if (entryPayloadBytes > _maxPageBytes)
                        {
                            throw new InvalidOperationException(
                                $"KV range cursor 单条记录 payload 为 {entryPayloadBytes} 字节，超过 MaxPageBytes={_maxPageBytes}。请显式提高页字节预算。");
                        }
                        if (page.Count != 0 && pagePayloadBytes + entryPayloadBytes > _maxPageBytes)
                        {
                            _hasPendingEntry = true;
                            break;
                        }

                        page.Add(new KvEntry(
                            current.Key.ToArray(),
                            current.Value.Value.ToArray(),
                            current.Value.Version,
                            current.Value.ExpiresAtUtc));
                        pagePayloadBytes += entryPayloadBytes;
                        EntryCopiedTestHook?.Invoke(page.Count);
                    }

                    return page.Count == 0 ? Array.Empty<KvEntry>() : page.ToArray();
                }
                catch (OperationCanceledException)
                {
                    _canceled = true;
                    ReleaseEnumerationLocked();
                    throw;
                }
                catch
                {
                    _faulted = true;
                    ReleaseEnumerationLocked();
                    throw;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _readInProgress, 0);
        }
    }

    /// <summary>释放游标、底层枚举器及其独立快照租约。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseEnumerationLocked();
        }
    }

    private void ThrowIfUnavailableLocked()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_canceled)
            throw new InvalidOperationException("KV range cursor 已因取消终止。");
        if (_faulted)
            throw new InvalidOperationException("KV range cursor 已因读取故障终止。");
    }

    private void ReleaseEnumerationLocked()
    {
        _enumerator?.Dispose();
        _enumerator = null;
        _hasPendingEntry = false;
        KvReadSnapshotState? state = _state;
        _state = null;
        state?.Release();
    }
}
