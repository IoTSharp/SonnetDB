using System.Collections.Frozen;
using System.Threading;

namespace SonnetDB.Catalog;

/// <summary>
/// 进程内 measurement schema 集合，按名建立索引。
/// 线程安全：写入路径通过新快照原子替换，读路径无锁读取冻结快照。
/// </summary>
public sealed class MeasurementCatalog
{
    private readonly object _sync = new();
    private Dictionary<string, MeasurementSchema> _mutable = new(StringComparer.Ordinal);
    private CatalogSnapshot _snapshot = CreateSnapshot(new Dictionary<string, MeasurementSchema>(StringComparer.Ordinal));

    /// <summary>由所属 Tsdb 安装的目录变更守卫；独立 catalog 默认不限制直接变更。</summary>
    internal Action<string, string>? MutationGuard { get; set; }

    /// <summary>当前已注册的 measurement 数量。</summary>
    public int Count => Volatile.Read(ref _snapshot).Exact.Count;

    /// <summary>
    /// 注册一个新的 measurement schema。若同名 schema 已存在则抛出。
    /// </summary>
    /// <param name="schema">待注册的 schema。</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">同名 measurement 已存在。</exception>
    public void Add(MeasurementSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        MutationGuard?.Invoke(schema.Name, "ADD");
        lock (_sync)
        {
            if (Volatile.Read(ref _snapshot).Unquoted.ContainsKey(schema.Name))
                throw new InvalidOperationException($"Measurement '{schema.Name}' 已存在。");

            _mutable.Add(schema.Name, schema);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除指定 measurement schema。
    /// </summary>
    /// <param name="name">measurement 名称（区分大小写）。</param>
    /// <returns>找到并删除返回 <c>true</c>；不存在返回 <c>false</c>。</returns>
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
    /// 直接装载 schema（覆盖已有同名条目）；仅供持久化层在加载时使用。
    /// </summary>
    /// <param name="schema">待装载的 schema。</param>
    internal void LoadOrReplace(MeasurementSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        MutationGuard?.Invoke(schema.Name, "LOAD OR REPLACE");
        lock (_sync)
        {
            _mutable[schema.Name] = schema;
            PublishSnapshot();
        }
    }

    /// <summary>在持久化前构造完整的新快照，避免保存成功后再进行可能失败的分配。</summary>
    internal PreparedReplacement PrepareReplacement(IReadOnlyList<MeasurementSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        var replacement = new Dictionary<string, MeasurementSchema>(schemas.Count, StringComparer.Ordinal);
        foreach (MeasurementSchema schema in schemas)
        {
            ArgumentNullException.ThrowIfNull(schema);
            replacement.Add(schema.Name, schema);
        }

        var snapshot = CreateSnapshot(replacement);
        return new PreparedReplacement(replacement, snapshot);
    }

    /// <summary>持久化成功后一次性发布预构造的快照。</summary>
    internal void PublishPrepared(PreparedReplacement prepared)
    {
        lock (_sync)
        {
            _mutable = prepared.Mutable;
            Volatile.Write(ref _snapshot, prepared.Snapshot);
        }
    }

    internal sealed record PreparedReplacement(
        Dictionary<string, MeasurementSchema> Mutable,
        CatalogSnapshot Snapshot);

    internal sealed record CatalogSnapshot(
        FrozenDictionary<string, MeasurementSchema> Exact,
        FrozenDictionary<string, MeasurementSchema?> Unquoted);

    /// <summary>按名查找 schema；未命中返回 null。</summary>
    /// <param name="name">measurement 名称（区分大小写）。</param>
    public MeasurementSchema? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Exact.TryGetValue(name, out var schema) ? schema : null;
    }

    /// <summary>按 SQL 标识符规则解析 measurement；普通引用忽略大小写，引号引用精确匹配。</summary>
    /// <param name="name">引用的 measurement 名称。</param>
    /// <param name="quoted">名称是否使用双引号；加引号时精确匹配。</param>
    /// <returns>匹配的 schema；不存在时返回 null。</returns>
    /// <exception cref="InvalidOperationException">普通引用匹配到旧目录中仅大小写不同的多个 measurement。</exception>
    public MeasurementSchema? Resolve(string name, bool quoted)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (quoted)
            return TryGet(name);
        if (!Volatile.Read(ref _snapshot).Unquoted.TryGetValue(name, out MeasurementSchema? schema))
            return null;
        return schema ?? throw new InvalidOperationException(
            $"Measurement 名称 '{name}' 存在大小写歧义；请使用双引号精确引用并显式迁移冲突名称。");
    }

    /// <summary>判断指定名称的 measurement 是否已注册。</summary>
    /// <param name="name">measurement 名称。</param>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).Exact.ContainsKey(name);
    }

    /// <summary>返回当前所有 schema 的快照（按 measurement 名称的字典序排序）。</summary>
    public IReadOnlyList<MeasurementSchema> Snapshot()
    {
        var list = Volatile.Read(ref _snapshot).Exact.Values.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, CreateSnapshot(_mutable));

    private static CatalogSnapshot CreateSnapshot(Dictionary<string, MeasurementSchema> schemas)
    {
        var unquoted = new Dictionary<string, MeasurementSchema?>(schemas.Count, StringComparer.OrdinalIgnoreCase);
        foreach (MeasurementSchema schema in schemas.Values)
        {
            if (!unquoted.TryAdd(schema.Name, schema))
                unquoted[schema.Name] = null;
        }

        return new CatalogSnapshot(
            schemas.ToFrozenDictionary(StringComparer.Ordinal),
            unquoted.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
