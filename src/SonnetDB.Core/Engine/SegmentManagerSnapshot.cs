using SonnetDB.Storage.Segments;

namespace SonnetDB.Engine;

internal sealed class SegmentManagerSnapshot
{
    private int _leaseCount;
    private int _retired;
    private int _disposed;
    private IReadOnlyList<SegmentReaderLeaseState> _readersToDispose = Array.Empty<SegmentReaderLeaseState>();
    private readonly IReadOnlyList<SegmentReaderLeaseState> _readerStates;

    public SegmentManagerSnapshot(
        MultiSegmentIndex index,
        IReadOnlyList<SegmentReaderLeaseState> readerStates)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(readerStates);

        Index = index;
        _readerStates = readerStates;
        Readers = readerStates.Select(static state => state.Reader).ToArray();
    }

    public MultiSegmentIndex Index { get; }

    public IReadOnlyList<SegmentReader> Readers { get; }

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

    public void Dispose()
    {
        Snapshot.Release();
    }
}
