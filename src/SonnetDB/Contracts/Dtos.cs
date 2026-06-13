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
