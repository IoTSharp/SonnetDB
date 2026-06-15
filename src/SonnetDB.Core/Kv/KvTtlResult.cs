namespace SonnetDB.Kv;

/// <summary>
/// KV TTL 查询结果，使用 Redis 风格哨兵值表达缺失和无过期时间。
/// </summary>
/// <param name="Milliseconds">剩余 TTL 毫秒；key 不存在为 -2，key 永不过期为 -1。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间；key 不存在或永不过期时为 <see langword="null"/>。</param>
public sealed record KvTtlResult(long Milliseconds, DateTimeOffset? ExpiresAtUtc)
{
    /// <summary>key 不存在时的 TTL 哨兵值。</summary>
    public const long Missing = -2;

    /// <summary>key 存在但没有过期时间时的 TTL 哨兵值。</summary>
    public const long NoExpiration = -1;
}
