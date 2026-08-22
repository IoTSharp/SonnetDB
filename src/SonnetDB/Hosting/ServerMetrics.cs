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
    private long _sparkplugLifecycleMessages;
    private long _sparkplugSequenceGaps;
    private long _sparkplugRebirthCommands;
    private long _modbusPolls;
    private long _modbusPollFailures;
    private long _modbusReadBatches;
    private long _modbusRowsWritten;
    private long _modbusReconnects;
    private long _modbusSlaveConnections;
    private long _modbusSlaveConnectionRejections;
    private long _modbusSlaveActiveConnections;
    private long _modbusSlaveReadRequests;
    private long _modbusSlaveReadFailures;
    private long _modbusSlaveWriteRequests;
    private long _modbusSlaveWriteFailures;

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

    /// <summary>累计处理的 BIRTH/DEATH 生命周期消息数。</summary>
    public long SparkplugLifecycleMessages => Interlocked.Read(ref _sparkplugLifecycleMessages);

    /// <summary>累计发现的序列或 BIRTH 上下文缺口数。</summary>
    public long SparkplugSequenceGaps => Interlocked.Read(ref _sparkplugSequenceGaps);

    /// <summary>累计发布的自动 Rebirth 命令数。</summary>
    public long SparkplugRebirthCommands => Interlocked.Read(ref _sparkplugRebirthCommands);

    /// <summary>累计完成的 Modbus master 轮询次数。</summary>
    public long ModbusPolls => Interlocked.Read(ref _modbusPolls);

    /// <summary>累计失败的 Modbus master 轮询次数。</summary>
    public long ModbusPollFailures => Interlocked.Read(ref _modbusPollFailures);

    /// <summary>累计发送的 Modbus 批量读取请求数。</summary>
    public long ModbusReadBatches => Interlocked.Read(ref _modbusReadBatches);

    /// <summary>累计由 Modbus 成功采样写入的关系表行数。</summary>
    public long ModbusRowsWritten => Interlocked.Read(ref _modbusRowsWritten);

    /// <summary>Modbus 失败后触发的累计重连次数。</summary>
    public long ModbusReconnects => Interlocked.Read(ref _modbusReconnects);

    /// <summary>累计接受的 Modbus slave TCP 连接数。</summary>
    public long ModbusSlaveConnections => Interlocked.Read(ref _modbusSlaveConnections);

    /// <summary>累计因白名单或连接上限拒绝的 Modbus slave TCP 连接数。</summary>
    public long ModbusSlaveConnectionRejections => Interlocked.Read(ref _modbusSlaveConnectionRejections);

    /// <summary>当前活跃的 Modbus slave TCP 连接数。</summary>
    public long ModbusSlaveActiveConnections => Interlocked.Read(ref _modbusSlaveActiveConnections);

    /// <summary>累计收到的 Modbus slave 读请求数。</summary>
    public long ModbusSlaveReadRequests => Interlocked.Read(ref _modbusSlaveReadRequests);

    /// <summary>累计返回异常响应的 Modbus slave 读请求数。</summary>
    public long ModbusSlaveReadFailures => Interlocked.Read(ref _modbusSlaveReadFailures);

    /// <summary>累计收到的 Modbus slave 外部写请求数。</summary>
    public long ModbusSlaveWriteRequests => Interlocked.Read(ref _modbusSlaveWriteRequests);

    /// <summary>累计未能持久 staging 或被策略拒绝的 Modbus slave 外部写请求数。</summary>
    public long ModbusSlaveWriteFailures => Interlocked.Read(ref _modbusSlaveWriteFailures);

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

    /// <summary>记录一条不落库的 Sparkplug 生命周期消息。</summary>
    public void RecordSparkplugLifecycleMessage() => Interlocked.Increment(ref _sparkplugLifecycleMessages);

    /// <summary>记录一次 Sparkplug 序列或 BIRTH 上下文缺口。</summary>
    public void RecordSparkplugSequenceGap() => Interlocked.Increment(ref _sparkplugSequenceGaps);

    /// <summary>记录一条由 host application 发布的 Rebirth 命令。</summary>
    public void RecordSparkplugRebirthCommand() => Interlocked.Increment(ref _sparkplugRebirthCommands);

    /// <summary>记录一次完成的 Modbus master 轮询。</summary>
    public void RecordModbusPoll(bool succeeded, int rowsWritten)
    {
        Interlocked.Increment(ref _modbusPolls);
        if (!succeeded)
            Interlocked.Increment(ref _modbusPollFailures);
        if (rowsWritten > 0)
            Interlocked.Add(ref _modbusRowsWritten, rowsWritten);
    }

    /// <summary>记录一个成功返回的 Modbus 批量读取请求。</summary>
    public void RecordModbusReadBatch() => Interlocked.Increment(ref _modbusReadBatches);

    /// <summary>记录一次失败后的 Modbus 重连。</summary>
    public void RecordModbusReconnect() => Interlocked.Increment(ref _modbusReconnects);

    /// <summary>记录一个已接受的 Modbus slave TCP 连接。</summary>
    public void RecordModbusSlaveConnectionOpened()
    {
        Interlocked.Increment(ref _modbusSlaveConnections);
        Interlocked.Increment(ref _modbusSlaveActiveConnections);
    }

    /// <summary>记录一个 Modbus slave TCP 连接关闭。</summary>
    public void RecordModbusSlaveConnectionClosed() => Interlocked.Decrement(ref _modbusSlaveActiveConnections);

    /// <summary>记录一个因网络边界被拒绝的 Modbus slave TCP 连接。</summary>
    public void RecordModbusSlaveConnectionRejected()
        => Interlocked.Increment(ref _modbusSlaveConnectionRejections);

    /// <summary>记录一个 Modbus slave 读请求。</summary>
    /// <param name="succeeded">是否返回成功读响应。</param>
    public void RecordModbusSlaveRead(bool succeeded)
    {
        Interlocked.Increment(ref _modbusSlaveReadRequests);
        if (!succeeded)
            Interlocked.Increment(ref _modbusSlaveReadFailures);
    }

    /// <summary>记录一个 Modbus slave 外部写请求。</summary>
    /// <param name="succeeded">是否已持久化进入待审批队列。</param>
    public void RecordModbusSlaveWrite(bool succeeded)
    {
        Interlocked.Increment(ref _modbusSlaveWriteRequests);
        if (!succeeded)
            Interlocked.Increment(ref _modbusSlaveWriteFailures);
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

        sb.AppendLine("# HELP sonnetdb_procedure_executions_total Total SQL procedure invocations per database.");
        sb.AppendLine("# TYPE sonnetdb_procedure_executions_total counter");
        sb.AppendLine("# HELP sonnetdb_procedure_failures_total Failed SQL procedure invocations per database.");
        sb.AppendLine("# TYPE sonnetdb_procedure_failures_total counter");
        sb.AppendLine("# HELP sonnetdb_procedure_elapsed_milliseconds_total Cumulative SQL procedure duration per database.");
        sb.AppendLine("# TYPE sonnetdb_procedure_elapsed_milliseconds_total counter");
        sb.AppendLine("# HELP sonnetdb_trigger_executions_total Total SQL trigger invocations per database.");
        sb.AppendLine("# TYPE sonnetdb_trigger_executions_total counter");
        sb.AppendLine("# HELP sonnetdb_trigger_failures_total Failed SQL trigger invocations per database.");
        sb.AppendLine("# TYPE sonnetdb_trigger_failures_total counter");
        sb.AppendLine("# HELP sonnetdb_trigger_elapsed_milliseconds_total Cumulative SQL trigger duration per database.");
        sb.AppendLine("# TYPE sonnetdb_trigger_elapsed_milliseconds_total counter");
        foreach (var name in registry.ListDatabases())
        {
            if (!registry.TryGet(name, out var db))
                continue;
            var routineMetrics = db.Routines.Diagnostics.GetMetrics();
            sb.Append("sonnetdb_procedure_executions_total{db=\"").Append(name).Append("\"} ").Append(routineMetrics.ProcedureExecutions).AppendLine();
            sb.Append("sonnetdb_procedure_failures_total{db=\"").Append(name).Append("\"} ").Append(routineMetrics.ProcedureFailures).AppendLine();
            sb.Append("sonnetdb_procedure_elapsed_milliseconds_total{db=\"").Append(name).Append("\"} ")
                .Append(routineMetrics.ProcedureElapsedMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
            sb.Append("sonnetdb_trigger_executions_total{db=\"").Append(name).Append("\"} ").Append(routineMetrics.TriggerExecutions).AppendLine();
            sb.Append("sonnetdb_trigger_failures_total{db=\"").Append(name).Append("\"} ").Append(routineMetrics.TriggerFailures).AppendLine();
            sb.Append("sonnetdb_trigger_elapsed_milliseconds_total{db=\"").Append(name).Append("\"} ")
                .Append(routineMetrics.TriggerElapsedMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        }

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

        sb.AppendLine("# HELP sonnetdb_sparkplug_lifecycle_messages_total Sparkplug B birth/death lifecycle messages.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_lifecycle_messages_total counter");
        sb.Append("sonnetdb_sparkplug_lifecycle_messages_total ").Append(metrics.SparkplugLifecycleMessages).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sparkplug_sequence_gaps_total Sparkplug B sequence or birth-context gaps.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_sequence_gaps_total counter");
        sb.Append("sonnetdb_sparkplug_sequence_gaps_total ").Append(metrics.SparkplugSequenceGaps).AppendLine();

        sb.AppendLine("# HELP sonnetdb_sparkplug_rebirth_commands_total Sparkplug B automatic rebirth commands.");
        sb.AppendLine("# TYPE sonnetdb_sparkplug_rebirth_commands_total counter");
        sb.Append("sonnetdb_sparkplug_rebirth_commands_total ").Append(metrics.SparkplugRebirthCommands).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_master_polls_total Completed Modbus master poll rounds.");
        sb.AppendLine("# TYPE sonnetdb_modbus_master_polls_total counter");
        sb.Append("sonnetdb_modbus_master_polls_total ").Append(metrics.ModbusPolls).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_master_poll_failures_total Failed Modbus master poll rounds.");
        sb.AppendLine("# TYPE sonnetdb_modbus_master_poll_failures_total counter");
        sb.Append("sonnetdb_modbus_master_poll_failures_total ").Append(metrics.ModbusPollFailures).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_master_read_batches_total Successful Modbus batched read requests.");
        sb.AppendLine("# TYPE sonnetdb_modbus_master_read_batches_total counter");
        sb.Append("sonnetdb_modbus_master_read_batches_total ").Append(metrics.ModbusReadBatches).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_master_rows_written_total Local table rows written by Modbus polls.");
        sb.AppendLine("# TYPE sonnetdb_modbus_master_rows_written_total counter");
        sb.Append("sonnetdb_modbus_master_rows_written_total ").Append(metrics.ModbusRowsWritten).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_master_reconnects_total Modbus reconnects after failures.");
        sb.AppendLine("# TYPE sonnetdb_modbus_master_reconnects_total counter");
        sb.Append("sonnetdb_modbus_master_reconnects_total ").Append(metrics.ModbusReconnects).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_connections_total Accepted Modbus slave TCP connections.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_connections_total counter");
        sb.Append("sonnetdb_modbus_slave_connections_total ").Append(metrics.ModbusSlaveConnections).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_connection_rejections_total Rejected Modbus slave TCP connections.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_connection_rejections_total counter");
        sb.Append("sonnetdb_modbus_slave_connection_rejections_total ").Append(metrics.ModbusSlaveConnectionRejections).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_active_connections Active Modbus slave TCP connections.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_active_connections gauge");
        sb.Append("sonnetdb_modbus_slave_active_connections ").Append(metrics.ModbusSlaveActiveConnections).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_read_requests_total Modbus slave read requests.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_read_requests_total counter");
        sb.Append("sonnetdb_modbus_slave_read_requests_total ").Append(metrics.ModbusSlaveReadRequests).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_read_failures_total Modbus slave read requests returning exceptions.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_read_failures_total counter");
        sb.Append("sonnetdb_modbus_slave_read_failures_total ").Append(metrics.ModbusSlaveReadFailures).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_write_requests_total Modbus slave external write requests.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_write_requests_total counter");
        sb.Append("sonnetdb_modbus_slave_write_requests_total ").Append(metrics.ModbusSlaveWriteRequests).AppendLine();

        sb.AppendLine("# HELP sonnetdb_modbus_slave_write_failures_total Modbus slave writes rejected or not durably staged.");
        sb.AppendLine("# TYPE sonnetdb_modbus_slave_write_failures_total counter");
        sb.Append("sonnetdb_modbus_slave_write_failures_total ").Append(metrics.ModbusSlaveWriteFailures).AppendLine();

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
