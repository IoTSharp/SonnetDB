using System.Collections.Concurrent;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Wal;

namespace SonnetDB.Memory;

/// <summary>
/// 写入路径的内存层：以 <see cref="SeriesFieldKey"/> 为主键聚合 <see cref="MemTableSeries"/>。
/// <para>
/// 并发契约（C10）：<see cref="Append"/> / <see cref="Reset"/> / <see cref="RemoveSeries"/> 三个写侧操作
/// 由调用方的 <c>_writeSync</c> 串行化（measurement 写、DropMeasurement 均在该锁内；WAL 回放在 Open
/// 期间单线程执行），彼此互斥，故不再需要内部的 <see cref="ReaderWriterLockSlim"/> 冗余门。
/// 读侧（Snapshot/TryGet/GetBySeries/PointCount 等）无锁并发安全，依赖三处机制：
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>（桶集合枚举 / GetOrAdd）、每桶 <c>_sync</c>（桶内点与
/// 聚合）、以及统计量的 <see cref="Interlocked"/> 读写——这三者服务 lock-free 读者，必须保留。
/// </para>
/// </summary>
public sealed class MemTable
{
    private readonly ConcurrentDictionary<SeriesFieldKey, MemTableSeries> _series = new();
    private long _pointCount;
    private long _estimatedBytes;
    private long _minTimestamp = long.MaxValue;
    private long _maxTimestamp = long.MinValue;
    private long _firstLsn = long.MinValue;
    private long _lastLsn = long.MinValue;
    private long _createdAtUtcTicks = DateTime.UtcNow.Ticks;

    /// <summary>当前 MemTable 包含的不同 (SeriesId, FieldName) 桶数量。</summary>
    public int SeriesCount => _series.Count;

    /// <summary>当前 MemTable 的总数据点数量。</summary>
    public long PointCount => Interlocked.Read(ref _pointCount);

    /// <summary>所有桶的估算总内存占用（字节）。</summary>
    public long EstimatedBytes => Interlocked.Read(ref _estimatedBytes);

    /// <summary>所有桶中最小时间戳；MemTable 为空时返回 <see cref="long.MaxValue"/>。</summary>
    public long MinTimestamp => Interlocked.Read(ref _minTimestamp);

    /// <summary>所有桶中最大时间戳；MemTable 为空时返回 <see cref="long.MinValue"/>。</summary>
    public long MaxTimestamp => Interlocked.Read(ref _maxTimestamp);

    /// <summary>自上次 Flush 后接收的第一条 WAL LSN；未写入任何记录时为 <see cref="long.MinValue"/>。</summary>
    public long FirstLsn => Interlocked.Read(ref _firstLsn);

    /// <summary>最近写入的 WAL LSN；未写入任何记录时为 <see cref="long.MinValue"/>。</summary>
    public long LastLsn => Interlocked.Read(ref _lastLsn);

    /// <summary>MemTable 创建（或上次 Reset）的 UTC 时间，用于 MaxAge 阈值判断。</summary>
    public DateTime CreatedAtUtc => new(Interlocked.Read(ref _createdAtUtcTicks), DateTimeKind.Utc);

    /// <summary>
    /// 追加一条写入点（来自上层调用或 WAL Replay）。
    /// </summary>
    /// <param name="seriesId">序列唯一标识（XxHash64 值）。</param>
    /// <param name="pointTimestamp">数据点时间戳（Unix 毫秒）。</param>
    /// <param name="fieldName">字段名称。</param>
    /// <param name="value">字段值，类型必须与该桶的 FieldType 一致。</param>
    /// <param name="lsn">WAL 日志序列号。</param>
    /// <exception cref="ArgumentNullException"><paramref name="fieldName"/> 为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">同 key 下 FieldType 不一致时抛出。</exception>
    public void Append(ulong seriesId, long pointTimestamp, string fieldName, FieldValue value, long lsn)
    {
        ArgumentNullException.ThrowIfNull(fieldName);

        // 写侧由调用方 _writeSync 串行化（见类型注释）；此处不再取内部生命周期锁。
        var key = new SeriesFieldKey(seriesId, fieldName);
        var fieldType = value.Type;

        var bucket = _series.GetOrAdd(key, static (k, ft) => new MemTableSeries(k, ft), fieldType);

        if (bucket.FieldType != fieldType)
            throw new InvalidOperationException(
                $"FieldType mismatch for key '{key}': existing={bucket.FieldType}, new={fieldType}.");

        long addedBytes = bucket.AppendAndEstimateBytes(pointTimestamp, value);
        Interlocked.Add(ref _estimatedBytes, addedBytes);
        UpdateMin(ref _minTimestamp, pointTimestamp);
        UpdateMax(ref _maxTimestamp, pointTimestamp);
        Interlocked.Increment(ref _pointCount);

        // FirstLsn: 仅首次 Append 时设置
        Interlocked.CompareExchange(ref _firstLsn, lsn, long.MinValue);
        // LastLsn: 单调推进
        Interlocked.Exchange(ref _lastLsn, lsn);
    }

    /// <summary>
    /// 批量回放 WAL 的 <see cref="WritePointRecord"/> 流（来自 PR #10 的 yield 流）。
    /// </summary>
    /// <param name="records">WritePointRecord 序列。</param>
    /// <returns>成功回放的记录数量。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> 为 null 时抛出。</exception>
    public int ReplayFrom(IEnumerable<WritePointRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        int count = 0;
        foreach (var record in records)
        {
            Append(record.SeriesId, record.PointTimestamp, record.FieldName, record.Value, record.Lsn);
            count++;
        }
        return count;
    }

