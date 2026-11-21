using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Backup;
using SonnetDB.Configuration;

namespace SonnetDB.Hosting;

/// <summary>
/// 按任务定义执行每日数据库备份，并执行有界的滚动清理。
/// </summary>
internal sealed class BackupSchedulerService : BackgroundService
{
    private readonly TsdbRegistry _registry;
    private readonly BackupTaskStore _store;
    private readonly ServerOptions _serverOptions;
    private readonly ILogger<BackupSchedulerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>创建备份调度器。</summary>
    public BackupSchedulerService(
        TsdbRegistry registry,
        BackupTaskStore store,
        IOptions<ServerOptions> options,
        ILogger<BackupSchedulerService> logger,
        TimeProvider timeProvider)
    {
        _registry = registry;
        _store = store;
        _serverOptions = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>执行一个任务并返回任务状态。</summary>
    public async Task<BackupTaskState> RunNowAsync(string taskId, CancellationToken cancellationToken)
    {
        BackupTaskDefinition task = _store.TryGetTask(taskId)
            ?? throw new KeyNotFoundException($"备份任务 '{taskId}' 不存在。");
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunTaskAsync(task, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>以有限周期检查到期任务。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_serverOptions.Backup.SchedulerTickSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_serverOptions.Backup.Enabled)
                continue;

            try
            {
                await RunDueTasksAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "备份调度轮询失败。");
            }
        }
    }

    /// <summary>枚举任务并执行当前本地日期尚未运行的任务。</summary>
    private async Task RunDueTasksAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (BackupTaskDefinition task in _store.ListTasks().Where(static task => task.Enabled).Take(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDue(task, now))
                    continue;
                _ = await RunTaskAsync(task, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    /// <summary>判断任务是否到达指定时区的每日执行时间。</summary>
    private bool IsDue(BackupTaskDefinition task, DateTimeOffset utcNow)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            _logger.LogWarning("备份任务 {TaskId} 使用未知时区 {TimeZoneId}，本次跳过。", task.Id, task.TimeZoneId);
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            _logger.LogWarning("备份任务 {TaskId} 的时区 {TimeZoneId} 无效，本次跳过。", task.Id, task.TimeZoneId);
            return false;
        }

        DateTime local = TimeZoneInfo.ConvertTime(utcNow, zone).DateTime;
        if (!TimeOnly.TryParseExact(task.LocalTime, "HH:mm", out TimeOnly runAt))
            return false;
        if (local.TimeOfDay < runAt.ToTimeSpan())
            return false;

        BackupTaskState state = _store.GetState(task.Id);
        return state.LastCompletedUtc is null
            || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(state.LastCompletedUtc.Value, zone).DateTime)
                < DateOnly.FromDateTime(local);
    }

    /// <summary>执行单个任务的数据库备份、校验和滚动清理。</summary>
    private async Task<BackupTaskState> RunTaskAsync(BackupTaskDefinition task, CancellationToken cancellationToken)
    {
        var state = _store.GetState(task.Id);
        state.LastStartedUtc = _timeProvider.GetUtcNow();
        state.Status = "running";
        state.LastError = null;
        _store.SetState(state);

        string batchDirectory = BuildBatchDirectory(task, state.LastStartedUtc.Value);
        try
        {
            Directory.CreateDirectory(batchDirectory);
            IReadOnlyList<string> databases = task.DatabaseNames.Count == 0
                ? _registry.ListDatabases()
                : task.DatabaseNames;
            int completed = 0;
            foreach (string databaseName in databases.Take(256))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_registry.TryGet(databaseName, out var database))
                    throw new InvalidOperationException($"备份任务引用的数据库 '{databaseName}' 未加载。");

                string output = Path.Combine(batchDirectory, databaseName);
                var manifest = new BackupService().Create(database, new BackupCreateOptions
                {
                    DestinationDirectory = output,
                    IncludeFullTextIndexes = task.Scope.IncludeFullTextIndexes,
                    IncludeKeyValue = task.Scope.IncludeKeyValue,
                    IncludeObjectContent = task.Scope.IncludeObjects,
                    ModelScope = ToModelScope(task.Scope),
                    Tables = task.Scope.Tables,
                    DocumentCollections = task.Scope.DocumentCollections,
                    Keyspaces = task.Scope.Keyspaces,
                    Buckets = task.Scope.Buckets,
                    Graphs = task.Scope.Graphs,
                    Measurements = task.Scope.Measurements,
                });
                if (task.VerifyAfterCreate)
                {
                    BackupVerificationResult verification = new BackupService().Verify(output);
                    if (!verification.IsValid)
                        throw new InvalidDataException($"数据库 '{databaseName}' 的备份校验失败：{string.Join("; ", verification.Errors.Take(4))}");
                }

                completed++;
                _logger.LogInformation(
                    "备份任务 {TaskId} 已完成数据库 {Database}，文件 {FileCount}，字节 {Bytes}。",
                    task.Id,
                    databaseName,
                    manifest.Files.Count,
                    manifest.Consistency.TotalBytes);
            }

            ApplyRetention(task);
            state.LastCompletedUtc = _timeProvider.GetUtcNow();
            state.Status = $"ok:{completed}";
            state.LastOutputDirectory = batchDirectory;
            state.LastError = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.LastCompletedUtc = _timeProvider.GetUtcNow();
            state.Status = "cancelled";
            state.LastOutputDirectory = batchDirectory;
            state.LastError = "备份任务已取消。";
            _store.SetState(state);
            throw;
        }
        catch (Exception exception)
        {
            state.LastCompletedUtc = _timeProvider.GetUtcNow();
            state.Status = "failed";
            state.LastOutputDirectory = batchDirectory;
            state.LastError = exception.Message;
            _logger.LogError(exception, "备份任务 {TaskId} 执行失败。", task.Id);
        }

        _store.SetState(state);
        return state;
    }

    /// <summary>构造任务批次目录并保证它位于全局备份根内。</summary>
    private string BuildBatchDirectory(BackupTaskDefinition task, DateTimeOffset startedUtc)
    {
        string root = string.IsNullOrWhiteSpace(task.DestinationRoot)
            ? _serverOptions.Backup.DestinationRoot!
            : Path.GetFullPath(task.DestinationRoot);
        EnsureUnderRoot(root, _serverOptions.Backup.DestinationRoot!);
        string taskRoot = Path.Combine(root, task.Id);
        return Path.Combine(taskRoot, startedUtc.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>删除超出时间或数量上限的批次目录。</summary>
    private void ApplyRetention(BackupTaskDefinition task)
    {
        string root = string.IsNullOrWhiteSpace(task.DestinationRoot)
            ? _serverOptions.Backup.DestinationRoot!
            : Path.GetFullPath(task.DestinationRoot);
        string taskRoot = Path.Combine(root, task.Id);
        if (!Directory.Exists(taskRoot))
            return;

        DateTimeOffset cutoff = _timeProvider.GetUtcNow().AddDays(-task.RetentionDays);
        var directories = Directory.EnumerateDirectories(taskRoot)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(static info => info.CreationTimeUtc)
            .Take(10_000)
            .ToArray();
        for (int index = 0; index < directories.Length; index++)
        {
            DirectoryInfo directory = directories[index];
            bool expired = directory.CreationTimeUtc < cutoff.UtcDateTime;
            bool overCount = index >= task.RetentionCount;
            if (!expired && !overCount)
                continue;
            directory.Delete(recursive: true);
        }
    }

    /// <summary>拒绝备份目录逃逸到全局安全根之外。</summary>
    private static void EnsureUnderRoot(string path, string root)
    {
        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(fullPath, fullRoot, comparison)
            && !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("备份任务目标目录必须位于 Backup.DestinationRoot 下。 ");
        }
    }

    /// <summary>把服务器任务范围映射为 Core 备份模型范围。</summary>
    private static BackupModelScope ToModelScope(BackupScopeOptions scope)
    {
        BackupModelScope result = BackupModelScope.None;
        if (scope.IncludeMeasurements) result |= BackupModelScope.Measurements;
        if (scope.IncludeTables) result |= BackupModelScope.Tables;
        if (scope.IncludeDocuments) result |= BackupModelScope.Documents;
        if (scope.IncludeGraphs) result |= BackupModelScope.Graphs;
        if (scope.IncludeKeyValue) result |= BackupModelScope.KeyValue;
        if (scope.IncludeObjects) result |= BackupModelScope.ObjectContent;
        return result;
    }
}
