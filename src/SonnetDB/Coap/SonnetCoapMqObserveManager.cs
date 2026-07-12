using System.Collections.Concurrent;
using CoAP;
using CoAP.Server.Routing;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Diagnostics;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetMQ;

namespace SonnetDB.Coap;

/// <summary>
/// 将 CoAP Observe 订阅桥接到 SonnetMQ 的新消息推送管线。
/// </summary>
internal sealed class SonnetCoapMqObserveManager : IDisposable
{
    private const int ObserveModulo = 1 << 24;
    private static readonly TimeSpan ObserverRegistrationGrace = TimeSpan.FromSeconds(5);

    private readonly TsdbRegistry _registry;
    private readonly GrantsStore _grants;
    private readonly UserStore _users;
    private readonly SonnetMqStore _mq;
    private readonly ServerOptions _options;
    private readonly CoapRouteObserveRegistry _observeRegistry;
    private readonly ILogger<SonnetCoapMqObserveManager> _logger;
    private readonly ConcurrentDictionary<string, ObserverState> _observers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TopicPump> _pumps = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>
    /// 创建 CoAP MQ Observe 管理器。
    /// </summary>
    /// <param name="registry">数据库注册表。</param>
    /// <param name="grants">用户授权存储。</param>
    /// <param name="users">动态用户存储。</param>
    /// <param name="mq">SonnetMQ 存储。</param>
    /// <param name="options">服务器配置。</param>
    /// <param name="observeRegistry">CoAP route Observe 注册表。</param>
    /// <param name="logger">日志记录器。</param>
    public SonnetCoapMqObserveManager(
        TsdbRegistry registry,
        GrantsStore grants,
        UserStore users,
        SonnetMqStore mq,
        IOptions<ServerOptions> options,
        CoapRouteObserveRegistry observeRegistry,
        ILogger<SonnetCoapMqObserveManager> logger)
    {
        _registry = registry;
        _grants = grants;
        _users = users;
        _mq = mq;
        _options = options.Value;
        _observeRegistry = observeRegistry;
        _logger = logger;
    }

