using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Hosting;

/// <summary>关系表启动预热阶段。</summary>
internal enum RelationalTableWarmupPhase
{
    Pending,
    Running,
    Completed,
    Failed,
}

/// <summary>关系表启动预热的线程安全快照。</summary>
internal readonly record struct RelationalTableWarmupSnapshot(
    RelationalTableWarmupPhase Phase,
    int DatabaseCount,
    int TableCount,
    string? Failure,
    int CompletedDatabaseCount = 0,
    int CompletedTableCount = 0,
    int TotalDatabaseCount = 0,
    int TotalTableCount = 0,
    string? CurrentDatabase = null,
    string? CurrentTable = null,
    long ManagedBytes = 0,
    long WorkingSetBytes = 0,
    long ElapsedMilliseconds = 0);

/// <summary>保存关系表启动预热状态，供托管服务和 readiness 检查共享。</summary>
internal sealed class RelationalTableWarmupState
{
    private readonly object _sync = new();
    private RelationalTableWarmupSnapshot _snapshot = new(
        RelationalTableWarmupPhase.Pending,
        DatabaseCount: 0,
        TableCount: 0,
        Failure: null);

    /// <summary>读取当前预热状态的稳定快照。</summary>
    public RelationalTableWarmupSnapshot Read()
    {
        lock (_sync)
            return _snapshot;
    }

    /// <summary>记录预热总量并将 readiness 置为执行中。</summary>
    public void MarkRunning(int totalDatabaseCount, int totalTableCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalDatabaseCount);
        ArgumentOutOfRangeException.ThrowIfNegative(totalTableCount);
        lock (_sync)
        {
            _snapshot = new(
                RelationalTableWarmupPhase.Running,
                DatabaseCount: 0,
                TableCount: 0,
                Failure: null,
                CompletedDatabaseCount: 0,
                CompletedTableCount: 0,
                TotalDatabaseCount: totalDatabaseCount,
                TotalTableCount: totalTableCount,
                CurrentDatabase: null,
                CurrentTable: null);
        }
    }

    /// <summary>兼容旧调用方，使用未知总量标记预热开始。</summary>
    public void MarkRunning() => MarkRunning(0, 0);

    /// <summary>记录当前正在处理的数据库。</summary>
    public void MarkDatabaseStarted(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        lock (_sync)
            _snapshot = _snapshot with { CurrentDatabase = databaseName, CurrentTable = null };
    }

    /// <summary>记录单张关系表完成冷开，供并行配置下的进度快照使用。</summary>
    public void MarkTableWarmed(string databaseName, string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                CompletedTableCount = checked(_snapshot.CompletedTableCount + 1),
                CurrentDatabase = databaseName,
                CurrentTable = tableName,
            };
        }
    }

    /// <summary>记录一个数据库的关系表冷开完成，并校正已完成计数。</summary>
    public void MarkDatabaseCompleted(string databaseName, int completedDatabaseCount, int completedTableCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentOutOfRangeException.ThrowIfNegative(completedDatabaseCount);
        ArgumentOutOfRangeException.ThrowIfNegative(completedTableCount);
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                CompletedDatabaseCount = completedDatabaseCount,
                CompletedTableCount = completedTableCount,
                CurrentDatabase = databaseName,
                CurrentTable = null,
            };
        }
    }

    /// <summary>保存自然 GC 状态下的内存采样，不为诊断主动触发垃圾回收。</summary>
    public void RecordMemorySample(long managedBytes, long workingSetBytes, long elapsedMilliseconds)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                ManagedBytes = managedBytes,
                WorkingSetBytes = workingSetBytes,
                ElapsedMilliseconds = elapsedMilliseconds,
            };
        }
    }

    /// <summary>记录全部已有数据库的关系表预热结果。</summary>
    public void MarkCompleted(int databaseCount, int tableCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(databaseCount);
        ArgumentOutOfRangeException.ThrowIfNegative(tableCount);
        lock (_sync)
        {
            _snapshot = new(
                RelationalTableWarmupPhase.Completed,
                databaseCount,
                tableCount,
                null,
                databaseCount,
                tableCount,
                databaseCount,
                tableCount,
                null,
                null,
                _snapshot.ManagedBytes,
                _snapshot.WorkingSetBytes,
                _snapshot.ElapsedMilliseconds);
        }
    }

    /// <summary>记录阻断 readiness 的预热异常。</summary>
    public void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_sync)
            _snapshot = _snapshot with { Phase = RelationalTableWarmupPhase.Failed, Failure = exception.Message };
    }
}

