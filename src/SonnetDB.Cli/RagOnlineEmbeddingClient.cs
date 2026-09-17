using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.SemanticContent;

namespace SonnetDB.Cli;

/// <summary>CLI 的显式外发边界；不持久化凭据、请求正文或 provider 错误正文。</summary>
internal sealed class RagOnlineEmbeddingClient : IDisposable
{
    private readonly EmbeddingProfile _profile;
    private readonly Uri _endpoint;
    private readonly HttpClient _client;
    private readonly StreamWriter? _audit;
    private readonly FileStream? _auditFile;

    internal RagOnlineEmbeddingClient(EmbeddingProfile profile, string endpoint, string keyEnvironment,
        bool allowEgress, string? auditPath, bool dryRun, Func<HttpMessageHandler>? handlerFactory = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new CliUsageException("--endpoint 必须为无凭据、query、fragment 且以 / 结尾的 HTTPS 基地址。");
        if (!allowEgress || profile.DataEgressPolicy.Mode == SemanticDataEgressMode.LocalOnly
            || !Uri.TryCreate(profile.DataEgressPolicy.Target, UriKind.Absolute, out Uri? target)
            || !string.Equals(target.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal)
            || !string.Equals(profile.Provider, "openai", StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("在线 embedding 需要 openai profile、--allow-egress 及精确匹配 endpoint 的外发策略。");
        if (profile.DataEgressPolicy.AuditRequired && string.IsNullOrWhiteSpace(auditPath))
            throw new CliUsageException("profile 要求审计；请提供 --audit JSONL 文件。");
        if (string.IsNullOrWhiteSpace(keyEnvironment) || keyEnvironment.Length > 128
            || keyEnvironment.Any(static c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new CliUsageException("--api-key-env 必须为有效的环境变量名。");
        string? key = Environment.GetEnvironmentVariable(keyEnvironment);
        if (!dryRun && string.IsNullOrWhiteSpace(key))
            throw new CliUsageException("--api-key-env 指定的凭据环境变量未设置。");
        _profile = profile;
        _endpoint = new Uri(uri, "embeddings");
        // dry-run 不打开审计文件、不发请求，也不要求实际凭据。
        _client = new HttpClient(dryRun ? new HttpClientHandler { AllowAutoRedirect = false }
            : handlerFactory?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(60),
            MaxResponseContentBufferSize = 1024 * 1024,
        };
        try
        {
            if (!dryRun)
            {
                _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                if (auditPath is not null)
                {
                    _auditFile = new FileStream(auditPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                    _audit = new StreamWriter(_auditFile, new UTF8Encoding(false));
                }
            }
        }
        catch { _client.Dispose(); throw; }
    }

    internal async ValueTask<float[]> EmbedAsync(SemanticContentChunk chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string requestId = Guid.NewGuid().ToString("N");
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk.Text)));
        await AuditAsync("started").ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(new RagOnlineEmbeddingRequest(_profile.Model, chunk.Text),
                    RagCliJsonContext.Default.RagOnlineEmbeddingRequest), Encoding.UTF8, "application/json"),
            };
            using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string message = $"在线 embedding provider 返回 HTTP {(int)response.StatusCode}。";
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                    throw new HttpRequestException(message, null, response.StatusCode);
                throw new InvalidOperationException(message);
            }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
            RagOnlineEmbeddingResponse? result;
            try { result = JsonSerializer.Deserialize(bytes, RagCliJsonContext.Default.RagOnlineEmbeddingResponse); }
            catch (JsonException) { throw new InvalidOperationException("在线 embedding 响应不是有效的协议 JSON。"); }
            if (result?.Data is not { Count: 1 } || result.Data[0] is null || result.Data[0].Index != 0
                || result.Model is not null && result.Model != _profile.Model
                || result.Data[0].Embedding is not { } vector || vector.Length != _profile.Dimensions
                || vector.Any(static value => !float.IsFinite(value)))
                throw new InvalidOperationException("在线 embedding 响应与 profile 的模型、维度或单项索引合同不匹配。");
            if (_profile.Normalization == EmbeddingNormalization.L2
                && Math.Abs(vector.Sum(static value => (double)value * value) - 1) > 0.001)
                throw new InvalidOperationException("在线 embedding 响应不满足 profile 的 L2 归一化合同。");
            await AuditAsync("succeeded").ConfigureAwait(false);
            return vector;
        }
        catch
        {
            // 审计取消结果同样有单独短期限；不写异常内容，避免泄漏上游正文或凭据。
            await AuditAsync(token.IsCancellationRequested ? "cancelled" : "failed").ConfigureAwait(false);
            throw;
        }

        async ValueTask AuditAsync(string outcome)
        {
            if (_audit is null) return;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string json = JsonSerializer.Serialize(new RagOnlineAudit(DateTimeOffset.UtcNow, requestId,
                _profile.Id, _endpoint.AbsoluteUri, hash, outcome), RagCliJsonContext.Default.RagOnlineAudit);
            await _audit.WriteLineAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
            await _audit.FlushAsync(deadline.Token).ConfigureAwait(false);
            _auditFile!.Flush(flushToDisk: true);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        _audit?.Dispose();
    }
}
