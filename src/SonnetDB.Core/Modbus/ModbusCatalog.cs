using System.Collections.Frozen;

namespace SonnetDB.Modbus;

/// <summary>
/// 保存当前数据库的 Modbus source、endpoint 与关系表绑定定义。
/// </summary>
/// <remarks>
/// 目录发布为冻结快照，读取不需要持有写锁。所有持久化变更应通过
/// <see cref="ModbusManager"/> 执行，以保证目录文件与内存状态一致。
/// </remarks>
public sealed class ModbusCatalog
{
    private readonly object _sync = new();
    private Dictionary<string, ModbusSourceDefinition> _sources = new(StringComparer.Ordinal);
    private Dictionary<string, ModbusEndpointDefinition> _endpoints = new(StringComparer.Ordinal);
    private Dictionary<string, ModbusTableBinding> _bindings = new(StringComparer.Ordinal);
    private ModbusCatalogSnapshot _snapshot = ModbusCatalogSnapshot.Empty;
    private long _revision;

    /// <summary>
    /// 创建空的 Modbus 目录，初始逻辑修订号为 0。
    /// </summary>
    public ModbusCatalog()
    {
    }

    /// <summary>从已解码的持久化状态创建目录。</summary>
    internal ModbusCatalog(ModbusCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        Restore(state);
    }

    /// <summary>当前目录的单调递增逻辑修订号。</summary>
    public long Revision => Volatile.Read(ref _snapshot).Revision;

    /// <summary>当前 source 数量。</summary>
    public int SourceCount => Volatile.Read(ref _snapshot).Sources.Count;

    /// <summary>当前 endpoint 数量。</summary>
    public int EndpointCount => Volatile.Read(ref _snapshot).Endpoints.Count;

    /// <summary>当前关系表绑定数量。</summary>
    public int BindingCount => Volatile.Read(ref _snapshot).Bindings.Count;

