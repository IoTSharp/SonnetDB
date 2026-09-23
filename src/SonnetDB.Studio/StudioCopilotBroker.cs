using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace SonnetDB.Studio;

/// <summary>原生 AI broker 的固定 HTTPS 公网目标和短期凭据寿命。</summary>
internal sealed record StudioCopilotOptions
{
    private StudioCopilotOptions(Uri publicBaseUri, TimeSpan tokenLifetime)
    { PublicBaseUri = publicBaseUri; TokenLifetime = tokenLifetime; }

    /// <summary>经过批准且不可由 renderer 改写的公网根地址。</summary>
    public Uri PublicBaseUri { get; }
    /// <summary>宿主允许保存的最长凭据寿命。</summary>
    public TimeSpan TokenLifetime { get; }
    /// <summary>按完整批准目标隔离的系统凭据目标名。</summary>
    public string CredentialTarget => "SonnetDB/Studio/Copilot/" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(PublicBaseUri.AbsoluteUri)));

    /// <summary>只接受明确批准的 HTTPS origin，不接受 renderer 提供的 URL。</summary>
    public static StudioCopilotOptions Create(string publicBaseUrl, IReadOnlyList<string> approvedOrigins, int ttlSeconds = 3600)
    {
        if (approvedOrigins.Count is < 1 or > 16 || ttlSeconds is < 1 or > 7200
            || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Studio Copilot 需要明确批准的 HTTPS 公网配置。");
        foreach (string origin in approvedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var approved) || approved.Scheme != Uri.UriSchemeHttps
                || approved.UserInfo.Length != 0 || approved.Query.Length != 0 || approved.Fragment.Length != 0
                || !string.Equals(origin, approved.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
                throw new ArgumentException("Studio Copilot approved origins 必须是 HTTPS origin。");
        }
        if (!approvedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal))
            throw new ArgumentException("Studio Copilot 公网地址未获批准。");
        return new StudioCopilotOptions(new Uri(uri.AbsoluteUri.TrimEnd('/') + '/'), TimeSpan.FromSeconds(ttlSeconds));
    }

    /// <summary>从宿主明确环境配置读取；缺少或错误配置保持 NOT_READY。</summary>
    public static StudioCopilotOptions? FromEnvironment()
    {
        string? address = Environment.GetEnvironmentVariable("SONNETDB_STUDIO_COPILOT_PUBLIC_BASE_URL");
        string? origins = Environment.GetEnvironmentVariable("SONNETDB_STUDIO_COPILOT_APPROVED_ORIGINS");
        string? lifetime = Environment.GetEnvironmentVariable("SONNETDB_STUDIO_COPILOT_TOKEN_TTL_SECONDS");
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(origins)) return null;
        int ttl = 3600;
        if (lifetime is not null && !int.TryParse(lifetime, out ttl)) return null;
        try { return Create(address.Trim(), origins.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), ttl); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>不会包含 access token 的宿主状态。</summary>
internal sealed record StudioCopilotStatus(string ContractVersion, bool Configured, bool Connected,
    string? PublicBaseUrl, DateTimeOffset? ExpiresAtUtc, bool Canceled, string? Error);

/// <summary>向 renderer 返回的受控错误，不转发提供方错误正文。</summary>
internal sealed record StudioCopilotError(string Error, string Message);

/// <summary>公网 readiness 合同。</summary>
internal sealed record StudioCopilotReadiness(string? Status);

/// <summary>公网工具继续执行的固定载荷。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StudioCopilotContinuation(string PreviousCursor, string ToolCallId, string ToolName, string ToolResult);

/// <summary>与 m27-browser-direct-v1 相同的受限公网段请求。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record StudioCopilotStreamRequest(string ContractVersion, string RunId, JsonElement Request,
    StudioCopilotContinuation? Continuation);

/// <summary>持有系统凭据并限定公网流量的原生 AI broker。</summary>
internal sealed class StudioCopilotBroker : IDisposable
{
    internal const string ContractVersion = "m27-browser-direct-v1";
    internal const string ContractHeader = "X-SonnetDB-Copilot-Contract";
    private readonly StudioCopilotOptions? _options;
    private readonly IStudioCopilotCredentialStore? _store;
    private readonly Func<string, CancellationToken, Task<string?>> _prompt;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly object _sync = new();
    private CancellationTokenSource _connectionLifetime = new();
    private bool _disposed;

