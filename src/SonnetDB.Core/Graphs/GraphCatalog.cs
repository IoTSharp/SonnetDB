using System.Collections.Frozen;

namespace SonnetDB.Graphs;

/// <summary>
/// 数据库中原生属性图定义的线程安全目录。
/// </summary>
public sealed class GraphCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GraphDefinition> _mutable = new(StringComparer.Ordinal);
    private FrozenDictionary<string, GraphDefinition> _snapshot =
        FrozenDictionary<string, GraphDefinition>.Empty;
    private long _revision;

    /// <summary>由所属 <see cref="GraphManager"/> 安装的目录变更守卫。</summary>
    internal Action<string, string>? MutationGuard { get; set; }

    /// <summary>初始化空图目录。</summary>
    public GraphCatalog()
    {
    }

    internal GraphCatalog(GraphCatalogState state)
    {
        Restore(state);
    }

    /// <summary>当前图数量。</summary>
    public int Count => Volatile.Read(ref _snapshot).Count;

    /// <summary>当前目录逻辑修订号。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// 新增图定义。
    /// </summary>
    /// <param name="definition">待新增的图定义。</param>
    public void Add(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        MutationGuard?.Invoke(definition.Name, "ADD");
        lock (_sync)
        {
            if (_mutable.ContainsKey(definition.Name))
                throw new InvalidOperationException($"graph '{definition.Name}' 已存在。");
            EnsureStorageIdAvailable(definition.StorageId, definition.Name);
            _revision = checked(_revision + 1);
            _mutable.Add(definition.Name, definition);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 加载或替换图定义；主要用于目录恢复和迁移。
    /// </summary>
    /// <param name="definition">待加载的图定义。</param>
    public void LoadOrReplace(GraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        MutationGuard?.Invoke(definition.Name, "LOAD OR REPLACE");
        lock (_sync)
        {
            EnsureStorageIdAvailable(definition.StorageId, definition.Name);
            _revision = checked(_revision + 1);
            _mutable[definition.Name] = definition;
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除图定义。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool Remove(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        MutationGuard?.Invoke(name, "REMOVE");
        lock (_sync)
        {
            if (!_mutable.Remove(name))
                return false;
            _revision = checked(_revision + 1);
            PublishSnapshot();
            return true;
        }
    }

    /// <summary>
    /// 尝试按名称读取图定义。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>找到时返回图定义，否则返回 <c>null</c>。</returns>
    public GraphDefinition? TryGet(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Volatile.Read(ref _snapshot).TryGetValue(name, out GraphDefinition? definition)
            ? definition
            : null;
    }

    /// <summary>
    /// 尝试按物理存储标识符读取图定义。
    /// </summary>
    /// <param name="storageId">图物理存储标识符。</param>
    /// <returns>找到时返回图定义，否则返回 <c>null</c>。</returns>
    public GraphDefinition? TryGet(Guid storageId)
    {
        if (storageId == Guid.Empty)
            return null;
        return Volatile.Read(ref _snapshot).Values.FirstOrDefault(
            definition => definition.StorageId == storageId);
    }

    /// <summary>
    /// 返回按图名称升序排列的当前目录快照。
    /// </summary>
    /// <returns>不可变语义的图定义列表。</returns>
    public IReadOnlyList<GraphDefinition> Snapshot()
        => Volatile.Read(ref _snapshot).Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    internal GraphCatalogState CaptureState()
    {
        lock (_sync)
            return new GraphCatalogState(_revision, Snapshot());
    }

    internal void Restore(GraphCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 0)
            throw new InvalidDataException("GraphCatalog: revision 不能为负数。");

        lock (_sync)
        {
            _mutable.Clear();
            var storageIds = new HashSet<Guid>();
            foreach (GraphDefinition definition in state.Definitions)
            {
                if (!_mutable.TryAdd(definition.Name, definition))
                    throw new InvalidDataException($"GraphCatalog: duplicate graph '{definition.Name}'。");
                if (!storageIds.Add(definition.StorageId))
                    throw new InvalidDataException($"GraphCatalog: duplicate storage id '{definition.StorageId:N}'。");
            }

            _revision = state.Revision;
            PublishSnapshot();
        }
    }

    private void EnsureStorageIdAvailable(Guid storageId, string ownerName)
    {
        foreach (GraphDefinition existing in _mutable.Values)
        {
            if (existing.StorageId == storageId
                && !string.Equals(existing.Name, ownerName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"graph storage id '{storageId:N}' 已由 graph '{existing.Name}' 使用。");
            }
        }
    }

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, _mutable.ToFrozenDictionary(StringComparer.Ordinal));
}

internal sealed record GraphCatalogState(
    long Revision,
    IReadOnlyList<GraphDefinition> Definitions);
