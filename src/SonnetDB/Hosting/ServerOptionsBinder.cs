using Microsoft.Extensions.Configuration;
using SonnetDB.Configuration;
using SonnetDB.Mqtt;

namespace SonnetDB.Hosting;

/// <summary>
/// 负责服务器配置绑定与启动期默认值补齐。
/// </summary>
internal static class ServerOptionsBinder
{
    private const long MinSqlQueryLimitBytes = 1024L * 1024;
    private const long MaxSqlQueryLimitBytes = 16L * 1024 * 1024 * 1024;
    private const long MinSqlGlobalLimitBytes = 1024L * 1024;
    private const long MaxSqlGlobalLimitBytes = 64L * 1024 * 1024 * 1024;
    private const long MinSqlParallelWorkerMemoryBytes = 4L * 1024;
    private const long MaxSqlParallelWorkerMemoryBytes = 64L * 1024 * 1024;
    private const long MaxSqlParallelismMinRows = 1_000_000_000;
    private static readonly int MaxSqlParallelWorkers = Math.Clamp(Environment.ProcessorCount, 1, 64);

    private static readonly string[] DefaultCopilotDocsRoots =
    [
        "./docs",
        "./web/help",
        "./src/SonnetDB/wwwroot/help",
    ];

    /// <summary>
    /// 从配置源绑定完整服务器选项，并补齐运行所需的默认值。
    /// </summary>
    /// <param name="configuration">应用配置根。</param>
    public static ServerOptions Bind(IConfiguration configuration)
    {
        var options = new ServerOptions();
        Bind(configuration, options);
        ApplyDefaults(options);
        return options;
    }

    /// <summary>
    /// 绑定 <see cref="ServerOptions"/>，保留配置系统覆盖集合属性的语义。
    /// </summary>
    /// <param name="configuration">应用配置根。</param>
    /// <param name="options">待填充的服务器选项。</param>
    public static void Bind(IConfiguration configuration, ServerOptions options)
    {
        options.Copilot.Docs.Roots.Clear();
        var serverSection = configuration.GetSection("SonnetDBServer");
        serverSection.Bind(options);
        ApplyLegacySlowQueryOptions(serverSection, options);
    }

    /// <summary>
    /// 补齐配置中未显式提供的服务器默认值。
    /// </summary>
    /// <param name="options">待补齐的服务器选项。</param>
    public static void ApplyDefaults(ServerOptions options)
    {
        if (options.Copilot.Docs.Roots.Count == 0)
            options.Copilot.Docs.Roots.AddRange(DefaultCopilotDocsRoots);

        options.RelationalTableWarmupConcurrency = Math.Clamp(
            options.RelationalTableWarmupConcurrency,
            1,
            16);

        options.Observability.SlowQueryLog.Capacity = Math.Clamp(
            options.Observability.SlowQueryLog.Capacity,
            16,
            4096);
        options.Observability.SlowQueryLog.AggregateCapacity = Math.Clamp(
            options.Observability.SlowQueryLog.AggregateCapacity,
            16,
            16_384);

        options.SqlHttpAdmission.PermitLimit = Math.Clamp(
            options.SqlHttpAdmission.PermitLimit,
            1,
            256);
        options.SqlHttpAdmission.QueueLimit = Math.Clamp(
            options.SqlHttpAdmission.QueueLimit,
            0,
            4096);

        // Rebirth 队列必须始终有固定上界，避免异常节点风暴持续占用内存。
        options.Mqtt.Sparkplug.RebirthQueueCapacity = Math.Clamp(
            options.Mqtt.Sparkplug.RebirthQueueCapacity,
            SparkplugRebirthQueue.MinCapacity,
            SparkplugRebirthQueue.MaxCapacity);
        options.Mqtt.Sparkplug.RebirthPublishTimeoutMilliseconds = Math.Clamp(
            options.Mqtt.Sparkplug.RebirthPublishTimeoutMilliseconds,
            SparkplugHostApplicationService.MinPublishTimeoutMilliseconds,
            SparkplugHostApplicationService.MaxPublishTimeoutMilliseconds);

        ApplySqlExecutionBounds(options.SqlExecution);

        options.Modbus.DiscoveryIntervalMilliseconds = Math.Clamp(
            options.Modbus.DiscoveryIntervalMilliseconds,
            10,
            60_000);
        options.Modbus.RetryBaseDelayMilliseconds = Math.Clamp(
            options.Modbus.RetryBaseDelayMilliseconds,
            1,
            60_000);
        options.Modbus.MaxRetryDelayMilliseconds = Math.Clamp(
            options.Modbus.MaxRetryDelayMilliseconds,
            options.Modbus.RetryBaseDelayMilliseconds,
            60_000);
        options.Modbus.ReconnectBaseDelayMilliseconds = Math.Clamp(
            options.Modbus.ReconnectBaseDelayMilliseconds,
            1,
            60_000);
        options.Modbus.MaxReconnectDelayMilliseconds = Math.Clamp(
            options.Modbus.MaxReconnectDelayMilliseconds,
            options.Modbus.ReconnectBaseDelayMilliseconds,
            600_000);

        // 索引恢复预算必须保持正数和明确上限，避免配置错误导致无界 WAL 或内存增长。
        options.Kv.IndexRebuildMaxOverlayEntries = Math.Clamp(
            options.Kv.IndexRebuildMaxOverlayEntries,
            1,
            50_000_000);
        options.Kv.IndexRebuildMaxWalBytes = Math.Clamp(
            options.Kv.IndexRebuildMaxWalBytes,
            1024L * 1024,
            64L * 1024 * 1024 * 1024);

        options.SemanticSearch.Dimensions = Math.Clamp(options.SemanticSearch.Dimensions, 1, 65_536);
        options.SemanticSearch.MaxTextTokens = Math.Clamp(options.SemanticSearch.MaxTextTokens, 2, 4_096);
        options.SemanticSearch.ImageSize = Math.Clamp(options.SemanticSearch.ImageSize, 16, 4_096);
        options.SemanticSearch.MaxImageBytes = Math.Clamp(
            options.SemanticSearch.MaxImageBytes,
            1,
            512 * 1024 * 1024);
        options.SemanticSearch.MaxTopK = Math.Clamp(options.SemanticSearch.MaxTopK, 1, 1_000);
        options.SemanticSearch.DefaultTopK = Math.Clamp(
            options.SemanticSearch.DefaultTopK,
            1,
            options.SemanticSearch.MaxTopK);
    }

