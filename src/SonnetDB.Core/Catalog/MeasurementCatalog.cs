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
    private readonly Dictionary<string, MeasurementSchema> _mutable = new(StringComparer.Ordinal);
    private FrozenDictionary<string, MeasurementSchema> _snapshot = EmptySnapshot();

    /// <summary>当前已注册的 measurement 数量。</summary>
    public int Count => Volatile.Read(ref _snapshot).Count;

    /// <summary>
    /// 注册一个新的 measurement schema。若同名 schema 已存在则抛出。
    /// </summary>
    /// <param name="schema">待注册的 schema。</param>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">同名 measurement 已存在。</exception>
    public void Add(MeasurementSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            if (_mutable.ContainsKey(schema.Name))
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
        lock (_sync)
        {
            _mutable[schema.Name] = schema;
            PublishSnapshot();
        }
    }

    /// <summary>按名查找 schema；未命中返回 null。</summary>
    /// <param name="name">measurement 名称（区分大小写）。</param>
    public MeasurementSchema? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.TryGetValue(name, out var schema) ? schema : null;
    }

    /// <summary>判断指定名称的 measurement 是否已注册。</summary>
    /// <param name="name">measurement 名称。</param>
    public bool Contains(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).ContainsKey(name);
    }

    /// <summary>返回当前所有 schema 的快照（按 measurement 名称的字典序排序）。</summary>
    public IReadOnlyList<MeasurementSchema> Snapshot()
    {
        var list = Volatile.Read(ref _snapshot).Values.ToList();
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, _mutable.ToFrozenDictionary(StringComparer.Ordinal));

    private static FrozenDictionary<string, MeasurementSchema> EmptySnapshot()
        => new Dictionary<string, MeasurementSchema>(0, StringComparer.Ordinal)
            .ToFrozenDictionary(StringComparer.Ordinal);
}
