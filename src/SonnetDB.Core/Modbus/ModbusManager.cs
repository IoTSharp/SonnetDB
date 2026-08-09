using SonnetDB.Tables;

namespace SonnetDB.Modbus;

/// <summary>
/// 管理单个数据库目录中的 Modbus 定义、关系表引用与原子持久化。
/// </summary>
public sealed class ModbusManager : IDisposable
{
    private readonly object _sync = new();
    private readonly object _schemaSync;
    private readonly TableCatalog _tables;
    private readonly Dictionary<string, ModbusSourceRuntimeStatus> _sourceRuntimeStatuses =
        new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>仅供测试在候选目录落盘后、内存快照发布前建立确定性同步点。</summary>
    internal Action? AfterCatalogPersistedBeforePublishTestHook { get; set; }

    /// <summary>
    /// 打开 Modbus 独立目录，并验证已加载绑定仍引用有效的 source、endpoint 和关系表。
    /// </summary>
    /// <param name="rootDirectory">Modbus 子目录路径。</param>
    /// <param name="tables">当前数据库的关系表目录。</param>
    internal ModbusManager(string rootDirectory, TableCatalog tables)
        : this(rootDirectory, tables, new object())
    {
    }

    /// <summary>
    /// 使用数据库级同步根打开 Modbus 目录，使 Modbus DDL 与关系表 DDL、备份共享发布边界。
    /// </summary>
    /// <param name="rootDirectory">Modbus 子目录路径。</param>
    /// <param name="tables">当前数据库的关系表目录。</param>
    /// <param name="synchronizationRoot">数据库级 schema 与备份同步根。</param>
    internal ModbusManager(string rootDirectory, TableCatalog tables, object synchronizationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(synchronizationRoot);

        Directory.CreateDirectory(rootDirectory);
        _schemaSync = synchronizationRoot;
        _tables = tables;
        CatalogPath = Path.Combine(rootDirectory, ModbusCatalogCodec.FileName);
        Catalog = ModbusCatalogCodec.Load(CatalogPath);
        ValidateLoadedCatalog();
    }

    /// <summary>当前 Modbus 定义目录。</summary>
    public ModbusCatalog Catalog { get; }

    /// <summary>Modbus 目录文件的完整路径。</summary>
    public string CatalogPath { get; }

    /// <summary>当前目录的单调递增逻辑修订号。</summary>
    public long Revision => Catalog.Revision;

    /// <summary>
    /// 返回指定 source 的瞬时运行状态；尚未由协议 runtime 发布状态时返回默认关闭状态。
    /// </summary>
    /// <param name="sourceName">Source 名称。</param>
    /// <returns>当前运行状态快照。</returns>
    public ModbusSourceRuntimeStatus GetSourceRuntimeStatus(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        lock (_sync)
        {
            ThrowIfDisposed();
            return _sourceRuntimeStatuses.GetValueOrDefault(sourceName)
                ?? ModbusSourceRuntimeStatus.Disabled;
        }
    }

