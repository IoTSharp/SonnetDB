using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;

namespace SonnetDB.Data.Kv;

/// <summary>
/// 通过 <see cref="SndbConnectionStringBuilder"/> 统一访问 SonnetDB KV 能力。
/// </summary>
public sealed class SndbKvClient : IDisposable
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private SonnetDB.Engine.Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>
    /// 使用 SonnetDB 连接字符串创建 KV 客户端。
    /// </summary>
    public SndbKvClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        Open();
    }

    /// <summary>当前连接模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>当前远程数据库名或嵌入式数据目录。</summary>
    public string Database => _database;

    /// <summary>
    /// 读取 key。
    /// </summary>
    public async Task<SndbKvEntry?> GetAsync(
        string keyspace,
        string @namespace,
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(key);

        if (_embedded is not null)
        {
            var entry = _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).GetEntry(key);
            return entry is null
                ? null
                : new SndbKvEntry(key, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc);
        }

        var request = new KvGetRequest(Qualify(@namespace, key));
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "get"),
            request,
            RemoteJsonContext.Default.KvGetRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvValueResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Found && body.Value is not null
            ? new SndbKvEntry(key, body.Value, body.Version ?? 0, body.ExpiresAtUtc)
            : null;
    }

    /// <summary>
    /// 写入 key。
    /// </summary>
    public async Task<long> SetAsync(
        string keyspace,
        string @namespace,
        string key,
        byte[] value,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        if (_embedded is not null)
            return _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).Put(key, value, expiresAtUtc);

        var request = new KvSetRequest(Qualify(@namespace, key), value, expiresAtUtc);
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "set"),
            request,
            RemoteJsonContext.Default.KvSetRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvSetResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Version;
    }

    /// <summary>
    /// 批量读取 key。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SndbKvEntry?>> GetManyAsync(
        string keyspace,
        string @namespace,
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(keys);

        var requested = keys.ToArray();
        foreach (string key in requested)
            ArgumentNullException.ThrowIfNull(key);

        if (_embedded is not null)
        {
            var ns = _embedded.Keyspaces.Open(keyspace).Namespace(@namespace);
            return requested.ToDictionary(
                static key => key,
                key =>
                {
                    var entry = ns.GetEntry(key);
                    return entry is null
                        ? null
                        : new SndbKvEntry(key, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc);
                },
                StringComparer.Ordinal);
        }

        var qualified = requested.Select(key => Qualify(@namespace, key)).ToArray();
        var request = new KvGetManyRequest(qualified);
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "get-many"),
            request,
            RemoteJsonContext.Default.KvGetManyRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvGetManyResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Values.ToDictionary(
            item => Unqualify(@namespace, item.Key),
            item => item.Found && item.Value is not null
                ? new SndbKvEntry(Unqualify(@namespace, item.Key), item.Value, item.Version ?? 0, item.ExpiresAtUtc)
                : null,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 批量写入 key。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> SetManyAsync(
        string keyspace,
        string @namespace,
        IEnumerable<KeyValuePair<string, byte[]>> values,
        DateTimeOffset? expiresAtUtc = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(values);

        var requested = values.ToArray();
        foreach (var pair in requested)
        {
            ArgumentNullException.ThrowIfNull(pair.Key);
            ArgumentNullException.ThrowIfNull(pair.Value);
        }

        if (_embedded is not null)
        {
            var ns = _embedded.Keyspaces.Open(keyspace).Namespace(@namespace);
            return requested.ToDictionary(
                static pair => pair.Key,
                pair => ns.Put(pair.Key, pair.Value, expiresAtUtc),
                StringComparer.Ordinal);
        }

        var request = new KvSetManyRequest(
            requested.Select(pair => new KvSetManyEntry(Qualify(@namespace, pair.Key), pair.Value)).ToArray(),
            expiresAtUtc);
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "set-many"),
            request,
            RemoteJsonContext.Default.KvSetManyRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvSetManyResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Versions.ToDictionary(
            pair => Unqualify(@namespace, pair.Key),
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// 删除 key。
    /// </summary>
    public async Task<bool> RemoveAsync(
        string keyspace,
        string @namespace,
        string key,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(key);

        if (_embedded is not null)
            return _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).Delete(key);

        var request = new KvDeleteRequest(Qualify(@namespace, key));
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "remove"),
            request,
            RemoteJsonContext.Default.KvDeleteRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvDeleteResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Removed > 0;
    }

    /// <summary>
    /// 批量删除 key。
    /// </summary>
    public async Task<int> RemoveManyAsync(
        string keyspace,
        string @namespace,
        IEnumerable<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(keys);

        var requested = keys.ToArray();
        foreach (string key in requested)
            ArgumentNullException.ThrowIfNull(key);

        if (_embedded is not null)
            return _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).DeleteMany(requested);

        var request = new KvDeleteManyRequest(requested.Select(key => Qualify(@namespace, key)).ToArray());
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "remove-many"),
            request,
            RemoteJsonContext.Default.KvDeleteManyRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvDeleteResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Removed;
    }

    /// <summary>
    /// 删除前缀。
    /// </summary>
    public async Task<int> RemovePrefixAsync(
        string keyspace,
        string @namespace,
        string prefix,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(prefix);

        if (_embedded is not null)
            return _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).DeletePrefix(prefix, limit);

        var request = new KvPrefixRequest(Qualify(@namespace, prefix), limit);
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "remove-prefix"),
            request,
            RemoteJsonContext.Default.KvPrefixRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvDeleteResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Removed;
    }

    /// <summary>
    /// 扫描前缀。
    /// </summary>
    public async Task<IReadOnlyList<SndbKvEntry>> ScanPrefixAsync(
        string keyspace,
        string @namespace,
        string prefix,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateNames(keyspace, @namespace);
        ArgumentNullException.ThrowIfNull(prefix);

        if (_embedded is not null)
        {
            return _embedded.Keyspaces.Open(keyspace).Namespace(@namespace).ScanPrefix(prefix, limit)
                .Select(static entry => new SndbKvEntry(
                    Encoding.UTF8.GetString(entry.Key.Span),
                    entry.Value.ToArray(),
                    entry.Version,
                    entry.ExpiresAtUtc))
                .ToArray();
        }

        var request = new KvPrefixRequest(Qualify(@namespace, prefix), limit);
        using var response = await PostJsonAsync(
            KvUrl(keyspace, "scan-prefix"),
            request,
            RemoteJsonContext.Default.KvPrefixRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvScanResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Entries
            .Select(entry => new SndbKvEntry(Unqualify(@namespace, entry.Key), entry.Value, entry.Version, entry.ExpiresAtUtc))
            .ToArray();
    }

    /// <summary>
    /// 清理已过期 key。
    /// </summary>
    public async Task<int> CleanExpiredAsync(
        string keyspace,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        if (_embedded is not null)
            return _embedded.Keyspaces.Open(keyspace).CleanExpired(limit: limit);

        using var response = await PostJsonAsync(
            KvUrl(keyspace, "clean-expired"),
            new KvCleanExpiredRequest(limit),
            RemoteJsonContext.Default.KvCleanExpiredRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvDeleteResponse, cancellationToken)
            .ConfigureAwait(false);
        return body.Removed;
    }

    /// <summary>
    /// 获取过期统计。
    /// </summary>
    public async Task<SndbKvExpirationStats> GetExpirationStatsAsync(
        string keyspace,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);

        if (_embedded is not null)
        {
            var stats = _embedded.Keyspaces.Open(keyspace).GetExpirationStats();
            return new SndbKvExpirationStats(
                stats.TotalKeys,
                stats.ActiveKeys,
                stats.ExpiredKeys,
                stats.ExpiringKeys,
                stats.NearestExpiresAtUtc);
        }

        using var response = await PostJsonAsync(
            KvUrl(keyspace, "stats"),
            new KvPrefixRequest(string.Empty, null),
            RemoteJsonContext.Default.KvPrefixRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, RemoteJsonContext.Default.KvStatsResponse, cancellationToken)
            .ConfigureAwait(false);
        return new SndbKvExpirationStats(
            body.TotalKeys,
            body.ActiveKeys,
            body.ExpiredKeys,
            body.ExpiringKeys,
            body.NearestExpiresAtUtc);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http?.Dispose();
        _embedded?.Dispose();
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            if (string.IsNullOrWhiteSpace(_builder.DataSource))
                throw new InvalidOperationException("KV 客户端缺少 Data Source。");

            _database = _builder.DataSource;
            _embedded = Tsdb.Open(new TsdbOptions { RootDirectory = _builder.DataSource });
            return;
        }

        var (baseUrl, dbFromUrl) = ParseRemoteEndpoint(_builder.DataSource);
        _database = !string.IsNullOrWhiteSpace(_builder.Database) ? _builder.Database! : dbFromUrl;
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程 KV 客户端缺少数据库名。");

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl, UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(_builder.Timeout),
        };
        if (!string.IsNullOrWhiteSpace(_builder.Token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _builder.Token);
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (_http is null)
            throw new InvalidOperationException("远程连接未打开。");

        using var content = JsonContent.Create(value, typeInfo);
        var response = await _http.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SonnetDB KV response body is empty.");
    }

    private async Task<SndbServerException> BuildHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var error = await JsonSerializer.DeserializeAsync(stream, RemoteJsonContext.Default.ServerErrorBody, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
                return new SndbServerException(error.Error, error.Message, response.StatusCode);
        }
        catch
        {
            // Fall through to generic error.
        }

        return new SndbServerException("http_error", response.ReasonPhrase ?? "SonnetDB HTTP error.", response.StatusCode);
    }

    private string KvUrl(string keyspace, string action) =>
        $"v1/db/{Uri.EscapeDataString(_database)}/kv/{Uri.EscapeDataString(keyspace)}/{action}";

    private static string Qualify(string @namespace, string key) =>
        string.IsNullOrEmpty(@namespace) ? key : @namespace + ":" + key;

    private static string Unqualify(string @namespace, string key)
    {
        string prefix = string.IsNullOrEmpty(@namespace) ? string.Empty : @namespace + ":";
        return prefix.Length > 0 && key.StartsWith(prefix, StringComparison.Ordinal)
            ? key[prefix.Length..]
            : key;
    }

    private static void ValidateNames(string keyspace, string @namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);
        ArgumentNullException.ThrowIfNull(@namespace);
    }

    private static (string BaseUrl, string Database) ParseRemoteEndpoint(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("远程 KV 客户端缺少 Data Source。");

        var ds = dataSource.Trim();
        if (ds.StartsWith("sonnetdb+http://", StringComparison.OrdinalIgnoreCase))
            ds = "http://" + ds["sonnetdb+http://".Length..];
        else if (ds.StartsWith("sonnetdb+https://", StringComparison.OrdinalIgnoreCase))
            ds = "https://" + ds["sonnetdb+https://".Length..];

        if (!Uri.TryCreate(ds, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"远程 Data Source 不是合法 URL: {dataSource}");

        return ($"{uri.Scheme}://{uri.Authority}/", uri.AbsolutePath.Trim('/'));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