    /// <summary>创建 broker；测试可替换凭据、原生输入和网络边界，但配置校验保持严格。</summary>
    public StudioCopilotBroker(StudioCopilotOptions? options = null, IStudioCopilotCredentialStore? store = null,
        Func<string, CancellationToken, Task<string?>>? prompt = null, HttpMessageHandler? handler = null)
    {
        _options = options ?? StudioCopilotOptions.FromEnvironment();
        _store = store ?? (_options is null ? null : new StudioCopilotWindowsCredentialStore(_options.CredentialTarget));
        _prompt = prompt ?? StudioCopilotCredentialPrompt.ShowAsync;
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }

    /// <summary>读取配置和未过期凭据状态，不把存在凭据误报为公网 readiness。</summary>
    public StudioCopilotStatus GetStatus(bool canceled = false, string? error = null)
    {
        lock (_sync)
        {
            var credential = ReadCredential();
            return new StudioCopilotStatus(ContractVersion, _options is not null, credential is not null,
                _options?.PublicBaseUri.AbsoluteUri, credential?.ExpiresAtUtc, canceled, error ?? (_options is null ? "not_ready" : null));
        }
    }

    /// <summary>调用原生密码输入并验证公网 readiness，成功后才保存当前用户凭据。</summary>
    public async Task<StudioCopilotStatus> ConnectAsync(string bridgeToken, CancellationToken cancellationToken)
    {
        if (_options is null) return GetStatus();
        if (!await _connectGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return GetStatus(error: "connect_busy");
        try
        {
            CancellationTokenSource operation;
            lock (_sync)
            {
                Disconnect();
                operation = CreateOperationCancellation(cancellationToken, TimeSpan.FromMinutes(5));
            }
            using var timeout = operation;
            string? token = await _prompt(_options.PublicBaseUri.GetLeftPart(UriPartial.Authority), timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (token is null) return GetStatus(canceled: true);
            if (!ValidToken(token) || string.Equals(token, bridgeToken, StringComparison.Ordinal))
                throw new StudioCopilotException("credential_invalid", StatusCodes.Status400BadRequest);
            var credential = new StudioCopilotCredential(token, DateTimeOffset.UtcNow + _options.TokenLifetime);
            using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            probeTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await ProbePublicAsync(credential, probeTimeout.Token).ConfigureAwait(false);
            lock (_sync)
            {
                timeout.Token.ThrowIfCancellationRequested();
                _store!.Write(credential);
                return GetStatus();
            }
        }
        finally { _connectGate.Release(); }
    }

    /// <summary>取消在途公网请求和原生输入，删除该 broker 独占的系统凭据。</summary>
    public StudioCopilotStatus Disconnect()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _connectionLifetime.Cancel();
            _connectionLifetime.Dispose();
            _connectionLifetime = new CancellationTokenSource();
            _store?.Delete();
            return GetStatus();
        }
    }