/// <summary>在服务器接受业务流量前逐阶段完成关系表冷开恢复。</summary>
internal sealed class RelationalTableWarmupService(
    TsdbRegistry registry,
    RelationalTableWarmupState state,
    IOptions<ServerOptions> options,
    ILogger<RelationalTableWarmupService> logger) : IHostedService
{
    private static readonly Action<ILogger, int, int, int, long, Exception?> _warmupCompleted =
        LoggerMessage.Define<int, int, int, long>(
            LogLevel.Information,
            new EventId(5900, nameof(RelationalTableWarmupService)),
            "关系表启动预热完成：数据库 {DatabaseCount} 个，关系表 {TableCount} 张，并发 {Concurrency}，耗时 {ElapsedMilliseconds} ms。");

    private static readonly Action<ILogger, Exception?> _warmupFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(5901, nameof(RelationalTableWarmupService)),
            "关系表启动预热失败，readiness 将保持不健康。");

    private static readonly Action<ILogger, string, int, int, int, int, Exception?> _warmupProgress =
        LoggerMessage.Define<string, int, int, int, int>(
            LogLevel.Information,
            new EventId(5902, nameof(RelationalTableWarmupService)),
            "关系表启动预热进度：数据库 {DatabaseName}，已完成数据库 {CompletedDatabaseCount}/{TotalDatabaseCount}，已完成表 {CompletedTableCount}/{TotalTableCount}。");

    private static readonly Action<ILogger, string, string, int, long, long, long, Exception?> _tableWarmed =
        LoggerMessage.Define<string, string, int, long, long, long>(
            LogLevel.Information,
            new EventId(5903, nameof(RelationalTableWarmupService)),
            "关系表冷开完成：{DatabaseName}.{TableName}，累计完成 {CompletedTableCount} 张，托管内存 {ManagedBytes} bytes，进程工作集 {WorkingSetBytes} bytes，累计耗时 {ElapsedMilliseconds} ms。");

    /// <summary>逐库预热已有关系表；启动阶段保持同步门禁，防止其他服务与冷开恢复竞争。</summary>
    public Task StartAsync(CancellationToken stoppingToken)
    {
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            IReadOnlyList<string> databaseNames = registry.ListDatabases();
            int totalDatabaseCount = 0;
            int totalTableCount = 0;
            foreach (string databaseName in databaseNames)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (!registry.TryGet(databaseName, out var database))
                    continue;

                totalDatabaseCount++;
                totalTableCount += database.Tables.Catalog.Snapshot().Count;
            }

            // 发布阶段总量，但在全部预热成功前不启动后续托管服务，避免绕过 readiness 的请求触发并发冷开。
            state.MarkRunning(totalDatabaseCount, totalTableCount);

            int databaseCount = 0;
            int tableCount = 0;
            int concurrency = options.Value.RelationalTableWarmupConcurrency;
            foreach (string databaseName in databaseNames)
            {
                stoppingToken.ThrowIfCancellationRequested();
                if (!registry.TryGet(databaseName, out var database))
                    continue;

                state.MarkDatabaseStarted(databaseName);
                IReadOnlyList<string> warmed = database.Tables.WarmUpAll(
                    stoppingToken,
                    concurrency,
                    tableName => ReportTableWarmed(databaseName, tableName, startedAt));
                databaseCount++;
                tableCount += warmed.Count;
                state.MarkDatabaseCompleted(databaseName, databaseCount, tableCount);
                RelationalTableWarmupSnapshot progress = state.Read();
                _warmupProgress(
                    logger,
                    databaseName,
                    progress.CompletedDatabaseCount,
                    progress.TotalDatabaseCount,
                    progress.CompletedTableCount,
                    progress.TotalTableCount,
                    null);
            }

            state.MarkCompleted(databaseCount, tableCount);
            _warmupCompleted(
                logger,
                databaseCount,
                tableCount,
                concurrency,
                Convert.ToInt64(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds),
                null);
        }
        catch (OperationCanceledException exception) when (stoppingToken.IsCancellationRequested)
        {
            state.MarkFailed(exception);
            throw;
        }
        catch (Exception exception)
        {
            // 保留 HTTP 诊断入口，但 readiness 必须持续失败，避免业务请求承担未完成的冷开恢复。
            state.MarkFailed(exception);
            _warmupFailed(logger, exception);
        }
        return Task.CompletedTask;
    }

    /// <summary>同步预热不持有独立后台任务，停止时无需额外处理。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>在每张表完成时记录进度及进程内存，定位恢复峰值所属阶段，不强制 GC。</summary>
    private void ReportTableWarmed(string databaseName, string tableName, long startedAt)
    {
        state.MarkTableWarmed(databaseName, tableName);
        long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
        using Process process = Process.GetCurrentProcess();
        long workingSetBytes = process.WorkingSet64;
        long elapsedMilliseconds = Convert.ToInt64(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        state.RecordMemorySample(managedBytes, workingSetBytes, elapsedMilliseconds);
        _tableWarmed(logger, databaseName, tableName, state.Read().CompletedTableCount,
            managedBytes, workingSetBytes, elapsedMilliseconds, null);
    }
}

