using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Modbus;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// 执行 Modbus Phase A DDL 与只读元数据查询；本类型只访问本地 catalog，不执行协议网络 I/O。
/// </summary>
internal static class ModbusSqlExecutor
{
    private static readonly IReadOnlyDictionary<string, ModbusSourceRuntimeStatus> _emptyRuntimeStatuses =
        new Dictionary<string, ModbusSourceRuntimeStatus>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, ModbusEndpointRuntimeStatus> _emptyEndpointRuntimeStatuses =
        new Dictionary<string, ModbusEndpointRuntimeStatus>(StringComparer.Ordinal);

    private static readonly IReadOnlyList<string> _sourceColumns =
    [
        "name", "transport", "endpoint", "unit_id", "addressing", "byte_order", "word_order",
        "poll_interval", "timeout", "retry", "runtime_enabled", "health", "last_success_at",
        "last_error_code", "configured_enabled", "configuration_source", "catalog_revision",
        "last_attempt_at", "last_error_at", "consecutive_failures",
    ];

    private static readonly IReadOnlyList<string> _endpointColumns =
    [
        "name", "transport", "bind", "unit_id", "addressing", "byte_order", "word_order",
        "write_policy", "allowlist", "max_connections", "runtime_enabled", "health",
        "last_error_code", "configured_enabled", "configuration_source", "catalog_revision",
    ];

    private static readonly IReadOnlyList<string> _tableColumns =
    [
        "column_name", "direction", "area", "declared_address", "pdu_address", "register_count",
        "bit_index", "wire_type", "byte_order", "word_order", "scale", "offset", "access",
        "table_mode", "on_error", "external_write_action", "target_kind", "target_name", "row_key",
        "store_history", "sample_time_column", "quality_column", "binding_enabled", "catalog_revision",
    ];

    /// <summary>创建并持久化 Modbus source 定义。</summary>
    internal static ModbusSourceDefinition ExecuteCreateSource(
        Tsdb tsdb,
        CreateModbusSourceStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        tsdb.Modbus.CreateSource(statement.Definition);
        return statement.Definition;
    }

    /// <summary>创建并持久化 Modbus endpoint 定义。</summary>
    internal static ModbusEndpointDefinition ExecuteCreateEndpoint(
        Tsdb tsdb,
        CreateModbusEndpointStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        tsdb.Modbus.CreateEndpoint(statement.Definition);
        return statement.Definition;
    }

    /// <summary>
    /// 把 CREATE TABLE 的语法子句解析成带规范化 PDU 地址和有效字节序的持久化绑定。
    /// </summary>
    internal static ModbusTableBinding? ResolveTableBinding(
        Tsdb tsdb,
        CreateTableStatement statement,
        TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        ModbusTableBindingClause? clause = statement.ModbusBinding;
        TableColumnDefinition[] mappedColumns = statement.Columns
            .Where(static column => column.ModbusMapping is not null)
            .ToArray();
        TableColumnDefinition[] sampleTimeColumns = statement.Columns
            .Where(static column => column.IsModbusSampleTime)
            .ToArray();
        TableColumnDefinition[] qualityColumns = statement.Columns
            .Where(static column => column.IsModbusQuality)
            .ToArray();

        if (clause is null)
        {
            if (mappedColumns.Length != 0 || sampleTimeColumns.Length != 0 || qualityColumns.Length != 0)
            {
                throw new InvalidOperationException(
                    "Modbus 列映射、SAMPLE_TIME 或 QUALITY 必须配合 USING MODBUS 表绑定。");
            }
            return null;
        }

        if (sampleTimeColumns.Length > 1)
            throw new InvalidOperationException("一张 Modbus 表只能声明一个 SAMPLE_TIME 列。");
        if (qualityColumns.Length > 1)
            throw new InvalidOperationException("一张 Modbus 表只能声明一个 QUALITY 列。");

        ModbusAddressingMode addressingMode;
        ModbusByteOrder defaultByteOrder;
        ModbusWordOrder defaultWordOrder;
        switch (clause.Direction)
        {
            case ModbusMappingDirection.SourceToTable:
                ModbusSourceDefinition source = tsdb.Modbus.Catalog.TryGetSource(clause.TargetName)
                    ?? throw new InvalidOperationException($"MODBUS SOURCE '{clause.TargetName}' 不存在。");
                addressingMode = source.AddressingMode;
                defaultByteOrder = source.ByteOrder;
                defaultWordOrder = source.WordOrder;
                break;
            case ModbusMappingDirection.TableToEndpoint:
                ModbusEndpointDefinition endpoint = tsdb.Modbus.Catalog.TryGetEndpoint(clause.TargetName)
                    ?? throw new InvalidOperationException($"MODBUS ENDPOINT '{clause.TargetName}' 不存在。");
                addressingMode = endpoint.AddressingMode;
                defaultByteOrder = endpoint.ByteOrder;
                defaultWordOrder = endpoint.WordOrder;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(statement), clause.Direction, "未知的 Modbus 表映射方向。");
        }

