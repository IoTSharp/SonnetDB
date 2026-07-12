using System.Diagnostics;
using SonnetDB.Engine;

namespace SonnetDB.Hosting;

/// <summary>
/// 进程级运行时统计：服务启动时刻、累计请求数、Flush/Compaction 计数等，用于 <c>/metrics</c> 暴露。
/// </summary>
public sealed class ServerMetrics
{
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private long _sqlRequests;
    private long _sqlErrors;
    private long _rowsInserted;
    private long _rowsReturned;
    private long _sparkplugMessages;
    private long _sparkplugMetricsSkipped;
    private long _sparkplugOrphanMetrics;
    private long _sparkplugUnsupportedMetrics;

    /// <summary>服务运行时间（秒）。</summary>
    public double UptimeSeconds => _uptime.Elapsed.TotalSeconds;

    /// <summary>累计 SQL 请求数。</summary>
    public long SqlRequests => Interlocked.Read(ref _sqlRequests);

    /// <summary>累计 SQL 错误数。</summary>
    public long SqlErrors => Interlocked.Read(ref _sqlErrors);

    /// <summary>累计 INSERT 行数。</summary>
    public long RowsInserted => Interlocked.Read(ref _rowsInserted);

    /// <summary>累计 SELECT 返回行数。</summary>
    public long RowsReturned => Interlocked.Read(ref _rowsReturned);

    /// <summary>成功处理的 Sparkplug B 消息数。</summary>
    public long SparkplugMessages => Interlocked.Read(ref _sparkplugMessages);

    /// <summary>累计跳过的 Sparkplug metric 数。</summary>
    public long SparkplugMetricsSkipped => Interlocked.Read(ref _sparkplugMetricsSkipped);

    /// <summary>因 alias 上下文缺失跳过的 Sparkplug metric 数。</summary>
    public long SparkplugOrphanMetrics => Interlocked.Read(ref _sparkplugOrphanMetrics);

    /// <summary>因类型不受支持跳过的 Sparkplug metric 数。</summary>
    public long SparkplugUnsupportedMetrics => Interlocked.Read(ref _sparkplugUnsupportedMetrics);

    /// <summary>记录一次 SQL 请求。</summary>
    public void RecordSqlRequest() => Interlocked.Increment(ref _sqlRequests);

    /// <summary>记录一次 SQL 错误。</summary>
    public void RecordSqlError() => Interlocked.Increment(ref _sqlErrors);

    /// <summary>累加 INSERT 行数。</summary>
    public void AddInsertedRows(long count) => Interlocked.Add(ref _rowsInserted, count);

    /// <summary>累加 SELECT 返回行数。</summary>
    public void AddReturnedRows(long count) => Interlocked.Add(ref _rowsReturned, count);

    /// <summary>
    /// 记录一次成功的 Sparkplug B 消息处理结果。
    /// </summary>
    public void RecordSparkplugIngest(int skipped, int orphan, int unsupported)
    {
        Interlocked.Increment(ref _sparkplugMessages);
        Interlocked.Add(ref _sparkplugMetricsSkipped, skipped);
        Interlocked.Add(ref _sparkplugOrphanMetrics, orphan);
        Interlocked.Add(ref _sparkplugUnsupportedMetrics, unsupported);
    }
}

/// <summary>
/// Prometheus 文本格式渲染器。仅暴露最小指标集（per-db 维度后续按需扩展）。
/// </summary>
public static class PrometheusFormatter
{
    /// <summary>
    /// 把当前指标渲染成 Prometheus exposition 文本。
    /// </summary>
    public static string Render(ServerMetrics metrics, TsdbRegistry registry)
    {
        var sb = new System.Text.StringBuilder(512);

        sb.AppendLine("# HELP sonnetdb_uptime_seconds Server uptime in seconds.");
        sb.AppendLine("# TYPE sonnetdb_uptime_seconds gauge");
        sb.Append("sonnetdb_uptime_seconds ").Append(metrics.UptimeSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();

        sb.AppendLine("# HELP sonnetdb_databases Number of registered databases.");
        sb.AppendLine("# TYPE sonnetdb_databases gauge");
        sb.Append("sonnetdb_databases ").Append(registry.Count).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sql_requests_total Total number of SQL requests handled.");
        sb.AppendLine("# TYPE sonnetdb_sql_requests_total counter");
        sb.Append("sonnetdb_sql_requests_total ").Append(metrics.SqlRequests).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sql_errors_total Total number of failed SQL requests.");
        sb.AppendLine("# TYPE sonnetdb_sql_errors_total counter");
        sb.Append("sonnetdb_sql_errors_total ").Append(metrics.SqlErrors).AppendLine();

        sb.AppendLine("# HELP sonnetdb_rows_inserted_total Total rows inserted across all databases.");
        sb.AppendLine("# TYPE sonnetdb_rows_inserted_total counter");
        sb.Append("sonnetdb_rows_inserted_total ").Append(metrics.RowsInserted).AppendLine();

        sb.AppendLine("# HELP sonnetdb_rows_returned_total Total rows returned by SELECT across all databases.");
        sb.AppendLine("# TYPE sonnetdb_rows_returned_total counter");
        sb.Append("sonnetdb_rows_returned_total ").Append(metrics.RowsReturned).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sparkplug_messages_total Successfully processed Sparkplug B messages.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_messages_total counter");
        sb.Append("sonnetdb_sparkplug_messages_total ").Append(metrics.SparkplugMessages).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sparkplug_metrics_skipped_total Sparkplug B metrics skipped during mapping.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_metrics_skipped_total counter");
        sb.Append("sonnetdb_sparkplug_metrics_skipped_total ").Append(metrics.SparkplugMetricsSkipped).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sparkplug_orphan_metrics_total Sparkplug B alias-only metrics missing BIRTH context.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_orphan_metrics_total counter");
        sb.Append("sonnetdb_sparkplug_orphan_metrics_total ").Append(metrics.SparkplugOrphanMetrics).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sparkplug_unsupported_metrics_total Sparkplug B non-scalar or unsupported metrics.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_unsupported_metrics_total counter");
        sb.Append("sonnetdb_sparkplug_unsupported_metrics_total ").Append(metrics.SparkplugUnsupportedMetrics).AppendLine();

        // 每个 db 的活跃 segment 数 + memtable 点数（粗粒度，后续可扩展）
        sb.AppendLine("# HELP sonnetdb_segments Active segment count per database.");
        sb.AppendLine("# TYPE sonnetdb_segments gauge");
        foreach (var name in registry.ListDatabases())
        {
            if (registry.TryGet(name, out var db))
            {
                sb.Append("sonnetdb_segments{db=\"").Append(name).Append("\"} ").Append(db.Segments.SegmentCount).AppendLine();
            }
        }

        return sb.ToString();
    }
}
