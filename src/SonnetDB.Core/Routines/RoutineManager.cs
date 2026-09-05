using System.Collections.Frozen;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>管理单个数据库目录中的 SQL 过程、触发器与调用诊断。</summary>
public sealed class RoutineManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ProcedureDefinition> _procedures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TriggerDefinition> _triggers = new(StringComparer.Ordinal);
    private CatalogState _snapshot = new(
        FrozenDictionary<string, ProcedureDefinition>.Empty,
        FrozenDictionary<string, TriggerDefinition>.Empty,
        FrozenDictionary<(string, SqlTriggerEvent), IReadOnlyList<TriggerDefinition>>.Empty);

    /// <summary>打开独立版本化例程目录并加载已有定义。</summary>
    /// <param name="rootDirectory">例程目录。</param>
    public RoutineManager(string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        Directory.CreateDirectory(rootDirectory);
        CatalogPath = Path.Combine(rootDirectory, RoutineCatalogCodec.FileName);
        var loaded = RoutineCatalogCodec.Load(CatalogPath);
        foreach (var procedure in loaded.Procedures)
            _procedures.Add(procedure.Name, procedure);
        foreach (var trigger in loaded.Triggers)
            _triggers.Add(trigger.Name, trigger);
        PublishSnapshots();
        Diagnostics = new RoutineDiagnostics();
    }

    /// <summary>目录文件路径。</summary>
    public string CatalogPath { get; }

    /// <summary>过程与触发器调用诊断。</summary>
    public RoutineDiagnostics Diagnostics { get; }

    /// <summary>当前过程数量。</summary>
    public int ProcedureCount => Volatile.Read(ref _snapshot).Procedures.Count;

    /// <summary>当前触发器数量。</summary>
    public int TriggerCount => Volatile.Read(ref _snapshot).Triggers.Count;

    /// <summary>按名称读取过程；不存在时返回 null。</summary>
    /// <param name="name">过程名称。</param>
    /// <returns>过程定义或 null。</returns>
    public ProcedureDefinition? TryGetProcedure(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).Procedures.GetValueOrDefault(name);
    }

    /// <summary>按名称读取触发器；不存在时返回 null。</summary>
    /// <param name="name">触发器名称。</param>
    /// <returns>触发器定义或 null。</returns>
    public TriggerDefinition? TryGetTrigger(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).Triggers.GetValueOrDefault(name);
    }

    /// <summary>返回按名称升序排列的过程快照。</summary>
    /// <returns>过程定义列表。</returns>
    public IReadOnlyList<ProcedureDefinition> ListProcedures()
        => Volatile.Read(ref _snapshot).Procedures.Values
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>返回按目标表、创建时间和名称确定性排序的触发器快照。</summary>
    /// <param name="tableName">可选目标表过滤。</param>
    /// <returns>触发器定义列表。</returns>
    public IReadOnlyList<TriggerDefinition> ListTriggers(string? tableName = null)
        => Volatile.Read(ref _snapshot).Triggers.Values
            .Where(value => tableName is null || string.Equals(value.TableName, tableName, StringComparison.Ordinal))
            .OrderBy(static value => value.TableName, StringComparer.Ordinal)
            .ThenBy(static value => value.ExecutionOrder)
            .ThenBy(static value => value.CreatedAtUtcTicks)
            .ThenBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<TriggerDefinition> FindTriggers(string tableName, SqlTriggerEvent triggerEvent)
        => Volatile.Read(ref _snapshot).Dispatch.GetValueOrDefault((tableName, triggerEvent)) ?? [];

    internal IReadOnlyList<ProcedureDefinition> FindProceduresDependingOnObject(string objectName)
        => Volatile.Read(ref _snapshot).Procedures.Values
            .Where(value => value.ObjectDependencies.Contains(objectName, StringComparer.Ordinal))
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<TriggerDefinition> FindTriggersDependingOnObject(string objectName)
        => Volatile.Read(ref _snapshot).Triggers.Values
            .Where(value => value.ObjectDependencies.Contains(objectName, StringComparer.Ordinal))
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<ProcedureDefinition> FindProceduresCalling(string procedureName)
        => Volatile.Read(ref _snapshot).Procedures.Values
            .Where(value => value.ProcedureDependencies.Contains(procedureName, StringComparer.Ordinal))
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal void Create(ProcedureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_procedures.ContainsKey(definition.Name))
                throw new InvalidOperationException($"procedure '{definition.Name}' 已存在。");
            _procedures.Add(definition.Name, definition);
            PersistOrRollback(() => _procedures.Remove(definition.Name));
        }
    }

    internal void Create(TriggerDefinition definition, string? relativeTo = null, bool precedes = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_triggers.ContainsKey(definition.Name))
                throw new InvalidOperationException($"trigger '{definition.Name}' 已存在。");
            var previous = _triggers.ToArray();
            try
            {
                var group = OrderedGroup(definition);
                _triggers.Add(definition.Name, definition.WithLifecycle(order:
                    group.Count == 0 ? 0 : checked(group[^1].ExecutionOrder + 1)));
                if (relativeTo is not null) MoveTrigger(definition.Name, relativeTo, precedes);
                PersistOrRollback(() => RestoreTriggers(previous));
            }
            catch { RestoreTriggers(previous); throw; }
        }
    }

    internal void Alter(AlterTriggerStatement statement)
    {
        lock (_sync)
        {
            var existing = _triggers.GetValueOrDefault(statement.Name)
                ?? throw new RoutineExecutionException(RoutineErrorCodes.TriggerNotFound, $"trigger '{statement.Name}' 不存在。");
            var previous = _triggers.ToArray();
            try
            {
                switch (statement.Action)
                {
                    case SqlAlterTriggerAction.Enable:
                    case SqlAlterTriggerAction.Disable:
                        _triggers[existing.Name] = existing.WithLifecycle(enabled: statement.Action == SqlAlterTriggerAction.Enable);
                        break;
                    case SqlAlterTriggerAction.Rename:
                        ArgumentException.ThrowIfNullOrWhiteSpace(statement.Target);
                        if (_triggers.ContainsKey(statement.Target))
                            throw new InvalidOperationException($"trigger '{statement.Target}' 已存在。");
                        _triggers.Remove(existing.Name);
                        _triggers.Add(statement.Target, existing.WithLifecycle(name: statement.Target));
                        break;
                    case SqlAlterTriggerAction.Follows:
                    case SqlAlterTriggerAction.Precedes:
                        ArgumentException.ThrowIfNullOrWhiteSpace(statement.Target);
                        MoveTrigger(existing.Name, statement.Target, statement.Action == SqlAlterTriggerAction.Precedes);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(statement));
                }
                PersistOrRollback(() => RestoreTriggers(previous));
            }
            catch { RestoreTriggers(previous); throw; }
        }
    }

    private List<TriggerDefinition> OrderedGroup(TriggerDefinition definition)
        => _triggers.Values.Where(value => value.TableName == definition.TableName && value.Event == definition.Event)
            .OrderBy(static value => value.ExecutionOrder)
            .ThenBy(static value => value.CreatedAtUtcTicks)
            .ThenBy(static value => value.Name, StringComparer.Ordinal).ToList();

    private void MoveTrigger(string name, string relativeTo, bool precedes)
    {
        var definition = _triggers[name];
        if (name == relativeTo || !_triggers.TryGetValue(relativeTo, out var reference)
            || reference.TableName != definition.TableName || reference.Event != definition.Event)
            throw new RoutineExecutionException(RoutineErrorCodes.Dependency,
                "FOLLOWS/PRECEDES 必须引用不同的、已存在的同表同事件触发器。");
        var group = OrderedGroup(definition);
        group.RemoveAll(value => value.Name == name);
        int index = group.FindIndex(value => value.Name == relativeTo);
        group.Insert(precedes ? index : index + 1, definition);
        for (int order = 0; order < group.Count; order++)
            _triggers[group[order].Name] = group[order].WithLifecycle(order: order);
    }

    private void RestoreTriggers(KeyValuePair<string, TriggerDefinition>[] previous)
    {
        _triggers.Clear();
        foreach (var pair in previous) _triggers.Add(pair.Key, pair.Value);
    }

    internal bool DropProcedure(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            if (!_procedures.Remove(name, out var existing))
                return false;
            PersistOrRollback(() => _procedures.Add(existing.Name, existing));
            return true;
        }
    }

    internal bool DropTrigger(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            if (!_triggers.Remove(name, out var existing))
                return false;
            PersistOrRollback(() => _triggers.Add(existing.Name, existing));
            return true;
        }
    }

    private void PersistOrRollback(Action rollback)
    {
        CatalogState snapshot;
        try
        {
            snapshot = BuildSnapshot();
            RoutineCatalogCodec.Save(
                CatalogPath,
                snapshot.Procedures.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
                snapshot.Triggers.Values
                    .OrderBy(static value => value.TableName, StringComparer.Ordinal)
                    .ThenBy(static value => value.CreatedAtUtcTicks)
                    .ThenBy(static value => value.Name, StringComparer.Ordinal)
                    .ToArray());
        }
        catch
        {
            rollback();
            throw;
        }

        Volatile.Write(ref _snapshot, snapshot);
    }

    private void PublishSnapshots()
    {
        Volatile.Write(ref _snapshot, BuildSnapshot());
    }

    private CatalogState BuildSnapshot() => new(
        _procedures.ToFrozenDictionary(StringComparer.Ordinal),
        _triggers.ToFrozenDictionary(StringComparer.Ordinal),
        _triggers.Values.Where(static trigger => trigger.Enabled)
            .GroupBy(static trigger => (trigger.TableName, trigger.Event))
            .ToFrozenDictionary(static group => group.Key,
                static group => (IReadOnlyList<TriggerDefinition>)Array.AsReadOnly(group
                    .OrderBy(static trigger => trigger.ExecutionOrder)
                    .ThenBy(static trigger => trigger.CreatedAtUtcTicks)
                    .ThenBy(static trigger => trigger.Name, StringComparer.Ordinal).ToArray())));

    private sealed record CatalogState(
        FrozenDictionary<string, ProcedureDefinition> Procedures,
        FrozenDictionary<string, TriggerDefinition> Triggers,
        FrozenDictionary<(string, SqlTriggerEvent), IReadOnlyList<TriggerDefinition>> Dispatch);
}
