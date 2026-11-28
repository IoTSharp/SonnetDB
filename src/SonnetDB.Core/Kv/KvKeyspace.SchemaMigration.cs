using System.Diagnostics;

namespace SonnetDB.Kv;

public sealed partial class KvKeyspace
{
    /// <summary>按稳定的原始键读取迁移页。调用方必须保持整个迁移期间的源键空间稳定。</summary>
    /// <remarks>截断页以严格排除上次位置的方式继续。即使没有可见行，也可能消耗一页物理候选。</remarks>
    internal KvBoundedReadPage ReadSchemaMigrationPage(byte prefix, byte[]? afterKey, int maxEntries, int maxPageBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPageBytes);
        if (afterKey is not null && (afterKey.Length == 0 || afterKey[0] != prefix))
            throw new ArgumentException("Schema migration cursor must belong to the scanned key prefix.", nameof(afterKey));

        TimeSpan timeout = TimeSpan.FromSeconds(15);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested();
        byte[] prefixBytes = [prefix];
        int maxCandidates = Math.Max(maxEntries, 4096);
        if (_lsm is not null)
            return LsmScanPrefixBounded(prefixBytes, afterKey, maxEntries, maxPageBytes, maxPageBytes,
                maxCandidates, deadline.Token);

        PrepareLegacySchemaMigrationScan(timeout, deadline.Token);
        KvCandidatePage page = ScanLegacyCandidatePage(prefixBytes, null, afterKey, maxEntries,
            maxCandidates, timeout, deadline.Token, maxPageBytes);
        return new KvBoundedReadPage(page.Entries, page.HasMore, page.HasMore ? page.ContinuationKey : null);
    }

    private void PrepareLegacySchemaMigrationScan(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using (EnterAtomicWriteLock(cancellationToken))
        {
            // 只为仍驻留内存的 WAL 覆盖层建立有序索引。磁盘行使用已有索引，不参与排序。
            int overlayLimit = Math.Min(1_000_000, _options.IndexRebuildMaxOverlayEntries > 0
                ? _options.IndexRebuildMaxOverlayEntries : _options.MaxSnapshotOverlayEntries);
            long started = Stopwatch.GetTimestamp();
            for (int attempt = 0; attempt < 1000; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                var frozen = _frozenValues as KvOrderedOverlay;
                if (_values.OrderedScansEnabled && (_frozenValues is null || frozen is { OrderedScansEnabled: true }))
                    return;
                if ((long)_values.Count + (_frozenValues?.Count ?? 0) <= overlayLimit)
                {
                    _values.EnableOrderedScans(cancellationToken);
                    frozen?.EnableOrderedScans(cancellationToken);
                    return;
                }

                if (!_options.AutoCheckpointEnabled)
                    throw new IOException("Schema migration requires a Legacy checkpoint before scanning the large WAL overlay.");
                if (LastCheckpointException is { } failure && _checkpointState?.IsRunning != true)
                    throw new IOException("Schema migration could not prepare the Legacy checkpoint.", failure);
                if (!_autoCheckpointQueued)
                    ScheduleAutoCheckpointLocked(force: true);
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                    break;
                Monitor.Wait(_sync, remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50));
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Schema migration timed out while waiting for the Legacy checkpoint.");
        }
    }

    // 迁移持有 TableStore 锁；此处额外保持 KV 视图稳定，并在复制 key/value 或磁盘点读前计费。
    // readDiskValues:false 只枚举键和 metadata，不能先物化一整页大 value 再检查预算。
    internal void ReadSchemaMigrationPrefix(byte prefix, Action<int, int> reserve,
        Action<byte[], byte[]> consume, CancellationToken cancellationToken)
    {
        if (_lsm is not null)
        {
            LsmReadSchemaMigrationPrefix(prefix, reserve, consume, cancellationToken);
            return;
        }
        using (EnterAtomicWriteLock(cancellationToken))
        {
            ThrowIfDisposed();
            foreach (var entry in EnumerateVisibleEntriesLocked([prefix], afterKey: null,
                readDiskValues: false, cancellationToken: cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? value = ReadSchemaMigrationValueLocked(entry.Key,
                    length => reserve(entry.Key.Length, length), cancellationToken);
                if (value is not null) consume(entry.Key.ToArray(), value);
            }
        }
    }

    internal byte[]? ReadSchemaMigrationValue(byte[] key, Action<int> reserve, CancellationToken cancellationToken)
    {
        if (_lsm is not null)
            return LsmReadSchemaMigrationValue(key, reserve, cancellationToken);
        using (EnterAtomicWriteLock(cancellationToken))
        {
            ThrowIfDisposed();
            return ReadSchemaMigrationValueLocked(key, reserve, cancellationToken);
        }
    }

    private byte[]? ReadSchemaMigrationValueLocked(byte[] key, Action<int> reserve, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (TryResolveOverlayEntryLocked(key, out KvValueEntry? overlay))
        {
            if (overlay is null || overlay.IsExpired(now)) return null;
            reserve(overlay.Value.Length);
            return overlay.Value.ToArray();
        }
        KvDiskIndexEntry? disk = _diskState?.ScanRange(key, startInclusive: null, endExclusive: null, afterKey: null)
            .FirstOrDefault();
        if (disk is null || !disk.Key.AsSpan().SequenceEqual(key)
            || disk.ExpiresAtUtc is { } expires && expires <= now) return null;
        reserve(disk.ValueLength);
        return _diskState!.Read(disk, cancellationToken, maintenanceRead: true).Value;
    }
}
