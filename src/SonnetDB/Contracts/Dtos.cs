using System.Text.Json.Serialization;

namespace SonnetDB.Contracts;

/// <summary>
/// 单条 SQL 提交请求体。
/// </summary>
/// <param name="Sql">要执行的 SQL 文本。</param>
/// <param name="Parameters">可选命名参数集合（支持基础标量：bool/long/double/string/null）。</param>
public sealed record SqlRequest(string Sql, IReadOnlyDictionary<string, JsonElementValue>? Parameters = null);

/// <summary>
/// 批量 SQL 提交请求体。所有语句按顺序、单事务语义执行。
/// </summary>
/// <param name="Statements">SQL 语句列表。</param>
public sealed record SqlBatchRequest(IReadOnlyList<SqlRequest> Statements);

/// <summary>
/// 简化的标量参数包装。仅支持时序场景常用的几个 JSON 类型，避免在 AOT 下处理任意 <c>JsonElement</c>。
/// </summary>
/// <param name="Kind">参数类型。</param>
/// <param name="StringValue">当 <see cref="Kind"/> 为 <see cref="ScalarKind.String"/> 时使用。</param>
/// <param name="IntegerValue">当 <see cref="Kind"/> 为 <see cref="ScalarKind.Integer"/> 时使用。</param>
/// <param name="DoubleValue">当 <see cref="Kind"/> 为 <see cref="ScalarKind.Double"/> 时使用。</param>
/// <param name="BooleanValue">当 <see cref="Kind"/> 为 <see cref="ScalarKind.Boolean"/> 时使用。</param>
public sealed record JsonElementValue(
    ScalarKind Kind,
    string? StringValue = null,
    long? IntegerValue = null,
    double? DoubleValue = null,
    bool? BooleanValue = null);

/// <summary>
/// 参数标量类型枚举。
/// </summary>
public enum ScalarKind
{
    /// <summary>JSON null。</summary>
    Null = 0,
    /// <summary>JSON 字符串。</summary>
    String,
    /// <summary>整数（fits in long）。</summary>
    Integer,
    /// <summary>双精度浮点。</summary>
    Double,
    /// <summary>布尔。</summary>
    Boolean,
}

/// <summary>
/// 通用错误响应。
/// </summary>
/// <param name="Error">错误标识，例如 <c>unauthorized</c> / <c>forbidden</c> / <c>db_not_found</c> / <c>sql_error</c>。</param>
/// <param name="Message">人类可读的描述。</param>
public sealed record ErrorResponse(string Error, string Message);

/// <summary>
/// SQL 流式响应的元信息行（ndjson 第一行）。
/// </summary>
/// <param name="Type">固定为 <c>"meta"</c>。</param>
/// <param name="Columns">列名列表。</param>
public sealed record ResultMeta(string Type, IReadOnlyList<string> Columns);

/// <summary>
/// SQL 流式响应的尾部统计（ndjson 最后一行）。
/// </summary>
/// <param name="Type">固定为 <c>"end"</c>。</param>
/// <param name="RowCount">本次结果集行数。</param>
/// <param name="RecordsAffected">受影响的行数（非 SELECT 时有效；SELECT 始终为 -1）。</param>
/// <param name="ElapsedMilliseconds">服务端执行耗时（毫秒）。</param>
public sealed record ResultEnd(string Type, long RowCount, int RecordsAffected, double ElapsedMilliseconds);

/// <summary>
/// CREATE DATABASE 请求体。
/// </summary>
/// <param name="Name">数据库名（仅允许 <c>[a-zA-Z0-9_-]</c>，长度 1–64）。</param>
public sealed record CreateDatabaseRequest(string Name);

/// <summary>
/// 数据库管理操作的统一返回体。
/// </summary>
/// <param name="Database">数据库名。</param>
/// <param name="Status">操作结果（<c>"created"</c> / <c>"dropped"</c> / <c>"exists"</c>）。</param>
public sealed record DatabaseOperationResponse(string Database, string Status);

