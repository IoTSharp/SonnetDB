using System.Collections.Frozen;

namespace SonnetDB.Views;

/// <summary>
/// 物化视图定义和刷新元数据的线程安全目录。
/// </summary>
public sealed class MaterializedViewCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, MaterializedViewDefinition> _mutable = new(StringComparer.Ordinal);
    private CatalogSnapshot _snapshot = new(
        FrozenDictionary<string, MaterializedViewDefinition>.Empty,
        new Dictionary<string, MaterializedViewDefinition?>(StringComparer.OrdinalIgnoreCase).ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    /// <summary>由所属 MaterializedViewManager 安装的目录变更守卫；独立 catalog 默认不限制直接变更。</summary>
    internal Action<string, string>? MutationGuard { get; set; }

    /// <summary>当前物化视图数量。</summary>
    public int Count => Volatile.Read(ref _snapshot).Exact.Count;

    /// <summary>
    /// 新增物化视图定义。
    /// </summary>
    /// <param name="definition">待新增定义。</param>
    public void Add(MaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        MutationGuard?.Invoke(definition.Name, "ADD");
        lock (_sync)
        {
            if (Volatile.Read(ref _snapshot).Unquoted.ContainsKey(definition.Name))
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
        MutationGuard?.Invoke(definition.Name, "LOAD OR REPLACE");
        lock (_sync)
        {
            if (!_mutable.ContainsKey(definition.Name)
                && Volatile.Read(ref _snapshot).Unquoted.ContainsKey(definition.Name))
                throw new InvalidOperationException($"materialized view '{definition.Name}' 已存在仅大小写不同的名称。");
            _mutable[definition.Name] = definition;
            PublishSnapshot();
        }
    }

    /// <summary>恢复旧目录时保留精确名称及历史大小写冲突。</summary>
    internal void LoadExisting(MaterializedViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            _mutable.Add(definition.Name, definition);
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
    /// 尝试按名称读取当前定义。
    /// </summary>
    /// <param name="name">物化视图名称。</param>
    /// <returns>找到时返回定义，否则返回 <c>null</c>。</returns>
    public MaterializedViewDefinition? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).Exact.TryGetValue(name, out var definition)
            ? definition
            : null;
    }

    /// <summary>解析 SQL 物化视图名称；普通引用忽略大小写，引号引用精确匹配。</summary>
    /// <param name="name">引用的名称。</param>
    /// <param name="quoted">是否使用双引号。</param>
    /// <returns>匹配的定义；不存在时返回 null。</returns>
    public MaterializedViewDefinition? Resolve(string name, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(name);
        var snapshot = Volatile.Read(ref _snapshot);
        if (quoted)
            return snapshot.Exact.GetValueOrDefault(name);
        if (!snapshot.Unquoted.TryGetValue(name, out var definition))
            return null;
        return definition ?? throw new InvalidOperationException($"materialized view '{name}' 存在大小写歧义；请使用双引号精确引用并显式迁移。");
    }

    /// <summary>
    /// 返回按名称升序排列的当前目录快照。
    /// </summary>
    /// <returns>不可变语义的定义列表。</returns>
    public IReadOnlyList<MaterializedViewDefinition> Snapshot()
        => Volatile.Read(ref _snapshot).Exact.Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    private void PublishSnapshot()
    {
        var unquoted = new Dictionary<string, MaterializedViewDefinition?>(_mutable.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var definition in _mutable.Values)
            if (!unquoted.TryAdd(definition.Name, definition))
                unquoted[definition.Name] = null;
        Volatile.Write(ref _snapshot, new CatalogSnapshot(
            _mutable.ToFrozenDictionary(StringComparer.Ordinal),
            unquoted.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)));
    }

    private sealed record CatalogSnapshot(
        FrozenDictionary<string, MaterializedViewDefinition> Exact,
        FrozenDictionary<string, MaterializedViewDefinition?> Unquoted);
}