    /// <summary>桥处理统一入口；只接受固定操作且永不转发入站 Authorization。</summary>
    public async Task HandleAsync(HttpContext context, string operation, string bridgeToken)
    {
        try
        {
            if (context.Request.Headers.ContainsKey("Authorization") || context.Request.Query.Count != 0)
                throw new StudioCopilotException("request_invalid", StatusCodes.Status400BadRequest);
            context.Response.Headers.CacheControl = "no-store";
            if (operation is "status" or "connect" or "disconnect" or "readiness")
            {
                if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
                    || context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
                    throw new StudioCopilotException("request_invalid", StatusCodes.Status400BadRequest);
            }
            if (operation is "status" or "connect" or "disconnect")
            {
                var status = operation switch
                {
                    "connect" => await ConnectAsync(bridgeToken, context.RequestAborted).ConfigureAwait(false),
                    "disconnect" => Disconnect(),
                    _ => GetStatus(),
                };
                await context.Response.WriteAsJsonAsync(status, StudioBridgeJsonContext.Default.StudioCopilotStatus, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                return;
            }
            if (!await _requests.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
                throw new StudioCopilotException("capacity_exceeded", StatusCodes.Status429TooManyRequests);
            try
            {
                using var timeout = CreateOperationCancellation(context.RequestAborted, TimeSpan.FromSeconds(60));
                StudioCopilotCredential credential;
                lock (_sync) credential = ReadCredential() ?? throw new StudioCopilotException("credential_missing", StatusCodes.Status401Unauthorized);
                timeout.CancelAfter(Minimum(TimeSpan.FromSeconds(60), credential.ExpiresAtUtc - DateTimeOffset.UtcNow));
                if (operation == "readiness")
                {
                    timeout.CancelAfter(Minimum(TimeSpan.FromSeconds(15), credential.ExpiresAtUtc - DateTimeOffset.UtcNow));
                    await ProbePublicAsync(credential, timeout.Token).ConfigureAwait(false);
                    context.Response.Headers[ContractHeader] = ContractVersion;
                    await context.Response.WriteAsJsonAsync(new StudioCopilotReadiness("ready"), StudioBridgeJsonContext.Default.StudioCopilotReadiness, cancellationToken: timeout.Token).ConfigureAwait(false);
                    return;
                }
                await ForwardStreamAsync(context, operation, credential, timeout.Token).ConfigureAwait(false);
            }
            finally { _requests.Release(); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) { context.Abort(); return; }
            var failure = error as StudioCopilotException;
            context.Response.StatusCode = failure?.StatusCode ?? (error is JsonException ? 400 : 503);
            await context.Response.WriteAsJsonAsync(new StudioCopilotError(failure?.Code ?? "broker_unavailable", "Studio AI 服务暂不可用，请检查宿主连接。"),
                StudioBridgeJsonContext.Default.StudioCopilotError, cancellationToken: context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task ProbePublicAsync(StudioCopilotCredential credential, CancellationToken cancellationToken)
    {
        using var request = PublicRequest(HttpMethod.Get, "v1/copilot/readiness", credential);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        ValidateResponse(response);
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new StudioCopilotException("public_contract_invalid", 502);
        byte[] bytes = await ReadBoundedAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), 4096, 64, cancellationToken).ConfigureAwait(false);
        StudioCopilotReadiness? readiness;
        try { readiness = JsonSerializer.Deserialize(bytes, StudioBridgeJsonContext.Default.StudioCopilotReadiness); }
        catch (JsonException) { throw new StudioCopilotException("public_contract_invalid", 502); }
        if (readiness?.Status is not ("ok" or "ready")) throw new StudioCopilotException("public_not_ready", 503);
    }

    private async Task ForwardStreamAsync(HttpContext context, string operation, StudioCopilotCredential credential, CancellationToken cancellationToken)
    {
        if (!context.Request.HasJsonContentType()) throw new StudioCopilotException("request_invalid", 400);
        byte[] bytes = await ReadBoundedAsync(context.Request.Body, 256 * 1024, 256, cancellationToken).ConfigureAwait(false);
        var body = JsonSerializer.Deserialize(bytes, StudioBridgeJsonContext.Default.StudioCopilotStreamRequest)
            ?? throw new StudioCopilotException("request_invalid", 400);
        ValidateRequest(body, operation);
        body = EnsureReadOnlyMode(body);
        using var request = PublicRequest(HttpMethod.Post, "v1/copilot/chat/stream", credential);
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body, StudioBridgeJsonContext.Default.StudioCopilotStreamRequest));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        ValidateResponse(response);
        string? contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not ("application/x-ndjson" or "text/event-stream")) throw new StudioCopilotException("public_contract_invalid", 502);
        if (response.Content.Headers.ContentLength is > 4 * 1024 * 1024) throw new StudioCopilotException("stream_limit", 502);
        context.Response.ContentType = contentType;
        context.Response.Headers[ContractHeader] = ContractVersion;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[8192];
        int total = 0;
        for (int chunk = 0; chunk < 2048; chunk++)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            total += count;
            if (total > 4 * 1024 * 1024) throw new StudioCopilotException("stream_limit", 502);
            await context.Response.Body.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        throw new StudioCopilotException("stream_limit", 502);
    }

