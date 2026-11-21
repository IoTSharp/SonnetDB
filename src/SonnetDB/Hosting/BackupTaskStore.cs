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
            int index = _state.Tasks.FindIndex(candidate => string.Equals(candidate.Id, task.Id, StringComparison.Ordinal));
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
            int index = _state.States.FindIndex(candidate => string.Equals(candidate.TaskId, state.TaskId, StringComparison.Ordinal));
            if (index < 0)
                _state.States.Add(CloneState(state));
            else
                _state.States[index] = CloneState(state);
            PersistLocked();
        }
    }

    /// <summary>验证任务中会影响文件系统的字段。</summary>
    private static void ValidateTask(BackupTaskDefinition task)
    {
        if (string.IsNullOrWhiteSpace(task.Id) || task.Id.Length > 96)
            throw new ArgumentException("备份任务 Id 不能为空且长度不能超过 96。", nameof(task));
        if (string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 160)
            throw new ArgumentException("备份任务名称不能为空且长度不能超过 160。", nameof(task));
        if (!TimeOnly.TryParseExact(task.LocalTime, "HH:mm", out _))
            throw new ArgumentException("备份任务 LocalTime 必须使用 HH:mm 格式。", nameof(task));
        if (task.RetentionDays is < 1 or > 3650 || task.RetentionCount is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(task), "备份保留策略超出允许范围。");
        if (task.DatabaseNames.Any(name => !TsdbRegistry.IsValidName(name)))
            throw new ArgumentException("备份任务包含非法数据库名称。", nameof(task));
        if (task.Scope is null)
            task.Scope = new BackupScopeOptions();
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
