using System.Collections.Frozen;

namespace SonnetDB.Views;

/// <summary>
/// 逻辑视图定义的线程安全目录。
/// </summary>
public sealed class ViewCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ViewDefinition> _mutable = new(StringComparer.Ordinal);
    private FrozenDictionary<string, ViewDefinition> _snapshot =
        FrozenDictionary<string, ViewDefinition>.Empty;

    /// <summary>由所属 ViewManager 安装的目录变更守卫；独立 catalog 默认不限制直接变更。</summary>
    internal Action<string, string>? MutationGuard { get; set; }

    /// <summary>当前视图数量。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _mutable.Count;
        }
    }

    /// <summary>
    /// 新增视图定义。
    /// </summary>
    /// <param name="definition">待新增的视图定义。</param>
    public void Add(ViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        MutationGuard?.Invoke(definition.Name, "ADD");
        lock (_sync)
        {
            if (_mutable.ContainsKey(definition.Name))
                throw new InvalidOperationException($"view '{definition.Name}' 已存在。");
            _mutable.Add(definition.Name, definition);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 加载或替换视图定义，主要用于启动恢复。
    /// </summary>
    /// <param name="definition">视图定义。</param>
    public void LoadOrReplace(ViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        MutationGuard?.Invoke(definition.Name, "LOAD OR REPLACE");
        lock (_sync)
        {
            _mutable[definition.Name] = definition;
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除视图定义。
    /// </summary>
    /// <param name="name">视图名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        MutationGuard?.Invoke(name, "REMOVE");
        lock (_sync)
        {
            if (!_mutable.Remove(name))
                return false;
            PublishSnapshot();
            return true;
        }
    }

    /// <summary>
    /// 尝试按名称读取视图定义。
    /// </summary>
    /// <param name="name">视图名称。</param>
    /// <returns>找到时返回定义，否则返回 <c>null</c>。</returns>
    public ViewDefinition? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).TryGetValue(name, out var definition)
            ? definition
            : null;
    }

    /// <summary>
    /// 返回按视图名升序排列的当前快照。
    /// </summary>
    /// <returns>不可变语义的视图定义列表。</returns>
    public IReadOnlyList<ViewDefinition> Snapshot()
        => Volatile.Read(ref _snapshot).Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, _mutable.ToFrozenDictionary(StringComparer.Ordinal));
}