    /// <summary>
    /// 收紧每数据库 SQL 内部资源边界，并保证共享预算可覆盖全部 worker 的固定预留。
    /// </summary>
    /// <param name="options">待规范化的 SQL 内部执行资源配置。</param>
    private static void ApplySqlExecutionBounds(SqlExecutionResourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.MaxRoutineStatements = Math.Clamp(options.MaxRoutineStatements, 1, 100_000);
        options.MaxRoutineDepth = Math.Clamp(options.MaxRoutineDepth, 1, 32);
        options.MaxRoutineResultRows = Math.Clamp(options.MaxRoutineResultRows, 1, 100_000);

        options.QueryLimitBytes = Math.Clamp(
            options.QueryLimitBytes,
            MinSqlQueryLimitBytes,
            MaxSqlQueryLimitBytes);
        options.GlobalLimitBytes = Math.Max(
            options.QueryLimitBytes,
            Math.Clamp(
                options.GlobalLimitBytes,
                MinSqlGlobalLimitBytes,
                MaxSqlGlobalLimitBytes));
        options.MaxParallelWorkers = Math.Clamp(
            options.MaxParallelWorkers,
            1,
            MaxSqlParallelWorkers);
        options.ParallelismMinRows = Math.Clamp(
            options.ParallelismMinRows,
            1,
            MaxSqlParallelismMinRows);

        // worker 预留同时计入单查询和数据库全局预算；分别用除法求上限，避免极值乘法溢出。
        long queryBoundPerWorker = options.QueryLimitBytes / options.MaxParallelWorkers;
        long globalBoundPerWorker = options.GlobalLimitBytes / options.MaxParallelWorkers;
        long workerUpperBound = Math.Min(
            MaxSqlParallelWorkerMemoryBytes,
            Math.Min(queryBoundPerWorker, globalBoundPerWorker));
        options.ParallelWorkerMemoryBytes = Math.Clamp(
            options.ParallelWorkerMemoryBytes,
            MinSqlParallelWorkerMemoryBytes,
            workerUpperBound);
    }

    /// <summary>当新分层配置不存在时，把旧版慢查询键迁移到当前选项对象。</summary>
    /// <param name="serverSection">服务器配置根节。</param>
    /// <param name="options">待补齐兼容值的服务器选项。</param>
    private static void ApplyLegacySlowQueryOptions(IConfigurationSection serverSection, ServerOptions options)
    {
        var nestedSection = serverSection.GetSection("Observability:SlowQueryLog");
        if (nestedSection.Exists())
            return;

        var hasLegacyConfiguration = serverSection["SlowQueryEnabled"] is not null
            || serverSection["SlowQueryThresholdMs"] is not null
            || serverSection["SlowQueryWarningThresholdMs"] is not null
            || serverSection["SlowQueryCriticalThresholdMs"] is not null;
        if (!hasLegacyConfiguration)
            return;

        var target = options.Observability.SlowQueryLog;
        target.Enabled = options.SlowQueryEnabled;
        target.ThresholdMs = options.SlowQueryThresholdMs;
        target.WarningThresholdMs = options.SlowQueryWarningThresholdMs;
        target.CriticalThresholdMs = options.SlowQueryCriticalThresholdMs;
    }
}
