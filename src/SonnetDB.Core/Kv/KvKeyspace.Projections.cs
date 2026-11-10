namespace SonnetDB.Kv;

internal delegate T KvEntryProjection<T>(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value);
internal delegate T KvValueProjection<T>(ReadOnlySpan<byte> value);

public sealed partial class KvKeyspace
{
    /// <summary>在稳定视图内立即投影条目；借用的载荷只在投影回调期间有效，不为被跳过的大列复制 value。</summary>
    internal void ReadStablePrefixProjectedPages<T>(ReadOnlySpan<byte> prefix, int pageSize,
        KvEntryProjection<T> project, Action<IReadOnlyList<T>> readPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(readPage);
        byte[] prefixCopy = prefix.ToArray();
        var page = new List<T>(pageSize);
        void Visit(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            page.Add(project(key, value));
            if (page.Count < pageSize) return;
            readPage(page);
            page = new List<T>(pageSize);
        }
        if (_lsm is not null)
        {
            ThrowIfDisposedForLsm();
            StablePrefixScanTestHook?.Invoke(prefixCopy);
            long now = DateTimeOffset.UtcNow.UtcTicks;
            _lsm.ScanForward(prefixCopy, null, CancellationToken.None, cursor =>
            {
                if (cursor.ExpiresAtUtcTicks == 0 || cursor.ExpiresAtUtcTicks > now)
                    Visit(cursor.Key, cursor.Value);
                return true;
            });
        }
        else
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                StablePrefixScanTestHook?.Invoke(prefixCopy);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                foreach (var pair in EnumerateVisibleEntriesLocked(prefixCopy, afterKey: null))
                    if (!pair.Value.IsExpired(now)) Visit(pair.Key, pair.Value.Value);
            }
        }
        if (page.Count != 0) readPage(page);
    }

    /// <summary>借用点查载荷完成内部投影，返回值必须独立于借用的载荷。</summary>
    internal T? ReadProjectedValue<T>(byte[] key, KvValueProjection<T> project) where T : class
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_lsm is null)
        {
            byte[]? value = Get(key);
            return value is null ? null : project(value);
        }
        ThrowIfDisposedForLsm();
        return _lsm.ReadProjectedValue(key, project);
    }
}
