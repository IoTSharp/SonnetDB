using System.Text;

namespace SonnetDB.Kv;

/// <summary>
/// SonnetDB 内置 KV Keyspace，提供轻量 <c>Put</c>、<c>Get</c>、<c>Delete</c> 和 prefix scan 能力。
/// </summary>
public sealed class KvKeyspace : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<byte[], KvValueEntry> _values;
    private readonly KvOptions _options;
    private KvWalFile? _wal;
    private long _lastSequence;
    private bool _disposed;

    private KvKeyspace(
        string name,
        string rootDirectory,
        KvOptions options,
        Dictionary<byte[], KvValueEntry> values,
        long lastSequence,
        KvWalFile wal)
    {
        Name = name;
        RootDirectory = rootDirectory;
        _options = options;
        _values = values;
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
                return _values.Count;
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

            ApplyRecord(state.Values, record);
            lastSequence = Math.Max(lastSequence, record.Sequence);
        }

        var wal = KvWalFile.Open(walPath, lastSequence + 1, options.WalBufferSize);
        return new KvKeyspace(name, rootDirectory, options, state.Values, lastSequence, wal);
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
            long sequence = _wal!.AppendPut(keyCopy, valueCopy, expiresAtUtc);
            if (_options.SyncWalOnEveryWrite)
                _wal.Sync();

            _values[keyCopy] = new KvValueEntry(valueCopy, sequence, expiresAtUtc);
            _lastSequence = sequence;
            return sequence;
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
            if (!_values.TryGetValue(lookup, out var entry))
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
            if (!_values.TryGetValue(lookup, out var entry))
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
            if (!_values.TryGetValue(lookup, out var entry))
                return false;

            if (TryDeleteExpiredLocked(lookup, entry, DateTimeOffset.UtcNow))
                return false;

            long sequence = _wal!.AppendDelete(lookup);
            if (_options.SyncWalOnEveryWrite)
                _wal.Sync();

            _values.Remove(lookup);
            _lastSequence = sequence;
            return true;
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
        int take = limit ?? _options.DefaultScanLimit;
        if (take <= 0)
            return Array.Empty<KvEntry>();

        byte[] prefixCopy = prefix.ToArray();

        lock (_sync)
        {
            ThrowIfDisposed();
            var rows = new List<KvEntry>(Math.Min(take, _values.Count));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (var pair in _values.OrderBy(static x => x.Key, KvKeyComparer.Instance))
            {
                if (!pair.Key.AsSpan().StartsWith(prefixCopy))
                    continue;

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
            var keys = _values
                .Keys
                .Where(key => key.AsSpan().StartsWith(prefixCopy))
                .Order(KvKeyComparer.Instance)
                .Take(take)
                .Select(static key => key.ToArray())
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

            foreach (var entry in _values.Values)
            {
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
                _values.Count,
                _values.Count - expired,
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
            KvStateFile.SaveSnapshot(SnapshotPath(RootDirectory, sequence), sequence, _values);
            ResetWalLocked(sequence + 1);
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
            KvStateFile.SaveSegment(SegmentPath(RootDirectory, sequence), sequence, _values);
            ResetWalLocked(sequence + 1);
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
            _wal = null;
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
        var candidates = new List<(long Sequence, string Path)>();
        AddStateCandidates(candidates, SnapshotsDirectory(rootDirectory), "*.SDBKVSNP");
        AddStateCandidates(candidates, SegmentsDirectory(rootDirectory), "*.SDBKVSEG");

        if (candidates.Count == 0)
            return new KvStateSnapshot(0, new Dictionary<byte[], KvValueEntry>(KvKeyComparer.Instance));

        var latest = candidates.OrderByDescending(static x => x.Sequence).First();
        return KvStateFile.Load(latest.Path);
    }

    private static void AddStateCandidates(List<(long Sequence, string Path)> candidates, string directory, string pattern)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (string file in Directory.EnumerateFiles(directory, pattern))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (long.TryParse(name, out long sequence))
                candidates.Add((sequence, file));
        }
    }

    private static void ApplyRecord(Dictionary<byte[], KvValueEntry> values, KvWalRecord record)
    {
        if (record.Kind == KvWalRecordKind.Put)
        {
            values[record.Key] = new KvValueEntry(record.Value ?? [], record.Sequence, record.ExpiresAtUtc);
            return;
        }

        values.Remove(record.Key);
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
        var keys = _values
            .Where(pair => pair.Value.IsExpired(utcNow))
            .OrderBy(static pair => pair.Key, KvKeyComparer.Instance)
            .Take(limit)
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

    private bool DeleteExistingLocked(byte[] key)
    {
        if (!_values.ContainsKey(key))
            return false;

        long sequence = _wal!.AppendDelete(key);
        if (_options.SyncWalOnEveryWrite)
            _wal.Sync();

        _values.Remove(key);
        _lastSequence = sequence;
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