/// <summary>把关系表启动预热状态映射为标准 readiness 结果。</summary>
internal sealed class RelationalTableWarmupHealthCheck(RelationalTableWarmupState state) : IHealthCheck
{
    /// <summary>预热完成后放行；尚未开始、执行中或失败时阻断 readiness。</summary>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        RelationalTableWarmupSnapshot snapshot = state.Read();
        HealthCheckResult result = snapshot.Phase switch
        {
            RelationalTableWarmupPhase.Completed => HealthCheckResult.Healthy(
                $"已预热 {snapshot.DatabaseCount} 个数据库中的 {snapshot.TableCount} 张关系表。",
                BuildData(snapshot)),
            RelationalTableWarmupPhase.Failed => HealthCheckResult.Unhealthy(
                $"关系表启动预热失败：{snapshot.Failure}",
                data: BuildData(snapshot)),
            RelationalTableWarmupPhase.Running => HealthCheckResult.Unhealthy(
                $"关系表正在执行启动预热：已完成 {snapshot.CompletedTableCount}/{snapshot.TotalTableCount} 张表，"
                + $"数据库 {snapshot.CompletedDatabaseCount}/{snapshot.TotalDatabaseCount}。",
                data: BuildData(snapshot)),
            _ => HealthCheckResult.Unhealthy("关系表启动预热尚未开始。", data: BuildData(snapshot)),
        };
        return Task.FromResult(result);
    }

    /// <summary>构造不含业务数据的启动阶段计数，便于监控采集而不泄露表内容。</summary>
    private static IReadOnlyDictionary<string, object> BuildData(RelationalTableWarmupSnapshot snapshot)
        => new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["phase"] = snapshot.Phase.ToString(),
            ["completedDatabases"] = snapshot.CompletedDatabaseCount,
            ["totalDatabases"] = snapshot.TotalDatabaseCount,
            ["completedTables"] = snapshot.CompletedTableCount,
            ["totalTables"] = snapshot.TotalTableCount,
            ["currentDatabase"] = snapshot.CurrentDatabase ?? string.Empty,
            ["currentTable"] = snapshot.CurrentTable ?? string.Empty,
            ["managedBytes"] = snapshot.ManagedBytes,
            ["workingSetBytes"] = snapshot.WorkingSetBytes,
            ["elapsedMilliseconds"] = snapshot.ElapsedMilliseconds,
        };
}
