using System.Diagnostics;

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
                KvValueEntry value = takeMutable ? mutable.Current.Value
                    : takeFrozen ? frozen.Current.Value : _diskState!.Read(disk.Current);
                continuation = key.ToArray();
                // 上层墓碑屏蔽同键的下层数据；不在只读调度中额外写入 TTL 清理 WAL。
                if (!value.IsDeleted && !value.IsExpired(now))
                    rows.Add(new KvEntry(continuation, value.Value.ToArray(), value.Version, value.ExpiresAtUtc));
                if (takeMutable) hasMutable = mutable.MoveNext();
                if (takeFrozen) hasFrozen = frozen.MoveNext();
                if (takeDisk) hasDisk = disk.MoveNext();
                if (rows.Count >= resultLimit)
                {
                    visited++;
                    break;
                }
            }
            return new KvCandidatePage(rows, continuation, hasMutable || hasFrozen || hasDisk, visited);
        }
    }
}
