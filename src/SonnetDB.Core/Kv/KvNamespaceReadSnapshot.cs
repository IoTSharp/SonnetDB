using System.Runtime.CompilerServices;

namespace SonnetDB.Kv;

/// <summary>
/// 命名空间内的稳定只读快照租约。
/// </summary>
/// <remarks>
/// 底层快照固定创建时可见的 key/value、版本与 TTL 判断时刻。
/// 传给 <see cref="OpenRangeCursor(KvRangeScanOptions?)"/> 的范围选项均以命名空间内的本地 key 表示，
/// 返回页也会自动剥离内部 namespace 前缀。
/// </remarks>
public sealed class KvNamespaceReadSnapshot : IDisposable
{
    private readonly object _sync = new();
    private readonly byte[] _prefix;
    private KvReadSnapshot? _snapshot;

    internal KvNamespaceReadSnapshot(KvReadSnapshot snapshot, ReadOnlyMemory<byte> prefix)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _prefix = prefix.ToArray();
        Sequence = snapshot.Sequence;
        ReadTimestampUtc = snapshot.ReadTimestampUtc;
    }

    /// <summary>创建快照时 keyspace 已应用的单调版本号。</summary>
    public long Sequence { get; }

    /// <summary>快照用于判断 TTL 可见性的固定 UTC 时刻。</summary>
    public DateTimeOffset ReadTimestampUtc { get; }

    /// <summary>
    /// 创建一个命名空间相对的范围游标。
    /// </summary>
    /// <param name="options">本地前缀、边界、方向和页预算；为空时扫描整个命名空间。</param>
    /// <returns>持有独立快照租约的命名空间范围游标。</returns>
    /// <exception cref="ArgumentOutOfRangeException">页条目数或页字节预算不是正数。</exception>
    /// <exception cref="ObjectDisposedException">快照已经释放。</exception>
    public KvNamespaceRangeCursor OpenRangeCursor(KvRangeScanOptions? options = null)
    {
        lock (_sync)
        {
            KvReadSnapshot snapshot = _snapshot
                ?? throw new ObjectDisposedException(nameof(KvNamespaceReadSnapshot));
            KvRangeScanOptions qualified = QualifyOptions(options ?? new KvRangeScanOptions());
            return new KvNamespaceRangeCursor(snapshot.OpenRangeCursor(qualified), _prefix);
        }
    }

    /// <summary>释放快照自身持有的状态租约。</summary>
    public void Dispose()
    {
        KvReadSnapshot? snapshot;
        lock (_sync)
        {
            snapshot = _snapshot;
            _snapshot = null;
        }

        snapshot?.Dispose();
    }

    private KvRangeScanOptions QualifyOptions(KvRangeScanOptions options)
        => options with
        {
            Prefix = QualifyPrefix(options.Prefix),
            StartInclusive = QualifyBoundary(options.StartInclusive),
            EndExclusive = QualifyBoundary(options.EndExclusive),
            AfterKey = QualifyBoundary(options.AfterKey),
        };

    private ReadOnlyMemory<byte> QualifyPrefix(ReadOnlyMemory<byte> localPrefix)
    {
        if (_prefix.Length == 0)
            return localPrefix.ToArray();

        return Concatenate(_prefix, localPrefix.Span);
    }

    private ReadOnlyMemory<byte> QualifyBoundary(ReadOnlyMemory<byte> localBoundary)
        => localBoundary.IsEmpty
            ? ReadOnlyMemory<byte>.Empty
            : Concatenate(_prefix, localBoundary.Span);

    private static byte[] Concatenate(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix)
    {
        byte[] qualified = new byte[checked(prefix.Length + suffix.Length)];
        prefix.CopyTo(qualified);
        suffix.CopyTo(qualified.AsSpan(prefix.Length));
        return qualified;
    }
}

/// <summary>
/// 命名空间内按 key 字节序分页读取的稳定范围游标。
/// </summary>
/// <remarks>
/// 游标页中的 key/value 均由底层稳定游标独立拥有；返回 key 已剥离 namespace 内部前缀，
/// 因此调用方无需了解 keyspace 的物理命名约定。
/// </remarks>
public sealed class KvNamespaceRangeCursor : IDisposable, IAsyncDisposable
{
    private readonly KvRangeCursor _cursor;
    private readonly byte[] _prefix;

    internal KvNamespaceRangeCursor(KvRangeCursor cursor, ReadOnlyMemory<byte> prefix)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        _cursor = cursor;
        _prefix = prefix.ToArray();
    }

    /// <summary>游标所属快照的单调版本号。</summary>
    public long SnapshotSequence => _cursor.SnapshotSequence;

    /// <summary>游标所属快照用于判断 TTL 可见性的固定 UTC 时刻。</summary>
    public DateTimeOffset ReadTimestampUtc => _cursor.ReadTimestampUtc;

    /// <summary>每页最多返回的条目数。</summary>
    public int PageSize => _cursor.PageSize;

    /// <summary>每页最多复制的 key/value payload 字节数。</summary>
    public int MaxPageBytes => _cursor.MaxPageBytes;

    /// <summary>是否已经确认读取到范围末尾。</summary>
    public bool IsExhausted => _cursor.IsExhausted;

    /// <summary>读取下一页，并返回 namespace-local key。</summary>
    /// <param name="cancellationToken">取消令牌；取消会终止游标。</param>
    /// <returns>最多包含 <see cref="PageSize"/> 条记录的独立结果页。</returns>
    public IReadOnlyList<KvEntry> ReadNextPage(CancellationToken cancellationToken = default)
        => Project(_cursor.ReadNextPage(cancellationToken));

    /// <summary>异步读取下一页，并返回 namespace-local key。</summary>
    /// <param name="cancellationToken">取消令牌；取消会终止游标。</param>
    /// <returns>最多包含 <see cref="PageSize"/> 条记录的独立结果页。</returns>
    public async ValueTask<IReadOnlyList<KvEntry>> ReadNextPageAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KvEntry> page = await _cursor
            .ReadNextPageAsync(cancellationToken)
            .ConfigureAwait(false);
        return Project(page);
    }

    /// <summary>从当前位置开始逐条异步枚举 namespace-local 记录。</summary>
    /// <param name="cancellationToken">取消令牌；取消会终止游标。</param>
    /// <returns>按游标方向返回的记录序列。</returns>
    public async IAsyncEnumerable<KvEntry> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (KvEntry entry in _cursor.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return Project(entry);
    }

    /// <summary>释放游标、底层枚举器及独立快照租约。</summary>
    public void Dispose() => _cursor.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private IReadOnlyList<KvEntry> Project(IReadOnlyList<KvEntry> page)
    {
        if (page.Count == 0)
            return Array.Empty<KvEntry>();

        var projected = new KvEntry[page.Count];
        for (int index = 0; index < page.Count; index++)
            projected[index] = Project(page[index]);
        return projected;
    }

    private KvEntry Project(KvEntry entry)
    {
        ReadOnlyMemory<byte> key = entry.Key;
        if (key.Length < _prefix.Length || !key.Span.StartsWith(_prefix))
        {
            throw new InvalidOperationException(
                "KV namespace cursor received a key outside its namespace prefix.");
        }

        return new KvEntry(
            key[_prefix.Length..],
            entry.Value,
            entry.Version,
            entry.ExpiresAtUtc);
    }
}
