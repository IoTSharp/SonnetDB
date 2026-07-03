using SonnetDB.Memory;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Engine;

internal sealed class SegmentManagerSnapshot
{
    private static readonly IReadOnlyList<MemTable> EmptyMemTables = Array.Empty<MemTable>();

    private int _leaseCount;
    private int _retired;
    private int _disposed;
    private IReadOnlyList<SegmentReaderLeaseState> _readersToDispose = Array.Empty<SegmentReaderLeaseState>();
    private readonly IReadOnlyList<SegmentReaderLeaseState> _readerStates;

    public SegmentManagerSnapshot(
        MultiSegmentIndex index,
        IReadOnlyList<SegmentReaderLeaseState> readerStates,
        MemTable? activeMemTable = null,
        IReadOnlyList<MemTable>? sealingMemTables = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(readerStates);

        Index = index;
        _readerStates = readerStates;
        Readers = readerStates.Select(static state => state.Reader).ToArray();
        ActiveMemTable = activeMemTable;
        SealingMemTables = sealingMemTables ?? EmptyMemTables;
    }

    public MultiSegmentIndex Index { get; }

    public IReadOnlyList<SegmentReader> Readers { get; }

    /// <summary>
    /// 当前活跃 MemTable（写入目标）；SegmentManager 初始快照或纯段测试场景下可能为 null。
    /// MemTable 是纯托管对象、密封后不可变，随快照持有引用即可，无需租约计数。
    /// </summary>
    public MemTable? ActiveMemTable { get; }

    /// <summary>
    /// 已密封、正在 flush（尚未落盘发布为 segment）的 MemTable 列表；Phase 1 恒为空。
    /// 查询必须把这些一并纳入合并，保证 flush 期间数据在 {MemTable, segment} 之一且仅一处可见。
    /// </summary>
    public IReadOnlyList<MemTable> SealingMemTables { get; }

    public bool TryAcquire()
    {
        while (true)
        {
            if (Volatile.Read(ref _retired) != 0)
                return false;

            int current = Volatile.Read(ref _leaseCount);
            if (Interlocked.CompareExchange(ref _leaseCount, current + 1, current) != current)
                continue;

            foreach (var state in _readerStates)
                state.Acquire();

            if (Volatile.Read(ref _retired) != 0)
            {
                foreach (var state in _readerStates)
                    state.Release();

                ReleaseSnapshotLease();
                return false;
            }

            return true;
        }
    }

    public void Release()
    {
        foreach (var state in _readerStates)
            state.Release();

        int count = ReleaseSnapshotLease();
        if (count == 0 && Volatile.Read(ref _retired) != 0)
            DisposeRetiredReaders();
    }

    public void Retire(IReadOnlyList<SegmentReaderLeaseState> readersToDispose)
    {
        ArgumentNullException.ThrowIfNull(readersToDispose);

        _readersToDispose = readersToDispose;
        if (Interlocked.Exchange(ref _retired, 1) == 0
            && Volatile.Read(ref _leaseCount) == 0)
        {
            DisposeRetiredReaders();
        }
    }

    private int ReleaseSnapshotLease()
        => Interlocked.Decrement(ref _leaseCount);

    private void DisposeRetiredReaders()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        foreach (var state in _readersToDispose)
            state.Retire();
    }
}

internal sealed class SegmentReaderLeaseState
{
    private int _leaseCount;
    private int _retired;
    private int _disposed;

    public SegmentReaderLeaseState(SegmentReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Reader = reader;
    }

    public SegmentReader Reader { get; }

    public void Acquire()
    {
        Interlocked.Increment(ref _leaseCount);
    }

    public void Release()
    {
        int count = Interlocked.Decrement(ref _leaseCount);
        if (count == 0 && Volatile.Read(ref _retired) != 0)
            DisposeReader();
    }

    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0
            && Volatile.Read(ref _leaseCount) == 0)
        {
            DisposeReader();
        }
    }

    private void DisposeReader()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { Reader.Dispose(); } catch { }
    }
}

internal readonly struct SegmentManagerSnapshotLease : IDisposable
{
    public SegmentManagerSnapshotLease(SegmentManagerSnapshot snapshot)
    {
        Snapshot = snapshot;
    }

    public SegmentManagerSnapshot Snapshot { get; }

    /// <summary>当前活跃 MemTable（可能为 null）。</summary>
    public MemTable? ActiveMemTable => Snapshot.ActiveMemTable;

    /// <summary>正在 flush 的已密封 MemTable 列表（Phase 1 恒空）。</summary>
    public IReadOnlyList<MemTable> SealingMemTables => Snapshot.SealingMemTables;

    /// <summary>
    /// 按合并优先级返回本租约内的全部 MemTable：先 sealing（较旧），后 active（最新）。
    /// </summary>
    public IReadOnlyList<MemTable> AllMemTables()
    {
        var sealing = Snapshot.SealingMemTables;
        var active = Snapshot.ActiveMemTable;
        var result = new List<MemTable>(sealing.Count + (active is null ? 0 : 1));
        result.AddRange(sealing);
        if (active is not null)
            result.Add(active);
        return result;
    }

    /// <summary>当前已发布的段读取器集合。</summary>
    public IReadOnlyList<SegmentReader> Readers => Snapshot.Readers;

    public void Dispose()
    {
        Snapshot.Release();
    }
}
