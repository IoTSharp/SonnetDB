namespace SonnetDB.Contracts;

/// <summary>
/// 推送给 SSE 订阅者的统一事件包装。<see cref="Type"/> 决定 <see cref="Data"/> 的具体形状。
/// </summary>
/// <param name="Type">事件类型：<c>metrics</c> / <c>slow_query</c> / <c>db</c>。</param>
/// <param name="Data">JSON 序列化后的事件数据。</param>
/// <param name="TimestampMs">事件产生时间（Unix 毫秒，UTC）。</param>
public sealed record ServerEvent(string Type, string Data, long TimestampMs)
{
    /// <summary>事件 channel：周期性指标快照。</summary>
    public const string ChannelMetrics = "metrics";

    /// <summary>事件 channel：慢查询。</summary>
    public const string ChannelSlowQuery = "slow_query";

    /// <summary>事件 channel：数据库 DDL（CREATE/DROP）。</summary>
    public const string ChannelDatabase = "db";
}

/// <summary>
/// 周期性指标快照（与 Prometheus 文本相同口径，但 JSON 友好，便于前端图表/卡片直接绑定）。
/// </summary>
/// <param name="UptimeSeconds">服务运行秒数。</param>
/// <param name="Databases">已注册的数据库数量。</param>
/// <param name="SqlRequests">累计 SQL 请求数。</param>
/// <param name="SqlErrors">累计 SQL 错误数。</param>
/// <param name="RowsInserted">累计 INSERT 行数。</param>
/// <param name="RowsReturned">累计 SELECT 返回行数。</param>
/// <param name="SubscriberCount">当前 SSE 订阅者数量。</param>
/// <param name="PerDatabaseSegments">各数据库当前活跃 segment 数。</param>
public sealed record MetricsSnapshotEvent(
    double UptimeSeconds,
    int Databases,
    long SqlRequests,
    long SqlErrors,
    long RowsInserted,
    long RowsReturned,
    int SubscriberCount,
    IReadOnlyDictionary<string, int> PerDatabaseSegments);

/// <summary>
/// 慢查询事件：单条 SQL 执行耗时超过阈值时上报。
/// </summary>
/// <param name="Database">数据库名（控制面 SQL 时为 <c>"__control"</c>）。</param>
/// <param name="Sql">原始 SQL 文本（已截断到最多 1024 字符）。</param>
/// <param name="ElapsedMs">服务端执行耗时（毫秒）。</param>
/// <param name="RowCount">返回行数（非 SELECT 为 0）。</param>
/// <param name="RecordsAffected">受影响行数（非 SELECT 时有效；SELECT 为 -1）。</param>
/// <param name="Failed">是否因异常失败。</param>
/// <param name="Severity">慢查询等级：<c>slow</c> / <c>warning</c> / <c>critical</c>。</param>
public sealed record SlowQueryEvent(
    string Database,
    string Sql,
    double ElapsedMs,
    long RowCount,
    int RecordsAffected,
    bool Failed,
    string Severity);

/// <summary>
/// 慢查询事件严重等级。
/// </summary>
public static class SlowQuerySeverity
{
    /// <summary>达到基础慢查询阈值。</summary>
    public const string Slow = "slow";

    /// <summary>达到警告级阈值。</summary>
    public const string Warning = "warning";

    /// <summary>达到严重级阈值。</summary>
    public const string Critical = "critical";
}

/// <summary>
/// 数据库生命周期事件（CREATE / DROP）。
/// </summary>
/// <param name="Database">数据库名。</param>
/// <param name="Action">操作：<c>created</c> / <c>dropped</c>。</param>
public sealed record DatabaseEvent(string Database, string Action)
{
    /// <summary>CREATE DATABASE 后广播。</summary>
    public const string ActionCreated = "created";

    /// <summary>DROP DATABASE 后广播。</summary>
    public const string ActionDropped = "dropped";
}