/// <summary>
/// <c>GET /v1/db</c> 列表响应。
/// </summary>
/// <param name="Databases">已注册的数据库名列表。</param>
public sealed record DatabaseListResponse(IReadOnlyList<string> Databases);

/// <summary>
/// 健康检查响应。
/// </summary>
/// <param name="Status">固定为 <c>"ok"</c>。</param>
/// <param name="Databases">已加载的数据库数量。</param>
/// <param name="UptimeSeconds">服务端运行秒数。</param>
/// <param name="CopilotEnabled">Copilot 子系统是否启用。</param>
/// <param name="CopilotReady">Copilot 子系统是否已满足基础就绪条件。</param>
public sealed record HealthResponse(string Status, int Databases, double UptimeSeconds, bool CopilotEnabled, bool CopilotReady);

/// <summary>
/// <c>POST /v1/db/{db}/measurements/{m}/{lp|json|bulk}</c> 批量入库的成功响应。
/// </summary>
/// <param name="WrittenRows">实际写入数据库的行数。</param>
/// <param name="SkippedRows"><c>onerror=skip</c> 模式下跳过的非法行数；FailFast 模式始终为 0。</param>
/// <param name="ElapsedMilliseconds">服务端处理耗时（毫秒）。</param>
public sealed record BulkIngestResponse(long WrittenRows, long SkippedRows, double ElapsedMilliseconds);

/// <summary>
/// KV 单 key 读取请求。
/// </summary>
/// <param name="Key">完整 key。调用方负责命名空间前缀。</param>
public sealed record KvGetRequest(string Key);

