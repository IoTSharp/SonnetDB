using System.Text;

namespace SonnetDB.Kv;

/// <summary>
/// SonnetDB 内置 KV Keyspace，提供轻量 <c>Put</c>、<c>Get</c>、<c>Delete</c>、
/// prefix scan、原子计数、乐观锁与 TTL 能力。
/// </summary>
public sealed class KvKeyspace : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<byte[], KvValueEntry> _values;
    private readonly KvOptions _options;
    private KvDiskState? _diskState;
    private KvWalFile? _wal;
    private long _lastSequence;
    private bool _disposed;

    private KvKeyspace(
        string name,
        string rootDirectory,
        KvOptions options,
        Dictionary<byte[], KvValueEntry> values,
        KvDiskState? diskState,
        long lastSequence,
        KvWalFile wal)
    {
        Name = name;
        RootDirectory = rootDirectory;
        _options = options;
        _values = values;
        _diskState = diskState;
        _lastSequence = lastSequence;
        _wal = wal;
    }

    /// <summary>Keyspace 名称。</summary>
    public string Name { get; }

    /// <summary>Keyspace 根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>当前内存视图中的 key 数量。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return CountVisibleLocked();
        }
    }

    /// <summary>当前 keyspace 已应用的最后一个单调版本号。</summary>
    public long LastSequence
    {
        get
        {
            lock (_sync)
                return _lastSequence;
        }
    }

    internal bool IsDisposed
    {
        get
        {
            lock (_sync)
                return _disposed;
        }
    }

    internal void ValidateWrite(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ValidateKey(key, _options);
        ValidateValue(value, _options);
    }

    internal static KvKeyspace Open(string name, string rootDirectory, KvOptions options)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(options);

        Directory.CreateDirectory(rootDirectory);
        Directory.CreateDirectory(WalDirectory(rootDirectory));
        Directory.CreateDirectory(SnapshotsDirectory(rootDirectory));
        Directory.CreateDirectory(SegmentsDirectory(rootDirectory));

        var state = LoadLatestState(rootDirectory);
        long lastSequence = state.Sequence;

        string walPath = ActiveWalPath(rootDirectory);
        foreach (var record in KvWalFile.Replay(walPath))
        {
            if (record.Sequence <= state.Sequence)
                continue;

            ApplyRecord(state, record);
            lastSequence = Math.Max(lastSequence, record.Sequence);
        }

        var wal = KvWalFile.Open(walPath, lastSequence + 1, options.WalBufferSize);
        return new KvKeyspace(name, rootDirectory, options, state.Values, state.DiskState, lastSequence, wal);
    }

    /// <summary>
    /// 写入或覆盖指定 key。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <param name="value">value 字节序列，可为空。</param>
    /// <returns>本次写入的单调版本号。</returns>
    public long Put(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, DateTimeOffset? expiresAtUtc = null)
    {
        ValidateKey(key, _options);
        ValidateValue(value, _options);
        ValidateExpiresAtUtc(expiresAtUtc);

        byte[] keyCopy = key.ToArray();
        byte[] valueCopy = value.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            return PutLocked(keyCopy, valueCopy, expiresAtUtc);
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码写入或覆盖指定字符串 key。
    /// </summary>
    /// <param name="key">非空字符串 key。</param>
    /// <param name="value">value 字节序列，可为空。</param>
    /// <returns>本次写入的单调版本号。</returns>
    public long Put(string key, ReadOnlySpan<byte> value, DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Put(Encoding.UTF8.GetBytes(key), value, expiresAtUtc);
    }

    /// <summary>
    /// 读取指定 key 的当前值。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <returns>找到时返回 value 副本；否则返回 null。</returns>
    public byte[]? Get(ReadOnlySpan<byte> key)
    {
        ValidateKey(key, _options);
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryGetEntryLocked(lookup, out var entry))
                return null;

            if (TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                return null;

            return entry.Value.ToArray();
        }
    }

    /// <summary>
    /// 读取指定 key 的当前值与 metadata。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <returns>找到未过期 key 时返回记录副本；否则返回 null。</returns>
    public KvEntry? GetEntry(ReadOnlySpan<byte> key)
    {
        ValidateKey(key, _options);
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryGetEntryLocked(lookup, out var entry))
                return null;

            if (TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                return null;

            return new KvEntry(lookup, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc);
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码读取指定字符串 key 的当前值与 metadata。
    /// </summary>
    public KvEntry? GetEntry(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return GetEntry(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// 使用 UTF-8 编码读取指定字符串 key 的当前值。
    /// </summary>
    /// <param name="key">非空字符串 key。</param>
    /// <returns>找到时返回 value 副本；否则返回 null。</returns>
    public byte[]? Get(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Get(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// 尝试读取指定 key 的当前值。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <param name="value">找到时输出 value 副本；否则输出空数组。</param>
    /// <returns>找到 key 时返回 <c>true</c>。</returns>
    public bool TryGet(ReadOnlySpan<byte> key, out byte[] value)
    {
        byte[]? found = Get(key);
        if (found is null)
        {
            value = [];
            return false;
        }

        value = found;
        return true;
    }

    /// <summary>
    /// 原子增加整数 value。key 不存在或已过期时按 0 处理；已有 value 必须是 UTF-8 十进制整数。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <param name="delta">增量；可为负数。</param>
    /// <returns>增加后的整数值与写入版本。</returns>
    public (long Value, long Version) Increment(ReadOnlySpan<byte> key, long delta = 1)
    {
        ValidateKey(key, _options);
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            DateTimeOffset? expiresAtUtc = null;
            long current = 0;
            if (TryGetEntryLocked(lookup, out var entry))
            {
                if (!TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                {
                    current = ParseInteger(entry.Value);
                    expiresAtUtc = entry.ExpiresAtUtc;
                }
            }

            long next = checked(current + delta);
            byte[] value = Encoding.UTF8.GetBytes(next.ToString(System.Globalization.CultureInfo.InvariantCulture));
            long version = PutLocked(lookup, value, expiresAtUtc);
            return (next, version);
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码原子增加字符串 key 的整数 value。
    /// </summary>
    /// <param name="key">非空字符串 key。</param>
    /// <param name="delta">增量；可为负数。</param>
    /// <returns>增加后的整数值与写入版本。</returns>
    public (long Value, long Version) Increment(string key, long delta = 1)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Increment(Encoding.UTF8.GetBytes(key), delta);
    }

    /// <summary>
    /// 原子减少整数 value。key 不存在或已过期时按 0 处理；已有 value 必须是 UTF-8 十进制整数。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <param name="delta">减少量；必须非负。</param>
    /// <returns>减少后的整数值与写入版本。</returns>
    public (long Value, long Version) Decrement(ReadOnlySpan<byte> key, long delta = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(delta);
        return Increment(key, -delta);
    }

    /// <summary>
    /// 使用 UTF-8 编码原子减少字符串 key 的整数 value。
    /// </summary>
    /// <param name="key">非空字符串 key。</param>
    /// <param name="delta">减少量；必须非负。</param>
    /// <returns>减少后的整数值与写入版本。</returns>
    public (long Value, long Version) Decrement(string key, long delta = 1)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Decrement(Encoding.UTF8.GetBytes(key), delta);
    }

    /// <summary>
    /// 当 key 当前版本等于期望版本时写入新值；key 不存在时版本视为 0。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <param name="expectedVersion">期望版本；0 表示仅当 key 不存在时创建。</param>
    /// <param name="value">要写入的新 value。</param>
    /// <param name="expiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
    /// <returns>CAS 操作结果。</returns>
    public KvCasResult CompareAndSet(
        ReadOnlySpan<byte> key,
        long expectedVersion,
        ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        ValidateKey(key, _options);
        ValidateValue(value, _options);
        ValidateExpiresAtUtc(expiresAtUtc);

        byte[] lookup = key.ToArray();
        byte[] valueCopy = value.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            long currentVersion = 0;
            if (TryGetEntryLocked(lookup, out var entry))
            {
                if (!TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                    currentVersion = entry.Version;
            }

            if (currentVersion != expectedVersion)
                return new KvCasResult(false, currentVersion, null);

            long newVersion = PutLocked(lookup, valueCopy, expiresAtUtc);
            return new KvCasResult(true, currentVersion, newVersion);
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码对字符串 key 执行比较并交换。
    /// </summary>
    public KvCasResult CompareAndSet(
        string key,
        long expectedVersion,
        ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        return CompareAndSet(Encoding.UTF8.GetBytes(key), expectedVersion, value, expiresAtUtc);
    }

    /// <summary>
    /// 为已存在 key 设置相对 TTL。key 不存在或已过期时返回 <see langword="false"/>。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <param name="ttl">相对过期时间；必须大于 0。</param>
    /// <returns>成功设置 TTL 时为 <see langword="true"/>。</returns>
    public bool Expire(ReadOnlySpan<byte> key, TimeSpan ttl)
    {
        if (ttl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "KV TTL 必须大于 0。");
        return ExpireAt(key, DateTimeOffset.UtcNow.Add(ttl));
    }

    /// <summary>
    /// 使用 UTF-8 编码为字符串 key 设置相对 TTL。
    /// </summary>
    public bool Expire(string key, TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Expire(Encoding.UTF8.GetBytes(key), ttl);
    }

    /// <summary>
    /// 为已存在 key 设置绝对 UTC 过期时间。key 不存在或已过期时返回 <see langword="false"/>。
    /// </summary>
    public bool ExpireAt(ReadOnlySpan<byte> key, DateTimeOffset expiresAtUtc)
    {
        ValidateKey(key, _options);
        ValidateUtc(expiresAtUtc, nameof(expiresAtUtc));
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryGetEntryLocked(lookup, out var entry))
                return false;
            if (TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                return false;

            PutLocked(lookup, entry.Value.ToArray(), expiresAtUtc);
            return true;
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码为字符串 key 设置绝对 UTC 过期时间。
    /// </summary>
    public bool ExpireAt(string key, DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ExpireAt(Encoding.UTF8.GetBytes(key), expiresAtUtc);
    }

    /// <summary>
    /// 移除已存在 key 的过期时间。key 不存在或已过期时返回 <see langword="false"/>。
    /// </summary>
    public bool Persist(ReadOnlySpan<byte> key)
    {
        ValidateKey(key, _options);
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryGetEntryLocked(lookup, out var entry))
                return false;
            if (TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                return false;
            if (!entry.ExpiresAtUtc.HasValue)
                return false;

            PutLocked(lookup, entry.Value.ToArray(), expiresAtUtc: null);
            return true;
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码移除字符串 key 的过期时间。
    /// </summary>
    public bool Persist(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Persist(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// 查询 key 的剩余 TTL。返回值使用 Redis 风格哨兵：不存在为 -2，永不过期为 -1。
    /// </summary>
    public KvTtlResult GetTimeToLive(ReadOnlySpan<byte> key, DateTimeOffset? utcNow = null)
    {
        ValidateKey(key, _options);
        DateTimeOffset now = utcNow ?? DateTimeOffset.UtcNow;
        ValidateUtc(now, nameof(utcNow));
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryGetEntryLocked(lookup, out var entry))
                return new KvTtlResult(KvTtlResult.Missing, null);
            if (TryDeleteExpiredLocked(lookup, entry, now))
                return new KvTtlResult(KvTtlResult.Missing, null);
            if (!entry.ExpiresAtUtc.HasValue)
                return new KvTtlResult(KvTtlResult.NoExpiration, null);

            long remaining = Math.Max(0, (long)Math.Ceiling((entry.ExpiresAtUtc.Value - now).TotalMilliseconds));
            return new KvTtlResult(remaining, entry.ExpiresAtUtc);
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码查询字符串 key 的剩余 TTL。
    /// </summary>
    public KvTtlResult GetTimeToLive(string key, DateTimeOffset? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        return GetTimeToLive(Encoding.UTF8.GetBytes(key), utcNow);
    }

    /// <summary>
    /// 删除指定 key。
    /// </summary>
    /// <param name="key">非空 key 字节序列。</param>
    /// <returns>key 原本存在并已删除时返回 <c>true</c>；不存在时返回 <c>false</c>。</returns>
    public bool Delete(ReadOnlySpan<byte> key)
    {
        ValidateKey(key, _options);
        byte[] lookup = key.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            if (!TryGetEntryLocked(lookup, out var entry))
                return false;

            if (TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                return false;

            return DeleteExistingLocked(lookup);
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码删除指定字符串 key。
    /// </summary>
    /// <param name="key">非空字符串 key。</param>
    /// <returns>key 原本存在并已删除时返回 <c>true</c>；不存在时返回 <c>false</c>。</returns>
    public bool Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Delete(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>
    /// 批量读取多个 key。
    /// </summary>
    public IReadOnlyDictionary<string, byte[]?> GetMany(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            result[key] = Get(key);
        }

        return result;
    }

    /// <summary>
    /// 批量写入多个 key。
    /// </summary>
    /// <returns>每个 key 对应的写入版本号。</returns>
    public IReadOnlyDictionary<string, long> PutMany(
        IEnumerable<KeyValuePair<string, byte[]>> values,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
            result[pair.Key] = Put(pair.Key, pair.Value, expiresAtUtc);
        }

        return result;
    }

    /// <summary>
    /// 批量删除多个 key。
    /// </summary>
    /// <returns>实际删除的 key 数量。</returns>
    public int DeleteMany(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        int removed = 0;
        foreach (string key in keys)
        {
            ArgumentNullException.ThrowIfNull(key);
            if (Delete(key))
                removed++;
        }

        return removed;
    }

    /// <summary>
    /// 打开当前 keyspace 下的逻辑命名空间。
    /// </summary>
    public KvNamespace Namespace(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new KvNamespace(this, name);
    }

    /// <summary>
    /// 按 key 前缀扫描当前内存视图。
    /// </summary>
    /// <param name="prefix">key 前缀；为空时扫描全部 key。</param>
    /// <param name="limit">最大返回行数；小于等于 0 时返回空集合。</param>
    /// <returns>按 key 字节序升序排列的结果快照。</returns>
    public IReadOnlyList<KvEntry> ScanPrefix(ReadOnlySpan<byte> prefix, int? limit = null)
    {
        return ScanPrefixAfter(prefix, afterKey: null, limit);
    }

    /// <summary>
    /// 按 key 前缀扫描当前内存视图，并从指定 key 之后继续读取。
    /// </summary>
    /// <param name="prefix">key 前缀；为空时扫描全部 key。</param>
    /// <param name="afterKey">上一页最后一个 key；为 null 时从前缀起点开始。</param>
    /// <param name="limit">最大返回行数；小于等于 0 时返回空集合。</param>
    /// <returns>按 key 字节序升序排列的结果快照。</returns>
    public IReadOnlyList<KvEntry> ScanPrefixAfter(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> afterKey,
        int? limit = null)
    {
        return ScanPrefixAfter(prefix, afterKey.IsEmpty ? null : afterKey.ToArray(), limit);
    }

    private IReadOnlyList<KvEntry> ScanPrefixAfter(
        ReadOnlySpan<byte> prefix,
        byte[]? afterKey,
        int? limit)
    {
        int take = limit ?? _options.DefaultScanLimit;
        if (take <= 0)
            return Array.Empty<KvEntry>();

        byte[] prefixCopy = prefix.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            var rows = new List<KvEntry>(Math.Min(take, CountVisibleLocked()));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (var pair in EnumerateVisibleEntriesLocked(prefixCopy, afterKey))
            {
                if (TryDeleteExpiredLocked(pair.Key, pair.Value, now))
                    continue;

                rows.Add(new KvEntry(
                    pair.Key.ToArray(),
                    pair.Value.Value.ToArray(),
                    pair.Value.Version,
                    pair.Value.ExpiresAtUtc));
                if (rows.Count >= take)
                    break;
            }

            return rows;
        }
    }

    /// <summary>
    /// 统计指定 key 前缀下的可见 key 数量，不读取 value。
    /// </summary>
    /// <param name="prefix">key 前缀；为空时统计全部 key。</param>
    /// <returns>未过期的可见 key 数量。</returns>
    public int CountPrefix(ReadOnlySpan<byte> prefix)
    {
        byte[] prefixCopy = prefix.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            int count = 0;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (var pair in EnumerateVisibleEntriesLocked(prefixCopy, afterKey: null, readDiskValues: false))
            {
                if (TryDeleteExpiredLocked(pair.Key, pair.Value, now))
                    continue;

                count++;
            }

            return count;
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码统计指定字符串前缀下的可见 key 数量，不读取 value。
    /// </summary>
    /// <param name="prefix">key 前缀；为空时统计全部 key。</param>
    /// <returns>未过期的可见 key 数量。</returns>
    public int CountPrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return CountPrefix(Encoding.UTF8.GetBytes(prefix));
    }

    /// <summary>
    /// 使用 UTF-8 编码按字符串前缀扫描当前内存视图。
    /// </summary>
    /// <param name="prefix">key 前缀；为空时扫描全部 key。</param>
    /// <param name="limit">最大返回行数；小于等于 0 时返回空集合。</param>
    /// <returns>按 key 字节序升序排列的结果快照。</returns>
    public IReadOnlyList<KvEntry> ScanPrefix(string prefix, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return ScanPrefix(Encoding.UTF8.GetBytes(prefix), limit);
    }

    /// <summary>
    /// 使用 UTF-8 编码按字符串前缀扫描，并从指定 key 之后继续读取。
    /// </summary>
    public IReadOnlyList<KvEntry> ScanPrefixAfter(string prefix, string? afterKey, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return ScanPrefixAfter(
            Encoding.UTF8.GetBytes(prefix),
            string.IsNullOrEmpty(afterKey) ? null : Encoding.UTF8.GetBytes(afterKey),
            limit);
    }

    /// <summary>
    /// 删除指定前缀下的未过期 key，并顺带清理命中的已过期 key。
    /// </summary>
    /// <param name="prefix">key 前缀；为空时匹配全部 key。</param>
    /// <param name="limit">最大删除数量；小于等于 0 时不删除。</param>
    /// <returns>实际删除的 key 数量。</returns>
    public int DeletePrefix(ReadOnlySpan<byte> prefix, int? limit = null)
    {
        int take = limit ?? int.MaxValue;
        if (take <= 0)
            return 0;

        byte[] prefixCopy = prefix.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            var keys = EnumerateVisibleEntriesLocked(prefixCopy, afterKey: null, readDiskValues: false)
                .Select(static pair => pair.Key.ToArray())
                .Take(take)
                .ToArray();

            int removed = 0;
            foreach (byte[] key in keys)
            {
                if (DeleteExistingLocked(key))
                    removed++;
            }

            return removed;
        }
    }

    /// <summary>
    /// 使用 UTF-8 编码删除指定字符串前缀下的 key。
    /// </summary>
    public int DeletePrefix(string prefix, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return DeletePrefix(Encoding.UTF8.GetBytes(prefix), limit);
    }

    /// <summary>
    /// 清理已过期 key。
    /// </summary>
    /// <param name="utcNow">用于判定过期的 UTC 时间。</param>
    /// <param name="limit">最大清理数量；小于等于 0 时不清理。</param>
    /// <returns>实际清理的 key 数量。</returns>
    public int CleanExpired(DateTimeOffset? utcNow = null, int? limit = null)
    {
        int take = limit ?? int.MaxValue;
        if (take <= 0)
            return 0;

        DateTimeOffset now = utcNow ?? DateTimeOffset.UtcNow;
        ValidateUtc(now, nameof(utcNow));

        lock (_sync)
        {
            ThrowIfDisposed();
            var keys = _values
                .Where(pair => pair.Value.IsExpired(now))
                .OrderBy(static pair => pair.Key, KvKeyComparer.Instance)
                .Take(take)
                .Select(static pair => pair.Key.ToArray())
                .ToArray();

            int removed = 0;
            foreach (byte[] key in keys)
            {
                if (DeleteExistingLocked(key))
                    removed++;
            }

            return removed;
        }
    }

    /// <summary>
    /// 统计当前 keyspace 的过期状态。
    /// </summary>
    public KvExpirationStats GetExpirationStats(DateTimeOffset? utcNow = null)
    {
        DateTimeOffset now = utcNow ?? DateTimeOffset.UtcNow;
        ValidateUtc(now, nameof(utcNow));

        lock (_sync)
        {
            ThrowIfDisposed();
            int expired = 0;
            int expiring = 0;
            DateTimeOffset? nearest = null;
            int total = 0;

            foreach (var pair in EnumerateVisibleEntriesLocked(prefix: [], afterKey: null, readDiskValues: false))
            {
                var entry = pair.Value;
                total++;
                if (!entry.ExpiresAtUtc.HasValue)
                    continue;

                expiring++;
                if (entry.IsExpired(now))
                {
                    expired++;
                    continue;
                }

                nearest = nearest is null || entry.ExpiresAtUtc.Value < nearest.Value
                    ? entry.ExpiresAtUtc
                    : nearest;
            }

            return new KvExpirationStats(
                total,
                total - expired,
                expired,
                expiring,
                nearest);
        }
    }

    /// <summary>
    /// 写出当前 keyspace 的完整快照，并截断快照版本之前的 KV WAL。
    /// </summary>
    /// <returns>快照覆盖到的版本号。</returns>
    public long CreateSnapshot()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            CleanExpiredLocked(DateTimeOffset.UtcNow, int.MaxValue);
            _wal!.Sync();
            long sequence = _lastSequence;
            string snapshotPath = SnapshotPath(RootDirectory, sequence);
            KvStateFile.SaveSnapshot(
                snapshotPath,
                sequence,
                EnumerateVisibleEntriesLocked(prefix: [], afterKey: null),
                CountVisibleLocked());
            var newDiskState = KvStateFile.OpenDiskState(snapshotPath);
            ResetWalLocked(sequence + 1);
            _values.Clear();
            ReplaceDiskStateLocked(newDiskState);
            DeleteOlderFiles(SnapshotsDirectory(RootDirectory), "*.SDBKVSNP", sequence);
            return sequence;
        }
    }

    /// <summary>
    /// 将当前 keyspace 压实为一个不可变段文件，并截断已压实版本之前的 KV WAL。
    /// </summary>
    /// <returns>压实覆盖到的版本号。</returns>
    public long Compact()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            CleanExpiredLocked(DateTimeOffset.UtcNow, int.MaxValue);
            _wal!.Sync();
            long sequence = _lastSequence;
            string segmentPath = SegmentPath(RootDirectory, sequence);
            KvStateFile.SaveSegment(
                segmentPath,
                sequence,
                EnumerateVisibleEntriesLocked(prefix: [], afterKey: null),
                CountVisibleLocked());
            var newDiskState = KvStateFile.OpenDiskState(segmentPath);
            ResetWalLocked(sequence + 1);
            _values.Clear();
            ReplaceDiskStateLocked(newDiskState);
            DeleteOlderFiles(SegmentsDirectory(RootDirectory), "*.SDBKVSEG", sequence);
            DeleteOlderFiles(SnapshotsDirectory(RootDirectory), "*.SDBKVSNP", sequence);
            return sequence;
        }
    }

    /// <summary>
    /// 关闭 keyspace 并刷盘 KV WAL。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _wal?.Dispose();
            _diskState?.Dispose();
            _wal = null;
            _diskState = null;
        }
    }

    internal static string WalDirectory(string rootDirectory) => Path.Combine(rootDirectory, "wal");

    internal static string SnapshotsDirectory(string rootDirectory) => Path.Combine(rootDirectory, "snapshots");

    internal static string SegmentsDirectory(string rootDirectory) => Path.Combine(rootDirectory, "segments");

    internal static string ActiveWalPath(string rootDirectory) =>
        Path.Combine(WalDirectory(rootDirectory), "active.SDBKVWAL");

    internal static string SnapshotPath(string rootDirectory, long sequence) =>
        Path.Combine(SnapshotsDirectory(rootDirectory), $"{sequence:D20}.SDBKVSNP");

    internal static string SegmentPath(string rootDirectory, long sequence) =>
        Path.Combine(SegmentsDirectory(rootDirectory), $"{sequence:D20}.SDBKVSEG");

    private static KvStateSnapshot LoadLatestState(string rootDirectory)
    {
        var candidates = new List<(long Sequence, bool IsSegment, string Path)>();
        AddStateCandidates(candidates, SnapshotsDirectory(rootDirectory), "*.SDBKVSNP", isSegment: false);
        AddStateCandidates(candidates, SegmentsDirectory(rootDirectory), "*.SDBKVSEG", isSegment: true);

        if (candidates.Count == 0)
            return new KvStateSnapshot(0, new Dictionary<byte[], KvValueEntry>(KvKeyComparer.Instance), diskState: null);

        var latest = candidates
            .OrderByDescending(static x => x.Sequence)
            .ThenByDescending(static x => x.IsSegment)
            .First();

        var diskState = KvStateFile.OpenDiskState(latest.Path);
        return new KvStateSnapshot(
            diskState.Sequence,
            new Dictionary<byte[], KvValueEntry>(KvKeyComparer.Instance),
            diskState);
    }

    private static void AddStateCandidates(
        List<(long Sequence, bool IsSegment, string Path)> candidates,
        string directory,
        string pattern,
        bool isSegment)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, pattern))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (long.TryParse(name, out long sequence))
                candidates.Add((sequence, isSegment, file));
        }
    }

    private static void ApplyRecord(KvStateSnapshot state, KvWalRecord record)
    {
        if (record.Kind == KvWalRecordKind.Put)
        {
            state.Values[record.Key] = new KvValueEntry(record.Value ?? [], record.Sequence, record.ExpiresAtUtc);
            return;
        }

        if (state.DiskState is not null && state.DiskState.Contains(record.Key))
            state.Values[record.Key] = KvValueEntry.Deleted(record.Sequence);
        else
            state.Values.Remove(record.Key);
    }

    private static void ValidateKey(ReadOnlySpan<byte> key, KvOptions options)
    {
        if (key.IsEmpty)
            throw new ArgumentException("KV key 不能为空。", nameof(key));
        if (key.Length > options.MaxKeyBytes)
            throw new ArgumentOutOfRangeException(nameof(key), $"KV key 不能超过 {options.MaxKeyBytes} 字节。");
    }

    private static void ValidateValue(ReadOnlySpan<byte> value, KvOptions options)
    {
        if (value.Length > options.MaxValueBytes)
            throw new ArgumentOutOfRangeException(nameof(value), $"KV value 不能超过 {options.MaxValueBytes} 字节。");
    }

    private static void ValidateExpiresAtUtc(DateTimeOffset? expiresAtUtc)
    {
        if (expiresAtUtc.HasValue)
            ValidateUtc(expiresAtUtc.Value, nameof(expiresAtUtc));
    }

    private static void ValidateUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
            throw new ArgumentException("KV expires-at 必须使用 UTC 时间。", parameterName);
    }

    private static void DeleteOlderFiles(string directory, string pattern, long keepSequence)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, pattern))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (long.TryParse(name, out long sequence) && sequence < keepSequence)
                File.Delete(file);
        }
    }

    private void ResetWalLocked(long nextSequence)
    {
        _wal?.Dispose();
        string walPath = ActiveWalPath(RootDirectory);
        File.Delete(walPath);
        _wal = KvWalFile.Open(walPath, Math.Max(nextSequence, 1), _options.WalBufferSize);
    }

    private bool TryDeleteExpiredLocked(byte[] key, KvValueEntry entry, DateTimeOffset utcNow)
    {
        if (!entry.IsExpired(utcNow))
            return false;

        DeleteExistingLocked(key);
        return true;
    }

    private int CleanExpiredLocked(DateTimeOffset utcNow, int limit)
    {
        var keys = EnumerateVisibleEntriesLocked(prefix: [], afterKey: null, readDiskValues: false)
            .Where(pair => pair.Value.IsExpired(utcNow))
            .Select(static pair => pair.Key.ToArray())
            .Take(limit)
            .ToArray();

        int removed = 0;
        foreach (byte[] key in keys)
        {
            if (DeleteExistingLocked(key))
                removed++;
        }

        return removed;
    }

    private bool DeleteExistingLocked(byte[] key)
    {
        if (!TryGetEntryLocked(key, out _))
            return false;

        long sequence = _wal!.AppendDelete(key);
        if (_options.SyncWalOnEveryWrite)
            _wal.Sync();

        if (_diskState is not null && _diskState.Contains(key))
            _values[key.ToArray()] = KvValueEntry.Deleted(sequence);
        else
            _values.Remove(key);

        _lastSequence = sequence;
        return true;
    }

    private long PutLocked(byte[] keyCopy, byte[] valueCopy, DateTimeOffset? expiresAtUtc)
    {
        long sequence = _wal!.AppendPut(keyCopy, valueCopy, expiresAtUtc);
        if (_options.SyncWalOnEveryWrite)
            _wal.Sync();

        _values[keyCopy] = new KvValueEntry(valueCopy, sequence, expiresAtUtc);
        _lastSequence = sequence;
        return sequence;
    }

    private bool TryGetEntryLocked(ReadOnlySpan<byte> key, out KvValueEntry entry)
    {
        if (_values.TryGetValue(key.ToArray(), out entry!))
            return !entry.IsDeleted;

        entry = _diskState?.Get(key)!;
        return entry is not null;
    }

    private int CountVisibleLocked()
    {
        int count = _diskState?.Count ?? 0;
        foreach (var pair in _values)
        {
            bool existsOnDisk = _diskState?.Contains(pair.Key) == true;
            if (pair.Value.IsDeleted)
            {
                if (existsOnDisk)
                    count--;
                continue;
            }

            if (!existsOnDisk)
                count++;
        }

        return count;
    }

    private IEnumerable<KeyValuePair<byte[], KvValueEntry>> EnumerateVisibleEntriesLocked(
        byte[] prefix,
        byte[]? afterKey,
        bool readDiskValues = true)
    {
        using var memory = _values
            .Where(pair => !pair.Value.IsDeleted
                && pair.Key.AsSpan().StartsWith(prefix)
                && (afterKey is null || KvKeyComparer.Instance.Compare(pair.Key, afterKey) > 0))
            .OrderBy(static pair => pair.Key, KvKeyComparer.Instance)
            .GetEnumerator();

        using var disk = (_diskState?.ScanPrefixAfter(prefix, afterKey)
                ?? Enumerable.Empty<KvDiskIndexEntry>())
            .GetEnumerator();

        bool hasMemory = memory.MoveNext();
        bool hasDisk = disk.MoveNext();
        while (hasMemory || hasDisk)
        {
            if (!hasDisk)
            {
                yield return new KeyValuePair<byte[], KvValueEntry>(
                    memory.Current.Key,
                    memory.Current.Value);
                hasMemory = memory.MoveNext();
                continue;
            }

            if (!hasMemory)
            {
                var diskEntry = disk.Current;
                if (!_values.ContainsKey(diskEntry.Key))
                    yield return new KeyValuePair<byte[], KvValueEntry>(
                        diskEntry.Key,
                        readDiskValues ? _diskState!.Read(diskEntry) : diskEntry.ToValueEntry());
                hasDisk = disk.MoveNext();
                continue;
            }

            int comparison = KvKeyComparer.Instance.Compare(memory.Current.Key, disk.Current.Key);
            if (comparison < 0)
            {
                yield return new KeyValuePair<byte[], KvValueEntry>(
                    memory.Current.Key,
                    memory.Current.Value);
                hasMemory = memory.MoveNext();
                continue;
            }

            if (comparison == 0)
            {
                yield return new KeyValuePair<byte[], KvValueEntry>(
                    memory.Current.Key,
                    memory.Current.Value);
                hasMemory = memory.MoveNext();
                hasDisk = disk.MoveNext();
                continue;
            }

            var currentDisk = disk.Current;
            if (!_values.ContainsKey(currentDisk.Key))
                yield return new KeyValuePair<byte[], KvValueEntry>(
                    currentDisk.Key,
                    readDiskValues ? _diskState!.Read(currentDisk) : currentDisk.ToValueEntry());
            hasDisk = disk.MoveNext();
        }
    }

    private void ReplaceDiskStateLocked(KvDiskState? diskState)
    {
        var old = _diskState;
        _diskState = diskState;
        old?.Dispose();
    }

    private static long ParseInteger(byte[] value)
    {
        string text = Encoding.UTF8.GetString(value);
        if (!long.TryParse(
            text,
            System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture,
            out long parsed))
        {
            throw new InvalidOperationException("KV value 不是有效的 UTF-8 十进制整数，无法执行 INCR/DECR。");
        }

        return parsed;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