    /// <summary>
    /// 发布指定 source 的瞬时运行状态；状态只保存在内存中，不修改 catalog 修订号或磁盘格式。
    /// </summary>
    /// <param name="sourceName">Source 名称。</param>
    /// <param name="status">待发布状态。</param>
    public void ReportSourceRuntimeStatus(string sourceName, ModbusSourceRuntimeStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(status);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Catalog.TryGetSource(sourceName) is not null)
                _sourceRuntimeStatuses[sourceName] = status;
        }
    }

    /// <summary>
    /// 清除指定 source 的瞬时运行状态，使元数据恢复为默认关闭状态。
    /// </summary>
    /// <param name="sourceName">Source 名称。</param>
    public void ClearSourceRuntimeStatus(string sourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        lock (_sync)
        {
            if (!_disposed)
                _sourceRuntimeStatuses.Remove(sourceName);
        }
    }

    /// <summary>
    /// 创建 source 定义并立即持久化。
    /// </summary>
    /// <param name="definition">待创建的 source 定义。</param>
    public void CreateSource(ModbusSourceDefinition definition)
    {
        ModbusMappingValidator.ValidateSource(definition);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            ModbusCatalog candidate = CreateCandidateCatalog();
            candidate.AddSource(definition);
            PersistAndPublish(candidate);
        }
    }

    /// <summary>
    /// 创建 endpoint 定义并立即持久化。
    /// </summary>
    /// <param name="definition">待创建的 endpoint 定义。</param>
    public void CreateEndpoint(ModbusEndpointDefinition definition)
    {
        ModbusEndpointDefinition snapshot = SnapshotEndpoint(definition);
        ModbusMappingValidator.ValidateEndpoint(snapshot);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            ModbusCatalog candidate = CreateCandidateCatalog();
            candidate.AddEndpoint(snapshot);
            PersistAndPublish(candidate);
        }
    }

    /// <summary>
    /// 为已存在的关系表创建 Modbus 绑定并立即持久化。
    /// </summary>
    /// <param name="binding">待创建的关系表绑定。</param>
    public void CreateBinding(ModbusTableBinding binding)
    {
        ModbusTableBinding snapshot = SnapshotBinding(binding);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            TableSchema schema = _tables.TryGet(snapshot.TableName)
                ?? throw new InvalidOperationException($"table '{snapshot.TableName}' 不存在，无法创建 MODBUS 绑定。");
            ValidateBindingCore(snapshot, schema);

            ModbusCatalog candidate = CreateCandidateCatalog();
            candidate.AddBinding(snapshot);
            PersistAndPublish(candidate);
        }
    }

    /// <summary>
    /// 在关系表落盘前预检一个 Modbus 绑定及其目标引用。
    /// </summary>
    /// <param name="binding">待校验的关系表绑定。</param>
    /// <param name="schema">绑定关系表的候选 schema。</param>
    public void ValidateBinding(ModbusTableBinding binding, TableSchema schema)
    {
        ModbusTableBinding snapshot = SnapshotBinding(binding);
        ArgumentNullException.ThrowIfNull(schema);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            ValidateBindingCore(snapshot, schema);
        }
    }

    /// <summary>
    /// 删除未被关系表绑定引用的 source，并立即持久化。
    /// </summary>
    /// <param name="name">Source 名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool DropSource(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Catalog.TryGetSource(name) is null)
                return false;
            EnsureTargetHasNoBindings(name, ModbusMappingDirection.SourceToTable, "SOURCE");

            ModbusCatalog candidate = CreateCandidateCatalog();
            _ = candidate.RemoveSource(name);
            PersistAndPublish(candidate);
            _sourceRuntimeStatuses.Remove(name);
            return true;
        }
    }

    /// <summary>
    /// 删除未被关系表绑定引用的 endpoint，并立即持久化。
    /// </summary>
    /// <param name="name">Endpoint 名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool DropEndpoint(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Catalog.TryGetEndpoint(name) is null)
                return false;
            EnsureTargetHasNoBindings(name, ModbusMappingDirection.TableToEndpoint, "ENDPOINT");

            ModbusCatalog candidate = CreateCandidateCatalog();
            _ = candidate.RemoveEndpoint(name);
            PersistAndPublish(candidate);
            return true;
        }
    }

    /// <summary>
    /// 删除指定关系表的 Modbus 绑定，并立即持久化。
    /// </summary>
    /// <param name="tableName">关系表名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool DropBinding(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Catalog.TryGetBinding(tableName) is null)
                return false;

            ModbusCatalog candidate = CreateCandidateCatalog();
            _ = candidate.RemoveBinding(tableName);
            PersistAndPublish(candidate);
            return true;
        }
    }

    /// <summary>拒绝会使现有 Modbus 绑定失效的关系表 schema 变更。</summary>
    internal void EnsureTableCanMutate(string tableName, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        lock (_schemaSync)
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Catalog.TryGetBinding(tableName) is not null)
            {
                throw new InvalidOperationException(
                    $"无法执行 {operation}：table '{tableName}' 存在 MODBUS 绑定；请先删除该绑定。");
            }
        }
    }

    /// <summary>
    /// 关闭管理器并阻止后续 Modbus catalog 变更；目录内容已在每次 DDL 中同步持久化。
    /// </summary>
    public void Dispose()
    {
        lock (_schemaSync)
        lock (_sync)
        {
            _sourceRuntimeStatuses.Clear();
            _disposed = true;
        }
    }

    /// <summary>捕获 source 运行状态的一致只读副本，供单次元数据查询使用。</summary>
    internal IReadOnlyDictionary<string, ModbusSourceRuntimeStatus> CaptureSourceRuntimeStatuses()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return new Dictionary<string, ModbusSourceRuntimeStatus>(
                _sourceRuntimeStatuses,
                StringComparer.Ordinal);
        }
    }

    /// <summary>校验加载目录中的业务定义和全部跨目录引用。</summary>
    private void ValidateLoadedCatalog()
    {
        try
        {
            foreach (var source in Catalog.ListSources())
                ModbusMappingValidator.ValidateSource(source);
            foreach (var endpoint in Catalog.ListEndpoints())
                ModbusMappingValidator.ValidateEndpoint(endpoint);
            foreach (var binding in Catalog.ListBindings())
            {
                TableSchema schema = _tables.TryGet(binding.TableName)
                    ?? throw new InvalidOperationException($"table '{binding.TableName}' 不存在。");
                ValidateBindingCore(binding, schema);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException("ModbusCatalog: 已持久化定义包含无效引用或配置。", exception);
        }
    }

    /// <summary>校验绑定本身、关系表 schema 与方向对应的目标定义。</summary>
    private void ValidateBindingCore(ModbusTableBinding binding, TableSchema schema)
    {
        ModbusMappingValidator.ValidateBinding(binding, schema);
        ModbusAddressingMode addressingMode;
        switch (binding.Direction)
        {
            case ModbusMappingDirection.SourceToTable:
                ModbusSourceDefinition source = Catalog.TryGetSource(binding.TargetName)
                    ?? throw new InvalidOperationException(
                        $"MODBUS SOURCE '{binding.TargetName}' 不存在，无法绑定 table '{binding.TableName}'。");
                addressingMode = source.AddressingMode;
                break;
            case ModbusMappingDirection.TableToEndpoint:
                ModbusEndpointDefinition endpoint = Catalog.TryGetEndpoint(binding.TargetName)
                    ?? throw new InvalidOperationException(
                        $"MODBUS ENDPOINT '{binding.TargetName}' 不存在，无法绑定 table '{binding.TableName}'。");
                addressingMode = endpoint.AddressingMode;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(binding), binding.Direction, "未知的 Modbus 绑定方向。");
        }

        // 声明地址用于 SHOW/重建合同，PDU 地址用于冲突检查和未来运行时；两者必须始终一致。
        foreach (var mapping in binding.Columns)
        {
            ushort expected = ModbusAddress.ToPduAddress(
                mapping.DeclaredAddress,
                mapping.Area,
                addressingMode);
            if (mapping.PduAddress != expected)
            {
                throw new ArgumentException(
                    $"列 '{mapping.ColumnName}' 的 PDU 地址为 {mapping.PduAddress}，"
                    + $"但声明地址 {mapping.DeclaredAddress} 在 {addressingMode} 模式下应规范化为 {expected}。",
                    nameof(binding));
            }
        }
    }

    /// <summary>拒绝删除仍被关系表绑定引用的 source 或 endpoint。</summary>
    private void EnsureTargetHasNoBindings(
        string targetName,
        ModbusMappingDirection direction,
        string targetKind)
    {
        string[] dependents = Catalog.ListBindings()
            .Where(binding => binding.Direction == direction
                              && string.Equals(binding.TargetName, targetName, StringComparison.Ordinal))
            .Select(static binding => binding.TableName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (dependents.Length == 0)
            return;

        throw new InvalidOperationException(
            $"无法删除 MODBUS {targetKind} '{targetName}'：table '{string.Join("', '", dependents)}' 仍引用该定义。");
    }

    /// <summary>基于当前已发布状态创建仅供本次 DDL 使用的候选目录。</summary>
    private ModbusCatalog CreateCandidateCatalog()
        => new(Catalog.CaptureState());

    /// <summary>先原子持久化候选目录，成功后再一次发布；发布失败时恢复旧磁盘与内存状态。</summary>
    private void PersistAndPublish(ModbusCatalog candidate)
    {
        ModbusCatalogState previousState = Catalog.CaptureState();
        var catalogPersisted = false;
        try
        {
            ModbusCatalogCodec.Save(CatalogPath, candidate);
            catalogPersisted = true;
            AfterCatalogPersistedBeforePublishTestHook?.Invoke();
            Catalog.Restore(candidate.CaptureState());
        }
        catch (Exception publishException)
        {
            var rollbackErrors = new List<Exception>();

            // 两个恢复动作都必须尝试，避免一个失败遮蔽另一个并留下更难诊断的混合状态。
            try
            {
                Catalog.Restore(previousState);
            }
            catch (Exception rollbackException)
            {
                rollbackErrors.Add(rollbackException);
            }

            if (catalogPersisted)
            {
                try
                {
                    ModbusCatalogCodec.Save(
                        CatalogPath,
                        new ModbusCatalog(previousState),
                        ".rollback.tmp");
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add(rollbackException);
                }
            }

            if (rollbackErrors.Count != 0)
            {
                rollbackErrors.Insert(0, publishException);
                throw new InvalidOperationException(
                    "Modbus catalog 发布失败，且旧目录回滚失败。",
                    new AggregateException(rollbackErrors));
            }

            throw;
        }
    }

    /// <summary>先固化 endpoint 的 allowlist，确保校验、发布与持久化观察同一份不可变输入。</summary>
    private static ModbusEndpointDefinition SnapshotEndpoint(ModbusEndpointDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition with
        {
            AllowedClientNetworks = definition.AllowedClientNetworks is null
                ? null
                : Array.AsReadOnly(definition.AllowedClientNetworks.ToArray()),
        };
    }

    /// <summary>先固化绑定列集合，防止可变列表在校验后改变待持久化内容。</summary>
    private static ModbusTableBinding SnapshotBinding(ModbusTableBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(binding.Columns);
        return binding with { Columns = Array.AsReadOnly(binding.Columns.ToArray()) };
    }

    /// <summary>管理器关闭后拒绝继续读取校验上下文或发布目录变更。</summary>
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