    /// <summary>
    /// 查找单个桶；未命中返回 null。
    /// </summary>
    /// <param name="key">要查找的 (SeriesId, FieldName) 复合键。</param>
    /// <returns>对应的 <see cref="MemTableSeries"/>，未命中返回 null。</returns>
    public MemTableSeries? TryGet(in SeriesFieldKey key)
        => _series.TryGetValue(key, out var bucket) ? bucket : null;

    /// <summary>
    /// 按 SeriesId 返回该 series 下的所有桶（按 FieldName Ordinal 排序）。
    /// </summary>
    /// <param name="seriesId">目标序列唯一标识（XxHash64 值）。</param>
    /// <returns>按字段名升序排列的桶列表（可能为空列表）。</returns>
    public IReadOnlyList<MemTableSeries> GetBySeries(ulong seriesId)
    {
        var result = new List<MemTableSeries>();
        foreach (var kvp in _series)
        {
            if (kvp.Key.SeriesId == seriesId)
                result.Add(kvp.Value);
        }
        result.Sort(static (a, b) => string.Compare(a.Key.FieldName, b.Key.FieldName, StringComparison.Ordinal));
        return result;
    }

    /// <summary>
    /// 枚举全部桶（调用时拷贝键列表的快照）。
    /// </summary>
    /// <returns>所有桶的只读列表快照。</returns>
    public IReadOnlyList<MemTableSeries> SnapshotAll()
        => [.. _series.Values];

    /// <summary>
    /// 移除指定 SeriesId 集合对应的全部内存桶。
    /// </summary>
    /// <param name="seriesIds">要移除的 SeriesId 集合。</param>
    /// <returns>被移除的桶数量。</returns>
    public int RemoveSeries(IReadOnlySet<ulong> seriesIds)
    {
        ArgumentNullException.ThrowIfNull(seriesIds);
        if (seriesIds.Count == 0)
            return 0;

        // 写侧由调用方 _writeSync 串行化（DropMeasurement 在 _maintenanceSync→_writeSync 内）。
        var keysToRemove = new List<SeriesFieldKey>();
        foreach (var key in _series.Keys)
        {
            if (seriesIds.Contains(key.SeriesId))
                keysToRemove.Add(key);
        }

        var removed = 0;
        foreach (var key in keysToRemove)
        {
            if (_series.TryRemove(key, out _))
                removed++;
        }

        if (removed > 0)
            RecomputeStats();

        return removed;
    }

    /// <summary>
    /// 清空 MemTable（仅供 Flush 完成后调用）。
    /// 清空所有桶、重置计数器与 LSN，刷新 <see cref="CreatedAtUtc"/>。
    /// 写侧由调用方 <c>_writeSync</c> 串行化（当前 double-buffering 下已无生产调用方，保留供测试与兼容）。
    /// </summary>
    public void Reset()
    {
        _series.Clear();
        Interlocked.Exchange(ref _pointCount, 0L);
        Interlocked.Exchange(ref _estimatedBytes, 0L);
        Interlocked.Exchange(ref _minTimestamp, long.MaxValue);
        Interlocked.Exchange(ref _maxTimestamp, long.MinValue);
        Interlocked.Exchange(ref _firstLsn, long.MinValue);
        Interlocked.Exchange(ref _lastLsn, long.MinValue);
        Interlocked.Exchange(ref _createdAtUtcTicks, DateTime.UtcNow.Ticks);
    }

    private void RecomputeStats()
    {
        long pointCount = 0;
        long estimatedBytes = 0;
        long minTimestamp = long.MaxValue;
        long maxTimestamp = long.MinValue;

        foreach (var bucket in _series.Values)
        {
            pointCount += bucket.Count;
            estimatedBytes += bucket.EstimatedBytes;
            long bucketMin = bucket.MinTimestamp;
            long bucketMax = bucket.MaxTimestamp;
            if (bucketMin < minTimestamp)
                minTimestamp = bucketMin;
            if (bucketMax > maxTimestamp)
                maxTimestamp = bucketMax;
        }

        Interlocked.Exchange(ref _pointCount, pointCount);
        Interlocked.Exchange(ref _estimatedBytes, estimatedBytes);
        Interlocked.Exchange(ref _minTimestamp, minTimestamp);
        Interlocked.Exchange(ref _maxTimestamp, maxTimestamp);
        if (pointCount == 0)
        {
            Interlocked.Exchange(ref _firstLsn, long.MinValue);
            Interlocked.Exchange(ref _lastLsn, long.MinValue);
        }
    }

    /// <summary>
    /// 判断是否达到 Flush 阈值。
    /// 满足以下任一条件即返回 true：
    /// <list type="bullet">
    ///   <item><description>EstimatedBytes >= policy.MaxBytes</description></item>
    ///   <item><description>PointCount >= policy.MaxPoints</description></item>
    ///   <item><description>距 MemTable 创建时间 >= policy.MaxAge</description></item>
    /// </list>
    /// </summary>
    /// <param name="policy">Flush 阈值策略。</param>
    /// <returns>满足任一阈值条件返回 true，否则返回 false。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> 为 null 时抛出。</exception>
    public bool ShouldFlush(MemTableFlushPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (EstimatedBytes >= policy.MaxBytes)
            return true;
        if (PointCount >= policy.MaxPoints)
            return true;
        if (DateTime.UtcNow - CreatedAtUtc >= policy.MaxAge)
            return true;
        return false;
    }

    private static void UpdateMin(ref long target, long value)
    {
        while (true)
        {
            long current = Interlocked.Read(ref target);
            if (value >= current)
                return;
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private static void UpdateMax(ref long target, long value)
    {
        while (true)
        {
            long current = Interlocked.Read(ref target);
            if (value <= current)
                return;
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }
}