    /// <summary>
    /// 处理 <c>db/{db}/mq/{topic}</c> 的 CoAP GET/Observe 请求。
    /// </summary>
    /// <param name="db">数据库名。</param>
    /// <param name="topic">MQ topic 名。</param>
    /// <param name="context">CoAP 路由上下文。</param>
    /// <returns>当前订阅游标对应的消息响应。</returns>
    public CoapRouteResult Get(string db, string topic, CoapRouteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string resourcePath = BuildResourcePath(db, topic);
        string relationKey = BuildRelationKey(context);

        if (!Validate(db, topic, out var validationError))
            return RemoveAndText(resourcePath, relationKey, validationError.StatusCode, validationError.Message);

        if (!SonnetCoapAuthentication.TryAuthenticate(context.Queries, _options, _users, out var principal))
            return RemoveAndText(resourcePath, relationKey, StatusCode.Unauthorized, "缺失或无效的 SonnetDB CoAP token。");

        if (!principal.HasPermission(_grants, db, DatabasePermission.Read))
            return RemoveAndText(resourcePath, relationKey, StatusCode.Forbidden, $"当前 CoAP 凭据对数据库 '{db}' 没有读权限。");

        string qualifiedTopic = SonnetDbEndpoints.QualifyMqTopic(db, topic);
        if (context.Observe == 1)
        {
            RemoveObserver(resourcePath, relationKey);
            return CoapRouteResult.Changed();
        }

        if (context.Observe == 0)
        {
            var key = StateKey(resourcePath, relationKey);
            _observers.GetOrAdd(
                key,
                _ => new ObserverState(resourcePath, relationKey, db, topic, qualifiedTopic, _mq.GetStats(qualifiedTopic).NextOffset));
            EnsurePump(resourcePath, qualifiedTopic);
        }

        if (!_observers.TryGetValue(StateKey(resourcePath, relationKey), out var state))
        {
            return CoapRouteResult.Content(ReadOnlyMemory<byte>.Empty, MediaType.ApplicationOctetStream)
                .WithMaxAge(0);
        }

        lock (state.SyncRoot)
        {
            IReadOnlyList<SonnetMqMessage> messages = _mq.Pull(qualifiedTopic, state.Cursor, 1);
            if (messages.Count == 0)
            {
                return CoapRouteResult.Content(ReadOnlyMemory<byte>.Empty, MediaType.ApplicationOctetStream)
                    .WithObserve(ToObserveValue(state.Cursor))
                    .WithMaxAge(0);
            }

            SonnetMqMessage message = messages[0];
            state.Cursor = message.Offset + 1;
            return CoapRouteResult.Content(message.Payload, ResolveContentFormat(message))
                .WithObserve(ToObserveValue(state.Cursor))
                .WithMaxAge(0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        foreach (var pump in _pumps.Values)
            pump.Cancellation.Cancel();

        try
        {
            Task.WaitAll(_pumps.Values.Select(p => p.Task).ToArray(), TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 应用释放阶段只需尽力等待后台 pump 退出。
        }

        foreach (var pump in _pumps.Values)
            pump.Cancellation.Dispose();
    }

    private bool Validate(string db, string topic, out CoapError error)
    {
        error = default;
        if (!TsdbRegistry.IsValidName(db))
        {
            error = new CoapError(StatusCode.BadRequest, $"非法数据库名 '{db}'。");
            return false;
        }

        if (!_registry.TryGet(db, out _))
        {
            error = new CoapError(StatusCode.NotFound, $"数据库 '{db}' 不存在。");
            return false;
        }

        if (!IsValidTopicName(topic))
        {
            error = new CoapError(StatusCode.BadRequest, $"非法 topic 名 '{topic}'。");
            return false;
        }

        return true;
    }

    private void EnsurePump(string resourcePath, string qualifiedTopic)
    {
        if (_disposed)
            return;

        _pumps.GetOrAdd(resourcePath, _ =>
        {
            var pump = new TopicPump(resourcePath, qualifiedTopic);
            pump.Task = Task.Run(() => RunPumpAsync(pump));
            return pump;
        });
    }

    private async Task RunPumpAsync(TopicPump pump)
    {
        try
        {
            while (!pump.Cancellation.IsCancellationRequested)
            {
                CleanupStaleObservers(pump.ResourcePath);
                if (!TryGetMinimumCursor(pump.ResourcePath, out long cursor))
                    return;

                await _mq.WaitForMessagesAsync(pump.QualifiedTopic, cursor, pump.Cancellation.Token).ConfigureAwait(false);
                while (!pump.Cancellation.IsCancellationRequested)
                {
                    CleanupStaleObservers(pump.ResourcePath);
                    if (!TryGetMinimumCursor(pump.ResourcePath, out cursor))
                        return;

                    var stats = _mq.GetStats(pump.QualifiedTopic);
                    if (stats.NextOffset <= cursor)
                        break;

                    int notified = _observeRegistry.NotifyObservers(pump.ResourcePath);
                    if (notified == 0)
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退订 / 应用关闭。
        }
        catch (ObjectDisposedException)
        {
            // SonnetMQ 释放，服务即将关闭。
        }
        catch (Exception ex)
        {
            _logger.CoapObservePumpFailed(ex, pump.QualifiedTopic);
        }
        finally
        {
            if (_pumps.TryGetValue(pump.ResourcePath, out var current) && ReferenceEquals(current, pump))
                _pumps.TryRemove(pump.ResourcePath, out _);
        }
    }

    private void CleanupStaleObservers(string resourcePath)
    {
        IReadOnlyList<string> activeKeys = _observeRegistry.GetObserverKeys(resourcePath);
        if (activeKeys.Count == 0)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var pair in _observers)
            {
                if (string.Equals(pair.Value.ResourcePath, resourcePath, StringComparison.Ordinal)
                    && now - pair.Value.CreatedAtUtc >= ObserverRegistrationGrace)
                {
                    _observers.TryRemove(pair.Key, out _);
                }
            }

            return;
        }

        var active = new HashSet<string>(activeKeys, StringComparer.Ordinal);
        foreach (var pair in _observers)
        {
            if (string.Equals(pair.Value.ResourcePath, resourcePath, StringComparison.Ordinal)
                && !active.Contains(pair.Value.RelationKey))
            {
                _observers.TryRemove(pair.Key, out _);
            }
        }
    }

    private bool TryGetMinimumCursor(string resourcePath, out long cursor)
    {
        cursor = long.MaxValue;
        var found = false;
        foreach (var state in _observers.Values)
        {
            if (!string.Equals(state.ResourcePath, resourcePath, StringComparison.Ordinal))
                continue;

            lock (state.SyncRoot)
                cursor = Math.Min(cursor, state.Cursor);
            found = true;
        }

        if (!found)
        {
            cursor = 0;
            return false;
        }

        return true;
    }

    private CoapRouteResult RemoveAndText(
        string resourcePath,
        string relationKey,
        StatusCode statusCode,
        string message)
    {
        RemoveObserver(resourcePath, relationKey);
        return CoapRouteResult.Text(statusCode, message);
    }

    private void RemoveObserver(string resourcePath, string relationKey)
        => _observers.TryRemove(StateKey(resourcePath, relationKey), out _);

    private static int ResolveContentFormat(SonnetMqMessage message)
    {
        if ((message.Headers.TryGetValue("contentType", out var contentType)
                || message.Headers.TryGetValue("content-type", out contentType)
                || message.Headers.TryGetValue("Content-Type", out contentType))
            && !string.IsNullOrWhiteSpace(contentType))
        {
            int parsed = MediaType.Parse(contentType);
            if (parsed != MediaType.Undefined)
                return parsed;
        }

        return MediaType.ApplicationOctetStream;
    }

    private static string BuildResourcePath(string db, string topic)
        => CoapRouteObserveRegistry.NormalizeResourcePath($"db/{db}/mq/{topic}");

    private static string BuildRelationKey(CoapRouteContext context)
    {
        string source = context.RemoteEndPoint?.ToString() ?? string.Empty;
        string token = context.Token is { Length: > 0 } ? Convert.ToHexString(context.Token) : string.Empty;
        return source + "#" + token;
    }

    private static string StateKey(string resourcePath, string relationKey)
        => resourcePath + "\n" + relationKey;

    private static int ToObserveValue(long offset)
        => (int)(Math.Max(0, offset) % ObserveModulo);

    private static bool IsValidTopicName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 128)
            return false;

        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_' or '-' or '.';
            if (!valid)
                return false;
        }

        return true;
    }

    private readonly record struct CoapError(StatusCode StatusCode, string Message);

    private sealed class ObserverState(
        string resourcePath,
        string relationKey,
        string db,
        string topic,
        string qualifiedTopic,
        long cursor)
    {
        public object SyncRoot { get; } = new();

        public string ResourcePath { get; } = resourcePath;

        public string RelationKey { get; } = relationKey;

        public string Db { get; } = db;

        public string Topic { get; } = topic;

        public string QualifiedTopic { get; } = qualifiedTopic;

        public DateTimeOffset CreatedAtUtc { get; } = DateTimeOffset.UtcNow;

        public long Cursor { get; set; } = cursor;
    }

    private sealed class TopicPump(string resourcePath, string qualifiedTopic)
    {
        public string ResourcePath { get; } = resourcePath;

        public string QualifiedTopic { get; } = qualifiedTopic;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
