using System.Collections.Frozen;

namespace SonnetDB.Graphs;

/// <summary>SQL/PGQ 关系映射图的线程安全目录。</summary>
public sealed class PropertyGraphCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PropertyGraphDefinition> _mutable = new(StringComparer.Ordinal);
    private FrozenDictionary<string, PropertyGraphDefinition> _snapshot =
        FrozenDictionary<string, PropertyGraphDefinition>.Empty;
    private long _revision;

    /// <summary>初始化空目录。</summary>
    public PropertyGraphCatalog()
    {
    }

    internal PropertyGraphCatalog(PropertyGraphCatalogState state) => Restore(state);

    /// <summary>当前映射图数量。</summary>
    public int Count => Volatile.Read(ref _snapshot).Count;

    /// <summary>当前目录修订号。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>按名称查找映射图。</summary>
    /// <param name="name">映射图名称。</param>
    /// <returns>找到时返回定义，否则返回 <c>null</c>。</returns>
    public PropertyGraphDefinition? TryGet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Volatile.Read(ref _snapshot).GetValueOrDefault(name);
    }

    /// <summary>返回按名称升序排列的目录快照。</summary>
    /// <returns>不可变语义的映射定义列表。</returns>
    public IReadOnlyList<PropertyGraphDefinition> Snapshot()
        => Volatile.Read(ref _snapshot).Values
            .OrderBy(static item => item.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>返回引用指定关系表的映射图。</summary>
    /// <param name="tableName">关系表名称。</param>
    /// <returns>按映射图名称排序的依赖定义。</returns>
    public IReadOnlyList<PropertyGraphDefinition> FindDependents(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return Snapshot().Where(definition =>
                definition.VertexTables.Any(item => string.Equals(item.TableName, tableName, StringComparison.Ordinal))
                || definition.EdgeTables.Any(item => string.Equals(item.TableName, tableName, StringComparison.Ordinal)))
            .ToArray();
    }

    internal PropertyGraphCatalogState CaptureState()
    {
        lock (_sync)
            return new PropertyGraphCatalogState(_revision, Snapshot());
    }

    internal void Add(PropertyGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (!_mutable.TryAdd(definition.Name, definition))
                throw new InvalidOperationException($"property graph '{definition.Name}' 已存在。");
            _revision = checked(_revision + 1);
            PublishSnapshot();
        }
    }

    internal bool Remove(string name)
    {
        lock (_sync)
        {
            if (!_mutable.Remove(name))
                return false;
            _revision = checked(_revision + 1);
            PublishSnapshot();
            return true;
        }
    }

    internal void Restore(PropertyGraphCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 0)
            throw new InvalidDataException("PropertyGraphCatalog: revision 不能为负数。");
        lock (_sync)
        {
            _mutable.Clear();
            foreach (PropertyGraphDefinition definition in state.Definitions)
                if (!_mutable.TryAdd(definition.Name, definition))
                    throw new InvalidDataException($"PropertyGraphCatalog: duplicate graph '{definition.Name}'。");
            _revision = state.Revision;
            PublishSnapshot();
        }
    }

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, _mutable.ToFrozenDictionary(StringComparer.Ordinal));
}

internal sealed record PropertyGraphCatalogState(
    long Revision,
    IReadOnlyList<PropertyGraphDefinition> Definitions);
