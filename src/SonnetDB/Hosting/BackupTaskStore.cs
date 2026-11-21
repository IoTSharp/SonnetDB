using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Json;

namespace SonnetDB.Hosting;

/// <summary>
/// 备份任务定义和最近运行状态的原子文件存储。
/// </summary>
internal sealed class BackupTaskStore
{
    private readonly object _sync = new();
    private readonly string _path;
    private BackupTaskFile _state;
    internal const int MaximumTasks = 256;
    internal const int MaximumDatabases = 256;

    /// <summary>初始化任务存储并读取已有定义。</summary>
    /// <param name="dataRoot">服务器数据根目录。</param>
    public BackupTaskStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _path = Path.Combine(Path.GetFullPath(dataRoot), ".system", "backup-tasks.json");
        _state = AtomicJsonFile.Read(_path, ServerJsonContext.Default.BackupTaskFile, static () => new BackupTaskFile());
        NormalizeState(_state);
    }

    /// <summary>读取任务定义快照。</summary>
    public IReadOnlyList<BackupTaskDefinition> ListTasks()
    {
        lock (_sync)
            return _state.Tasks.Select(CloneTask).ToArray();
    }

    /// <summary>读取指定任务。</summary>
    public BackupTaskDefinition? TryGetTask(string id)
    {
        lock (_sync)
            return _state.Tasks.FirstOrDefault(task => string.Equals(task.Id, id, StringComparison.Ordinal)) is { } task
                ? CloneTask(task)
                : null;
    }

    /// <summary>新增或替换任务定义。</summary>
    public BackupTaskDefinition Upsert(BackupTaskDefinition task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ValidateTask(task);
        lock (_sync)
        {
            int index = _state.Tasks.FindIndex(candidate => string.Equals(candidate.Id, task.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && !string.Equals(_state.Tasks[index].Id, task.Id, StringComparison.Ordinal))
                throw new ArgumentException("备份任务 ID 不得仅有大小写差异。", nameof(task));
            if (index < 0 && _state.Tasks.Count >= MaximumTasks)
                throw new ArgumentException("备份任务数量已达到上限。", nameof(task));
            if (index < 0)
                _state.Tasks.Add(CloneTask(task));
            else
                _state.Tasks[index] = CloneTask(task);
            PersistLocked();
            return CloneTask(task);
        }
    }

    /// <summary>删除指定任务及其状态。</summary>
    public bool Remove(string id)
    {
        lock (_sync)
        {
            int removed = _state.Tasks.RemoveAll(task => string.Equals(task.Id, id, StringComparison.Ordinal));
            _state.States.RemoveAll(state => string.Equals(state.TaskId, id, StringComparison.Ordinal));
            if (removed == 0)
                return false;
            PersistLocked();
            return true;
        }
    }

    /// <summary>读取任务运行状态快照。</summary>
    public BackupTaskState GetState(string id)
    {
        lock (_sync)
        {
            var state = _state.States.FirstOrDefault(candidate => string.Equals(candidate.TaskId, id, StringComparison.Ordinal));
            return state is null ? new BackupTaskState { TaskId = id } : CloneState(state);
        }
    }

    /// <summary>原子写入任务运行状态。</summary>
    public void SetState(BackupTaskState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_sync)
        {
            if (!_state.Tasks.Exists(task => string.Equals(task.Id, state.TaskId, StringComparison.Ordinal)))
                return;
            int index = _state.States.FindIndex(candidate => string.Equals(candidate.TaskId, state.TaskId, StringComparison.Ordinal));
            if (index < 0)
                _state.States.Add(CloneState(state));
            else
                _state.States[index] = CloneState(state);
            PersistLocked();
        }
    }

    /// <summary>验证任务中会影响文件系统的字段。</summary>
    internal static void ValidateTask(BackupTaskDefinition task)
    {
        if (task.Id is not { Length: >= 1 and <= 96 }
            || !task.Id.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            || IsReservedDirectoryName(task.Id))
            throw new ArgumentException("备份任务 ID 必须为 1 至 96 个字母、数字、短横线或下划线，且不能使用设备保留名。", nameof(task));
        if (string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 160)
            throw new ArgumentException("备份任务名称不能为空且长度不能超过 160。", nameof(task));
        if (!TimeOnly.TryParseExact(task.LocalTime, "HH:mm", out _))
            throw new ArgumentException("备份任务 LocalTime 必须使用 HH:mm 格式。", nameof(task));
        if (task.RetentionDays is < 1 or > 3650 || task.RetentionCount is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(task), "备份保留策略超出允许范围。");
        if (task.DatabaseNames is null || task.DatabaseNames.Count > MaximumDatabases
            || task.DatabaseNames.Any(name => !TsdbRegistry.IsValidName(name) || IsReservedDirectoryName(name))
            || task.DatabaseNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != task.DatabaseNames.Count)
            throw new ArgumentException("备份任务包含非法、重复或超出数量上限的数据库名称。", nameof(task));
        if (task.DestinationRoot is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(task.DestinationRoot)
            && !Path.IsPathFullyQualified(task.DestinationRoot))
            throw new ArgumentException("备份任务目标必须为绝对路径。", nameof(task));
        if (string.IsNullOrWhiteSpace(task.TimeZoneId) || task.TimeZoneId.Length > 128)
            throw new ArgumentException("备份任务时区无效。", nameof(task));
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException("备份任务时区无效。", nameof(task), exception);
        }
        ValidateScope(task.Scope ?? new BackupScopeOptions());
        task.Scope ??= new BackupScopeOptions();
    }

    /// <summary>跨平台拒绝不能用作普通目录的设备保留名。</summary>
    internal static bool IsReservedDirectoryName(string name)
    {
        string upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4
                && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal))
                && char.IsAsciiDigit(upper[3]));
    }

    /// <summary>拒绝暂时无法一致备份的资源选择，并保留可准确执行的范围。</summary>
    private static void ValidateScope(BackupScopeOptions scope)
    {
        scope.Measurements ??= [];
        scope.Tables ??= [];
        scope.Keyspaces ??= [];
        scope.DocumentCollections ??= [];
        scope.Buckets ??= [];
        scope.Graphs ??= [];
        if (scope.IncludeMessageQueue || scope.Measurements.Count != 0 || scope.Tables.Count != 0
            || scope.Buckets.Count != 0 || scope.Keyspaces.Count != 0
            || scope.DocumentCollections.Count != 0 || scope.Graphs.Count != 0)
        {
            throw new ArgumentException("备份任务暂不支持实例消息队列和按资源名称选择；请按数据模型选择备份范围。", nameof(scope));
        }
        if (!scope.IncludeMeasurements && !scope.IncludeTables && !scope.IncludeDocuments
            && !scope.IncludeGraphs && !scope.IncludeKeyValue && !scope.IncludeObjects)
            throw new ArgumentException("备份任务必须包含至少一种数据模型。", nameof(scope));
        if (scope.IncludeObjects && !scope.IncludeKeyValue)
            throw new ArgumentException("备份对象正文时必须包含存有对象元数据的 KV 数据模型。", nameof(scope));
    }

    /// <summary>规范化旧任务文件中的空集合和状态。</summary>
    private static void NormalizeState(BackupTaskFile state)
    {
        state.Tasks ??= [];
        state.States ??= [];
        foreach (var task in state.Tasks)
        {
            task.DatabaseNames ??= [];
            task.Scope ??= new BackupScopeOptions();
            task.Scope.Measurements ??= [];
            task.Scope.Tables ??= [];
            task.Scope.Keyspaces ??= [];
            task.Scope.DocumentCollections ??= [];
            task.Scope.Buckets ??= [];
            task.Scope.Graphs ??= [];
        }
    }

    /// <summary>写入当前状态。</summary>
    private void PersistLocked()
        => AtomicJsonFile.Write(_path, _state, ServerJsonContext.Default.BackupTaskFile);

    private static BackupTaskDefinition CloneTask(BackupTaskDefinition task)
        => new()
        {
            Id = task.Id,
            Name = task.Name,
            Enabled = task.Enabled,
            LocalTime = task.LocalTime,
            TimeZoneId = task.TimeZoneId,
            DestinationRoot = task.DestinationRoot,
            DatabaseNames = [.. task.DatabaseNames],
            Scope = new BackupScopeOptions
            {
                IncludeMeasurements = task.Scope.IncludeMeasurements,
                IncludeTables = task.Scope.IncludeTables,
                IncludeKeyValue = task.Scope.IncludeKeyValue,
                IncludeDocuments = task.Scope.IncludeDocuments,
                IncludeObjects = task.Scope.IncludeObjects,
                IncludeMessageQueue = task.Scope.IncludeMessageQueue,
                IncludeGraphs = task.Scope.IncludeGraphs,
                IncludeFullTextIndexes = task.Scope.IncludeFullTextIndexes,
                Measurements = [.. task.Scope.Measurements],
                Tables = [.. task.Scope.Tables],
                Keyspaces = [.. task.Scope.Keyspaces],
                DocumentCollections = [.. task.Scope.DocumentCollections],
                Buckets = [.. task.Scope.Buckets],
                Graphs = [.. task.Scope.Graphs],
            },
            RetentionDays = task.RetentionDays,
            RetentionCount = task.RetentionCount,
            VerifyAfterCreate = task.VerifyAfterCreate,
        };

    private static BackupTaskState CloneState(BackupTaskState state)
        => new()
        {
            TaskId = state.TaskId,
            LastStartedUtc = state.LastStartedUtc,
            LastCompletedUtc = state.LastCompletedUtc,
            Status = state.Status,
            LastOutputDirectory = state.LastOutputDirectory,
            LastError = state.LastError,
        };
}
