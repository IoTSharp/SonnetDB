using SonnetDB.Contracts;

namespace SonnetDB.Diagnostics;

/// <summary>
/// 线程安全的进程内慢查询环形缓冲。写满后覆盖最旧记录，读取结果始终按时间倒序。
/// </summary>
internal sealed class SlowQueryRing
{
    private readonly object _gate = new();
    private readonly SlowQueryDiagnosticEntry?[] _entries;
    private int _next;
    private int _count;

    /// <summary>
    /// 创建指定容量的环形缓冲。
    /// </summary>
    /// <param name="capacity">容量，必须大于 0。</param>
    public SlowQueryRing(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _entries = new SlowQueryDiagnosticEntry[capacity];
    }

    /// <summary>缓冲容量。</summary>
    public int Capacity => _entries.Length;

    /// <summary>
    /// 写入一条慢查询记录；容量已满时覆盖最旧记录。
    /// </summary>
    /// <param name="entry">诊断记录。</param>
    public void Add(SlowQueryDiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[_next] = entry;
            _next = (_next + 1) % _entries.Length;
            if (_count < _entries.Length)
                _count++;
        }
    }

    /// <summary>
    /// 获取调用方可见的记录快照。
    /// </summary>
    /// <param name="predicate">可见性过滤器。</param>
    /// <returns>按时间倒序排列的独立快照。</returns>
    public List<SlowQueryDiagnosticEntry> Snapshot(Func<SlowQueryDiagnosticEntry, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<SlowQueryDiagnosticEntry> snapshot;
        lock (_gate)
        {
            snapshot = new List<SlowQueryDiagnosticEntry>(_count);
            for (var offset = 0; offset < _count; offset++)
            {
                var index = (_next - 1 - offset + _entries.Length) % _entries.Length;
                var entry = _entries[index];
                if (entry is not null)
                    snapshot.Add(entry);
            }
        }

        return snapshot.Where(predicate).ToList();
    }

    /// <summary>
    /// 按数据库与 SQL 指纹聚合可见样本，并返回最慢的 Top-N。
    /// </summary>
    /// <param name="predicate">可见性过滤器。</param>
    /// <param name="limit">最大返回分组数。</param>
    /// <returns>聚合项与参与聚合的样本总数。</returns>
    public (List<TopQueryDiagnosticEntry> Items, int SampleCount) Top(
        Func<SlowQueryDiagnosticEntry, bool> predicate,
        int limit)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var snapshot = Snapshot(predicate);
        var groups = new Dictionary<QueryGroupKey, List<SlowQueryDiagnosticEntry>>();
        foreach (var entry in snapshot)
        {
            var key = new QueryGroupKey(entry.Database, entry.Fingerprint, entry.NormalizedSql);
            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups.Add(key, values);
            }
            values.Add(entry);
        }

        var result = new List<TopQueryDiagnosticEntry>(groups.Count);
        foreach (var pair in groups)
        {
            var values = pair.Value;
            var durations = values.Select(static item => item.ElapsedMs).Order().ToArray();
            result.Add(new TopQueryDiagnosticEntry(
                pair.Key.Database,
                pair.Key.NormalizedSql,
                pair.Key.Fingerprint,
                values.Count,
                values.Count(static item => item.Failed),
                Percentile(durations, 0.50),
                Percentile(durations, 0.95),
                durations[^1],
                values.Max(static item => item.TimestampMs)));
        }

        return (
            result
                .OrderByDescending(static item => item.P95Ms)
                .ThenByDescending(static item => item.MaxMs)
                .ThenByDescending(static item => item.Count)
                .ThenBy(static item => item.Fingerprint, StringComparer.Ordinal)
                .Take(limit)
                .ToList(),
            snapshot.Count);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        var rank = Math.Max(0, (int)Math.Ceiling(percentile * sortedValues.Length) - 1);
        return sortedValues[rank];
    }

    private readonly record struct QueryGroupKey(string Database, string Fingerprint, string NormalizedSql);
}
