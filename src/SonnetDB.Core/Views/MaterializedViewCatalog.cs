using System.Collections.Frozen;

namespace SonnetDB.Views;

/// <summary>
/// 物化视图定义和刷新元数据的线程安全目录。
/// </summary>
public sealed class MaterializedViewCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, MaterializedViewDefinition> _mutable = new(StringComparer.Ordinal);
    private FrozenDictionary<string, MaterializedViewDefinition> _snapshot =
        FrozenDictionary<string, MaterializedViewDefinition>.Empty;

    /// <summary>当前物化视图数量。</summary>
    public int Count => Volatile.Read(ref _snapshot).Count;

    /// <summary>
    /// 新增物化视图定义。
    /// </summary>
    /// <param name="definition">待新增定义。</param>
    public void Add(MaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_mutable.ContainsKey(definition.Name))
                throw new InvalidOperationException($"materialized view '{definition.Name}' 已存在。");
            _mutable.Add(definition.Name, definition);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 加载或替换物化视图定义，供启动恢复和原子状态发布使用。
    /// </summary>
    /// <param name="definition">物化视图定义。</param>
    public void LoadOrReplace(MaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            _mutable[definition.Name] = definition;
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除物化视图定义。
    /// </summary>
    /// <param name="name">物化视图名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            if (!_mutable.Remove(name))
                return false;
            PublishSnapshot();
            return true;
        }
    }

    /// <summary>
    /// 尝试按名称读取当前定义。
    /// </summary>
    /// <param name="name">物化视图名称。</param>
    /// <returns>找到时返回定义，否则返回 <c>null</c>。</returns>
    public MaterializedViewDefinition? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).TryGetValue(name, out var definition)
            ? definition
            : null;
    }

    /// <summary>
    /// 返回按名称升序排列的当前目录快照。
    /// </summary>
    /// <returns>不可变语义的定义列表。</returns>
    public IReadOnlyList<MaterializedViewDefinition> Snapshot()
        => Volatile.Read(ref _snapshot).Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, _mutable.ToFrozenDictionary(StringComparer.Ordinal));
}
