using System.Collections.Frozen;

namespace SonnetDB.Views;

/// <summary>
/// 逻辑视图定义的线程安全目录。
/// </summary>
public sealed class ViewCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ViewDefinition> _mutable = new(StringComparer.Ordinal);
    private CatalogSnapshot _snapshot = new(
        FrozenDictionary<string, ViewDefinition>.Empty,
        new Dictionary<string, ViewDefinition?>(StringComparer.OrdinalIgnoreCase).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

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
            if (Volatile.Read(ref _snapshot).Unquoted.ContainsKey(definition.Name))
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
            if (!_mutable.ContainsKey(definition.Name)
                && Volatile.Read(ref _snapshot).Unquoted.ContainsKey(definition.Name))
                throw new InvalidOperationException($"view '{definition.Name}' 已存在仅大小写不同的名称。");
            _mutable[definition.Name] = definition;
            PublishSnapshot();
        }
    }

    /// <summary>恢复旧目录时保留精确名称及历史大小写冲突。</summary>
    internal void LoadExisting(ViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            _mutable.Add(definition.Name, definition);
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
        return Volatile.Read(ref _snapshot).Exact.TryGetValue(name, out var definition)
            ? definition
            : null;
    }

    /// <summary>解析 SQL 视图名称；普通引用忽略大小写，引号引用精确匹配。</summary>
    /// <param name="name">引用的名称。</param>
    /// <param name="quoted">是否使用双引号。</param>
    /// <returns>匹配的定义；不存在时返回 null。</returns>
    public ViewDefinition? Resolve(string name, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(name);
        var snapshot = Volatile.Read(ref _snapshot);
        if (quoted)
            return snapshot.Exact.GetValueOrDefault(name);
        if (!snapshot.Unquoted.TryGetValue(name, out var definition))
            return null;
        return definition ?? throw new InvalidOperationException($"view '{name}' 存在大小写歧义；请使用双引号精确引用并显式迁移。");
    }

    /// <summary>
    /// 返回按视图名升序排列的当前快照。
    /// </summary>
    /// <returns>不可变语义的视图定义列表。</returns>
    public IReadOnlyList<ViewDefinition> Snapshot()
        => Volatile.Read(ref _snapshot).Exact.Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private void PublishSnapshot()
    {
        var unquoted = new Dictionary<string, ViewDefinition?>(_mutable.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _mutable.Values)
            if (!unquoted.TryAdd(definition.Name, definition))
                unquoted[definition.Name] = null;
        Volatile.Write(ref _snapshot, new CatalogSnapshot(
            _mutable.ToFrozenDictionary(StringComparer.Ordinal),
            unquoted.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)));
    }

    private sealed record CatalogSnapshot(
        FrozenDictionary<string, ViewDefinition> Exact,
        FrozenDictionary<string, ViewDefinition?> Unquoted);
}
