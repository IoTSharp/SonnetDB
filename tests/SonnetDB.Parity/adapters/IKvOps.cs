namespace SonnetDB.Parity.Adapters;

/// <summary>
/// KV / 缓存支柱的语义操作集合。
/// </summary>
public interface IKvOps
{
    /// <summary>清空当前场景使用的 keyspace 或前缀。</summary>
    Task ResetAsync(string scope, CancellationToken ct);

    /// <summary>写入 key。</summary>
    Task SetAsync(string scope, string key, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken ct);

    /// <summary>读取 key。</summary>
    Task<KvRecord?> GetAsync(string scope, string key, CancellationToken ct);

    /// <summary>扫描前缀。</summary>
    Task<IReadOnlyList<KvRecord>> ScanPrefixAsync(string scope, string prefix, int limit, CancellationToken ct);

    /// <summary>原子自增。</summary>
    Task<long> IncrementAsync(string scope, string key, long delta, CancellationToken ct);

    /// <summary>原子自减。</summary>
    Task<long> DecrementAsync(string scope, string key, long delta, CancellationToken ct);

    /// <summary>乐观锁比较并交换。</summary>
    Task<KvCasOutcome> CompareAndSetAsync(
        string scope,
        string key,
        long expectedVersion,
        byte[] value,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct);

    /// <summary>设置绝对 UTC 过期时间。</summary>
    Task<bool> ExpireAsync(string scope, string key, DateTimeOffset expiresAtUtc, CancellationToken ct);

    /// <summary>移除过期时间。</summary>
    Task<bool> PersistAsync(string scope, string key, CancellationToken ct);

    /// <summary>查询剩余 TTL 毫秒；key 不存在为 -2，永不过期为 -1。</summary>
    Task<long> TtlMillisecondsAsync(string scope, string key, CancellationToken ct);
}

/// <summary>
/// 不支持 KV 能力的空操作对象。
/// </summary>
public sealed class UnsupportedKvOps : IKvOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedKvOps Instance { get; } = new();

    private UnsupportedKvOps() { }

    /// <inheritdoc />
    public Task ResetAsync(string scope, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task SetAsync(string scope, string key, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<KvRecord?> GetAsync(string scope, string key, CancellationToken ct) => Unsupported<KvRecord?>();

    /// <inheritdoc />
    public Task<IReadOnlyList<KvRecord>> ScanPrefixAsync(string scope, string prefix, int limit, CancellationToken ct) => Unsupported<IReadOnlyList<KvRecord>>();

    /// <inheritdoc />
    public Task<long> IncrementAsync(string scope, string key, long delta, CancellationToken ct) => Unsupported<long>();

    /// <inheritdoc />
    public Task<long> DecrementAsync(string scope, string key, long delta, CancellationToken ct) => Unsupported<long>();

    /// <inheritdoc />
    public Task<KvCasOutcome> CompareAndSetAsync(string scope, string key, long expectedVersion, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken ct) => Unsupported<KvCasOutcome>();

    /// <inheritdoc />
    public Task<bool> ExpireAsync(string scope, string key, DateTimeOffset expiresAtUtc, CancellationToken ct) => Unsupported<bool>();

    /// <inheritdoc />
    public Task<bool> PersistAsync(string scope, string key, CancellationToken ct) => Unsupported<bool>();

    /// <inheritdoc />
    public Task<long> TtlMillisecondsAsync(string scope, string key, CancellationToken ct) => Unsupported<long>();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持 KV 操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持 KV 操作。");
}

/// <summary>
/// 规范化 KV 记录。
/// </summary>
public sealed record KvRecord(string Key, byte[] Value, long Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// 规范化 KV CAS 结果。
/// </summary>
public sealed record KvCasOutcome(bool Succeeded, long CurrentVersion, long? NewVersion);
