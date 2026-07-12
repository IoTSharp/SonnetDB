using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;

namespace SonnetDB.Diagnostics;

/// <summary>
/// 慢查询采集入口：统一完成阈值判断、环形缓冲、Activity 事件、结构化日志与 SSE 广播。
/// </summary>
internal sealed partial class SlowQueryDiagnostics
{
    private const int SqlMaxLength = 1024;

    private readonly IOptions<ServerOptions> _options;
    private readonly SlowQueryRing _ring;
    private readonly EventBroadcaster _broadcaster;
    private readonly ILogger<SlowQueryDiagnostics> _logger;

    /// <summary>
    /// 创建慢查询诊断收集器。
    /// </summary>
    public SlowQueryDiagnostics(
        IOptions<ServerOptions> options,
        SlowQueryRing ring,
        EventBroadcaster broadcaster,
        ILogger<SlowQueryDiagnostics> logger)
    {
        _options = options;
        _ring = ring;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    /// <summary>当前生效的慢查询配置。</summary>
    public SlowQueryLogOptions Options => _options.Value.Observability.SlowQueryLog;

    /// <summary>慢查询环形缓冲。</summary>
    public SlowQueryRing Ring => _ring;

    /// <summary>
    /// 在 SQL 完成后记录达到阈值的诊断数据。
    /// </summary>
    /// <param name="database">数据库名。</param>
    /// <param name="sql">原始 SQL。</param>
    /// <param name="elapsedMs">执行耗时（毫秒）。</param>
    /// <param name="rowCount">返回行数。</param>
    /// <param name="recordsAffected">受影响行数。</param>
    /// <param name="failed">是否失败。</param>
    public void Record(
        string database,
        string sql,
        double elapsedMs,
        long rowCount,
        int recordsAffected,
        bool failed)
    {
        var options = Options;
        if (!options.Enabled || options.ThresholdMs < 0)
            return;
        if (options.ThresholdMs > 0 && elapsedMs < options.ThresholdMs)
            return;

        var truncatedSql = sql.Length > SqlMaxLength ? sql[..SqlMaxLength] : sql;
        var normalizedSql = SqlFingerprint.Normalize(sql);
        var fingerprint = SqlFingerprint.Compute(normalizedSql);
        var severity = GetSeverity(options, elapsedMs);
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entry = new SlowQueryDiagnosticEntry(
            timestampMs,
            database,
            truncatedSql,
            normalizedSql,
            fingerprint,
            elapsedMs,
            rowCount,
            recordsAffected,
            failed,
            severity);

        _ring.Add(entry);
        AddActivityEvent(entry);
        WriteStructuredLog(entry);

        if (_broadcaster.SubscriberCount > 0)
        {
            var payload = new SlowQueryEvent(
                database,
                truncatedSql,
                elapsedMs,
                rowCount,
                recordsAffected,
                failed,
                severity);
            _broadcaster.Publish(ServerEventFactory.SlowQuery(payload));
        }
    }

    private static string GetSeverity(SlowQueryLogOptions options, double elapsedMs)
    {
        if (options.CriticalThresholdMs > 0 && elapsedMs >= options.CriticalThresholdMs)
            return SlowQuerySeverity.Critical;
        if (options.WarningThresholdMs > 0 && elapsedMs >= options.WarningThresholdMs)
            return SlowQuerySeverity.Warning;
        return SlowQuerySeverity.Slow;
    }

    private static void AddActivityEvent(SlowQueryDiagnosticEntry entry)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;

        var tags = new ActivityTagsCollection
        {
            ["db.system"] = "sonnetdb",
            ["sonnetdb.database"] = entry.Database,
            ["sonnetdb.query.fingerprint"] = entry.Fingerprint,
            ["sonnetdb.query.duration_ms"] = entry.ElapsedMs,
            ["sonnetdb.query.severity"] = entry.Severity,
            ["sonnetdb.query.failed"] = entry.Failed,
        };
        activity.AddEvent(new ActivityEvent("slow_query", tags: tags));
    }

    private void WriteStructuredLog(SlowQueryDiagnosticEntry entry)
    {
        if (entry.Severity == SlowQuerySeverity.Critical)
        {
            LogCritical(
                _logger,
                entry.Database,
                entry.Fingerprint,
                entry.ElapsedMs,
                entry.Failed,
                entry.NormalizedSql);
            return;
        }

        LogSlow(
            _logger,
            entry.Database,
            entry.Fingerprint,
            entry.ElapsedMs,
            entry.Severity,
            entry.Failed,
            entry.NormalizedSql);
    }

    [LoggerMessage(
        EventId = 2001,
        EventName = "Query.Slow",
        Level = LogLevel.Warning,
        Message = "慢查询 database={Database} fingerprint={Fingerprint} elapsed_ms={ElapsedMs} severity={Severity} failed={Failed} normalized_sql={NormalizedSql}")]
    private static partial void LogSlow(
        ILogger logger,
        string database,
        string fingerprint,
        double elapsedMs,
        string severity,
        bool failed,
        string normalizedSql);

    [LoggerMessage(
        EventId = 2002,
        EventName = "Query.Critical",
        Level = LogLevel.Error,
        Message = "严重慢查询 database={Database} fingerprint={Fingerprint} elapsed_ms={ElapsedMs} failed={Failed} normalized_sql={NormalizedSql}")]
    private static partial void LogCritical(
        ILogger logger,
        string database,
        string fingerprint,
        double elapsedMs,
        bool failed,
        string normalizedSql);
}
