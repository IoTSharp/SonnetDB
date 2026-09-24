using System.Text;

namespace SonnetDB.Kv;

/// <summary>单个热点 key 的读取统计。</summary>
/// <param name="Key">UTF-8 key；无法解码时使用 base64url 表示。</param>
/// <param name="Reads">统计窗口内的读取次数。</param>
public sealed record KvHotKey(string Key, long Reads);

/// <summary>KV keyspace 的容量、TTL 与热点诊断快照。</summary>
/// <param name="TotalKeys">当前可见 key 总数。</param>
/// <param name="ActiveKeys">当前未过期 key 数量。</param>
/// <param name="ExpiredKeys">已过期但尚未清理的 key 数量。</param>
/// <param name="ExpiringKeys">带有 TTL 的 key 数量。</param>
/// <param name="NearestExpiresAtUtc">最近的 UTC 过期时间。</param>
/// <param name="MutableOverlayEntries">当前可变覆盖层条目数。</param>
/// <param name="FrozenOverlayEntries">当前冻结覆盖层条目数。</param>
/// <param name="WalBytes">当前活动 WAL 字节数。</param>
/// <param name="MaxSnapshotOverlayEntries">快照覆盖层配置上限。</param>
/// <param name="HotKeys">按读取次数降序排列的热点 key。</param>
public sealed record KvDiagnostics(
    int TotalKeys,
    int ActiveKeys,
    int ExpiredKeys,
    int ExpiringKeys,
    DateTimeOffset? NearestExpiresAtUtc,
    int MutableOverlayEntries,
    int FrozenOverlayEntries,
    long WalBytes,
    int MaxSnapshotOverlayEntries,
    IReadOnlyList<KvHotKey> HotKeys);

public sealed partial class KvKeyspace
{
    private readonly Dictionary<byte[], long> _readCounts = new(KvKeyComparer.Instance);

    private void RecordRead(byte[] key)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _readCounts.TryGetValue(key, out long count);
            _readCounts[key.ToArray()] = count + 1;
        }
    }

    /// <summary>
    /// 返回 keyspace 当前容量、TTL 和热点 key 诊断。
    /// </summary>
    /// <param name="topHotKeys">最多返回的热点 key 数；必须为非负数。</param>
    /// <param name="utcNow">用于判断 TTL 的 UTC 时刻。</param>
    /// <returns>稳定的诊断快照；读取统计仅覆盖当前进程生命周期。</returns>
    public KvDiagnostics GetDiagnostics(int topHotKeys = 10, DateTimeOffset? utcNow = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topHotKeys);
        KvExpirationStats expiration = GetExpirationStats(utcNow);
        lock (_sync)
        {
            ThrowIfDisposed();
            var hotKeys = _readCounts
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, KvKeyComparer.Instance)
                .Take(topHotKeys)
                .Select(static pair => new KvHotKey(FormatKey(pair.Key), pair.Value))
                .ToArray();
            return new KvDiagnostics(
                expiration.TotalKeys,
                expiration.ActiveKeys,
                expiration.ExpiredKeys,
                expiration.ExpiringKeys,
                expiration.NearestExpiresAtUtc,
                _values.Count,
                _frozenValues?.Count ?? 0,
                _wal?.Length ?? 0,
                _options.MaxSnapshotOverlayEntries,
                hotKeys);
        }
    }

    private static string FormatKey(byte[] key)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(key);
        }
        catch (DecoderFallbackException)
        {
            return "base64:" + Convert.ToBase64String(key);
        }
    }
}