        var mappings = new ModbusColumnMapping[mappedColumns.Length];
        for (int index = 0; index < mappedColumns.Length; index++)
        {
            TableColumnDefinition column = mappedColumns[index];
            ModbusColumnMappingClause mapping = column.ModbusMapping!;
            if (mapping.Direction != clause.Direction)
            {
                throw new InvalidOperationException(
                    $"列 '{column.Name}' 的 Modbus 方向与 USING MODBUS 绑定不一致。");
            }

            // PDU 地址只能在取得目标对象的寻址模式后计算，不能由 parser 猜测默认值。
            ushort pduAddress = ModbusAddress.ToPduAddress(
                mapping.DeclaredAddress,
                mapping.Area,
                addressingMode);
            mappings[index] = new ModbusColumnMapping(
                column.Name,
                mapping.Area,
                mapping.DeclaredAddress,
                pduAddress,
                mapping.ValueType,
                mapping.RegisterCount,
                mapping.StringLength,
                mapping.BitIndex,
                mapping.ByteOrderOverride ?? defaultByteOrder,
                mapping.WordOrderOverride ?? defaultWordOrder,
                mapping.Scale,
                mapping.Offset,
                mapping.Access);
        }

        var binding = new ModbusTableBinding(
            statement.Name,
            clause.Direction,
            clause.TargetName,
            Array.AsReadOnly(mappings),
            clause.RowKey,
            clause.TableMode,
            clause.ErrorPolicy,
            clause.ApprovedWriteAction,
            clause.StoreHistory,
            sampleTimeColumns.FirstOrDefault()?.Name,
            qualityColumns.FirstOrDefault()?.Name,
            Enabled: true);
        tsdb.Modbus.ValidateBinding(binding, schema);
        return binding;
    }

    /// <summary>列出全部 Modbus source 的本地配置与禁用状态。</summary>
    internal static SelectExecutionResult ShowSources(ModbusCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return BuildSourcesResult(catalog.CaptureSnapshot(), _emptyRuntimeStatuses, name: null);
    }

    /// <summary>列出全部 Modbus source 的本地配置与瞬时运行状态。</summary>
    internal static SelectExecutionResult ShowSources(ModbusManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return BuildSourcesResult(
            manager.Catalog.CaptureSnapshot(),
            manager.CaptureSourceRuntimeStatuses(),
            name: null);
    }

    /// <summary>列出全部 Modbus endpoint 的本地配置与禁用状态。</summary>
    internal static SelectExecutionResult ShowEndpoints(ModbusCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return BuildEndpointsResult(catalog.CaptureSnapshot(), _emptyEndpointRuntimeStatuses, name: null);
    }

    /// <summary>列出全部 Modbus endpoint 的本地配置与瞬时运行状态。</summary>
    internal static SelectExecutionResult ShowEndpoints(ModbusManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return BuildEndpointsResult(
            manager.Catalog.CaptureSnapshot(),
            manager.CaptureEndpointRuntimeStatuses(),
            name: null);
    }

    /// <summary>返回一个指定 Modbus source 的完整有效配置。</summary>
    internal static SelectExecutionResult DescribeSource(ModbusCatalog catalog, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return BuildSourcesResult(catalog.CaptureSnapshot(), _emptyRuntimeStatuses, name);
    }

    /// <summary>返回一个指定 Modbus source 的完整有效配置与瞬时运行状态。</summary>
    internal static SelectExecutionResult DescribeSource(ModbusManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return BuildSourcesResult(
            manager.Catalog.CaptureSnapshot(),
            manager.CaptureSourceRuntimeStatuses(),
            name);
    }

    /// <summary>返回一个指定 Modbus endpoint 的完整有效配置。</summary>
    internal static SelectExecutionResult DescribeEndpoint(ModbusCatalog catalog, string name)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return BuildEndpointsResult(catalog.CaptureSnapshot(), _emptyEndpointRuntimeStatuses, name);
    }

    /// <summary>返回一个指定 Modbus endpoint 的完整有效配置与瞬时运行状态。</summary>
    internal static SelectExecutionResult DescribeEndpoint(ModbusManager manager, string name)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return BuildEndpointsResult(
            manager.Catalog.CaptureSnapshot(),
            manager.CaptureEndpointRuntimeStatuses(),
            name);
    }

    /// <summary>按列返回指定关系表的完整 Modbus 映射。</summary>
    internal static SelectExecutionResult DescribeTable(ModbusCatalog catalog, string tableName)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ModbusCatalogSnapshot snapshot = catalog.CaptureSnapshot();
        ModbusTableBinding binding = snapshot.Bindings.GetValueOrDefault(tableName)
            ?? throw new InvalidOperationException($"table '{tableName}' 不存在 MODBUS 绑定。");
        var rows = new List<IReadOnlyList<object?>>(binding.Columns.Count);
        foreach (ModbusColumnMapping mapping in binding.Columns)
        {
            rows.Add(new object?[]
            {
                mapping.ColumnName,
                FormatDirection(binding.Direction),
                FormatArea(mapping.Area),
                (long)mapping.DeclaredAddress,
                (long)mapping.PduAddress,
                (long)mapping.RegisterCount,
                mapping.BitIndex is null ? null : (long)mapping.BitIndex.Value,
                FormatValueType(mapping),
                FormatByteOrder(mapping.ByteOrder),
                FormatWordOrder(mapping.WordOrder),
                mapping.Scale,
                mapping.Offset,
                FormatAccess(mapping.Access),
                FormatTableMode(binding.TableMode),
                binding.Direction == ModbusMappingDirection.SourceToTable
                    ? FormatErrorPolicy(binding.ErrorPolicy)
                    : null,
                binding.Direction == ModbusMappingDirection.TableToEndpoint
                    ? FormatApprovedWriteAction(binding.ApprovedWriteAction)
                    : null,
                binding.Direction == ModbusMappingDirection.SourceToTable ? "SOURCE" : "ENDPOINT",
                binding.TargetName,
                binding.RowKey,
                binding.StoreHistory,
                binding.SampleTimeColumn,
                binding.QualityColumn,
                binding.Enabled,
                snapshot.Revision,
            });
        }

        return new SelectExecutionResult(_tableColumns, rows);
    }

    /// <summary>构造 source 元数据结果，可选按名称过滤。</summary>
    private static SelectExecutionResult BuildSourcesResult(
        ModbusCatalogSnapshot snapshot,
        IReadOnlyDictionary<string, ModbusSourceRuntimeStatus> runtimeStatuses,
        string? name)
    {
        IReadOnlyList<ModbusSourceDefinition> definitions = snapshot.Sources.Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
        if (name is not null)
        {
            ModbusSourceDefinition definition = snapshot.Sources.GetValueOrDefault(name)
                ?? throw new InvalidOperationException($"MODBUS SOURCE '{name}' 不存在。");
            definitions = [definition];
        }

        var rows = new List<IReadOnlyList<object?>>(definitions.Count);
        foreach (ModbusSourceDefinition source in definitions)
        {
            ModbusSourceRuntimeStatus runtimeStatus = runtimeStatuses.GetValueOrDefault(source.Name)
                ?? ModbusSourceRuntimeStatus.Disabled;
            rows.Add(new object?[]
            {
                source.Name,
                "TCP",
                FormatHostAndPort(source.Host, source.Port),
                (long)source.UnitId,
                FormatAddressingMode(source.AddressingMode),
                FormatByteOrder(source.ByteOrder),
                FormatWordOrder(source.WordOrder),
                (long)source.PollIntervalMilliseconds,
                (long)source.TimeoutMilliseconds,
                (long)source.RetryCount,
                runtimeStatus.RuntimeEnabled,
                FormatRuntimeHealth(runtimeStatus.Health),
                runtimeStatus.LastSuccessAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                runtimeStatus.LastErrorCode,
                source.Enabled,
                "catalog",
                snapshot.Revision,
                runtimeStatus.LastAttemptAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                runtimeStatus.LastErrorAtUtc?.ToString("O", CultureInfo.InvariantCulture),
                runtimeStatus.ConsecutiveFailures,
            });
        }

        return new SelectExecutionResult(_sourceColumns, rows);
    }

    /// <summary>格式化 source 运行健康状态的稳定 SQL 名称。</summary>
    private static string FormatRuntimeHealth(ModbusSourceRuntimeHealth health) => health switch
    {
        ModbusSourceRuntimeHealth.Disabled => "disabled",
        ModbusSourceRuntimeHealth.Starting => "starting",
        ModbusSourceRuntimeHealth.Idle => "idle",
        ModbusSourceRuntimeHealth.Healthy => "healthy",
        ModbusSourceRuntimeHealth.Degraded => "degraded",
        _ => throw new ArgumentOutOfRangeException(nameof(health), health, "未知的 Modbus source 运行状态。"),
    };

    /// <summary>构造 endpoint 元数据结果，可选按名称过滤。</summary>
    private static SelectExecutionResult BuildEndpointsResult(
        ModbusCatalogSnapshot snapshot,
        IReadOnlyDictionary<string, ModbusEndpointRuntimeStatus> runtimeStatuses,
        string? name)
    {
        IReadOnlyList<ModbusEndpointDefinition> definitions = snapshot.Endpoints.Values
            .OrderBy(static definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
        if (name is not null)
        {
            ModbusEndpointDefinition definition = snapshot.Endpoints.GetValueOrDefault(name)
                ?? throw new InvalidOperationException($"MODBUS ENDPOINT '{name}' 不存在。");
            definitions = [definition];
        }

        var rows = new List<IReadOnlyList<object?>>(definitions.Count);
        foreach (ModbusEndpointDefinition endpoint in definitions)
        {
            ModbusEndpointRuntimeStatus runtimeStatus = runtimeStatuses.GetValueOrDefault(endpoint.Name)
                ?? ModbusEndpointRuntimeStatus.Disabled;
            rows.Add(new object?[]
            {
                endpoint.Name,
                "TCP",
                FormatHostAndPort(endpoint.BindAddress, endpoint.Port),
                (long)endpoint.UnitId,
                FormatAddressingMode(endpoint.AddressingMode),
                FormatByteOrder(endpoint.ByteOrder),
                FormatWordOrder(endpoint.WordOrder),
                FormatWritePolicy(endpoint.WritePolicy),
                endpoint.AllowedClientNetworks is null
                    ? string.Empty
                    : string.Join(",", endpoint.AllowedClientNetworks),
                (long)endpoint.MaxConnections,
                runtimeStatus.RuntimeEnabled,
                FormatEndpointRuntimeHealth(runtimeStatus.Health),
                runtimeStatus.LastErrorCode,
                endpoint.Enabled,
                "catalog",
                snapshot.Revision,
            });
        }

        return new SelectExecutionResult(_endpointColumns, rows);
    }

    /// <summary>格式化 endpoint 运行健康状态的稳定 SQL 名称。</summary>
    private static string FormatEndpointRuntimeHealth(ModbusEndpointRuntimeHealth health) => health switch
    {
        ModbusEndpointRuntimeHealth.Disabled => "disabled",
        ModbusEndpointRuntimeHealth.Starting => "starting",
        ModbusEndpointRuntimeHealth.Listening => "listening",
        ModbusEndpointRuntimeHealth.Degraded => "degraded",
        _ => throw new ArgumentOutOfRangeException(nameof(health), health, "未知的 Modbus endpoint 运行状态。"),
    };

    /// <summary>格式化主机与端口，并为 IPv6 地址补充方括号。</summary>
    private static string FormatHostAndPort(string host, int port)
        => host.Contains(':', StringComparison.Ordinal)
            ? $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"
            : $"{host}:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>格式化寻址模式的稳定 SQL 名称。</summary>
    private static string FormatAddressingMode(ModbusAddressingMode mode) => mode switch
    {
        ModbusAddressingMode.ZeroBased => "ZERO_BASED",
        ModbusAddressingMode.OneBased => "ONE_BASED",
        ModbusAddressingMode.Modicon => "MODICON",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的 Modbus 寻址模式。"),
    };

    /// <summary>格式化寄存器内字节序的稳定 SQL 名称。</summary>
    private static string FormatByteOrder(ModbusByteOrder order) => order switch
    {
        ModbusByteOrder.BigEndian => "BIG_ENDIAN",
        ModbusByteOrder.LittleEndian => "LITTLE_ENDIAN",
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "未知的 Modbus 字节序。"),
    };

    /// <summary>格式化多寄存器字序的稳定 SQL 名称。</summary>
    private static string FormatWordOrder(ModbusWordOrder order) => order switch
    {
        ModbusWordOrder.BigEndian => "BIG_ENDIAN",
        ModbusWordOrder.LittleEndian => "LITTLE_ENDIAN",
        _ => throw new ArgumentOutOfRangeException(nameof(order), order, "未知的 Modbus 字序。"),
    };

    /// <summary>格式化 endpoint 写入入口策略。</summary>
    private static string FormatWritePolicy(ModbusEndpointWritePolicy policy) => policy switch
    {
        ModbusEndpointWritePolicy.Reject => "REJECT",
        ModbusEndpointWritePolicy.Staged => "STAGED",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "未知的 Modbus endpoint 写入策略。"),
    };

    /// <summary>格式化表映射方向。</summary>
    private static string FormatDirection(ModbusMappingDirection direction) => direction switch
    {
        ModbusMappingDirection.SourceToTable => "FROM",
        ModbusMappingDirection.TableToEndpoint => "EXPOSE",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "未知的 Modbus 映射方向。"),
    };

    /// <summary>格式化 Modbus 地址空间。</summary>
    private static string FormatArea(ModbusRegisterArea area) => area switch
    {
        ModbusRegisterArea.Coil => "COIL",
        ModbusRegisterArea.DiscreteInput => "DISCRETE_INPUT",
        ModbusRegisterArea.InputRegister => "INPUT_REGISTER",
        ModbusRegisterArea.HoldingRegister => "HOLDING_REGISTER",
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "未知的 Modbus 地址空间。"),
    };

    /// <summary>格式化 wire type，并保留 STRING 的固定字节长度。</summary>
    private static string FormatValueType(ModbusColumnMapping mapping) => mapping.ValueType switch
    {
        ModbusValueType.Bit => "BIT",
        ModbusValueType.Int16 => "INT16",
        ModbusValueType.UInt16 => "UINT16",
        ModbusValueType.Int32 => "INT32",
        ModbusValueType.UInt32 => "UINT32",
        ModbusValueType.Float32 => "FLOAT32",
        ModbusValueType.Float64 => "FLOAT64",
        ModbusValueType.Bcd16 => "BCD16",
        ModbusValueType.Bcd32 => "BCD32",
        ModbusValueType.String => $"STRING({mapping.StringLength.ToString(CultureInfo.InvariantCulture)})",
        _ => throw new ArgumentOutOfRangeException(nameof(mapping), mapping.ValueType, "未知的 Modbus wire type。"),
    };

    /// <summary>格式化列访问模式。</summary>
    private static string FormatAccess(ModbusAccessMode access) => access switch
    {
        ModbusAccessMode.Read => "READ",
        ModbusAccessMode.Write => "WRITE",
        ModbusAccessMode.ReadWrite => "READ_WRITE",
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, "未知的 Modbus 访问模式。"),
    };

    /// <summary>格式化 source 表模式。</summary>
    private static string FormatTableMode(ModbusTableMode mode) => mode switch
    {
        ModbusTableMode.Latest => "LATEST",
        ModbusTableMode.History => "HISTORY",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的 Modbus 表模式。"),
    };

    /// <summary>格式化 source 采集错误策略。</summary>
    private static string FormatErrorPolicy(ModbusErrorPolicy policy) => policy switch
    {
        ModbusErrorPolicy.KeepLast => "KEEP_LAST",
        ModbusErrorPolicy.Null => "NULL",
        ModbusErrorPolicy.Skip => "SKIP",
        ModbusErrorPolicy.MarkBad => "MARK_BAD",
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "未知的 Modbus 错误策略。"),
    };

    /// <summary>格式化 endpoint 审批后的应用动作。</summary>
    private static string FormatApprovedWriteAction(ModbusApprovedWriteAction action) => action switch
    {
        ModbusApprovedWriteAction.StageOnly => "STAGE_ONLY",
        ModbusApprovedWriteAction.UpdateTable => "UPDATE_TABLE",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "未知的 Modbus 审批动作。"),
    };
}