    private HttpRequestMessage PublicRequest(HttpMethod method, string path, StudioCopilotCredential credential)
    {
        var request = new HttpRequestMessage(method, new Uri(_options!.PublicBaseUri, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Add(ContractHeader, ContractVersion);
        request.Headers.Accept.ParseAdd(method == HttpMethod.Get ? "application/json" : "application/x-ndjson, text/event-stream");
        return request;
    }

    private static void ValidateResponse(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new StudioCopilotException("public_request_failed", 502);
        if (!response.Headers.TryGetValues(ContractHeader, out var values) || !values.SequenceEqual([ContractVersion]))
            throw new StudioCopilotException("public_contract_invalid", 502);
    }

    private static StudioCopilotStreamRequest EnsureReadOnlyMode(StudioCopilotStreamRequest body)
    {
        if (body.Request.TryGetProperty("mode", out _)) return body;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            // ValidateRequest 已将属性限制为至多六个，不依赖公网服务的缺省权限模式。
            foreach (var property in body.Request.EnumerateObject()) property.WriteTo(writer);
            writer.WriteString("mode", "read-only");
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return body with { Request = document.RootElement.Clone() };
    }

    private static void ValidateRequest(StudioCopilotStreamRequest body, string operation)
    {
        if (operation is not ("chat" or "continue") || body.ContractVersion != ContractVersion || string.IsNullOrWhiteSpace(body.RunId) || body.RunId.Length > 128
            || body.Request.ValueKind != JsonValueKind.Object || (operation == "continue") != (body.Continuation is not null))
            throw new StudioCopilotException("request_invalid", 400);
        string[] allowed = ["db", "messages", "conversationId", "cloudMode", "mode", "model"];
        var fields = new HashSet<string>(StringComparer.Ordinal);
        if (body.Request.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)
                || !fields.Add(property.Name)
                || (property.Name != "messages" && (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 1024)))
            || (body.Request.TryGetProperty("mode", out var mode) && mode.GetString() != "read-only")
            || (body.Request.TryGetProperty("cloudMode", out var cloudMode) && cloudMode.GetString() is not ("sql_assist" or "sql_analyze" or "db_maintenance" or "knowledge_qa"))
            || !body.Request.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array
            || messages.GetArrayLength() is < 1 or > 128)
            throw new StudioCopilotException("request_invalid", 400);
        foreach (var message in messages.EnumerateArray())
        {
            fields.Clear();
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String || content.GetString()!.Length > 32768
                || !message.TryGetProperty("role", out var role) || role.ValueKind != JsonValueKind.String
                || role.GetString() is not ("user" or "assistant" or "system" or "tool")
                || message.EnumerateObject().Any(property => property.Name is not ("role" or "content" or "citations") || !fields.Add(property.Name)))
                throw new StudioCopilotException("request_invalid", 400);
            if (message.TryGetProperty("citations", out var citations))
            {
                if (citations.ValueKind != JsonValueKind.Array || citations.GetArrayLength() > 128)
                    throw new StudioCopilotException("request_invalid", 400);
                foreach (var citation in citations.EnumerateArray())
                {
                    fields.Clear();
                    if (citation.ValueKind != JsonValueKind.Object || citation.EnumerateObject().Any(property =>
                        property.Name is not ("id" or "kind" or "title" or "source" or "snippet") || !fields.Add(property.Name)
                        || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString()!.Length > 32768))
                        throw new StudioCopilotException("request_invalid", 400);
                }
            }
        }
        if (body.Continuation is { } next && (string.IsNullOrWhiteSpace(next.PreviousCursor) || next.PreviousCursor.Length > 1024
            || string.IsNullOrWhiteSpace(next.ToolCallId) || next.ToolCallId.Length > 128
            || string.IsNullOrWhiteSpace(next.ToolName) || next.ToolName.Length > 128 || next.ToolResult is null
            || Encoding.UTF8.GetByteCount(next.ToolResult) > 65536)) throw new StudioCopilotException("request_invalid", 400);
    }

    private StudioCopilotCredential? ReadCredential()
    {
        if (_options is null || _store is null || _disposed) return null;
        var credential = _store.Read();
        if (credential is null) return null;
        if (!ValidToken(credential.AccessToken) || credential.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || credential.ExpiresAtUtc > DateTimeOffset.UtcNow.AddHours(2)) { _store.Delete(); return null; }
        return credential;
    }

    private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken, TimeSpan timeout)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionLifetime.Token);
            linked.CancelAfter(timeout);
            return linked;
        }
    }

    private static bool ValidToken(string? token) => token is { Length: > 0 and <= 2048 }
        && token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '~' or '+' or '/' or '=');
    private static TimeSpan Minimum(TimeSpan first, TimeSpan second) => second <= TimeSpan.Zero ? TimeSpan.Zero : first < second ? first : second;

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, int maximumChunks, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        for (int chunk = 0; chunk < maximumChunks; chunk++)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximumBytes) throw new StudioCopilotException("payload_limit", 413);
            output.Write(buffer, 0, count);
        }
        throw new StudioCopilotException("payload_limit", 413);
    }

    /// <summary>结束本宿主在途操作；持久凭据仅在用户断开或到期时删除。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _connectionLifetime.Cancel();
            _connectionLifetime.Dispose();
            _http.Dispose();
        }
    }

    private sealed class StudioCopilotException(string code, int statusCode) : Exception("Studio Copilot request rejected.")
    {
        internal string Code { get; } = code;
        internal int StatusCode { get; } = statusCode;
    }
}
