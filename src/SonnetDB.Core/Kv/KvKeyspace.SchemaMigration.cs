namespace SonnetDB.Kv;

public sealed partial class KvKeyspace
{
    // 迁移持有 TableStore 锁；此处额外保持 KV 视图稳定，并在复制 key/value 或磁盘点读前计费。
    // readDiskValues:false 只枚举键和 metadata，不能先物化一整页大 value 再检查预算。
    internal void ReadSchemaMigrationPrefix(byte prefix, Action<int, int> reserve,
        Action<byte[], byte[]> consume, CancellationToken cancellationToken)
    {
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
