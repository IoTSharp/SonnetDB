namespace SonnetDB.Kv;

/// <summary>
/// KV keyspace 过期统计快照。
/// </summary>
/// <param name="TotalKeys">当前内存视图中的 key 总数，包含尚未清理的过期 key。</param>
/// <param name="ActiveKeys">未过期 key 数量。</param>
/// <param name="ExpiredKeys">已过期但尚未清理的 key 数量。</param>
/// <param name="ExpiringKeys">带有 expires-at metadata 的 key 数量。</param>
/// <param name="NearestExpiresAtUtc">最近的 UTC 过期时间；为空表示没有带过期时间的活跃 key。</param>
public sealed record KvExpirationStats(
    int TotalKeys,
    int ActiveKeys,
    int ExpiredKeys,
    int ExpiringKeys,
    DateTimeOffset? NearestExpiresAtUtc);