/// <summary>
/// KV 单 key 写入请求。
/// </summary>
/// <param name="Key">完整 key。调用方负责命名空间前缀。</param>
/// <param name="Value">二进制 value。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
public sealed record KvSetRequest(string Key, byte[] Value, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 单 key 删除请求。
/// </summary>
/// <param name="Key">完整 key。调用方负责命名空间前缀。</param>
public sealed record KvDeleteRequest(string Key);

/// <summary>
/// KV 批量读取请求。
/// </summary>
/// <param name="Keys">完整 key 列表。调用方负责命名空间前缀。</param>
public sealed record KvGetManyRequest(IReadOnlyList<string> Keys);

/// <summary>
/// KV 批量写入请求。
/// </summary>
/// <param name="Entries">完整 key/value 列表。调用方负责命名空间前缀。</param>
/// <param name="ExpiresAtUtc">本批次共享 UTC 过期时间；为空表示永不过期。</param>
public sealed record KvSetManyRequest(IReadOnlyList<KvSetManyEntry> Entries, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 批量写入条目。
/// </summary>
public sealed record KvSetManyEntry(string Key, byte[] Value);

/// <summary>
/// KV 批量删除请求。
/// </summary>
/// <param name="Keys">完整 key 列表。调用方负责命名空间前缀。</param>
public sealed record KvDeleteManyRequest(IReadOnlyList<string> Keys);

/// <summary>
/// KV 前缀扫描/删除请求。
/// </summary>
/// <param name="Prefix">完整前缀。调用方负责命名空间前缀。</param>
/// <param name="Limit">最多处理数量。</param>
public sealed record KvPrefixRequest(string Prefix, int? Limit);

/// <summary>
/// KV 清理过期 key 请求。
/// </summary>
/// <param name="Limit">最多清理数量。</param>
public sealed record KvCleanExpiredRequest(int? Limit);

/// <summary>
/// KV 原子自增 / 自减请求。
/// </summary>
/// <param name="Key">完整 key。调用方负责命名空间前缀。</param>
/// <param name="Delta">增量；INCR 默认为 1，DECR 表示减少量。</param>
public sealed record KvIncrementRequest(string Key, long Delta = 1);

/// <summary>
/// KV 比较并交换请求。
/// </summary>
/// <param name="Key">完整 key。调用方负责命名空间前缀。</param>
/// <param name="ExpectedVersion">期望版本；0 表示 key 不存在。</param>
/// <param name="Value">比较成功后写入的二进制 value。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间；为空表示永不过期。</param>
public sealed record KvCasRequest(string Key, long ExpectedVersion, byte[] Value, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 绝对过期时间请求。
/// </summary>
/// <param name="Key">完整 key。调用方负责命名空间前缀。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间。</param>
public sealed record KvExpireRequest(string Key, DateTimeOffset ExpiresAtUtc);

/// <summary>
/// KV 扫描返回的一条记录。
/// </summary>
public sealed record KvEntryResponse(string Key, byte[] Value, long Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 单 key 读取响应。
/// </summary>
public sealed record KvValueResponse(bool Found, byte[]? Value, long? Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 批量读取响应。
/// </summary>
public sealed record KvGetManyResponse(IReadOnlyList<KvValueItemResponse> Values);

/// <summary>
/// KV 批量读取中的单 key 结果。
/// </summary>
public sealed record KvValueItemResponse(string Key, bool Found, byte[]? Value, long? Version, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 写入响应。
/// </summary>
public sealed record KvSetResponse(long Version);

/// <summary>
/// KV 批量写入响应。
/// </summary>
public sealed record KvSetManyResponse(IReadOnlyDictionary<string, long> Versions);

/// <summary>
/// KV 删除/清理响应。
/// </summary>
public sealed record KvDeleteResponse(int Removed);

/// <summary>
/// KV 原子自增 / 自减响应。
/// </summary>
public sealed record KvIncrementResponse(long Value, long Version);

/// <summary>
/// KV 比较并交换响应。
/// </summary>
public sealed record KvCasResponse(bool Succeeded, long CurrentVersion, long? NewVersion);

/// <summary>
/// KV 布尔操作响应。
/// </summary>
public sealed record KvBooleanResponse(bool Succeeded);

/// <summary>
/// KV TTL 查询响应。
/// </summary>
public sealed record KvTtlResponse(long Milliseconds, DateTimeOffset? ExpiresAtUtc);

/// <summary>
/// KV 前缀扫描响应。
/// </summary>
public sealed record KvScanResponse(IReadOnlyList<KvEntryResponse> Entries);

/// <summary>
/// KV 过期统计响应。
/// </summary>
public sealed record KvStatsResponse(
    int TotalKeys,
    int ActiveKeys,
    int ExpiredKeys,
    int ExpiringKeys,
    DateTimeOffset? NearestExpiresAtUtc);

/// <summary>
/// MQ 发布请求。
/// </summary>
/// <param name="Payload">消息体。</param>
/// <param name="Headers">可选消息头。</param>
public sealed record MqPublishRequest(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// MQ 发布响应。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="Offset">写入后的消息 offset。</param>
public sealed record MqPublishResponse(string Topic, long Offset);

/// <summary>
/// MQ 批量发布单条消息。
/// </summary>
/// <param name="Payload">消息体。</param>
/// <param name="Headers">可选消息头。</param>
public sealed record MqPublishBatchEntry(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

/// <summary>
/// MQ 批量发布请求：同一 topic 下的多条消息共享一次刷盘。
/// </summary>
/// <param name="Messages">消息集合，按顺序分配连续 offset。</param>
public sealed record MqPublishBatchRequest(IReadOnlyList<MqPublishBatchEntry> Messages);

/// <summary>
/// MQ 批量发布响应。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="Offsets">按输入顺序分配的 offset。</param>
public sealed record MqPublishBatchResponse(string Topic, IReadOnlyList<long> Offsets);

/// <summary>
/// MQ 拉取请求。
/// </summary>
/// <param name="ConsumerGroup">消费者组名称。</param>
/// <param name="MaxCount">最多返回消息数量。</param>
public sealed record MqPullRequest(string ConsumerGroup, int? MaxCount = null);

/// <summary>
/// MQ 消息响应。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="Offset">消息 offset。</param>
/// <param name="TimestampUtc">服务端写入时间。</param>
/// <param name="Headers">消息头。</param>
/// <param name="Payload">消息体。</param>
public sealed record MqMessageResponse(
    string Topic,
    long Offset,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);

/// <summary>
/// MQ 拉取响应。
/// </summary>
/// <param name="Messages">消息列表。</param>
public sealed record MqPullResponse(IReadOnlyList<MqMessageResponse> Messages);

/// <summary>
/// MQ 确认请求。
/// </summary>
/// <param name="ConsumerGroup">消费者组名称。</param>
/// <param name="Offset">已处理完成的最后一条 offset。</param>
public sealed record MqAckRequest(string ConsumerGroup, long Offset);

/// <summary>
/// MQ 确认响应。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="ConsumerGroup">消费者组名称。</param>
/// <param name="NextOffset">消费者组下一条待消费 offset。</param>
public sealed record MqAckResponse(string Topic, string ConsumerGroup, long NextOffset);

/// <summary>
/// MQ Topic 统计响应。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="MessageCount">已追加消息数量。</param>
/// <param name="NextOffset">下一条消息 offset。</param>
/// <param name="ConsumerOffsets">消费者组 offset。</param>
public sealed record MqStatsResponse(
    string Topic,
    long MessageCount,
    long NextOffset,
    IReadOnlyDictionary<string, long> ConsumerOffsets);

// ---- M29 A #245 多模型只读管理契约 ----

/// <summary>
/// KV keyspace 列表响应。
/// </summary>
/// <param name="Keyspaces">按名称升序排列的 keyspace 名称。</param>
public sealed record KvKeyspaceListResponse(IReadOnlyList<string> Keyspaces);

/// <summary>
/// KV 游标扫描请求。
/// </summary>
/// <param name="Prefix">key 前缀；为空时扫描全部 key。</param>
/// <param name="Cursor">上一页返回的 <see cref="KvScanCursorResponse.NextCursor"/>；首页为空。</param>
/// <param name="Limit">单页最大返回行数；默认 100，上限 1000。</param>
public sealed record KvScanCursorRequest(string? Prefix = null, string? Cursor = null, int? Limit = null);

/// <summary>
/// KV 游标扫描响应。
/// </summary>
/// <param name="Entries">本页记录，按 key 字节序升序。</param>
/// <param name="NextCursor">下一页游标；无更多数据时为 null。</param>
/// <param name="HasMore">是否可能还有下一页。</param>
public sealed record KvScanCursorResponse(
    IReadOnlyList<KvEntryResponse> Entries,
    string? NextCursor,
    bool HasMore);

/// <summary>
/// 向量索引统计响应。
/// </summary>
/// <param name="Indexes">当前数据库声明的全部向量索引。</param>
public sealed record VectorIndexStatResponse(IReadOnlyList<VectorIndexStat> Indexes);

/// <summary>
/// 单个向量索引的声明级统计。
/// </summary>
/// <param name="Measurement">所属 measurement。</param>
/// <param name="Column">向量列名。</param>
/// <param name="Kind">索引类型（Hnsw / IvfFlat / IvfPq / Vamana）。</param>
/// <param name="Dimension">向量维度；未声明时为 null。</param>
/// <param name="Metric">距离度量。当前引擎构建时固定为 cosine。</param>
/// <param name="Params">图参数（如 HNSW m / ef）。</param>
public sealed record VectorIndexStat(
    string Measurement,
    string Column,
    string Kind,
    int? Dimension,
    string Metric,
    IReadOnlyList<KeyValueInfo> Params);

/// <summary>
/// 向量检索预览请求。走既有 <c>knn(...)</c> data-plane，不新增查询语义。
/// </summary>
/// <param name="Measurement">目标 measurement。</param>
/// <param name="Column">向量列名。</param>
/// <param name="Query">查询向量。</param>
/// <param name="TopK">返回前 K 条；默认 10，上限 100。</param>
public sealed record VectorSearchPreviewRequest(
    string Measurement,
    string Column,
    float[] Query,
    int? TopK = null);

/// <summary>
/// 向量检索预览响应。
/// </summary>
/// <param name="Hits">命中列表，按距离升序。</param>
public sealed record VectorSearchPreviewResponse(IReadOnlyList<VectorSearchPreviewHit> Hits);

/// <summary>
/// 向量检索预览命中项。
/// </summary>
/// <param name="TimestampUtc">命中点时间戳（UTC ticks）。</param>
/// <param name="Distance">与查询向量的距离。</param>
public sealed record VectorSearchPreviewHit(long TimestampUtc, double Distance);

/// <summary>
/// 全文索引统计响应。
/// </summary>
/// <param name="Indexes">当前数据库全部文档集合上的全文索引。</param>
public sealed record FullTextIndexStatResponse(IReadOnlyList<FullTextIndexStat> Indexes);

/// <summary>
/// 单个全文索引统计。
/// </summary>
/// <param name="Collection">所属文档集合。</param>
/// <param name="Name">索引名。</param>
/// <param name="Fields">被索引字段。</param>
/// <param name="Tokenizer">分词器 / analyzer 名称。</param>
/// <param name="DocumentCount">当前可见文档数。</param>
public sealed record FullTextIndexStat(
    string Collection,
    string Name,
    IReadOnlyList<string> Fields,
    string Tokenizer,
    int DocumentCount);

/// <summary>
/// 全文检索预览请求（BM25）。走既有全文检索 data-plane，不新增查询语义。
/// </summary>
/// <param name="Collection">文档集合。</param>
/// <param name="Index">全文索引名。</param>
/// <param name="Field">检索字段，或 <c>*</c>。</param>
/// <param name="Query">查询文本。</param>
/// <param name="TopK">返回前 K 条；默认 10，上限 100。</param>
/// <param name="Mode">检索模式：exact（默认）/ fuzzy。</param>
public sealed record FullTextSearchPreviewRequest(
    string Collection,
    string Index,
    string Field,
    string Query,
    int? TopK = null,
    string? Mode = null);

/// <summary>
/// 全文检索预览响应。
/// </summary>
/// <param name="Hits">命中列表，按 BM25 分数降序。</param>
public sealed record FullTextSearchPreviewResponse(IReadOnlyList<FullTextSearchPreviewHit> Hits);

/// <summary>
/// 全文检索预览命中项。
/// </summary>
/// <param name="DocumentId">文档 id。</param>
/// <param name="Score">BM25 相关性分数。</param>
public sealed record FullTextSearchPreviewHit(string DocumentId, double Score);

/// <summary>
/// 分词器 analyze 请求。
/// </summary>
/// <param name="Tokenizer">分词器名称：unicode / cjk / jieba。</param>
/// <param name="Text">待分词文本。</param>
public sealed record FullTextAnalyzeRequest(string Tokenizer, string Text);

/// <summary>
/// 分词器 analyze 响应。
/// </summary>
/// <param name="Tokens">切词结果。</param>
public sealed record FullTextAnalyzeResponse(IReadOnlyList<FullTextTokenInfo> Tokens);

/// <summary>
/// 单个切词结果。
/// </summary>
/// <param name="Text">词元文本。</param>
/// <param name="StartOffset">在原文中的起始字符偏移。</param>
/// <param name="EndOffset">在原文中的结束字符偏移。</param>
/// <param name="PositionIncrement">相对上一个词元的位置增量。</param>
public sealed record FullTextTokenInfo(
    string Text,
    int StartOffset,
    int EndOffset,
    int PositionIncrement);

/// <summary>
/// MQ topic 列表响应。
/// </summary>
/// <param name="Topics">当前数据库下的全部 topic。</param>
public sealed record MqTopicListResponse(IReadOnlyList<MqTopicInfo> Topics);

/// <summary>
/// 单个 MQ topic 概览。
/// </summary>
/// <param name="Topic">Topic 名称（不含数据库前缀）。</param>
/// <param name="MessageCount">当前保留的消息数量。</param>
/// <param name="NextOffset">下一条消息 offset（高水位）。</param>
public sealed record MqTopicInfo(string Topic, long MessageCount, long NextOffset);

/// <summary>
/// MQ topic offset / 消费 lag 响应。
/// </summary>
/// <param name="Topic">Topic 名称。</param>
/// <param name="NextOffset">下一条消息 offset（高水位）。</param>
/// <param name="Consumers">各消费者组的已提交 offset 与 lag。</param>
public sealed record MqOffsetsResponse(
    string Topic,
    long NextOffset,
    IReadOnlyList<MqConsumerLag> Consumers);

/// <summary>
/// 单个消费者组的 offset / lag。
/// </summary>
/// <param name="ConsumerGroup">消费者组名称。</param>
/// <param name="CommittedOffset">已提交（下一条待消费）offset。</param>
/// <param name="Lag">落后高水位的消息数（NextOffset − CommittedOffset）。</param>
public sealed record MqConsumerLag(string ConsumerGroup, long CommittedOffset, long Lag);

/// <summary>
/// MQ 按 offset 浏览请求。只读，不改变任何消费者组状态。
/// </summary>
/// <param name="FromOffset">起始 offset；默认从 0 开始。</param>
/// <param name="MaxCount">最多返回消息数量；默认 100，上限 1000。</param>
public sealed record MqBrowseRequest(long? FromOffset = null, int? MaxCount = null);

/// <summary>
/// MQ 按 offset 浏览响应。
/// </summary>
/// <param name="Messages">消息列表，按 offset 升序。</param>
public sealed record MqBrowseResponse(IReadOnlyList<MqMessageResponse> Messages);

/// <summary>
/// <c>POST /v1/auth/login</c> 请求体。
/// </summary>
/// <param name="Username">用户名。</param>
/// <param name="Password">明文密码。</param>
public sealed record LoginRequest(string Username, string Password);

/// <summary>
/// 登录成功响应：返回新颁发的 API token。token 明文仅在此处返回一次，
/// 服务端只持久化其 SHA-256 哈希。
/// </summary>
/// <param name="Username">用户名。</param>
/// <param name="Token">Bearer token 明文。</param>
/// <param name="TokenId">token 标识符（如 <c>tok_abcdef</c>）。</param>
/// <param name="IsSuperuser">是否为超级用户。</param>
public sealed record LoginResponse(string Username, string Token, string TokenId, bool IsSuperuser);

/// <summary>
/// <c>GET /v1/setup/status</c> 返回的安装状态。
/// </summary>
/// <param name="NeedsSetup">是否需要首次安装。</param>
/// <param name="SuggestedServerId">首次安装时推荐的服务器 ID。</param>
/// <param name="ServerId">当前已配置的服务器 ID。</param>
/// <param name="Organization">当前已配置的组织名称。</param>
/// <param name="UserCount">当前用户数。</param>
/// <param name="DatabaseCount">当前数据库数。</param>
public sealed record SetupStatusResponse(
    bool NeedsSetup,
    string SuggestedServerId,
    string? ServerId,
    string? Organization,
    int UserCount,
    int DatabaseCount);

/// <summary>
/// <c>POST /v1/setup/initialize</c> 的请求体。
/// </summary>
/// <param name="ServerId">当前服务器 ID。</param>
/// <param name="Organization">所属组织名称。</param>
/// <param name="Username">初始管理员用户名。</param>
/// <param name="Password">初始管理员密码。</param>
/// <param name="BearerToken">初始管理员 Bearer Token 明文。</param>
public sealed record SetupInitializeRequest(
    string ServerId,
    string Organization,
    string Username,
    string Password,
    string BearerToken);

/// <summary>
/// 首次安装成功响应。返回服务器身份信息以及可直接登录的管理员凭据。
/// </summary>
/// <param name="ServerId">当前服务器 ID。</param>
/// <param name="Organization">所属组织名称。</param>
/// <param name="Username">管理员用户名。</param>
/// <param name="Token">管理员 Bearer Token 明文。</param>
/// <param name="TokenId">管理员 Bearer Token 的 token id。</param>
/// <param name="IsSuperuser">是否超级用户。</param>
public sealed record SetupInitializeResponse(
    string ServerId,
    string Organization,
    string Username,
    string Token,
    string TokenId,
    bool IsSuperuser);
