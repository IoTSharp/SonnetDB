using System.Net.Http.Headers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Copilot;
using SonnetDB.Engine;

namespace SonnetDB.Hosting;

/// <summary>
/// 验证 Segment 存储目录能够完成真实落盘写入。
/// </summary>
internal sealed class SegmentStoreWritableHealthCheck(TsdbRegistry registry) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DirectoryWriteProbe.Check(
            registry,
            static tsdb => TsdbPaths.SegmentsDir(tsdb.RootDirectory),
            "Segment 存储目录可写。",
            "Segment 存储目录不可写。"));
}

/// <summary>
/// 验证各数据库 WAL 目录能够完成真实落盘写入。
/// </summary>
internal sealed class WalWritableHealthCheck(TsdbRegistry registry) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DirectoryWriteProbe.Check(
            registry,
            static tsdb => TsdbPaths.WalDir(tsdb.RootDirectory),
            "WAL 目录可写。",
            "WAL 目录不可写。"));
}

/// <summary>
/// 验证 Copilot Chat provider 的配置与网络可达性。
/// </summary>
internal sealed class CopilotChatProviderHealthCheck(
    IOptions<ServerOptions> options,
    CopilotReadiness readiness,
    IHttpClientFactory httpClientFactory)
    : CopilotProviderHealthCheck(options.Value, readiness, httpClientFactory)
{
    /// <inheritdoc />
    protected override ProviderProbeTarget ResolveTarget(CopilotOptions copilot)
    {
        var current = Readiness.Evaluate();
        return new ProviderProbeTarget(
            current.ChatReady,
            current.Reason?.StartsWith("chat.", StringComparison.Ordinal) == true ? current.Reason : "chat.not_ready",
            copilot.Chat.Endpoint,
            copilot.Chat.ApiKey,
            copilot.Chat.TimeoutSeconds);
    }
}

/// <summary>
/// 验证 Copilot Embedding provider 的配置与网络可达性。
/// </summary>
internal sealed class CopilotEmbeddingProviderHealthCheck(
    IOptions<ServerOptions> options,
    CopilotReadiness readiness,
    IHttpClientFactory httpClientFactory)
    : CopilotProviderHealthCheck(options.Value, readiness, httpClientFactory)
{
    /// <inheritdoc />
    protected override ProviderProbeTarget ResolveTarget(CopilotOptions copilot)
    {
        var current = Readiness.Evaluate();
        if (string.Equals(copilot.Embedding.Provider, "builtin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(copilot.Embedding.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderProbeTarget.Local(current.EmbeddingReady, current.Reason);
        }

        return new ProviderProbeTarget(
            current.EmbeddingReady,
            current.Reason?.StartsWith("embedding.", StringComparison.Ordinal) == true ? current.Reason : "embedding.not_ready",
            copilot.Embedding.Endpoint,
            copilot.Embedding.ApiKey,
            copilot.Embedding.TimeoutSeconds);
    }
}

internal abstract class CopilotProviderHealthCheck(
    ServerOptions serverOptions,
    CopilotReadiness readiness,
    IHttpClientFactory httpClientFactory) : IHealthCheck
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private HealthCheckResult? _cachedResult;
    private long _cachedAtUtcTicks;

    /// <summary>
    /// 当前 Copilot 配置就绪状态计算器。
    /// </summary>
    protected CopilotReadiness Readiness { get; } = readiness;

    /// <summary>
    /// 从当前 Copilot 配置解析待探测 provider。
    /// </summary>
    /// <param name="copilot">Copilot 运行配置。</param>
    /// <returns>本地或远程 provider 探测目标。</returns>
    protected abstract ProviderProbeTarget ResolveTarget(CopilotOptions copilot);

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cached = ReadCachedResult();
        if (cached is { } current)
            return current;

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cached = ReadCachedResult();
            if (cached is { } refreshed)
                return refreshed;

            var result = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            _cachedResult = result;
            Interlocked.Exchange(ref _cachedAtUtcTicks, DateTime.UtcNow.Ticks);
            return result;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private HealthCheckResult? ReadCachedResult()
    {
        var cachedAt = Interlocked.Read(ref _cachedAtUtcTicks);
        if (cachedAt == 0 || DateTime.UtcNow - new DateTime(cachedAt, DateTimeKind.Utc) >= CacheDuration)
            return null;

        return _cachedResult;
    }

    private async Task<HealthCheckResult> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!serverOptions.Copilot.Enabled)
            return HealthCheckResult.Healthy("Copilot 已禁用，此检查不阻断服务就绪。");

        var target = ResolveTarget(serverOptions.Copilot);
        if (!target.ConfigurationReady)
            return HealthCheckResult.Degraded(target.NotReadyReason ?? "provider.not_ready");

        if (target.IsLocal)
            return HealthCheckResult.Healthy("本地 provider 已就绪。");

        if (!CopilotReadiness.TryValidateAbsoluteUri(target.Endpoint, out var endpoint) || endpoint is null)
            return HealthCheckResult.Degraded("provider.endpoint_invalid");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(target.TimeoutSeconds, 1, 5)));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "models"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);
            using var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return HealthCheckResult.Healthy("Provider 可达。");

            return HealthCheckResult.Degraded($"Provider 返回 HTTP {(int)response.StatusCode}。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Degraded("Provider 探测超时。");
        }
        catch (HttpRequestException ex)
        {
            return HealthCheckResult.Degraded("Provider 网络不可达。", ex);
        }
    }
}

internal readonly record struct ProviderProbeTarget(
    bool ConfigurationReady,
    string? NotReadyReason,
    string? Endpoint,
    string? ApiKey,
    int TimeoutSeconds,
    bool IsLocal = false)
{
    /// <summary>
    /// 创建无需网络请求的本地 provider 探测目标。
    /// </summary>
    /// <param name="ready">本地 provider 是否已就绪。</param>
    /// <param name="reason">未就绪原因。</param>
    /// <returns>本地 provider 探测目标。</returns>
    public static ProviderProbeTarget Local(bool ready, string? reason)
        => new(ready, reason, null, null, 0, IsLocal: true);
}

internal static class DirectoryWriteProbe
{
    /// <summary>
    /// 逐个探测已加载数据库的目标目录；没有数据库时探测数据根目录。
    /// </summary>
    /// <param name="registry">数据库注册表。</param>
    /// <param name="resolveDirectory">从数据库实例解析目标目录。</param>
    /// <param name="healthyDescription">检查成功描述。</param>
    /// <param name="unhealthyDescription">检查失败描述。</param>
    /// <returns>目录落盘写入检查结果。</returns>
    public static HealthCheckResult Check(
        TsdbRegistry registry,
        Func<Tsdb, string> resolveDirectory,
        string healthyDescription,
        string unhealthyDescription)
    {
        try
        {
            var databases = registry.ListDatabases();
            if (databases.Count == 0)
            {
                Probe(registry.DataRoot);
            }
            else
            {
                foreach (var database in databases)
                {
                    if (registry.TryGet(database, out var tsdb))
                        Probe(resolveDirectory(tsdb));
                }
            }

            return HealthCheckResult.Healthy(healthyDescription);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy(unhealthyDescription, ex);
        }
    }

    private static void Probe(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $".sndb-health-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            stream.WriteByte(0x53);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
