using SonnetDB.Contracts;

namespace SonnetDB.Diagnostics;

/// <summary>
/// 线程安全的慢查询样本环与独立有界指纹聚合器。样本覆盖不会清零已经建立的指纹累计值。
/// </summary>
internal sealed class SlowQueryRing
{
    private const int DurationReservoirCapacity = 128;

    private readonly object _gate = new();
    private readonly SlowQueryDiagnosticEntry?[] _entries;
    private readonly int _aggregateCapacity;
    private readonly Dictionary<QueryGroupKey, QueryAggregate> _aggregates = [];
    private int _next;
    private int _count;
    private long _unattributedSampleCount;

    /// <summary>
    /// 创建指定容量的样本环和指纹聚合器。
    /// </summary>
    /// <param name="capacity">慢查询样本容量，必须大于 0。</param>
    /// <param name="aggregateCapacity">指纹分组上限；为空时使用样本容量的四倍。</param>
    public SlowQueryRing(int capacity, int? aggregateCapacity = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        if (aggregateCapacity is <= 0)
            throw new ArgumentOutOfRangeException(nameof(aggregateCapacity));
        _entries = new SlowQueryDiagnosticEntry[capacity];
        _aggregateCapacity = aggregateCapacity ?? checked(capacity * 4);
    }

    /// <summary>样本缓冲容量。</summary>
    public int Capacity => _entries.Length;

    /// <summary>指纹聚合分组容量。</summary>
    public int AggregateCapacity => _aggregateCapacity;

    /// <summary>容量耗尽后无法归属到具体指纹的累计查询数。</summary>
    public long UnattributedSampleCount
    {
        get
        {
            lock (_gate)
                return _unattributedSampleCount;
        }
    }

