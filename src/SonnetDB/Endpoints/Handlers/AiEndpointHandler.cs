using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// AI 助手相关端点。
/// </summary>
internal static class AiEndpointHandler
{
    private const string OfficialGatewayBaseUrl = "https://ai.sonnetdb.com";
    private const string OfficialPlatformApiBaseUrl = "https://api.sonnetdb.com";

    /// <summary>
    /// 向应用注册 AI 端点。
    /// </summary>
    public static void Map(
        WebApplication app,
        AiConfigStore configStore,
        GrantsStore grantsStore,
        TsdbRegistry registry,
        IHttpClientFactory httpClientFactory,
        CopilotChatOptions copilotChatOptions,
        CopilotEmbeddingOptions copilotEmbeddingOptions)
    {
        app.MapGet("/v1/ai/status", () =>
        {
            var cfg = configStore.Get();
            return Results.Json(
                new AiStatusResponse(Enabled: true, IsCloudBound(cfg)),
                ServerJsonContext.Default.AiStatusResponse);
        });

        app.MapGet("/v1/admin/ai-config", (HttpContext ctx) =>
        {
            if (!BearerAuthMiddleware.IsAdmin(BearerAuthMiddleware.GetRole(ctx)))
            {
                return Results.Json(
                    new ErrorResponse("forbidden", "仅 admin 可读取 AI 配置。"),
                    ServerJsonContext.Default.ErrorResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var cfg = configStore.Get();
            return Results.Json(
                new AiConfigResponse(
                    Enabled: true,
                    IsCloudBound(cfg),
                    cfg.CloudAccessTokenExpiresAtUtc,
                    cfg.CloudBoundAtUtc),
                ServerJsonContext.Default.AiConfigResponse);
        });

        app.MapMethods("/v1/admin/ai-config", ["PUT"], (RequestDelegate)(async ctx =>
        {
            if (!BearerAuthMiddleware.IsAdmin(BearerAuthMiddleware.GetRole(ctx)))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "仅 admin 可修改 AI 配置。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.AiConfigRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不能为空。").ConfigureAwait(false);
                return;
            }

            var existing = configStore.Get();
            var updated = new AiOptions
            {
                Enabled = true,
                GatewayBaseUrl = OfficialGatewayBaseUrl,
                PlatformApiBaseUrl = OfficialPlatformApiBaseUrl,
                ApiKey = existing.ApiKey,
                CloudAccessToken = existing.CloudAccessToken,
                CloudRefreshToken = existing.CloudRefreshToken,
                CloudDeviceLocalId = existing.CloudDeviceLocalId,
                CloudTokenType = string.IsNullOrWhiteSpace(existing.CloudTokenType) ? "Bearer" : existing.CloudTokenType,
                CloudAccessTokenExpiresAtUtc = existing.CloudAccessTokenExpiresAtUtc,
                CloudScope = existing.CloudScope,
                CloudBoundAtUtc = existing.CloudBoundAtUtc,
                Model = string.Empty,
                TimeoutSeconds = 60,
            };
            configStore.Save(updated);

            // M16/M2：同步到 Copilot 子系统选项，使 /v1/copilot/chat 依赖的 Cloud Token 立即生效。
            AiCopilotBridge.Apply(updated, copilotChatOptions, copilotEmbeddingOptions);

            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        }));

        app.MapMethods("/v1/admin/ai-cloud/models", ["GET"], (RequestDelegate)(async ctx =>
        {
            if (!BearerAuthMiddleware.IsAdmin(BearerAuthMiddleware.GetRole(ctx)))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "仅 admin 可读取平台模型列表。").ConfigureAwait(false);
                return;
            }

            var cfg = configStore.Get();
            if (!IsCloudBound(cfg))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "cloud_not_bound",
                    "AI 助手尚未绑定 sonnetdb.com 账号，请先完成绑定。").ConfigureAwait(false);
                return;
            }

