using System.Data;
using SonnetDB.Data;
using SonnetDB.Tables;

namespace SonnetDB.Quartz;

/// <summary>
/// 准备 SonnetDB 中的 Quartz.NET 4.8 持久化表和索引。
/// </summary>
public static class SonnetDbQuartzSchema
{
    // 每条变更独立提交，重试时通过数据库目录跳过已应用的列。
    private static readonly (string Table, (string Name, string SqlType)[] Columns)[] AddedColumns =
    [
        ("QRTZ_TRIGGERS",
        [
            ("MISFIRE_ORIG_FIRE_TIME", "INT"), ("EXECUTION_GROUP", "STRING"),
            ("PREFERRED_NODE", "STRING"), ("PREFERRED_NODE_AUTO", "BOOL NOT NULL DEFAULT FALSE"),
            ("RETRY_POLICY", "STRING"), ("RETRY_ATTEMPT", "INT"),
            ("CONTINUES_TRIGGER_NAME", "STRING"), ("CONTINUES_TRIGGER_GROUP", "STRING"),
            ("CONTINUATION_CONDITION", "INT"), ("OVERLAP_POLICY", "INT"),
            ("PAUSE_REASON", "STRING"), ("PAUSED_BY", "STRING"), ("PAUSED_AT", "INT"),
            ("MISFIRE_THRESHOLD", "INT"), ("FIXED_DELAY", "BOOL"),
        ]),
        ("QRTZ_FIRED_TRIGGERS",
        [
            ("EXECUTION_GROUP", "STRING"), ("PROGRESS", "INT"), ("PROGRESS_MESSAGE", "STRING"),
            ("INTERRUPT_REQUESTED_TIME", "INT"),
        ]),
        ("QRTZ_PAUSED_TRIGGER_GRPS",
        [
            ("PAUSE_REASON", "STRING"), ("PAUSED_BY", "STRING"), ("PAUSED_AT", "INT"),
        ]),
        ("QRTZ_SCHEDULER_STATE", [("QUARTZ_VERSION", "STRING")]),
    ];

    /// <summary>
    /// 创建调度库，或在调度器停止时将现有 Quartz 3.18 表补齐到 4.8。保留已有调度记录。
    /// </summary>
    /// <param name="connectionString">承载 Quartz 调度表的 SonnetDB ADO.NET 连接字符串。</param>
    /// <param name="cancellationToken">取消数据库和表的初始化。</param>
    public static async Task EnsureCreatedAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        cancellationToken.ThrowIfCancellationRequested();
        await SndbResourceInitializer.EnsureDatabaseAsync(connectionString, "Quartz 调度", cancellationToken).ConfigureAwait(false);

        using var stream = typeof(SonnetDbQuartzSchema).Assembly.GetManifestResourceStream("SonnetDB.Quartz.QuartzSchema.sql")
            ?? throw new InvalidOperationException("找不到 SonnetDB Quartz 初始化 SQL。");
        using var reader = new StreamReader(stream);
        var schema = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var statement in schema.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new SndbCommand(statement, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var (table, columns) in AddedColumns)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using DataTable metadata = await connection.GetSchemaAsync(
                "Columns", [null, TableObjectName.DefaultSchemaName, table, null], cancellationToken)
                .ConfigureAwait(false);
            var existing = metadata.Rows.Cast<DataRow>()
                .Select(row => row.Field<string>("COLUMN_NAME"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, sqlType) in columns)
            {
                if (existing.Contains(name))
                    continue;

                await using var command = new SndbCommand($"ALTER TABLE {table} ADD COLUMN {name} {sqlType}", connection);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