    /// <summary>
    /// 写入一条慢查询样本并更新指纹聚合；容量已满时仅覆盖最旧样本。
    /// </summary>
    /// <param name="entry">诊断记录。</param>
    public void Add(SlowQueryDiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            ObserveLocked(entry);
            AddSampleLocked(entry);
        }
    }

    /// <summary>只更新指纹聚合，不占用慢查询样本环。</summary>
    public void Observe(SlowQueryDiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
            ObserveLocked(entry);
    }

    /// <summary>只写入慢查询样本环；调用方必须已经通过 <see cref="Observe"/> 更新聚合。</summary>
    public void AddSample(SlowQueryDiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
            AddSampleLocked(entry);
    }

    /// <summary>
    /// 获取调用方可见的慢查询样本快照。
    /// </summary>
    /// <param name="predicate">可见性过滤器。</param>
    /// <returns>按时间倒序排列的独立快照。</returns>
    public List<SlowQueryDiagnosticEntry> Snapshot(Func<SlowQueryDiagnosticEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var snapshot = new List<SlowQueryDiagnosticEntry>();
        lock (_gate)
        {
            snapshot.EnsureCapacity(_count);
            for (int offset = 0; offset < _count; offset++)
            {
                int index = (_next - 1 - offset + _entries.Length) % _entries.Length;
                var entry = _entries[index];
                if (entry is not null)
                    snapshot.Add(entry);
            }
        }

        int write = 0;
        for (int read = 0; read < snapshot.Count; read++)
        {
            if (predicate(snapshot[read]))
                snapshot[write++] = snapshot[read];
        }
        if (write < snapshot.Count)
            snapshot.RemoveRange(write, snapshot.Count - write);
        return snapshot;
    }

    /// <summary>
    /// 按数据库与 SQL 指纹返回累计最慢的 Top-N。
    /// </summary>
    /// <param name="predicate">可见性过滤器。</param>
    /// <param name="limit">最大返回分组数。</param>
    /// <returns>聚合项与可归属累计样本总数。</returns>
    public (List<TopQueryDiagnosticEntry> Items, int SampleCount) Top(
        Func<SlowQueryDiagnosticEntry, bool> predicate,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        List<QueryAggregateSnapshot> aggregates;
        lock (_gate)
        {
            aggregates = new List<QueryAggregateSnapshot>(_aggregates.Count);
            foreach (var aggregate in _aggregates.Values)
                aggregates.Add(aggregate.Snapshot());
        }

        long sampleCount = 0;
        var result = new List<TopQueryDiagnosticEntry>(aggregates.Count);
        foreach (var aggregate in aggregates)
        {
            if (!predicate(aggregate.LastEntry))
                continue;
            sampleCount = checked(sampleCount + aggregate.Count);
            result.Add(aggregate.ToContract());
        }

        result.Sort(TopQueryComparer.Instance);
        if (result.Count > limit)
            result.RemoveRange(limit, result.Count - limit);
        return (result, (int)Math.Min(int.MaxValue, sampleCount));
    }

    /// <summary>返回可归属到调用方的精确累计查询数，不受 Top-N 截断影响。</summary>
    public long CountAttributed(Func<SlowQueryDiagnosticEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<QueryAttribution> attributions;
        lock (_gate)
        {
            attributions = new List<QueryAttribution>(_aggregates.Count);
            foreach (var aggregate in _aggregates.Values)
                attributions.Add(new QueryAttribution(aggregate.LastEntry, aggregate.Count));
        }

        long count = 0;
        foreach (QueryAttribution attribution in attributions)
        {
            if (predicate(attribution.Entry))
                count = checked(count + attribution.Count);
        }
        return count;
    }

    private void ObserveLocked(SlowQueryDiagnosticEntry entry)
    {
        var key = new QueryGroupKey(entry.Database, entry.Fingerprint, entry.NormalizedSql);
        if (!_aggregates.TryGetValue(key, out var aggregate))
        {
            if (_aggregates.Count >= _aggregateCapacity)
            {
                _unattributedSampleCount++;
                return;
            }

            aggregate = new QueryAggregate(entry);
            _aggregates.Add(key, aggregate);
            return;
        }

        aggregate.Add(entry);
    }

    private void AddSampleLocked(SlowQueryDiagnosticEntry entry)
    {
        _entries[_next] = entry;
        _next = (_next + 1) % _entries.Length;
        if (_count < _entries.Length)
            _count++;
    }

    private sealed class QueryAggregate
    {
        private readonly double[] _durations = new double[DurationReservoirCapacity];
        private int _durationCount;
        private int _durationNext;
        private long _failedCount;
        private double _maxMs;
        private long _candidateRows;
        private long _examinedRows;
        private long _returnedRows;
        private long _logicalReads;
        private long _logicalWrites;
        private long _physicalReads;
        private long _physicalReadBytes;
        private long _physicalWrites;
        private long _physicalWriteBytes;
        private double _queueWaitMs;
        private double _tableLockWaitMs;
        private double _kvLockWaitMs;
        private double _walFsyncMs;
        private long _walFsyncCount;
        private double _executionElapsedMs;
        private long _allocatedBytes;
        private long _gen0Collections;
        private long _gen1Collections;
        private long _gen2Collections;
        private string? _accessPath;
        private string? _indexName;
        private string? _fallbackReason;

        public QueryAggregate(SlowQueryDiagnosticEntry entry) => Add(entry);

        public long Count { get; private set; }

        public SlowQueryDiagnosticEntry LastEntry { get; private set; } = null!;

        public void Add(SlowQueryDiagnosticEntry entry)
        {
            Count++;
            if (entry.Failed)
                _failedCount++;
            _durations[_durationNext] = entry.ElapsedMs;
            _durationNext = (_durationNext + 1) % _durations.Length;
            if (_durationCount < _durations.Length)
                _durationCount++;
            _maxMs = Math.Max(_maxMs, entry.ElapsedMs);
            _candidateRows = checked(_candidateRows + entry.CandidateRows);
            _examinedRows = checked(_examinedRows + entry.ExaminedRows);
            _returnedRows = checked(_returnedRows + entry.RowCount);
            _logicalReads = checked(_logicalReads + entry.LogicalReads);
            _logicalWrites = checked(_logicalWrites + entry.LogicalWrites);
            _physicalReads = checked(_physicalReads + entry.PhysicalReads);
            _physicalReadBytes = checked(_physicalReadBytes + entry.PhysicalReadBytes);
            _physicalWrites = checked(_physicalWrites + entry.PhysicalWrites);
            _physicalWriteBytes = checked(_physicalWriteBytes + entry.PhysicalWriteBytes);
            _queueWaitMs += entry.QueueWaitMs;
            _tableLockWaitMs += entry.TableLockWaitMs;
            _kvLockWaitMs += entry.KvLockWaitMs;
            _walFsyncMs += entry.WalFsyncMs;
            _walFsyncCount = checked(_walFsyncCount + entry.WalFsyncCount);
            _executionElapsedMs += entry.ExecutionElapsedMs;
            if (entry.AllocatedBytes >= 0)
                _allocatedBytes = checked(_allocatedBytes + entry.AllocatedBytes);
            _gen0Collections = checked(_gen0Collections + entry.Gen0Collections);
            _gen1Collections = checked(_gen1Collections + entry.Gen1Collections);
            _gen2Collections = checked(_gen2Collections + entry.Gen2Collections);
            _accessPath = Merge(_accessPath, entry.AccessPath);
            _indexName = Merge(_indexName, entry.IndexName);
            _fallbackReason = Merge(_fallbackReason, entry.FallbackReason);
            LastEntry = entry;
        }

        public QueryAggregateSnapshot Snapshot()
        {
            var durations = new double[_durationCount];
            Array.Copy(_durations, durations, _durationCount);
            Array.Sort(durations);
            return new QueryAggregateSnapshot(
                LastEntry,
                Count,
                _failedCount,
                Percentile(durations, 0.50),
                Percentile(durations, 0.95),
                _maxMs,
                _candidateRows,
                _examinedRows,
                _returnedRows,
                _logicalReads,
                _logicalWrites,
                _physicalReads,
                _physicalReadBytes,
                _physicalWrites,
                _physicalWriteBytes,
                _queueWaitMs,
                _tableLockWaitMs,
                _kvLockWaitMs,
                _walFsyncMs,
                _walFsyncCount,
                _executionElapsedMs,
                _allocatedBytes,
                _gen0Collections,
                _gen1Collections,
                _gen2Collections,
                _accessPath,
                _indexName,
                _fallbackReason);
        }
    }

    private sealed record QueryAggregateSnapshot(
        SlowQueryDiagnosticEntry LastEntry,
        long Count,
        long FailedCount,
        double P50Ms,
        double P95Ms,
        double MaxMs,
        long CandidateRows,
        long ExaminedRows,
        long ReturnedRows,
        long LogicalReads,
        long LogicalWrites,
        long PhysicalReads,
        long PhysicalReadBytes,
        long PhysicalWrites,
        long PhysicalWriteBytes,
        double QueueWaitMs,
        double TableLockWaitMs,
        double KvLockWaitMs,
        double WalFsyncMs,
        long WalFsyncCount,
        double ExecutionElapsedMs,
        long AllocatedBytes,
        long Gen0Collections,
        long Gen1Collections,
        long Gen2Collections,
        string? AccessPath,
        string? IndexName,
        string? FallbackReason)
    {
        public TopQueryDiagnosticEntry ToContract()
            => new(
                LastEntry.Database,
                LastEntry.NormalizedSql,
                LastEntry.Fingerprint,
                (int)Math.Min(int.MaxValue, Count),
                (int)Math.Min(int.MaxValue, FailedCount),
                P50Ms,
                P95Ms,
                MaxMs,
                LastEntry.TimestampMs)
            {
                LifetimeCount = Count,
                LifetimeFailedCount = FailedCount,
                AccessPath = AccessPath,
                IndexName = IndexName,
                FallbackReason = FallbackReason,
                CandidateRows = CandidateRows,
                ExaminedRows = ExaminedRows,
                ReturnedRows = ReturnedRows,
                LogicalReads = LogicalReads,
                LogicalWrites = LogicalWrites,
                PhysicalReads = PhysicalReads,
                PhysicalReadBytes = PhysicalReadBytes,
                PhysicalWrites = PhysicalWrites,
                PhysicalWriteBytes = PhysicalWriteBytes,
                QueueWaitMs = QueueWaitMs,
                LockWaitMs = TableLockWaitMs + KvLockWaitMs,
                TableLockWaitMs = TableLockWaitMs,
                KvLockWaitMs = KvLockWaitMs,
                WalFsyncMs = WalFsyncMs,
                WalFsyncCount = WalFsyncCount,
                ExecutionElapsedMs = ExecutionElapsedMs,
                AllocatedBytes = AllocatedBytes,
                Gen0Collections = Gen0Collections,
                Gen1Collections = Gen1Collections,
                Gen2Collections = Gen2Collections,
            };
    }

    private sealed class TopQueryComparer : IComparer<TopQueryDiagnosticEntry>
    {
        public static TopQueryComparer Instance { get; } = new();

        public int Compare(TopQueryDiagnosticEntry? left, TopQueryDiagnosticEntry? right)
        {
            if (ReferenceEquals(left, right))
                return 0;
            if (left is null)
                return 1;
            if (right is null)
                return -1;
            int result = right.P95Ms.CompareTo(left.P95Ms);
            if (result != 0)
                return result;
            result = right.MaxMs.CompareTo(left.MaxMs);
            if (result != 0)
                return result;
            result = right.LifetimeCount.CompareTo(left.LifetimeCount);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(left.Fingerprint, right.Fingerprint);
        }
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        int rank = Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Length) - 1);
        return sortedValues[rank];
    }

    private static string? Merge(string? current, string? next)
    {
        if (next is null)
            return current;
        return current is null || string.Equals(current, next, StringComparison.Ordinal)
            ? next
            : "mixed";
    }

    private readonly record struct QueryGroupKey(string Database, string Fingerprint, string NormalizedSql);

    private readonly record struct QueryAttribution(SlowQueryDiagnosticEntry Entry, long Count);
}