            if (cfg.CloudAccessTokenExpiresAtUtc is not null &&
                cfg.CloudAccessTokenExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "cloud_token_expired",
                    "sonnetdb.com Cloud Access Token 已过期，请重新绑定账号。").ConfigureAwait(false);
                return;
            }

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds > 0 ? cfg.TimeoutSeconds : 60);
            using var request = new HttpRequestMessage(HttpMethod.Get, OfficialGatewayBaseUrl + "/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                string.IsNullOrWhiteSpace(cfg.CloudTokenType) ? "Bearer" : cfg.CloudTokenType.Trim(),
                cfg.CloudAccessToken);

            using var response = await client.SendAsync(request, ctx.RequestAborted).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "platform_models_failed",
                    $"平台模型列表读取失败 {(int)response.StatusCode}: {payload}").ConfigureAwait(false);
                return;
            }

            var parsed = JsonSerializer.Deserialize(payload, ServerJsonContext.Default.OpenAiModelsResponse);
            var candidates = parsed?.Data?
                .Where(static item => !string.IsNullOrWhiteSpace(item.Id))
                .Select(static item => item.Id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
            var defaultModel = candidates.Count > 0 ? candidates[0] : string.Empty;
            var resp = new AiCloudModelsResponse(defaultModel, candidates);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.AiCloudModelsResponse, ctx.RequestAborted).ConfigureAwait(false);
        }));

        app.MapMethods("/v1/admin/ai-cloud/device-code", ["POST"], (RequestDelegate)(async ctx =>
        {
            if (!BearerAuthMiddleware.IsAdmin(BearerAuthMiddleware.GetRole(ctx)))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "仅 admin 可发起 sonnetdb.com 账号绑定。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.AiCloudDeviceCodeRequest).ConfigureAwait(false);
            req ??= new AiCloudDeviceCodeRequest(null, null, null);

            var cfg = configStore.Get();
            var deviceLocalId = configStore.GetOrCreateCloudDeviceLocalId();
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds > 0 ? cfg.TimeoutSeconds : 60);

            var platformBaseUrl = NormalizePlatformApiBaseUrl(cfg.PlatformApiBaseUrl);
            using var response = await client.PostAsync(
                platformBaseUrl + "/api/v1/device-codes",
                JsonContent.Create(
                    new PlatformDeviceCodeRequest(
                        string.IsNullOrWhiteSpace(req.ClientName) ? "SonnetDB OSS Copilot" : req.ClientName.Trim(),
                        string.IsNullOrWhiteSpace(req.ClientVersion) ? null : req.ClientVersion.Trim(),
                        string.IsNullOrWhiteSpace(req.DeviceName) ? Environment.MachineName : req.DeviceName.Trim(),
                        deviceLocalId,
                        ["platform.cloud", "ai.invoke"]),
                    PlatformDeviceCodeJsonContext.Default.PlatformDeviceCodeRequest),
                ctx.RequestAborted).ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "platform_device_code_failed",
                    $"sonnetdb.com 设备码申请失败 {(int)response.StatusCode}: {payload}").ConfigureAwait(false);
                return;
            }

            var parsed = JsonSerializer.Deserialize(payload, PlatformDeviceCodeJsonContext.Default.PlatformDeviceCodeResponse);
            if (parsed is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "platform_device_code_invalid",
                    "sonnetdb.com 返回了无法解析的设备码响应。").ConfigureAwait(false);
                return;
            }

            var resp = new AiCloudDeviceCodeResponse(
                parsed.DeviceCode,
                parsed.UserCode,
                parsed.VerificationUri,
                parsed.VerificationUriComplete,
                parsed.ExpiresIn,
                parsed.Interval);
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.AiCloudDeviceCodeResponse, ctx.RequestAborted).ConfigureAwait(false);
        }));

        app.MapMethods("/v1/admin/ai-cloud/device-token", ["POST"], (RequestDelegate)(async ctx =>
        {
            if (!BearerAuthMiddleware.IsAdmin(BearerAuthMiddleware.GetRole(ctx)))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden", "仅 admin 可完成 sonnetdb.com 账号绑定。").ConfigureAwait(false);
                return;
            }

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.AiCloudDeviceTokenRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.DeviceCode))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "deviceCode 不可为空。").ConfigureAwait(false);
                return;
            }

            var cfg = configStore.Get();
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds > 0 ? cfg.TimeoutSeconds : 60);

            var platformBaseUrl = NormalizePlatformApiBaseUrl(cfg.PlatformApiBaseUrl);
            var platformReq = new PlatformDeviceTokenRequest(req.DeviceCode.Trim());
            using var response = await client.PostAsync(
                platformBaseUrl + "/api/v1/device-tokens",
                JsonContent.Create(platformReq, PlatformDeviceCodeJsonContext.Default.PlatformDeviceTokenRequest),
                ctx.RequestAborted).ConfigureAwait(false);
            var payload = await response.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = TryReadPlatformDeviceTokenError(payload);
                var pollResponse = new AiCloudDeviceTokenResponse(false, error.Code, error.Message, null);
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await JsonSerializer.SerializeAsync(ctx.Response.Body, pollResponse, ServerJsonContext.Default.AiCloudDeviceTokenResponse, ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            var token = JsonSerializer.Deserialize(payload, PlatformDeviceCodeJsonContext.Default.PlatformDeviceTokenResponse);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "platform_device_token_invalid",
                    "sonnetdb.com 返回了无法解析的 Cloud Token 响应。").ConfigureAwait(false);
                return;
            }

            var utcNow = DateTimeOffset.UtcNow;
            var expiresAt = utcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 1800);
            var updated = new AiOptions
            {
                Enabled = true,
                GatewayBaseUrl = OfficialGatewayBaseUrl,
                PlatformApiBaseUrl = OfficialPlatformApiBaseUrl,
                ApiKey = cfg.ApiKey,
                CloudAccessToken = token.AccessToken,
                CloudRefreshToken = token.RefreshToken,
                CloudDeviceLocalId = cfg.CloudDeviceLocalId,
                CloudTokenType = string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
                CloudAccessTokenExpiresAtUtc = expiresAt,
                CloudScope = token.Scope,
                CloudBoundAtUtc = utcNow,
                Model = string.Empty,
                TimeoutSeconds = 60,
            };
            configStore.Save(updated);
            AiCopilotBridge.Apply(updated, copilotChatOptions, copilotEmbeddingOptions);

            var resp = new AiCloudDeviceTokenResponse(true, null, null, expiresAt.ToString("O"));
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(ctx.Response.Body, resp, ServerJsonContext.Default.AiCloudDeviceTokenResponse, ctx.RequestAborted).ConfigureAwait(false);
        }));

        app.MapMethods("/v1/ai/chat", ["POST"], (RequestDelegate)(async ctx =>
        {
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.AiChatRequest).ConfigureAwait(false);
            if (req is null || req.Messages.Count == 0)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "messages 不可为空。").ConfigureAwait(false);
                return;
            }

            string? sqlGenPrompt = null;
            if (req.Mode == "sql_gen")
            {
                if (req.Db is not null)
                {
                    sqlGenPrompt = await TryBuildAuthorizedSqlGenPromptAsync(ctx, req.Db, registry, grantsStore).ConfigureAwait(false);
                    if (sqlGenPrompt is null)
                        return;
                }
                else
                {
                    // 控制面（未指定 db）也要给出 SonnetDB 方言的系统提示，避免回退到 MySQL/PG 语法。
                    sqlGenPrompt = BuildSqlGenPromptWithoutDb();
                }
            }

            var cfg = configStore.Get();
            if (!IsCloudBound(cfg))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "cloud_not_bound",
                    "AI 助手尚未绑定 sonnetdb.com 账号，请管理员在 Copilot 设置中完成绑定。").ConfigureAwait(false);
                return;
            }

            if (cfg.CloudAccessTokenExpiresAtUtc is not null &&
                cfg.CloudAccessTokenExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "cloud_token_expired",
                    "sonnetdb.com Cloud Access Token 已过期，请在 Copilot 设置中重新绑定账号。").ConfigureAwait(false);
                return;
            }

            var messages = BuildMessages(req, sqlGenPrompt);

            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            try
            {
                await ProxyOpenAiAsync(ctx, cfg, messages, httpClientFactory).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await WriteSseErrorAsync(ctx, ex.Message).ConfigureAwait(false);
            }
        }));
    }

    private static List<AiMessage> BuildMessages(AiChatRequest req, string? sqlGenPrompt)
    {
        var messages = new List<AiMessage>(req.Messages.Count + 1);
        if (!string.IsNullOrEmpty(sqlGenPrompt))
            messages.Add(new AiMessage("system", sqlGenPrompt));

        messages.AddRange(req.Messages);
        return messages;
    }

    private static async Task<string?> TryBuildAuthorizedSqlGenPromptAsync(
        HttpContext ctx,
        string db,
        TsdbRegistry registry,
        GrantsStore grantsStore)
    {
        if (!TsdbRegistry.IsValidName(db))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"非法数据库名 '{db}'。").ConfigureAwait(false);
            return null;
        }

        if (!registry.TryGet(db, out var tsdb))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "db_not_found", $"数据库 '{db}' 不存在。").ConfigureAwait(false);
            return null;
        }

        var permission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grantsStore, db);
        if (!DatabaseAccessEvaluator.HasPermission(permission, DatabasePermission.Read))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "forbidden",
                $"当前凭据对数据库 '{db}' 没有 read 权限。").ConfigureAwait(false);
            return null;
        }

        return BuildSqlGenPrompt(db, tsdb);
    }

    private static string BuildSqlGenPrompt(string db, Tsdb tsdb)
    {
        var measurements = tsdb.Measurements.Snapshot();
        string measurementsBlock;
        if (measurements.Count == 0)
        {
            measurementsBlock = "（空，需要先用 CREATE MEASUREMENT 建表）";
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var measurement in measurements)
            {
                sb.Append("- ").Append(measurement.Name).Append(" (time");
                foreach (var tag in measurement.TagColumns)
                    sb.Append(", ").Append(tag.Name).Append(" TAG");
                foreach (var field in measurement.FieldColumns)
                    sb.Append(", ").Append(field.Name).Append(" FIELD ").Append(field.DataType);
                sb.AppendLine(")");
            }
            measurementsBlock = sb.ToString().TrimEnd();
        }

        return PromptTemplates.Render("sql-gen", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["db"] = db,
            ["measurements"] = measurementsBlock,
        });
    }

    private static string BuildSqlGenPromptWithoutDb()
        => PromptTemplates.Load("sql-gen-no-db");

    private static async Task ProxyOpenAiAsync(
        HttpContext ctx,
        AiOptions cfg,
        List<AiMessage> messages,
        IHttpClientFactory factory)
    {
        using var client = factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);

        var model = await ResolvePlatformDefaultModelAsync(client, cfg, ctx.RequestAborted).ConfigureAwait(false);
        var requestBody = new OpenAiRequest(model, messages, Stream: true);
        var json = JsonSerializer.Serialize(requestBody, ServerJsonContext.Default.OpenAiRequest);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var baseUrl = NormalizeGatewayBaseUrl(cfg.GatewayBaseUrl);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/v1/chat/completions")
        {
            Content = content,
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue(
            string.IsNullOrWhiteSpace(cfg.CloudTokenType) ? "Bearer" : cfg.CloudTokenType.Trim(),
            cfg.CloudAccessToken);

        using var resp = await client.SendAsync(httpReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ctx.RequestAborted).ConfigureAwait(false);
            await WriteSseErrorAsync(ctx, $"AI 服务错误 {(int)resp.StatusCode}: {err}").ConfigureAwait(false);
            return;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!ctx.RequestAborted.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ctx.RequestAborted).ConfigureAwait(false);
            if (line is null)
                break;
            if (line.Length == 0 || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;

            OpenAiChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize(data, ServerJsonContext.Default.OpenAiChunk);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk?.Choices is null || chunk.Choices.Count == 0)
                continue;

            var token = chunk.Choices[0].Delta?.Content;
            if (!string.IsNullOrEmpty(token))
                await WriteSseTokenAsync(ctx, token).ConfigureAwait(false);
        }

        await WriteSseDoneAsync(ctx).ConfigureAwait(false);
    }

    private static async ValueTask<string?> ResolvePlatformDefaultModelAsync(
        HttpClient client,
        AiOptions cfg,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, OfficialGatewayBaseUrl + "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            string.IsNullOrWhiteSpace(cfg.CloudTokenType) ? "Bearer" : cfg.CloudTokenType.Trim(),
            cfg.CloudAccessToken);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize(payload, ServerJsonContext.Default.OpenAiModelsResponse);
        return parsed?.Data?
            .FirstOrDefault(static item => !string.IsNullOrWhiteSpace(item.Id))
            ?.Id
            .Trim();
    }

    private static async Task WriteSseTokenAsync(HttpContext ctx, string token)
    {
        var evt = JsonSerializer.Serialize(new SseTokenEvent(token), ServerJsonContext.Default.SseTokenEvent);
        await ctx.Response.WriteAsync($"data: {evt}\n\n", ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteSseErrorAsync(HttpContext ctx, string error)
    {
        var evt = JsonSerializer.Serialize(new SseErrorEvent(error), ServerJsonContext.Default.SseErrorEvent);
        await ctx.Response.WriteAsync($"data: {evt}\n\n", ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteSseDoneAsync(HttpContext ctx)
    {
        await ctx.Response.WriteAsync("data: [DONE]\n\n", ctx.RequestAborted).ConfigureAwait(false);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string code, string message)
    {
        if (ctx.Response.HasStarted)
            return;

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            ctx.RequestAborted).ConfigureAwait(false);
    }

    private static bool IsCloudBound(AiOptions cfg)
        => !string.IsNullOrWhiteSpace(cfg.CloudAccessToken);

    private static string NormalizeGatewayBaseUrl(string? value)
        => NormalizeAllowedBaseUrl(value, OfficialGatewayBaseUrl, OfficialGatewayBaseUrl);

    private static string NormalizePlatformApiBaseUrl(string? value)
        => NormalizeAllowedBaseUrl(value, OfficialPlatformApiBaseUrl, OfficialPlatformApiBaseUrl);

    private static string NormalizeAllowedBaseUrl(string? value, string allowed, string fallback)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('/');
        return string.Equals(candidate, allowed, StringComparison.OrdinalIgnoreCase)
            ? allowed
            : fallback;
    }

    private static (string Code, string Message) TryReadPlatformDeviceTokenError(string payload)
    {
        try
        {
            var problem = JsonSerializer.Deserialize(payload, PlatformDeviceCodeJsonContext.Default.PlatformProblemResponse);
            var code = problem?.Error;
            if (string.IsNullOrWhiteSpace(code) && problem?.Extensions is not null &&
                problem.Extensions.TryGetValue("error", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                code = value.GetString();
            }

            return (
                string.IsNullOrWhiteSpace(code) ? "authorization_pending" : code,
                problem?.Detail ?? problem?.Title ?? "等待用户在 sonnetdb.com 上确认绑定。");
        }
        catch (JsonException)
        {
            return ("authorization_pending", "等待用户在 sonnetdb.com 上确认绑定。");
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (ctx.Request.ContentLength == 0)
            return null;

        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed record PlatformDeviceCodeRequest(
    string ClientName,
    string? ClientVersion,
    string? DeviceName,
    string DeviceLocalId,
    IReadOnlyList<string> Scopes);

internal sealed record PlatformDeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    int ExpiresIn,
    int Interval);

internal sealed record PlatformDeviceTokenRequest(string DeviceCode);

internal sealed record PlatformDeviceTokenResponse(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    int ExpiresIn,
    string Scope);

internal sealed record PlatformProblemResponse(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    string? Error,
    [property: JsonExtensionData]
    Dictionary<string, JsonElement>? Extensions);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(PlatformDeviceCodeRequest))]
[JsonSerializable(typeof(PlatformDeviceCodeResponse))]
[JsonSerializable(typeof(PlatformDeviceTokenRequest))]
[JsonSerializable(typeof(PlatformDeviceTokenResponse))]
[JsonSerializable(typeof(PlatformProblemResponse))]
internal sealed partial class PlatformDeviceCodeJsonContext : JsonSerializerContext;
