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
    string? Failure);

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

    /// <summary>标记预热已经开始。</summary>
    public void MarkRunning()
    {
        lock (_sync)
            _snapshot = new(RelationalTableWarmupPhase.Running, 0, 0, null);
    }

    /// <summary>记录全部已有数据库的关系表预热结果。</summary>
    public void MarkCompleted(int databaseCount, int tableCount)
    {
        lock (_sync)
            _snapshot = new(RelationalTableWarmupPhase.Completed, databaseCount, tableCount, null);
    }

    /// <summary>记录阻断 readiness 的预热异常。</summary>
    public void MarkFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_sync)
            _snapshot = new(RelationalTableWarmupPhase.Failed, 0, 0, exception.Message);
    }
}

/// <summary>在服务器接受业务流量前完成全部已有关系表的冷开恢复。</summary>
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

    /// <summary>逐库预热已有关系表，并在完成前保持 readiness 不健康。</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        state.MarkRunning();
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            int databaseCount = 0;
            int tableCount = 0;
            int concurrency = options.Value.RelationalTableWarmupConcurrency;
            foreach (string databaseName in registry.ListDatabases())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!registry.TryGet(databaseName, out var database))
                    continue;

                databaseCount++;
                tableCount += database.Tables.WarmUpAll(cancellationToken, concurrency).Count;
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
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
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

    /// <summary>预热服务不持有独立后台循环，停止时无需额外处理。</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
                $"已预热 {snapshot.DatabaseCount} 个数据库中的 {snapshot.TableCount} 张关系表。"),
            RelationalTableWarmupPhase.Failed => HealthCheckResult.Unhealthy(
                $"关系表启动预热失败：{snapshot.Failure}"),
            RelationalTableWarmupPhase.Running => HealthCheckResult.Unhealthy("关系表正在执行启动预热。"),
            _ => HealthCheckResult.Unhealthy("关系表启动预热尚未开始。"),
        };
        return Task.FromResult(result);
    }
}
