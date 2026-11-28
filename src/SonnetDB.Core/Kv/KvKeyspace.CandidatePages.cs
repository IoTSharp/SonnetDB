using System.Diagnostics;
using SonnetDB.Exceptions;

namespace SonnetDB.Kv;

/// <summary>后台调度所需的物理候选有界范围读取，不为墓碑反复从前缀起点扫到第一个活键。</summary>
public sealed partial class KvKeyspace
{
    /// <summary>合并至多三个已排序层，返回可见值以及最后检查的物理键；墓碑同样消耗候选预算。</summary>
    internal KvCandidatePage ScanCandidatePage(
        byte[] prefix, byte[]? endExclusive, byte[]? afterKey,
        int resultLimit, int candidateLimit, TimeSpan timeBudget, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(resultLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(candidateLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeBudget, TimeSpan.Zero);
        if (_lsm is not null)
            return LsmScanCandidatePage(prefix, endExclusive, afterKey, resultLimit, candidateLimit, timeBudget, cancellationToken);
        return ScanLegacyCandidatePage(prefix, endExclusive, afterKey, resultLimit, candidateLimit, timeBudget, cancellationToken);
    }

    private KvCandidatePage ScanLegacyCandidatePage(
        byte[] prefix, byte[]? endExclusive, byte[]? afterKey,
        int resultLimit, int candidateLimit, TimeSpan timeBudget, CancellationToken cancellationToken,
        int? maxPageBytes = null)
    {
        using (EnterAtomicWriteLock(cancellationToken))
        {
            ThrowIfDisposed();
            if (!_values.OrderedScansEnabled
                || _frozenValues is not null && _frozenValues is not KvOrderedOverlay { OrderedScansEnabled: true })
                throw new IOException("KV ordered overlay is unavailable for bounded candidate scan.");
            long started = Stopwatch.GetTimestamp();
            var rows = new List<KvEntry>(Math.Min(resultLimit, ScanResultInitialCapacity));
            // 每层都必须包含墓碑，才能在达到物理候选上限时留下准确的续扫位置。
            using var mutable = _values.Scan(prefix, null, endExclusive, afterKey, cancellationToken, null, includeDeleted: true).GetEnumerator();
            using var frozen = (_frozenValues is KvOrderedOverlay frozenOverlay
                ? frozenOverlay.Scan(prefix, null, endExclusive, afterKey, cancellationToken, null, includeDeleted: true)
                : Enumerable.Empty<KeyValuePair<byte[], KvValueEntry>>()).GetEnumerator();
            using var disk = (_diskState?.ScanRange(prefix, null, endExclusive, afterKey)
                ?? Enumerable.Empty<KvDiskIndexEntry>()).GetEnumerator();
            bool hasMutable = mutable.MoveNext();
            bool hasFrozen = frozen.MoveNext();
            bool hasDisk = disk.MoveNext();
            byte[]? continuation = afterKey;
            int visited = 0;
            long usedBytes = 0;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (; visited < candidateLimit && (hasMutable || hasFrozen || hasDisk); visited++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (visited > 0 && Stopwatch.GetElapsedTime(started) >= timeBudget)
                    break;
                byte[] key = hasMutable ? mutable.Current.Key : hasFrozen ? frozen.Current.Key : disk.Current.Key;
                if (hasFrozen && KvKeyComparer.Instance.Compare(frozen.Current.Key, key) < 0)
                    key = frozen.Current.Key;
                if (hasDisk && KvKeyComparer.Instance.Compare(disk.Current.Key, key) < 0)
                    key = disk.Current.Key;
                bool takeMutable = hasMutable && KvKeyComparer.Instance.Compare(mutable.Current.Key, key) == 0;
                bool takeFrozen = hasFrozen && KvKeyComparer.Instance.Compare(frozen.Current.Key, key) == 0;
                bool takeDisk = hasDisk && KvKeyComparer.Instance.Compare(disk.Current.Key, key) == 0;
                KvValueEntry? overlayEntry = takeMutable ? mutable.Current.Value : takeFrozen ? frozen.Current.Value : null;
                KvDiskIndexEntry? diskEntry = overlayEntry is null ? disk.Current : null;
                DateTimeOffset? expiry = overlayEntry is null ? diskEntry!.ExpiresAtUtc : overlayEntry.ExpiresAtUtc;
                // 上层墓碑屏蔽同键的下层数据；不在只读调度中额外写入 TTL 清理 WAL。
                if (overlayEntry is not { IsDeleted: true } && (expiry is null || expiry > now))
                {
                    int length = overlayEntry is null ? diskEntry!.ValueLength : overlayEntry.Value.Length;
                    long entryBytes = (long)key.Length + length;
                    if (maxPageBytes is int maximum && entryBytes > maximum - usedBytes)
                    {
                        if (rows.Count == 0)
                            throw new PayloadLimitExceededException(maximum);
                        break;
                    }

                    // 检查页预算后才读取磁盘 value；磁盘读取的数组由本页独占。
                    byte[] value = overlayEntry is null
                        ? _diskState!.Read(diskEntry!, cancellationToken, maintenanceRead: maxPageBytes.HasValue).Value
                        : overlayEntry.Value.ToArray();
                    long version = overlayEntry is null ? diskEntry!.Version : overlayEntry.Version;
                    rows.Add(new KvEntry(key.ToArray(), value, version, expiry));
                    usedBytes += entryBytes;
                }
                continuation = key;
                if (takeMutable) hasMutable = mutable.MoveNext();
                if (takeFrozen) hasFrozen = frozen.MoveNext();
                if (takeDisk) hasDisk = disk.MoveNext();
                if (rows.Count >= resultLimit)
                {
                    visited++;
                    break;
                }
            }
            return new KvCandidatePage(rows, continuation?.ToArray(), hasMutable || hasFrozen || hasDisk, visited);
        }
    }
}
