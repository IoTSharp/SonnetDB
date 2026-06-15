using System.Net.Sockets;
using StackExchange.Redis;

namespace SonnetDB.Parity.Adapters.Redis;

/// <summary>
/// Redis 竞品适配器，使用官方 <c>StackExchange.Redis</c> 客户端实现 KV 场景。
/// </summary>
public sealed class RedisAdapter : IDataPlane, IKvOps
{
    private readonly ConnectionMultiplexer _connection;
    private readonly IDatabase _db;

    /// <summary>使用 <c>PARITY_REDIS_*</c> 环境变量创建 Redis 连接。</summary>
    public RedisAdapter()
    {
        _connection = ConnectionMultiplexer.Connect(BuildConfiguration());
        _db = _connection.GetDatabase();
    }

    /// <inheritdoc />
    public string BackendName => "redis";

    /// <inheritdoc />
    public Capability Capabilities => Capability.Kv | Capability.KvIncr | Capability.KvCas | Capability.KvRangeScan;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("Redis 适配器不支持关系型操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => UnsupportedTimeSeriesOps.Instance;

    /// <inheritdoc />
    public IKvOps Kv => this;

    /// <inheritdoc />
    public IObjectOps Objects => UnsupportedObjectOps.Instance;

    /// <inheritdoc />
    public IVectorOps Vector => UnsupportedVectorOps.Instance;

    /// <inheritdoc />
    public IMqOps Mq => UnsupportedMqOps.Instance;

    /// <inheritdoc />
    public IFullTextOps FullText => UnsupportedFullTextOps.Instance;

    /// <inheritdoc />
    public IAnalyticalOps Analytics => UnsupportedAnalyticalOps.Instance;

    /// <summary>探测 Redis 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var connection = await ConnectionMultiplexer.ConnectAsync(BuildConfiguration()).WaitAsync(ct).ConfigureAwait(false);
            await connection.GetDatabase().PingAsync().WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is RedisConnectionException or SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResetAsync(string scope, CancellationToken ct)
    {
        foreach (var server in _connection.GetServers())
        {
            var keys = server.Keys(pattern: scope + ":*").ToArray();
            if (keys.Length > 0)
                await _db.KeyDeleteAsync(keys).WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task SetAsync(string scope, string key, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        RedisKey versionKey = VersionKey(dataKey);
        long expiryMs = expiresAtUtc?.ToUnixTimeMilliseconds() ?? -1L;
        await _db.ScriptEvaluateAsync(
            SetScript,
            [dataKey, versionKey],
            [value, expiryMs]).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<KvRecord?> GetAsync(string scope, string key, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        RedisValue value = await _db.StringGetAsync(dataKey).WaitAsync(ct).ConfigureAwait(false);
        if (value.IsNull)
            return null;

        RedisValue version = await _db.StringGetAsync(VersionKey(dataKey)).WaitAsync(ct).ConfigureAwait(false);
        DateTimeOffset? expiresAt = null;
        TimeSpan? ttl = await _db.KeyTimeToLiveAsync(dataKey).WaitAsync(ct).ConfigureAwait(false);
        if (ttl.HasValue)
            expiresAt = DateTimeOffset.UtcNow.Add(ttl.Value);

        return new KvRecord(key, (byte[])value!, (long)(version.IsNull ? 0 : (long)version), expiresAt);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KvRecord>> ScanPrefixAsync(string scope, string prefix, int limit, CancellationToken ct)
    {
        var rows = new List<KvRecord>(Math.Max(0, limit));
        string qualifiedPrefix = scope + ":" + prefix;
        foreach (var server in _connection.GetServers())
        {
            foreach (RedisKey key in server.Keys(pattern: qualifiedPrefix + "*", pageSize: Math.Min(1000, Math.Max(10, limit))))
            {
                ct.ThrowIfCancellationRequested();
                string text = key.ToString();
                if (text.EndsWith("::__ver", StringComparison.Ordinal))
                    continue;

                var record = await GetAsync(scope, text[(scope.Length + 1)..], ct).ConfigureAwait(false);
                if (record is not null)
                    rows.Add(record);
                if (rows.Count >= limit)
                    return rows.OrderBy(static x => x.Key, StringComparer.Ordinal).ToArray();
            }
        }

        return rows.OrderBy(static x => x.Key, StringComparer.Ordinal).Take(limit).ToArray();
    }

    /// <inheritdoc />
    public async Task<long> IncrementAsync(string scope, string key, long delta, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        long value = await _db.StringIncrementAsync(dataKey, delta).WaitAsync(ct).ConfigureAwait(false);
        await _db.StringIncrementAsync(VersionKey(dataKey)).WaitAsync(ct).ConfigureAwait(false);
        return value;
    }

    /// <inheritdoc />
    public async Task<long> DecrementAsync(string scope, string key, long delta, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        long value = await _db.StringDecrementAsync(dataKey, delta).WaitAsync(ct).ConfigureAwait(false);
        await _db.StringIncrementAsync(VersionKey(dataKey)).WaitAsync(ct).ConfigureAwait(false);
        return value;
    }

    /// <inheritdoc />
    public async Task<KvCasOutcome> CompareAndSetAsync(
        string scope,
        string key,
        long expectedVersion,
        byte[] value,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        long expiryMs = expiresAtUtc?.ToUnixTimeMilliseconds() ?? -1L;
        RedisResult raw = await _db.ScriptEvaluateAsync(
            CasScript,
            [dataKey, VersionKey(dataKey)],
            [expectedVersion, value, expiryMs]).WaitAsync(ct).ConfigureAwait(false);
        var result = (RedisResult[]?)raw
            ?? throw new InvalidOperationException("Redis CAS script returned an unexpected null result.");

        bool succeeded = Convert.ToInt64(result[0], System.Globalization.CultureInfo.InvariantCulture) == 1L;
        long currentVersion = Convert.ToInt64(result[1], System.Globalization.CultureInfo.InvariantCulture);
        long? newVersion = succeeded
            ? Convert.ToInt64(result[2], System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new KvCasOutcome(succeeded, currentVersion, newVersion);
    }

    /// <inheritdoc />
    public async Task<bool> ExpireAsync(string scope, string key, DateTimeOffset expiresAtUtc, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        bool ok = await _db.KeyExpireAsync(dataKey, expiresAtUtc.UtcDateTime).WaitAsync(ct).ConfigureAwait(false);
        if (ok)
            await _db.KeyExpireAsync(VersionKey(dataKey), expiresAtUtc.UtcDateTime).WaitAsync(ct).ConfigureAwait(false);
        return ok;
    }

    /// <inheritdoc />
    public async Task<bool> PersistAsync(string scope, string key, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        bool ok = await _db.KeyPersistAsync(dataKey).WaitAsync(ct).ConfigureAwait(false);
        await _db.KeyPersistAsync(VersionKey(dataKey)).WaitAsync(ct).ConfigureAwait(false);
        return ok;
    }

    /// <inheritdoc />
    public async Task<long> TtlMillisecondsAsync(string scope, string key, CancellationToken ct)
    {
        RedisKey dataKey = Qualify(scope, key);
        if (!await _db.KeyExistsAsync(dataKey).WaitAsync(ct).ConfigureAwait(false))
            return -2;
        TimeSpan? ttl = await _db.KeyTimeToLiveAsync(dataKey).WaitAsync(ct).ConfigureAwait(false);
        return ttl.HasValue ? Math.Max(0L, (long)Math.Ceiling(ttl.Value.TotalMilliseconds)) : -1;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static ConfigurationOptions BuildConfiguration()
    {
        var host = Env("PARITY_REDIS_HOST", "127.0.0.1");
        var port = Env("PARITY_REDIS_PORT", "26379");
        return ConfigurationOptions.Parse($"{host}:{port},abortConnect=false,connectTimeout=3000,syncTimeout=10000");
    }

    private static RedisKey Qualify(string scope, string key) => scope + ":" + key;

    private static RedisKey VersionKey(RedisKey key) => key.ToString() + "::__ver";

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private const string SetScript = """
        local newver = redis.call('INCR', KEYS[2])
        redis.call('SET', KEYS[1], ARGV[1])
        if tonumber(ARGV[2]) >= 0 then
          redis.call('PEXPIREAT', KEYS[1], ARGV[2])
          redis.call('PEXPIREAT', KEYS[2], ARGV[2])
        else
          redis.call('PERSIST', KEYS[1])
          redis.call('PERSIST', KEYS[2])
        end
        return newver
        """;

    private const string CasScript = """
        if redis.call('EXISTS', KEYS[1]) == 0 then
          redis.call('DEL', KEYS[2])
        end
        local current = tonumber(redis.call('GET', KEYS[2]) or '0')
        if current ~= tonumber(ARGV[1]) then
          return {0, current, 0}
        end
        local newver = redis.call('INCR', KEYS[2])
        redis.call('SET', KEYS[1], ARGV[2])
        if tonumber(ARGV[3]) >= 0 then
          redis.call('PEXPIREAT', KEYS[1], ARGV[3])
          redis.call('PEXPIREAT', KEYS[2], ARGV[3])
        else
          redis.call('PERSIST', KEYS[1])
          redis.call('PERSIST', KEYS[2])
        end
        return {1, current, newver}
        """;
}
