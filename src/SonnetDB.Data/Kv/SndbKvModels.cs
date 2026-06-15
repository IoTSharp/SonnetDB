namespace SonnetDB.Data.Kv;

/// <summary>
/// KV 扫描返回的一条记录。
/// </summary>
public sealed record SndbKvEntry(string Key, byte[] Value, long Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 过期统计快照。
/// </summary>
public sealed record SndbKvExpirationStats(
    int TotalKeys,
    int ActiveKeys,
    int ExpiredKeys,
    int ExpiringKeys,
    DateTimeOffset? NearestExpiresAtUtc);

/// <summary>
/// KV 比较并交换结果。
/// </summary>
public sealed record SndbKvCasResult(bool Succeeded, long CurrentVersion, long? NewVersion);

/// <summary>
/// KV TTL 查询结果；key 不存在为 -2，key 永不过期为 -1。
/// </summary>
public sealed record SndbKvTtlResult(long Milliseconds, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvGetRequest(string Key);

internal sealed record KvSetRequest(string Key, byte[] Value, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvDeleteRequest(string Key);

internal sealed record KvGetManyRequest(IReadOnlyList<string> Keys);

internal sealed record KvSetManyRequest(IReadOnlyList<KvSetManyEntry> Entries, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvSetManyEntry(string Key, byte[] Value);

internal sealed record KvDeleteManyRequest(IReadOnlyList<string> Keys);

internal sealed record KvPrefixRequest(string Prefix, int? Limit);

internal sealed record KvCleanExpiredRequest(int? Limit);

internal sealed record KvIncrementRequest(string Key, long Delta = 1);

internal sealed record KvCasRequest(string Key, long ExpectedVersion, byte[] Value, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvExpireRequest(string Key, DateTimeOffset ExpiresAtUtc);

internal sealed record KvEntryResponse(string Key, byte[] Value, long Version, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvValueResponse(bool Found, byte[]? Value, long? Version, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvGetManyResponse(List<KvValueItemResponse> Values);

internal sealed record KvValueItemResponse(string Key, bool Found, byte[]? Value, long? Version, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvSetResponse(long Version);

internal sealed record KvSetManyResponse(Dictionary<string, long> Versions);

internal sealed record KvDeleteResponse(int Removed);

internal sealed record KvIncrementResponse(long Value, long Version);

internal sealed record KvCasResponse(bool Succeeded, long CurrentVersion, long? NewVersion);

internal sealed record KvBooleanResponse(bool Succeeded);

internal sealed record KvTtlResponse(long Milliseconds, DateTimeOffset? ExpiresAtUtc);

internal sealed record KvScanResponse(List<KvEntryResponse> Entries);

internal sealed record KvStatsResponse(
    int TotalKeys,
    int ActiveKeys,
    int ExpiredKeys,
    int ExpiringKeys,
    DateTimeOffset? NearestExpiresAtUtc);
