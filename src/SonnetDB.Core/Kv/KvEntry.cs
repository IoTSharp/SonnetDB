namespace SonnetDB.Kv;

/// <summary>
/// KV 前缀扫描返回的一条键值记录。
/// </summary>
public sealed class KvEntry
{
    /// <summary>
    /// 初始化一条 KV 扫描结果。
    /// </summary>
    /// <param name="key">键的字节内容。</param>
    /// <param name="value">值的字节内容。</param>
    /// <param name="version">最后一次写入该 key 的单调版本号。</param>
    /// <param name="expiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
    public KvEntry(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, long version, DateTimeOffset? expiresAtUtc = null)
    {
        Key = key;
        Value = value;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>键的字节内容。</summary>
    public ReadOnlyMemory<byte> Key { get; }

    /// <summary>值的字节内容。</summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>最后一次写入该 key 的单调版本号。</summary>
    public long Version { get; }

    /// <summary>UTC 过期时间；为空表示永不过期。</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }
}
