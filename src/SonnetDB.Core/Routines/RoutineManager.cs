using System.Collections.Frozen;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

/// <summary>管理单个数据库目录中的 SQL 过程、触发器与调用诊断。</summary>
public sealed class RoutineManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ProcedureDefinition> _procedures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TriggerDefinition> _triggers = new(StringComparer.Ordinal);
    private FrozenDictionary<string, ProcedureDefinition> _procedureSnapshot =
        FrozenDictionary<string, ProcedureDefinition>.Empty;
    private FrozenDictionary<string, TriggerDefinition> _triggerSnapshot =
        FrozenDictionary<string, TriggerDefinition>.Empty;

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
    public int ProcedureCount => Volatile.Read(ref _procedureSnapshot).Count;

    /// <summary>当前触发器数量。</summary>
    public int TriggerCount => Volatile.Read(ref _triggerSnapshot).Count;

    /// <summary>按名称读取过程；不存在时返回 null。</summary>
    /// <param name="name">过程名称。</param>
    /// <returns>过程定义或 null。</returns>
    public ProcedureDefinition? TryGetProcedure(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _procedureSnapshot).GetValueOrDefault(name);
    }

    /// <summary>按名称读取触发器；不存在时返回 null。</summary>
    /// <param name="name">触发器名称。</param>
    /// <returns>触发器定义或 null。</returns>
    public TriggerDefinition? TryGetTrigger(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _triggerSnapshot).GetValueOrDefault(name);
    }

    /// <summary>返回按名称升序排列的过程快照。</summary>
    /// <returns>过程定义列表。</returns>
    public IReadOnlyList<ProcedureDefinition> ListProcedures()
        => Volatile.Read(ref _procedureSnapshot).Values
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>返回按目标表、创建时间和名称确定性排序的触发器快照。</summary>
    /// <param name="tableName">可选目标表过滤。</param>
    /// <returns>触发器定义列表。</returns>
    public IReadOnlyList<TriggerDefinition> ListTriggers(string? tableName = null)
        => Volatile.Read(ref _triggerSnapshot).Values
            .Where(value => tableName is null || string.Equals(value.TableName, tableName, StringComparison.Ordinal))
            .OrderBy(static value => value.TableName, StringComparer.Ordinal)
            .ThenBy(static value => value.CreatedAtUtcTicks)
            .ThenBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<TriggerDefinition> FindTriggers(string tableName, SqlTriggerEvent triggerEvent)
        => Volatile.Read(ref _triggerSnapshot).Values
            .Where(value => value.Event == triggerEvent
                            && string.Equals(value.TableName, tableName, StringComparison.Ordinal))
            .OrderBy(static value => value.CreatedAtUtcTicks)
            .ThenBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<ProcedureDefinition> FindProceduresDependingOnObject(string objectName)
        => Volatile.Read(ref _procedureSnapshot).Values
            .Where(value => value.ObjectDependencies.Contains(objectName, StringComparer.Ordinal))
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<TriggerDefinition> FindTriggersDependingOnObject(string objectName)
        => Volatile.Read(ref _triggerSnapshot).Values
            .Where(value => value.ObjectDependencies.Contains(objectName, StringComparer.Ordinal))
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();

    internal IReadOnlyList<ProcedureDefinition> FindProceduresCalling(string procedureName)
        => Volatile.Read(ref _procedureSnapshot).Values
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

    internal void Create(TriggerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_triggers.ContainsKey(definition.Name))
                throw new InvalidOperationException($"trigger '{definition.Name}' 已存在。");
            _triggers.Add(definition.Name, definition);
            PersistOrRollback(() => _triggers.Remove(definition.Name));
        }
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
        FrozenDictionary<string, ProcedureDefinition> procedureSnapshot;
        FrozenDictionary<string, TriggerDefinition> triggerSnapshot;
        try
        {
            procedureSnapshot = _procedures.ToFrozenDictionary(StringComparer.Ordinal);
            triggerSnapshot = _triggers.ToFrozenDictionary(StringComparer.Ordinal);
            RoutineCatalogCodec.Save(
                CatalogPath,
                procedureSnapshot.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
                triggerSnapshot.Values
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

        Volatile.Write(ref _procedureSnapshot, procedureSnapshot);
        Volatile.Write(ref _triggerSnapshot, triggerSnapshot);
    }

    private void PublishSnapshots()
    {
        Volatile.Write(ref _procedureSnapshot, _procedures.ToFrozenDictionary(StringComparer.Ordinal));
        Volatile.Write(ref _triggerSnapshot, _triggers.ToFrozenDictionary(StringComparer.Ordinal));
    }
}
