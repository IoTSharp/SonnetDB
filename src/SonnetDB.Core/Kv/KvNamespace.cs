namespace SonnetDB.Kv;

/// <summary>
/// 基于同一个 KV keyspace 的逻辑命名空间视图。
/// </summary>
public sealed class KvNamespace
{
    private readonly KvKeyspace _keyspace;
    private readonly byte[] _prefix;
    private readonly string _prefixText;

    internal KvNamespace(KvKeyspace keyspace, string name)
    {
        ArgumentNullException.ThrowIfNull(keyspace);
        ArgumentNullException.ThrowIfNull(name);

        _keyspace = keyspace;
        Name = name;
        _prefixText = name.Length == 0 ? string.Empty : name + ":";
        _prefix = KvValueCodec.EncodeUtf8(_prefixText);
    }

    /// <summary>命名空间名称；空字符串表示 root 命名空间。</summary>
    public string Name { get; }

    /// <summary>
    /// 写入命名空间内的 key。
    /// </summary>
    public long Put(string key, ReadOnlySpan<byte> value, DateTimeOffset? expiresAtUtc = null) =>
        _keyspace.Put(Qualify(key), value, expiresAtUtc);

    /// <summary>
    /// 按无条件、NX 或 XX 存在性条件写入命名空间内的 key。
    /// </summary>
    /// <param name="key">命名空间内的字符串 key。</param>
    /// <param name="value">value 字节序列，可为空。</param>
    /// <param name="condition">写入存在性条件。</param>
    /// <param name="expiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
    /// <returns>是否提交以及成功写入后的版本号。</returns>
    public KvSetResult Set(
        string key,
        ReadOnlySpan<byte> value,
        KvSetCondition condition = KvSetCondition.Always,
        DateTimeOffset? expiresAtUtc = null)
    {
        _keyspace.ValidateQualifiedUtf8Key(key, _prefix.Length);
        return _keyspace.Set(Qualify(key), value, condition, expiresAtUtc);
    }

    /// <summary>
    /// 原子增加命名空间内 key 的整数 value。
    /// </summary>
    public (long Value, long Version) Increment(string key, long delta = 1) =>
        _keyspace.Increment(Qualify(key), delta);

    /// <summary>
    /// 原子减少命名空间内 key 的整数 value。
    /// </summary>
    public (long Value, long Version) Decrement(string key, long delta = 1) =>
        _keyspace.Decrement(Qualify(key), delta);

    /// <summary>
    /// 对命名空间内 key 执行乐观锁比较并交换。
    /// </summary>
    public KvCasResult CompareAndSet(
        string key,
        long expectedVersion,
        ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc = null) =>
        _keyspace.CompareAndSet(Qualify(key), expectedVersion, value, expiresAtUtc);

    /// <summary>
    /// 为命名空间内 key 设置相对 TTL。
    /// </summary>
    public bool Expire(string key, TimeSpan ttl) => _keyspace.Expire(Qualify(key), ttl);

    /// <summary>
    /// 为命名空间内 key 设置绝对 UTC 过期时间。
    /// </summary>
    public bool ExpireAt(string key, DateTimeOffset expiresAtUtc) => _keyspace.ExpireAt(Qualify(key), expiresAtUtc);

    /// <summary>
    /// 移除命名空间内 key 的过期时间。
    /// </summary>
    public bool Persist(string key) => _keyspace.Persist(Qualify(key));

    /// <summary>
    /// 查询命名空间内 key 的剩余 TTL。
    /// </summary>
    public KvTtlResult GetTimeToLive(string key, DateTimeOffset? utcNow = null) =>
        _keyspace.GetTimeToLive(Qualify(key), utcNow);

    /// <summary>
    /// 读取命名空间内的 key。
    /// </summary>
    public byte[]? Get(string key) => _keyspace.Get(Qualify(key));

    /// <summary>
    /// 原子读取命名空间内 key 的旧记录并写入新值。
    /// </summary>
    /// <param name="key">命名空间内的字符串 key。</param>
    /// <param name="value">要写入的新 value。</param>
    /// <param name="expiresAtUtc">新值的 UTC 过期时间；为空表示移除旧 TTL。</param>
    /// <returns>变更前记录的副本以及新写入版本。</returns>
    public KvExchangeResult GetAndSet(
        string key,
        ReadOnlySpan<byte> value,
        DateTimeOffset? expiresAtUtc = null)
    {
        _keyspace.ValidateQualifiedUtf8Key(key, _prefix.Length);
        return StripPrefix(_keyspace.GetAndSet(Qualify(key), value, expiresAtUtc));
    }

    /// <summary>
    /// 读取命名空间内 key 的当前值与 metadata。
    /// </summary>
    public KvEntry? GetEntry(string key)
    {
        var entry = _keyspace.GetEntry(Qualify(key));
        return entry is null ? null : StripPrefix(entry);
    }

    /// <summary>
    /// 删除命名空间内的 key。
    /// </summary>
    public bool Delete(string key) => _keyspace.Delete(Qualify(key));

    /// <summary>
    /// 原子读取并删除命名空间内的 key。
    /// </summary>
    /// <param name="key">命名空间内的字符串 key。</param>
    /// <returns>变更前记录的副本以及删除版本；未找到时两个字段均为空。</returns>
    public KvExchangeResult GetAndDelete(string key)
    {
        _keyspace.ValidateQualifiedUtf8Key(key, _prefix.Length);
        return StripPrefix(_keyspace.GetAndDelete(Qualify(key)));
    }

    /// <summary>
    /// 批量删除命名空间内的 key。
    /// </summary>
    public int DeleteMany(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return _keyspace.DeleteMany(keys.Select(Qualify));
    }

    /// <summary>
    /// 扫描命名空间内指定前缀。
    /// </summary>
    public IReadOnlyList<KvEntry> ScanPrefix(string prefix, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var rows = _keyspace.ScanPrefix(_prefixText + prefix, limit);
        return rows.Select(StripPrefix).ToArray();
    }

    /// <summary>
    /// 删除命名空间内指定前缀下的 key。
    /// </summary>
    public int DeletePrefix(string prefix, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return _keyspace.DeletePrefix(_prefixText + prefix, limit);
    }

    private string Qualify(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _prefixText + key;
    }

    private KvEntry StripPrefix(KvEntry entry)
    {
        ReadOnlyMemory<byte> key = entry.Key;
        if (_prefix.Length > 0 && key.Span.StartsWith(_prefix))
            key = key[_prefix.Length..];

        return new KvEntry(key, entry.Value, entry.Version, entry.ExpiresAtUtc);
    }

    private KvExchangeResult StripPrefix(KvExchangeResult result) =>
        result.PreviousEntry is null
            ? result
            : result with { PreviousEntry = StripPrefix(result.PreviousEntry) };
}
