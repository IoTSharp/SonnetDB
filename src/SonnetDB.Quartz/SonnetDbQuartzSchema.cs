using SonnetDB.Data;

namespace SonnetDB.Quartz;

/// <summary>
/// 准备 SonnetDB 中的 Quartz.NET 3.18 持久化表和索引。
/// </summary>
public static class SonnetDbQuartzSchema
{
    /// <summary>
    /// 创建调度数据库及缺失的 Quartz 表和索引，保留其中已有的调度记录。
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
    }
}
