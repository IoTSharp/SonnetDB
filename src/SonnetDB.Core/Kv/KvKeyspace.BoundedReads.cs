using SonnetDB.Exceptions;

namespace SonnetDB.Kv;

/// <summary>按调用方字节预算读取的一页 KV 结果。</summary>
/// <param name="Entries">独立拥有的 key/value 结果。</param>
/// <param name="Truncated">是否仍有未读取的候选记录。</param>
/// <param name="NextKey">继续读取的原始 key；不会越过未读取的超限记录。</param>
public sealed record KvBoundedReadPage(IReadOnlyList<KvEntry> Entries, bool Truncated, byte[]? NextKey);

public sealed partial class KvKeyspace
{
    /// <summary>按显式单值上限点读；磁盘 value 长度在分配和物理读取前检查。</summary>
    /// <param name="key">精确原始 key。</param>
    /// <param name="maxValueBytes">单值最大字节数。</param>
    /// <param name="cancellationToken">调用取消令牌。</param>
    /// <returns>独立拥有的记录；不存在或过期时为空。</returns>
    public KvEntry? GetEntryBounded(ReadOnlyMemory<byte> key, int maxValueBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxValueBytes);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        byte[] lookup = key.ToArray();
        using (EnterAtomicWriteLock(deadline.Token))
        {
            ThrowIfDisposed();
            return ReadBoundedEntryLocked(lookup, maxValueBytes, long.MaxValue, false, deadline.Token, out _);
        }
    }

    /// <summary>仅枚举候选 metadata，在复制内存 value 或读取磁盘 value 前预约页字节预算。</summary>
    /// <param name="prefix">必须匹配的原始 key 前缀。</param>
    /// <param name="afterKey">上一页原始 key，严格从其后继续。</param>
    /// <param name="maxEntries">最大返回条数。</param>
    /// <param name="maxValueBytes">单值最大字节数。</param>
    /// <param name="maxPageBytes">本页 key/value 总字节上限。</param>
    /// <param name="cancellationToken">调用取消令牌。</param>
    /// <param name="maxCandidates">最多检查的候选条数，防止大量墓碑或过期记录形成无界扫描。</param>
    /// <returns>有真实继续位置的预算页。</returns>
    public KvBoundedReadPage ScanPrefixBounded(
        ReadOnlyMemory<byte> prefix, ReadOnlyMemory<byte> afterKey, int maxEntries,
        int maxValueBytes, int maxPageBytes, CancellationToken cancellationToken = default, int maxCandidates = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxValueBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCandidates);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        byte[] prefixBytes = prefix.ToArray();
        byte[]? afterBytes = afterKey.IsEmpty ? null : afterKey.ToArray();
        using (EnterAtomicWriteLock(deadline.Token))
        {
            ThrowIfDisposed();
            var rows = new List<KvEntry>(Math.Min(maxEntries, 256));
            long used = 0;
            byte[]? consumed = afterBytes;
            int candidates = 0;
            foreach (var candidate in EnumerateVisibleEntriesLocked(prefixBytes, afterBytes, readDiskValues: false, cancellationToken: deadline.Token))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (candidates++ >= maxCandidates)
                    return new(rows, true, consumed?.ToArray());
                if (candidate.Value.IsDeleted || candidate.Value.IsExpired(DateTimeOffset.UtcNow))
                {
                    consumed = candidate.Key;
                    continue;
                }
                if (rows.Count == maxEntries)
                    return new(rows, true, consumed?.ToArray());
                var entry = ReadBoundedEntryLocked(candidate.Key, maxValueBytes, maxPageBytes - used,
                    rows.Count != 0, deadline.Token, out var pageFull);
                if (pageFull)
                    return new(rows, true, consumed?.ToArray());
                consumed = candidate.Key;
                if (entry is null)
                    continue;
                used += (long)entry.Key.Length + entry.Value.Length;
                rows.Add(entry);
            }
            return new(rows, false, null);
        }
    }

    private KvEntry? ReadBoundedEntryLocked(byte[] key, int maxValueBytes, long remainingPageBytes,
        bool allowDefer, CancellationToken cancellationToken, out bool pageFull)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pageFull = false;
        var now = DateTimeOffset.UtcNow;
        KvValueEntry? overlay = null;
        KvDiskIndexEntry? disk = null;
        bool inOverlay = TryResolveOverlayEntryLocked(key, out overlay);
        if (inOverlay)
        {
            if (overlay is null || overlay.IsDeleted || overlay.IsExpired(now))
                return null;
        }
        else
        {
            disk = _diskState?.ScanRange(key, startInclusive: null, endExclusive: null, afterKey: null).FirstOrDefault();
            if (disk is null || !disk.Key.AsSpan().SequenceEqual(key) || disk.ExpiresAtUtc is { } expires && expires <= now)
                return null;
        }

        int length = inOverlay ? overlay!.Value.Length : disk!.ValueLength;
        if (length > maxValueBytes || (long)key.Length + length > remainingPageBytes)
        {
            if (!allowDefer)
                throw new PayloadLimitExceededException(Math.Min(maxValueBytes, remainingPageBytes));
            pageFull = true;
            return null;
        }

        // 此处之后才允许复制 value 或进入 KvDiskState.Read 的 new byte[ValueLength]。
        byte[] value = inOverlay ? overlay!.Value.ToArray() : _diskState!.Read(disk!, cancellationToken).Value;
        long version = inOverlay ? overlay!.Version : disk!.Version;
        DateTimeOffset? expiry = inOverlay ? overlay!.ExpiresAtUtc : disk!.ExpiresAtUtc;
        return new KvEntry(key.ToArray(), value, version, expiry);
    }
}
