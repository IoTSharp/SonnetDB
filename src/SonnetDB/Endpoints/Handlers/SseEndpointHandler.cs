using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;

namespace SonnetDB.Endpoints;

/// <summary>
/// SSE（Server-Sent Events）端点：<c>GET /v1/events</c>。
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>使用 <c>text/event-stream</c> 长连接，每条事件按 <c>event:</c> + <c>data:</c> + 空行格式输出。</item>
///   <item>支持 query 参数 <c>?stream=metrics,slow_query,db</c> 过滤通道；缺省订阅全部。</item>
///   <item>连接建立后立即发送一条 <c>event: hello</c>，便于客户端确认握手成功。</item>
///   <item>每 30 秒发送一次注释行（以 <c>:</c> 开头）作为心跳，避免中间代理切断空闲连接。</item>
///   <item>对动态用户 token，数据库相关事件会按当前 grants 过滤，避免泄露未授权数据库名。</item>
/// </list>
/// </remarks>
internal static class SseEndpointHandler
{
    private const string _controlPlaneDatabaseLabel = "__control";
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 处理 <c>GET /v1/events</c>。在客户端断开或 host shutdown 时退出。
    /// </summary>
    public static async Task HandleAsync(HttpContext context, EventBroadcaster broadcaster, GrantsStore grantsStore)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(grantsStore);

        var filter = ParseStreamFilter(context.Request.Query["stream"].ToString());
        var eventProjector = CreateEventProjector(context, grantsStore);
        ConfigureResponse(context);

        await WriteEventAsync(context, "hello", "{\"ok\":true}").ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        using var sub = broadcaster.Subscribe(filter, capacity: 128);
        var reader = sub.Reader;

        var heartbeatCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, heartbeatCts.Token);

        try
        {
            while (!context.RequestAborted.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                timeoutCts.CancelAfter(_heartbeatInterval);

                bool gotEvent;
                try
                {
                    gotEvent = await reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    await WriteHeartbeatAsync(context).ConfigureAwait(false);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!gotEvent)
                    break;

                while (reader.TryRead(out var evt))
                {
                    var projected = eventProjector(evt);
                    if (projected is null)
                        continue;

                    await WriteEventAsync(context, projected.Type, projected.Data, projected.TimestampMs).ConfigureAwait(false);
                }

                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 客户端断开，静默退出。
        }
        finally
        {
            heartbeatCts.Cancel();
        }
    }

    private static Func<ServerEvent, ServerEvent?> CreateEventProjector(HttpContext context, GrantsStore grantsStore)
    {
        var user = BearerAuthMiddleware.GetUser(context);
        if (user is AuthenticatedUser authenticatedUser)
        {
            if (authenticatedUser.IsSuperuser)
                return static evt => evt;

            return evt => ProjectEventForDynamicUser(evt, authenticatedUser.UserName, grantsStore);
        }

        return static evt => evt;
    }

    private static ServerEvent? ProjectEventForDynamicUser(ServerEvent evt, string userName, GrantsStore grantsStore)
        => evt.Type switch
        {
            ServerEvent.ChannelDatabase => FilterDatabaseEvent(evt, userName, grantsStore),
            ServerEvent.ChannelSlowQuery => FilterSlowQueryEvent(evt, userName, grantsStore),
            ServerEvent.ChannelMetrics => FilterMetricsEvent(evt, userName, grantsStore),
            _ => evt,
        };

    private static ServerEvent? FilterDatabaseEvent(ServerEvent evt, string userName, GrantsStore grantsStore)
    {
        var payload = JsonSerializer.Deserialize(evt.Data, ServerJsonContext.Default.DatabaseEvent);
        if (payload is null)
            return null;

        return grantsStore.GetPermission(userName, payload.Database) >= DatabasePermission.Read
            ? evt
            : null;
    }

    private static ServerEvent? FilterSlowQueryEvent(ServerEvent evt, string userName, GrantsStore grantsStore)
    {
        var payload = JsonSerializer.Deserialize(evt.Data, ServerJsonContext.Default.SlowQueryEvent);
        if (payload is null)
            return null;

        if (payload.Database == _controlPlaneDatabaseLabel)
            return null;

        return grantsStore.GetPermission(userName, payload.Database) >= DatabasePermission.Read
            ? evt
            : null;
    }

    private static ServerEvent? FilterMetricsEvent(ServerEvent evt, string userName, GrantsStore grantsStore)
    {
        var payload = JsonSerializer.Deserialize(evt.Data, ServerJsonContext.Default.MetricsSnapshotEvent);
        if (payload is null)
            return null;

        var filteredSegments = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in payload.PerDatabaseSegments)
        {
            if (grantsStore.GetPermission(userName, pair.Key) >= DatabasePermission.Read)
                filteredSegments[pair.Key] = pair.Value;
        }

        var filteredPayload = payload with
        {
            Databases = filteredSegments.Count,
            PerDatabaseSegments = filteredSegments,
        };

        return evt with
        {
            Data = JsonSerializer.Serialize(filteredPayload, ServerJsonContext.Default.MetricsSnapshotEvent),
        };
    }

    private static void ConfigureResponse(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache, no-store, no-transform";
        context.Response.Headers.Connection = "keep-alive";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        var bufferingFeature = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
        bufferingFeature?.DisableBuffering();
    }

    private static async Task WriteEventAsync(HttpContext context, string type, string jsonData, long? timestampMs = null)
    {
        var sb = new StringBuilder(jsonData.Length + 64);
        sb.Append("event: ").Append(type).Append('\n');
        if (timestampMs.HasValue)
            sb.Append("id: ").Append(timestampMs.Value).Append('\n');

        foreach (var line in jsonData.Split('\n'))
        {
            sb.Append("data: ").Append(line).Append('\n');
        }

        sb.Append('\n');
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteHeartbeatAsync(HttpContext context)
    {
        var bytes = ": heartbeat\n\n"u8.ToArray();
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static IReadOnlySet<string>? ParseStreamFilter(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part);
        }

        return set.Count == 0 ? null : set;
    }
}

/// <summary>
/// 把强类型事件序列化为 <see cref="ServerEvent"/> 的便利工厂。
/// </summary>
internal static class ServerEventFactory
{
    /// <summary>
    /// 构造 <c>metrics</c> 事件。
    /// </summary>
    public static ServerEvent Metrics(MetricsSnapshotEvent payload)
    {
        var json = JsonSerializer.Serialize(payload, ServerJsonContext.Default.MetricsSnapshotEvent);
        return new ServerEvent(ServerEvent.ChannelMetrics, json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 构造 <c>slow_query</c> 事件。
    /// </summary>
    public static ServerEvent SlowQuery(SlowQueryEvent payload)
    {
        var json = JsonSerializer.Serialize(payload, ServerJsonContext.Default.SlowQueryEvent);
        return new ServerEvent(ServerEvent.ChannelSlowQuery, json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 构造 <c>db</c> 事件。
    /// </summary>
    public static ServerEvent Database(DatabaseEvent payload)
    {
        var json = JsonSerializer.Serialize(payload, ServerJsonContext.Default.DatabaseEvent);
        return new ServerEvent(ServerEvent.ChannelDatabase, json, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
