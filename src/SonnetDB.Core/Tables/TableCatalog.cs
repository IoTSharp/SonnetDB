using System.Collections.Frozen;

namespace SonnetDB.Tables;

/// <summary>
/// 关系表 schema catalog。
/// </summary>
public sealed class TableCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, TableSchema> _mutable = new(StringComparer.Ordinal);
    private CatalogSnapshot _snapshot = new(
        FrozenDictionary<string, TableSchema>.Empty,
        new Dictionary<string, TableSchema?>(StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    /// <summary>由所属 TableManager 安装的目录变更守卫；独立 catalog 默认不限制直接变更。</summary>
    internal Action<string, string>? MutationGuard { get; set; }

    /// <summary>当前表数量。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _mutable.Count;
        }
    }

    /// <summary>
    /// 新增表 schema。
    /// </summary>
    /// <param name="schema">schema。</param>
    public void Add(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        MutationGuard?.Invoke(schema.Name, "ADD");
        lock (_sync)
        {
            if (Volatile.Read(ref _snapshot).Unquoted.ContainsKey(schema.Name))
                throw new InvalidOperationException($"table '{schema.Name}' 已存在。");
            _mutable.Add(schema.Name, schema);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 加载或替换表 schema，主要用于启动恢复。
    /// </summary>
    /// <param name="schema">schema。</param>
    public void LoadOrReplace(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        MutationGuard?.Invoke(schema.Name, "LOAD OR REPLACE");
        lock (_sync)
        {
            if (!_mutable.ContainsKey(schema.Name)
                && Volatile.Read(ref _snapshot).Unquoted.ContainsKey(schema.Name))
                throw new InvalidOperationException($"table '{schema.Name}' 已存在仅大小写不同的名称。");
            _mutable[schema.Name] = schema;
            PublishSnapshot();
        }
    }

    /// <summary>恢复已有目录时保留历史大小写冲突，由名称解析报告歧义。</summary>
    internal void LoadExisting(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            _mutable.Add(schema.Name, schema);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除表 schema。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <returns>存在并删除时返回 true。</returns>
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
    /// 尝试按名称查找表 schema。
    /// </summary>
    /// <param name="name">表名。</param>
    /// <returns>找到时返回 schema；否则返回 null。</returns>
    public TableSchema? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).Exact.TryGetValue(name, out var schema) ? schema : null;
    }

    /// <summary>未加引号名称忽略大小写；加引号名称精确匹配。历史重名必须显式迁移。</summary>
    /// <param name="name">待解析的表名。</param>
    /// <param name="quoted">名称是否使用双引号。</param>
    /// <returns>匹配的表 schema；不存在时返回 <c>null</c>。</returns>
    public TableSchema? Resolve(string name, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(name);
        var snapshot = Volatile.Read(ref _snapshot);
        if (quoted)
            return snapshot.Exact.TryGetValue(name, out var exact) ? exact : null;
        if (!snapshot.Unquoted.TryGetValue(name, out var schema))
            return null;
        return schema ?? throw new InvalidOperationException(
            $"table '{name}' 存在大小写歧义，请使用双引号精确访问并显式迁移。");
    }

    /// <summary>
    /// 返回当前 schema 快照，按表名升序排列。
    /// </summary>
    public IReadOnlyList<TableSchema> Snapshot()
        => Volatile.Read(ref _snapshot).Exact.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

    private void PublishSnapshot()
    {
        var unquoted = new Dictionary<string, TableSchema?>(_mutable.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var schema in _mutable.Values)
        {
            if (!unquoted.TryAdd(schema.Name, schema))
                unquoted[schema.Name] = null;
        }
        Volatile.Write(ref _snapshot, new CatalogSnapshot(
            _mutable.ToFrozenDictionary(StringComparer.Ordinal),
            unquoted.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)));
    }

    private sealed record CatalogSnapshot(
        FrozenDictionary<string, TableSchema> Exact,
        FrozenDictionary<string, TableSchema?> Unquoted);
}
