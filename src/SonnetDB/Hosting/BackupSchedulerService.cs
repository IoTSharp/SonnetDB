using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Backup;
using SonnetDB.Configuration;
using SonnetMQ;

namespace SonnetDB.Hosting;

/// <summary>
/// 按任务定义执行每日数据库备份，并执行有界的滚动清理。
/// </summary>
internal sealed class BackupSchedulerService : BackgroundService
{
    private const string BatchMarkerName = ".sonnetdb-backup-complete";
    private const string MessageQueueDirectoryName = ".system-mq";
    private const string BatchTimeFormat = "yyyyMMdd-HHmmss";
    private const int MaximumRetentionEntries = 20_000;
    private const int MaximumBatchEntries = 1_000_000;
    private readonly TsdbRegistry _registry;
    private readonly BackupTaskStore _store;
    private readonly SonnetMqStore _messageQueue;
    private readonly ServerOptions _serverOptions;
    private readonly ILogger<BackupSchedulerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    /// <summary>创建备份调度器。</summary>
    public BackupSchedulerService(
        TsdbRegistry registry,
        BackupTaskStore store,
        SonnetMqStore messageQueue,
        IOptions<ServerOptions> options,
        ILogger<BackupSchedulerService> logger,
        TimeProvider timeProvider)
    {
        _registry = registry;
        _store = store;
        _messageQueue = messageQueue;
        _serverOptions = options.Value;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <summary>执行一个任务并返回任务状态。</summary>
    public async Task<BackupTaskState> RunNowAsync(string taskId, CancellationToken cancellationToken)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            BackupTaskDefinition task = _store.TryGetTask(taskId)
                ?? throw new KeyNotFoundException($"备份任务 '{taskId}' 不存在。");
            return RunTask(task, cancellationToken);
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
    internal async Task RunDueTasksAsync(CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            foreach (BackupTaskDefinition snapshot in _store.ListTasks().Take(BackupTaskStore.MaximumTasks))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_store.TryGetTask(snapshot.Id) is not { Enabled: true } task || !IsDue(task, now))
                    continue;
                _ = RunTask(task, cancellationToken);
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
        if (task.FirstRunNotBeforeUtc is { } notBefore && utcNow < notBefore)
            return false;

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
    private BackupTaskState RunTask(BackupTaskDefinition task, CancellationToken cancellationToken)
    {
        var state = _store.GetState(task.Id);
        state.LastStartedUtc = _timeProvider.GetUtcNow();
        state.Status = "running";
        state.LastError = null;
        _store.SetState(state);

        string? stagingDirectory = null;
        try
        {
            BackupModelScope databaseScope = ToModelScope(task.Scope);
            string taskRoot = ValidateTaskDestination(task);
            IReadOnlyList<string> databases = [];
            if (databaseScope != BackupModelScope.None)
            {
                databases = task.DatabaseNames.Count == 0 ? _registry.ListDatabases() : task.DatabaseNames;
                if (databases.Count is 0 or > BackupTaskStore.MaximumDatabases)
                    throw new InvalidOperationException("备份任务的数据库数量为空或超出上限。");
            }
            var selected = databases.Select(name =>
            {
                if (!TsdbRegistry.IsValidName(name) || BackupTaskStore.IsReservedDirectoryName(name)
                    || !_registry.TryGet(name, out var database))
                    throw new InvalidOperationException($"备份任务引用的数据库 '{name}' 未加载或名称无法用作目录。");
                return (Name: name, Database: database);
            }).ToArray();

            BackupDestinationMount.EnsureMounted(_serverOptions.Backup, cancellationToken);
            Directory.CreateDirectory(taskRoot);
            RejectLinkedPath(taskRoot);
            string batchName = state.LastStartedUtc.Value.UtcDateTime.ToString(BatchTimeFormat, CultureInfo.InvariantCulture)
                + "-" + Guid.NewGuid().ToString("N");
            string batchDirectory = Path.Combine(taskRoot, batchName);
            string candidate = Path.Combine(taskRoot, ".staging-" + batchName);
            if (Path.Exists(candidate))
                throw new IOException("备份暂存目录已存在。");
            RejectLinkedPath(candidate);
            Directory.CreateDirectory(candidate);
            stagingDirectory = candidate;
            var backupService = new BackupService();
            foreach (var entry in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string output = Path.Combine(stagingDirectory, entry.Name);
                var manifest = backupService.Create(entry.Database, new BackupCreateOptions
                {
                    DestinationDirectory = output,
                    IncludeFullTextIndexes = task.Scope.IncludeFullTextIndexes,
                    IncludeKeyValue = task.Scope.IncludeKeyValue,
                    IncludeObjectContent = task.Scope.IncludeObjects,
                    ExcludeObjectMetadata = !task.Scope.IncludeObjects,
                    ModelScope = databaseScope,
                });
                if (task.VerifyAfterCreate)
                {
                    BackupVerificationResult verification = backupService.Verify(output);
                    if (!verification.IsValid)
                        throw new InvalidDataException($"数据库 '{entry.Name}' 的备份校验失败：{string.Join("; ", verification.Errors.Take(4))}");
                }

                _logger.LogInformation(
                    "备份任务 {TaskId} 已完成数据库 {Database}，文件 {FileCount}，字节 {Bytes}。",
                    task.Id,
                    entry.Name,
                    manifest.Files.Count,
                    manifest.Consistency.TotalBytes);
            }

            // 消息队列按整个服务实例备份，快照及其校验结果随同本批数据库备份一起发布。
            if (task.Scope.IncludeMessageQueue)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string queueOutput = Path.Combine(stagingDirectory, MessageQueueDirectoryName);
                SonnetMqInstanceSnapshotResult queueSnapshot = _messageQueue.CreateSnapshot(queueOutput);
                if (task.VerifyAfterCreate)
                    _ = SonnetMqStore.ReadSnapshotManifest(queueOutput);

                _logger.LogInformation(
                    "备份任务 {TaskId} 已完成实例消息队列快照，文件 {FileCount}，字节 {Bytes}。",
                    task.Id, queueSnapshot.FileCount, queueSnapshot.TotalBytes);
            }

            cancellationToken.ThrowIfCancellationRequested();
            BackupDestinationMount.EnsureMounted(_serverOptions.Backup, cancellationToken);
            RejectLinkedPath(stagingDirectory);
            RejectLinkedPath(batchDirectory);
            WriteBatchMarker(stagingDirectory, task.Id, batchName, databases, task.Scope.IncludeMessageQueue);
            // 同一父目录内原子发布，任何部分完成的副本都留在暂存目录。
            Directory.Move(stagingDirectory, batchDirectory);
            stagingDirectory = null;
            state.LastCompletedUtc = _timeProvider.GetUtcNow();
            state.Status = $"ok:{selected.Length + (task.Scope.IncludeMessageQueue ? 1 : 0)}";
            state.LastOutputDirectory = batchDirectory;
            state.LastError = null;
            TryApplyRetention(task, taskRoot, batchDirectory, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            state.LastCompletedUtc = _timeProvider.GetUtcNow();
            state.Status = "cancelled";
            state.LastError = "备份任务已取消。";
            throw;
        }
        catch (Exception exception)
        {
            state.LastCompletedUtc = _timeProvider.GetUtcNow();
            state.Status = "failed";
            state.LastError = exception.Message;
            _logger.LogError(exception, "备份任务 {TaskId} 执行失败。", task.Id);
        }
        finally
        {
            if (stagingDirectory is not null)
                TryRemoveStagingDirectory(stagingDirectory);
            _store.SetState(state);
        }
        return state;
    }

    /// <summary>校验目录归属、链接和数据库源路径，返回任务的专属根目录。</summary>
    internal string ValidateTaskDestination(BackupTaskDefinition task)
    {
        BackupTaskStore.ValidateTask(task);
        string globalRoot = _serverOptions.Backup.DestinationRoot!;
        string root = string.IsNullOrWhiteSpace(task.DestinationRoot)
            ? globalRoot
            : Path.GetFullPath(task.DestinationRoot);
        EnsureUnderRoot(root, globalRoot);
        string taskRoot = Path.Combine(root, task.Id);
        RejectLinkedPath(taskRoot);
        string defaultBackupRoot = Path.Combine(_registry.DataRoot, ".system", "backups");
        if (PathsOverlap(taskRoot, _registry.DataRoot) && !IsSameOrDescendant(taskRoot, defaultBackupRoot))
            throw new InvalidOperationException("数据根目录中仅 .system/backups 可存放备份任务。");
        if (_registry.ObjectStorageRootDirectory is { } objectRoot && PathsOverlap(taskRoot, objectRoot))
            throw new InvalidOperationException("备份任务目录不能覆盖外置对象存储卷。");
        foreach (BackupTaskDefinition existing in _store.ListTasks().Take(BackupTaskStore.MaximumTasks))
        {
            if (string.Equals(task.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
                continue;
            string existingRoot = string.IsNullOrWhiteSpace(existing.DestinationRoot)
                ? globalRoot
                : existing.DestinationRoot;
            if (Path.IsPathFullyQualified(existingRoot) && TsdbRegistry.IsValidName(existing.Id)
                && PathsOverlap(taskRoot, Path.Combine(existingRoot, existing.Id)))
                throw new InvalidOperationException("备份任务目录不能与其他任务目录重叠。");
        }
        IReadOnlyList<string> registered = _registry.ListDatabases();
        if (registered.Count > BackupTaskStore.MaximumDatabases)
            throw new InvalidOperationException("已加载数据库数量超出任务路径核验上限。");
        var requested = new HashSet<string>(task.DatabaseNames, StringComparer.OrdinalIgnoreCase);
        foreach (string name in registered)
        {
            if (!_registry.TryGet(name, out var database))
                continue;
            if (PathsOverlap(taskRoot, database.RootDirectory))
                throw new InvalidOperationException("备份任务目录不能覆盖活动数据库目录。");
            if (task.Scope.IncludeObjects && (requested.Count == 0 || requested.Contains(name))
                && !PathsEqual(database.ObjectStorageRootDirectory, Path.Combine(database.RootDirectory, "objects")))
                throw new InvalidOperationException($"数据库 '{name}' 使用外置对象卷，备份任务暂不支持备份该对象卷。");
        }
        return taskRoot;
    }

    /// <summary>仅回收有本任务完成标记且超出保留策略的已发布批次。</summary>
    private void TryApplyRetention(BackupTaskDefinition task, string taskRoot, string currentBatch, CancellationToken cancellationToken)
    {
        try
        {
            BackupDestinationMount.EnsureMounted(_serverOptions.Backup, cancellationToken);
            RejectLinkedPath(taskRoot);
            var cutoff = _timeProvider.GetUtcNow().UtcDateTime.AddDays(-task.RetentionDays);
            var time = Stopwatch.StartNew();
            string[] entries = Directory.EnumerateFileSystemEntries(taskRoot)
                .Take(MaximumRetentionEntries + 1).ToArray();
            if (entries.Length > MaximumRetentionEntries)
                throw new IOException("备份保留策略目录数量超过上限。");
            var candidates = new List<(string Path, DateTime Started)>();
            foreach (string entry in entries)
            {
                CheckRetentionBudget(time, cancellationToken);
                if (PathsEqual(entry, currentBatch) || !IsCompletedBatch(entry, task.Id, out DateTime started))
                    continue;
                candidates.Add((entry, started));
            }
            int keepOlder = task.RetentionCount - 1;
            foreach (var candidate in candidates.OrderByDescending(static item => item.Started)
                .ThenByDescending(static item => item.Path, StringComparer.Ordinal))
            {
                CheckRetentionBudget(time, cancellationToken);
                if (keepOlder-- > 0 && candidate.Started >= cutoff)
                    continue;
                if (IsCompletedBatch(candidate.Path, task.Id, out _))
                    DeleteOwnedTree(candidate.Path, time, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "备份任务 {TaskId} 已发布，新批次保留，旧批次清理提前结束。", task.Id);
        }
    }

    /// <summary>持久化任务 ID、批次 ID 和本次成功备份的目录名称。</summary>
    private static void WriteBatchMarker(string stage, string taskId, string batchName, IReadOnlyList<string> databases, bool includesMessageQueue)
    {
        using var stream = new FileStream(Path.Combine(stage, BatchMarkerName), FileMode.CreateNew, FileAccess.Write);
        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            writer.WriteLine(taskId);
            writer.WriteLine(batchName);
            foreach (string name in databases)
                writer.WriteLine(name);
            if (includesMessageQueue)
                writer.WriteLine(MessageQueueDirectoryName);
        }
        stream.Flush(flushToDisk: true);
    }

    /// <summary>核对批次名、归属标记及已发布组件的 manifest。</summary>
    private static bool IsCompletedBatch(string path, string taskId, out DateTime started)
    {
        started = default;
        string name = Path.GetFileName(path);
        if (name.Length != 48 || name[15] != '-' || !Guid.TryParseExact(name.AsSpan(16), "N", out _)
            || !DateTime.TryParseExact(name.AsSpan(0, 15), BatchTimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out started)
            || !Directory.Exists(path) || HasLink(path))
            return false;
        string marker = Path.Combine(path, BatchMarkerName);
        if (!File.Exists(marker) || HasLink(marker) || new FileInfo(marker).Length > 32_768)
            return false;
        string[] lines = File.ReadAllLines(marker);
        if (lines.Length is < 3 or > BackupTaskStore.MaximumDatabases + 3
            || lines[0] != taskId || lines[1] != name)
            return false;
        var expected = new HashSet<string>(StringComparer.Ordinal) { BatchMarkerName };
        foreach (string component in lines.AsSpan(2))
        {
            bool isMessageQueue = component == MessageQueueDirectoryName;
            if ((!isMessageQueue && (!TsdbRegistry.IsValidName(component) || BackupTaskStore.IsReservedDirectoryName(component)))
                || !expected.Add(component))
                return false;
            string componentPath = Path.Combine(path, component);
            string manifest = Path.Combine(componentPath, isMessageQueue ? SonnetMqInstanceSnapshotManifest.FileName : BackupManifest.FileName);
            if (!Directory.Exists(componentPath) || HasLink(componentPath)
                || !File.Exists(manifest) || HasLink(manifest))
                return false;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(path).Take(expected.Count + 1))
            if (!expected.Remove(Path.GetFileName(entry)))
                return false;
        return expected.Count == 0;
    }

    /// <summary>仅清理本次运行新建的暂存目录，并在无法确认时保留现场。</summary>
    private void TryRemoveStagingDirectory(string path)
    {
        try
        {
            DeleteOwnedTree(path, Stopwatch.StartNew(), CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "备份暂存目录未能清理，保留目录 {Path}。", path);
        }
    }

    /// <summary>先在时限和数量限制内确认待清理树没有链接，再删除已确认归属的目录。</summary>
    private static void DeleteOwnedTree(string path, Stopwatch time, CancellationToken cancellationToken)
    {
        RejectLinkedPath(path);
        var files = new List<string>();
        var directories = new List<string> { path };
        for (int index = 0; index < directories.Count; index++)
        {
            RejectLinkedPath(directories[index]);
            foreach (string child in Directory.EnumerateFileSystemEntries(directories[index]))
            {
                CheckRetentionBudget(time, cancellationToken);
                if (directories.Count + files.Count >= MaximumBatchEntries)
                    throw new IOException("备份批次文件数量超出清理上限。");
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("备份批次含有链接，保留目录。");
                if ((attributes & FileAttributes.Directory) != 0)
                    directories.Add(child);
                else
                    files.Add(child);
            }
        }
        foreach (string file in files)
        {
            CheckRetentionBudget(time, cancellationToken);
            RejectLinkedPath(file);
            File.Delete(file);
        }
        for (int index = directories.Count - 1; index >= 0; index--)
        {
            CheckRetentionBudget(time, cancellationToken);
            RejectLinkedPath(directories[index]);
            Directory.Delete(directories[index]);
        }
    }

    /// <summary>限制一次保留策略扫描的墙钟预算。</summary>
    private static void CheckRetentionBudget(Stopwatch time, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (time.Elapsed > TimeSpan.FromSeconds(30))
            throw new IOException("备份保留策略超过扫描时限。");
    }

    /// <summary>逐级核对目标路径，拒绝现存的符号链接和 Windows 重解析点。</summary>
    internal static void RejectLinkedPath(string path)
    {
        string? current = Path.GetFullPath(path);
        for (int depth = 0; current is not null && depth < 256; depth++, current = Path.GetDirectoryName(current))
            if (HasLink(current))
                throw new InvalidOperationException("备份目标路径不能包含符号链接或重解析点。");
        if (current is not null)
            throw new InvalidOperationException("备份目标路径深度超出上限。");
    }

    /// <summary>查验实际目录项，支持识别悬空链接。</summary>
    private static bool HasLink(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileInfo(path).LinkTarget is not null;
        }
    }

    /// <summary>判断两个规范化目录是否相等。</summary>
    private static bool PathsEqual(string left, string right)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>判断两个路径是否重叠。</summary>
    private static bool PathsOverlap(string first, string second)
        => IsSameOrDescendant(first, second) || IsSameOrDescendant(second, first);

    /// <summary>拒绝备份目录逃逸到全局安全根之外。</summary>
    private static void EnsureUnderRoot(string path, string root)
    {
        if (!IsSameOrDescendant(path, root))
            throw new InvalidOperationException("备份任务目标目录必须位于 Backup.DestinationRoot 下。");
    }

    /// <summary>使用完整路径片段判断目录的包含关系。</summary>
    private static bool IsSameOrDescendant(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