    /// <summary>
    /// 按名称读取 source 定义。
    /// </summary>
    /// <param name="name">Source 名称。</param>
    /// <returns>找到时返回定义，否则返回 <c>null</c>。</returns>
    public ModbusSourceDefinition? TryGetSource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Volatile.Read(ref _snapshot).Sources.GetValueOrDefault(name);
    }

    /// <summary>
    /// 按名称读取 endpoint 定义。
    /// </summary>
    /// <param name="name">Endpoint 名称。</param>
    /// <returns>找到时返回定义，否则返回 <c>null</c>。</returns>
    public ModbusEndpointDefinition? TryGetEndpoint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Volatile.Read(ref _snapshot).Endpoints.GetValueOrDefault(name);
    }

    /// <summary>
    /// 按关系表名称读取 Modbus 绑定。
    /// </summary>
    /// <param name="tableName">关系表名称。</param>
    /// <returns>找到时返回绑定，否则返回 <c>null</c>。</returns>
    public ModbusTableBinding? TryGetBinding(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return Volatile.Read(ref _snapshot).Bindings.GetValueOrDefault(tableName);
    }

    /// <summary>
    /// 返回按名称升序排列的 source 快照。
    /// </summary>
    /// <returns>Source 定义列表。</returns>
    public IReadOnlyList<ModbusSourceDefinition> ListSources()
        => Volatile.Read(ref _snapshot).Sources.Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 返回按名称升序排列的 endpoint 快照。
    /// </summary>
    /// <returns>Endpoint 定义列表。</returns>
    public IReadOnlyList<ModbusEndpointDefinition> ListEndpoints()
        => Volatile.Read(ref _snapshot).Endpoints.Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// 返回按关系表名称升序排列的绑定快照。
    /// </summary>
    /// <returns>关系表绑定列表。</returns>
    public IReadOnlyList<ModbusTableBinding> ListBindings()
        => Volatile.Read(ref _snapshot).Bindings.Values
            .OrderBy(static definition => definition.TableName, StringComparer.Ordinal)
            .ToArray();

    /// <summary>新增 source，并推进逻辑修订号。</summary>
    internal void AddSource(ModbusSourceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_sources.ContainsKey(definition.Name))
                throw new InvalidOperationException($"MODBUS SOURCE '{definition.Name}' 已存在。");
            EnsureRevisionCanAdvance();
            _sources.Add(definition.Name, definition);
            _revision++;
            PublishSnapshots();
        }
    }

    /// <summary>新增 endpoint，并推进逻辑修订号。</summary>
    internal void AddEndpoint(ModbusEndpointDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            if (_endpoints.ContainsKey(definition.Name))
                throw new InvalidOperationException($"MODBUS ENDPOINT '{definition.Name}' 已存在。");
            EnsureRevisionCanAdvance();
            _endpoints.Add(definition.Name, Clone(definition));
            _revision++;
            PublishSnapshots();
        }
    }

    /// <summary>新增关系表绑定，并推进逻辑修订号。</summary>
    internal void AddBinding(ModbusTableBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        lock (_sync)
        {
            if (_bindings.ContainsKey(binding.TableName))
                throw new InvalidOperationException($"table '{binding.TableName}' 已存在 MODBUS 绑定。");
            EnsureRevisionCanAdvance();
            _bindings.Add(binding.TableName, Clone(binding));
            _revision++;
            PublishSnapshots();
        }
    }

    /// <summary>删除 source，并在实际删除时推进逻辑修订号。</summary>
    internal bool RemoveSource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            if (!_sources.ContainsKey(name))
                return false;
            EnsureRevisionCanAdvance();
            _sources.Remove(name);
            _revision++;
            PublishSnapshots();
            return true;
        }
    }

    /// <summary>删除 endpoint，并在实际删除时推进逻辑修订号。</summary>
    internal bool RemoveEndpoint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_sync)
        {
            if (!_endpoints.ContainsKey(name))
                return false;
            EnsureRevisionCanAdvance();
            _endpoints.Remove(name);
            _revision++;
            PublishSnapshots();
            return true;
        }
    }

    /// <summary>删除关系表绑定，并在实际删除时推进逻辑修订号。</summary>
    internal bool RemoveBinding(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        lock (_sync)
        {
            if (!_bindings.ContainsKey(tableName))
                return false;
            EnsureRevisionCanAdvance();
            _bindings.Remove(tableName);
            _revision++;
            PublishSnapshots();
            return true;
        }
    }

    /// <summary>在同一把锁内捕获可持久化的一致目录状态。</summary>
    internal ModbusCatalogState CaptureState()
    {
        lock (_sync)
        {
            return new ModbusCatalogState(
                _revision,
                _sources.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
                _endpoints.Values.OrderBy(static value => value.Name, StringComparer.Ordinal).ToArray(),
                _bindings.Values.OrderBy(static value => value.TableName, StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>原子捕获供元数据查询使用的只读目录快照，不复制无关定义集合。</summary>
    internal ModbusCatalogSnapshot CaptureSnapshot()
        => Volatile.Read(ref _snapshot);

    /// <summary>恢复完整目录状态，用于启动加载和持久化失败回滚。</summary>
    internal void Restore(ModbusCatalogState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Revision < 0)
            throw new InvalidDataException("ModbusCatalog: revision 不能为负数。");

        lock (_sync)
        {
            var sources = new Dictionary<string, ModbusSourceDefinition>(StringComparer.Ordinal);
            foreach (var definition in state.Sources)
            {
                if (!sources.TryAdd(definition.Name, definition))
                    throw new InvalidDataException($"ModbusCatalog: duplicate source '{definition.Name}'。");
            }

            var endpoints = new Dictionary<string, ModbusEndpointDefinition>(StringComparer.Ordinal);
            foreach (var definition in state.Endpoints)
            {
                if (!endpoints.TryAdd(definition.Name, Clone(definition)))
                    throw new InvalidDataException($"ModbusCatalog: duplicate endpoint '{definition.Name}'。");
            }

            var bindings = new Dictionary<string, ModbusTableBinding>(StringComparer.Ordinal);
            foreach (var binding in state.Bindings)
            {
                if (!bindings.TryAdd(binding.TableName, Clone(binding)))
                    throw new InvalidDataException($"ModbusCatalog: duplicate table binding '{binding.TableName}'。");
            }

            _revision = state.Revision;
            _sources = sources;
            _endpoints = endpoints;
            _bindings = bindings;
            PublishSnapshots();
        }
    }

    /// <summary>复制 endpoint 中的集合，避免调用方后续修改目录快照。</summary>
    private static ModbusEndpointDefinition Clone(ModbusEndpointDefinition definition)
        => definition with
        {
            AllowedClientNetworks = definition.AllowedClientNetworks is null
                ? null
                : Array.AsReadOnly(definition.AllowedClientNetworks.ToArray()),
        };

    /// <summary>复制绑定中的列集合，避免调用方后续修改目录快照。</summary>
    private static ModbusTableBinding Clone(ModbusTableBinding binding)
        => binding with { Columns = Array.AsReadOnly(binding.Columns.ToArray()) };

    /// <summary>检查逻辑修订号仍可单调推进。</summary>
    private void EnsureRevisionCanAdvance()
    {
        if (_revision == long.MaxValue)
            throw new InvalidOperationException("ModbusCatalog: revision 已达到 Int64 上限。");
    }

    /// <summary>用单次引用写入发布修订号和三个冻结字典，避免读者观察到混合版本。</summary>
    private void PublishSnapshots()
    {
        var snapshot = new ModbusCatalogSnapshot(
            _revision,
            _sources.ToFrozenDictionary(StringComparer.Ordinal),
            _endpoints.ToFrozenDictionary(StringComparer.Ordinal),
            _bindings.ToFrozenDictionary(StringComparer.Ordinal));
        Volatile.Write(ref _snapshot, snapshot);
    }
}

/// <summary>供无锁读取一次捕获全部 Modbus 定义和修订号的原子快照。</summary>
internal sealed record ModbusCatalogSnapshot(
    long Revision,
    FrozenDictionary<string, ModbusSourceDefinition> Sources,
    FrozenDictionary<string, ModbusEndpointDefinition> Endpoints,
    FrozenDictionary<string, ModbusTableBinding> Bindings)
{
    /// <summary>修订号为 0 的空目录快照。</summary>
    public static ModbusCatalogSnapshot Empty { get; } = new(
        0,
        FrozenDictionary<string, ModbusSourceDefinition>.Empty,
        FrozenDictionary<string, ModbusEndpointDefinition>.Empty,
        FrozenDictionary<string, ModbusTableBinding>.Empty);
}

/// <summary>Modbus 目录的完整持久化状态。</summary>
internal sealed record ModbusCatalogState(
    long Revision,
    IReadOnlyList<ModbusSourceDefinition> Sources,
    IReadOnlyList<ModbusEndpointDefinition> Endpoints,
    IReadOnlyList<ModbusTableBinding> Bindings);
