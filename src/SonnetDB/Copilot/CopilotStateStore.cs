using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Tables;

namespace SonnetDB.Copilot;

/// <summary>
/// 在隐藏的 <c>__copilot__</c> 系统库中持久化 Copilot 会话、消息与调用用量。
/// </summary>
internal sealed class CopilotStateStore
{
    internal const string DatabaseName = "__copilot__";
    private const string ConversationsTable = "conversations";
    private const string MessagesTable = "messages";
    private const string UsageEventsTable = "usage_events";
    private const int MaxConversationsPerOwner = 50;
    private readonly Lock _sync = new();
    private readonly TableStore _conversations;
    private readonly TableStore _messages;
    private readonly TableStore _usageEvents;

    /// <summary>
    /// 打开系统库并确保所需关系系统表存在。
    /// </summary>
    /// <param name="registry">数据库注册表。</param>
    public CopilotStateStore(TsdbRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.TryCreate(DatabaseName, out var database);
        EnsureSchema(database.Tables);
        _conversations = database.Tables.Open(ConversationsTable);
        _messages = database.Tables.Open(MessagesTable);
        _usageEvents = database.Tables.Open(UsageEventsTable);
    }

    /// <summary>
    /// 根据当前认证主体生成稳定且不可逆的 owner 标识。
    /// </summary>
    /// <param name="context">HTTP 请求上下文。</param>
    /// <returns>用户 owner 或静态 Token owner。</returns>
    public static string ResolveOwner(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (BearerAuthMiddleware.GetUser(context) is AuthenticatedUser user)
            return "user:" + user.UserName.ToLowerInvariant();

        var authorization = context.Request.Headers.Authorization.ToString().Trim();
        var separator = authorization.IndexOf(' ');
        var credential = separator >= 0 ? authorization[(separator + 1)..].Trim() : authorization;
        if (credential.Length == 0)
            throw new InvalidOperationException("当前请求没有可用于隔离 Copilot 数据的认证主体。");

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(credential), hash);
        return "token:" + Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// 列出指定 owner 最近更新的会话。
    /// </summary>
    public IReadOnlyList<CopilotConversationResponse> ListConversations(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_sync)
        {
            return GetRowsByOwner(_conversations, "ix_conversations_owner", owner)
                .Select(ToConversation)
                .OrderByDescending(static item => item.UpdatedAtUtc)
                .Take(MaxConversationsPerOwner)
                .ToArray();
        }
    }

    /// <summary>
    /// 创建或更新会话。只有 owner 匹配时才能修改已有会话。
    /// </summary>
    public CopilotConversationResponse UpsertConversation(
        string owner,
        string? id,
        string? title,
        string? database,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var conversationId = NormalizeId(id) ?? "sndb_" + Guid.NewGuid().ToString("N");
        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;

        lock (_sync)
        {
            var existing = _conversations.GetByPrimaryKey([conversationId]);
            if (existing is not null && !string.Equals(existing.Values[1] as string, owner, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("无权访问该 Copilot 会话。");

            var existingTitle = existing?.Values[2] as string;
            var normalizedTitle = NormalizeTitle(title, existingTitle ?? "新会话");
            var normalizedDatabase = NormalizeDatabase(database) ?? existing?.Values[3] as string;
            var createdAt = existing?.Values[4] is DateTime existingCreatedAt ? existingCreatedAt : timestamp;
            var messageCount = existing?.Values[6] as long? ?? 0L;

            _conversations.Upsert([
                conversationId,
                owner,
                normalizedTitle,
                normalizedDatabase,
                createdAt,
                timestamp,
                messageCount,
            ]);

            TrimConversations(owner);
            return new CopilotConversationResponse(
                conversationId,
                normalizedTitle,
                normalizedDatabase,
                new DateTimeOffset(createdAt, TimeSpan.Zero),
                new DateTimeOffset(timestamp, TimeSpan.Zero),
                checked((int)messageCount));
        }
    }

    /// <summary>
    /// 追加一条会话消息并更新会话摘要。
    /// </summary>
    public CopilotMessageResponse AppendMessage(
        string owner,
        string conversationId,
        string role,
        string content,
        IReadOnlyList<CopilotCitation>? citations = null,
        string? model = null,
        long inputTokens = 0,
        long outputTokens = 0,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);
        var timestamp = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
        var normalizedRole = role.Trim().ToLowerInvariant();
        var messageId = "msg_" + Guid.NewGuid().ToString("N");

        lock (_sync)
        {
            var conversation = _conversations.GetByPrimaryKey([conversationId])
                ?? throw new KeyNotFoundException("Copilot 会话不存在。");
            if (!string.Equals(conversation.Values[1] as string, owner, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("无权访问该 Copilot 会话。");

            long existingMessageCount = conversation.Values[6] as long? ?? 0L;
            DateTime previousTimestamp = ToDateTimeOffset(conversation.Values[5]).UtcDateTime;
            if (existingMessageCount > 0
                && timestamp <= previousTimestamp
                && previousTimestamp.Ticks <= DateTime.MaxValue.Ticks - TimeSpan.TicksPerMillisecond)
            {
                // DATETIME 按 Unix 毫秒持久化；至少推进 1ms，避免重启后随机 GUID 主键打乱消息。
                timestamp = previousTimestamp.AddMilliseconds(1);
            }

            var citationList = citations is null ? null : citations.ToList();
            var citationsJson = citationList is { Count: > 0 }
                ? JsonSerializer.Serialize(citationList, ServerJsonContext.Default.ListCopilotCitation)
                : null;
            _messages.Insert([
                messageId,
                conversationId,
                owner,
                normalizedRole,
                content,
                citationsJson,
                NormalizeNullable(model),
                Math.Max(0, inputTokens),
                Math.Max(0, outputTokens),
                timestamp,
            ]);

            var nextTitle = conversation.Values[2] as string ?? "新会话";
            if (normalizedRole == "user" && string.Equals(nextTitle, "新会话", StringComparison.Ordinal))
                nextTitle = NormalizeTitle(content, nextTitle);
            var messageCount = existingMessageCount + 1;
            _conversations.Upsert([
                conversationId,
                owner,
                nextTitle,
                conversation.Values[3],
                conversation.Values[4],
                timestamp,
                messageCount,
            ]);

            return new CopilotMessageResponse(
                messageId,
                conversationId,
                normalizedRole,
                content,
                citationList,
                NormalizeNullable(model),
                Math.Max(0, inputTokens),
                Math.Max(0, outputTokens),
                new DateTimeOffset(timestamp, TimeSpan.Zero));
        }
    }

    /// <summary>
    /// 读取会话消息，结果按创建时间升序排列。
    /// </summary>
    public IReadOnlyList<CopilotMessageResponse> ListMessages(string owner, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_sync)
        {
            RequireOwnedConversation(owner, conversationId);
            var index = _messages.Schema.TryGetIndex("ix_messages_conversation_owner")
                ?? throw new InvalidOperationException("Copilot messages owner 索引缺失。");
            return _messages.GetByIndex(index, [conversationId, owner])
                .Select(ToMessage)
                .OrderBy(static item => item.CreatedAtUtc)
                .ToArray();
        }
    }

    /// <summary>
    /// 删除一个会话及其全部消息。
    /// </summary>
    public bool DeleteConversation(string owner, string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        lock (_sync)
        {
            var conversation = _conversations.GetByPrimaryKey([conversationId]);
            if (conversation is null)
                return false;
            if (!string.Equals(conversation.Values[1] as string, owner, StringComparison.Ordinal))
                throw new UnauthorizedAccessException("无权访问该 Copilot 会话。");

            DeleteMessages(owner, conversationId);
            return _conversations.DeleteByPrimaryKey([conversationId]);
        }
    }

    /// <summary>
    /// 清空指定 owner 的全部会话与消息。
    /// </summary>
    public int ClearConversations(string owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        lock (_sync)
        {
            var rows = GetRowsByOwner(_conversations, "ix_conversations_owner", owner);
            foreach (var row in rows)
            {
                var id = (string)row.Values[0]!;
                DeleteMessages(owner, id);
                _conversations.DeleteByPrimaryKey([id]);
            }
            return rows.Count;
        }
    }

    /// <summary>
    /// 记录一次 Copilot 调用的服务端用量事实。
    /// </summary>
    public void RecordUsage(string owner, CopilotUsageRecord usage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(usage);
        lock (_sync)
        {
            _usageEvents.Insert([
                "use_" + Guid.NewGuid().ToString("N"),
                owner,
                NormalizeId(usage.ConversationId),
                NormalizeNullable(usage.Model),
                NormalizeNullable(usage.Mode) ?? "sql_assist",
                Math.Max(0, usage.InputTokens),
                Math.Max(0, usage.OutputTokens),
                Math.Max(0, usage.TotalTokens),
                usage.EstimatedTokens,
                Math.Max(0, usage.ToolCalls),
                Math.Max(0, usage.DurationMilliseconds),
                usage.Succeeded,
                usage.CreatedAtUtc.UtcDateTime,
            ]);
        }
    }

    /// <summary>
    /// 聚合指定 owner 在时间窗口内的调用量与 token 摘要。
    /// </summary>
    public CopilotMetricsResponse GetMetrics(string owner, TimeSpan window, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        if (window <= TimeSpan.Zero || window > TimeSpan.FromDays(7))
            throw new ArgumentOutOfRangeException(nameof(window));

        var to = now ?? DateTimeOffset.UtcNow;
        var from = to - window;
        lock (_sync)
        {
            var rows = GetRowsByOwner(_usageEvents, "ix_usage_owner", owner)
                .Where(row => ToDateTimeOffset(row.Values[12]) >= from && ToDateTimeOffset(row.Values[12]) <= to)
                .ToArray();
            var byModel = rows
                .GroupBy(row => row.Values[3] as string ?? "unknown", StringComparer.Ordinal)
                .Select(group => new CopilotModelMetricsResponse(
                    group.Key,
                    group.LongCount(),
                    group.Sum(static row => (long)row.Values[7]!)))
                .OrderByDescending(static item => item.RequestCount)
                .ToArray();

            var requests = rows.LongLength;
            return new CopilotMetricsResponse(
                from,
                to,
                requests,
                rows.LongCount(static row => (bool)row.Values[11]!),
                rows.LongCount(static row => !(bool)row.Values[11]!),
                rows.Sum(static row => (long)row.Values[5]!),
                rows.Sum(static row => (long)row.Values[6]!),
                rows.Sum(static row => (long)row.Values[7]!),
                rows.Sum(static row => (long)row.Values[9]!),
                requests == 0 ? 0 : rows.Average(static row => (double)row.Values[10]!),
                rows.Any(static row => (bool)row.Values[8]!),
                byModel);
        }
    }

    private static void EnsureSchema(TableManager tables)
    {
        if (tables.Catalog.TryGet(ConversationsTable) is null)
        {
            tables.Create(TableSchema.Create(
                ConversationsTable,
                [
                    ("id", TableColumnType.String, false),
                    ("owner", TableColumnType.String, false),
                    ("title", TableColumnType.String, false),
                    ("database_name", TableColumnType.String, true),
                    ("created_at", TableColumnType.DateTime, false),
                    ("updated_at", TableColumnType.DateTime, false),
                    ("message_count", TableColumnType.Int64, false),
                ],
                ["id"],
                [new TableIndexDefinition("ix_conversations_owner", ["owner"], false)]));
        }

        if (tables.Catalog.TryGet(MessagesTable) is null)
        {
            tables.Create(TableSchema.Create(
                MessagesTable,
                [
                    ("id", TableColumnType.String, false),
                    ("conversation_id", TableColumnType.String, false),
                    ("owner", TableColumnType.String, false),
                    ("role", TableColumnType.String, false),
                    ("content", TableColumnType.String, false),
                    ("citations_json", TableColumnType.Json, true),
                    ("model", TableColumnType.String, true),
                    ("input_tokens", TableColumnType.Int64, false),
                    ("output_tokens", TableColumnType.Int64, false),
                    ("created_at", TableColumnType.DateTime, false),
                ],
                ["id"],
                [
                    new TableIndexDefinition("ix_messages_conversation_owner", ["conversation_id", "owner"], false),
                    new TableIndexDefinition("ix_messages_owner", ["owner"], false),
                ]));
        }

        if (tables.Catalog.TryGet(UsageEventsTable) is null)
        {
            tables.Create(TableSchema.Create(
                UsageEventsTable,
                [
                    ("id", TableColumnType.String, false),
                    ("owner", TableColumnType.String, false),
                    ("conversation_id", TableColumnType.String, true),
                    ("model", TableColumnType.String, true),
                    ("mode", TableColumnType.String, false),
                    ("input_tokens", TableColumnType.Int64, false),
                    ("output_tokens", TableColumnType.Int64, false),
                    ("total_tokens", TableColumnType.Int64, false),
                    ("estimated_tokens", TableColumnType.Boolean, false),
                    ("tool_calls", TableColumnType.Int64, false),
                    ("duration_ms", TableColumnType.Float64, false),
                    ("succeeded", TableColumnType.Boolean, false),
                    ("created_at", TableColumnType.DateTime, false),
                ],
                ["id"],
                [new TableIndexDefinition("ix_usage_owner", ["owner"], false)]));
        }
    }

    private void RequireOwnedConversation(string owner, string conversationId)
    {
        var row = _conversations.GetByPrimaryKey([conversationId])
            ?? throw new KeyNotFoundException("Copilot 会话不存在。");
        if (!string.Equals(row.Values[1] as string, owner, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("无权访问该 Copilot 会话。");
    }

    private void DeleteMessages(string owner, string conversationId)
    {
        var index = _messages.Schema.TryGetIndex("ix_messages_conversation_owner")
            ?? throw new InvalidOperationException("Copilot messages owner 索引缺失。");
        foreach (var row in _messages.GetByIndex(index, [conversationId, owner]))
            _messages.DeleteByPrimaryKey([row.Values[0]]);
    }

    private void TrimConversations(string owner)
    {
        var overflow = GetRowsByOwner(_conversations, "ix_conversations_owner", owner)
            .OrderByDescending(static row => ToDateTimeOffset(row.Values[5]))
            .Skip(MaxConversationsPerOwner)
            .ToArray();
        foreach (var row in overflow)
            DeleteConversation(owner, (string)row.Values[0]!);
    }

    private static IReadOnlyList<TableRow> GetRowsByOwner(TableStore store, string indexName, string owner)
    {
        var index = store.Schema.TryGetIndex(indexName)
            ?? throw new InvalidOperationException($"Copilot 系统表索引 '{indexName}' 缺失。");
        return store.GetByIndex(index, [owner]);
    }

    private static CopilotConversationResponse ToConversation(TableRow row)
        => new(
            (string)row.Values[0]!,
            (string)row.Values[2]!,
            row.Values[3] as string,
            ToDateTimeOffset(row.Values[4]),
            ToDateTimeOffset(row.Values[5]),
            checked((int)(long)row.Values[6]!));

    private static CopilotMessageResponse ToMessage(TableRow row)
    {
        List<CopilotCitation>? citations = null;
        if (row.Values[5] is string json && json.Length > 0)
            citations = JsonSerializer.Deserialize(json, ServerJsonContext.Default.ListCopilotCitation);
        return new CopilotMessageResponse(
            (string)row.Values[0]!,
            (string)row.Values[1]!,
            (string)row.Values[3]!,
            (string)row.Values[4]!,
            citations,
            row.Values[6] as string,
            (long)row.Values[7]!,
            (long)row.Values[8]!,
            ToDateTimeOffset(row.Values[9]));
    }

    private static DateTimeOffset ToDateTimeOffset(object? value)
        => value switch
        {
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            _ => throw new InvalidDataException("Copilot 系统表包含无效时间值。"),
        };

    private static string NormalizeTitle(string? value, string fallback)
    {
        var title = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return title.Length <= 64 ? title : title[..64];
    }

    private static string? NormalizeDatabase(string? value)
    {
        var normalized = NormalizeNullable(value);
        return normalized is not null && TsdbRegistry.IsValidName(normalized) ? normalized : null;
    }

    private static string? NormalizeId(string? value)
    {
        var normalized = NormalizeNullable(value);
        if (normalized is null)
            return null;
        if (normalized.Length > 128 || normalized.Any(static ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-')))
            throw new ArgumentException("Copilot 会话 ID 仅允许字母、数字、下划线和连字符，长度不超过 128。", nameof(value));
        return normalized;
    }

    private static string? NormalizeNullable(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 单次 Copilot 调用的持久化用量记录。
/// </summary>
internal sealed record CopilotUsageRecord(
    string? ConversationId,
    string? Model,
    string? Mode,
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    bool EstimatedTokens,
    long ToolCalls,
    double DurationMilliseconds,
    bool Succeeded,
    DateTimeOffset CreatedAtUtc);
