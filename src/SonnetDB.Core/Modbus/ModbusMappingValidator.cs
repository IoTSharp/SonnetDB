using System.Globalization;
using System.Net;
using System.Net.Sockets;
using SonnetDB.Tables;

namespace SonnetDB.Modbus;

/// <summary>
/// 对 Modbus source、endpoint、列映射和关系表绑定执行确定性校验。
/// </summary>
public static class ModbusMappingValidator
{
    /// <summary>
    /// 校验主动轮询 source 的连接参数和默认编解码设置。
    /// </summary>
    /// <param name="source">要校验的 source 定义。</param>
    public static void ValidateSource(ModbusSourceDefinition source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Host);
        ValidateTcpPort(source.Port, nameof(source));
        if (source.PollIntervalMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.PollIntervalMilliseconds,
                "Modbus source 轮询间隔必须大于 0 毫秒。");
        }
        if (source.TimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.TimeoutMilliseconds,
                "Modbus source 超时必须大于 0 毫秒。");
        }
        if (source.RetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source.RetryCount,
                "Modbus source 重试次数不能为负数。");
        }

        ValidateEnum(source.AddressingMode, nameof(source));
        ValidateEnum(source.ByteOrder, nameof(source));
        ValidateEnum(source.WordOrder, nameof(source));
    }

    /// <summary>
    /// 校验从站 endpoint 的监听、安全边界和默认编解码设置。
    /// </summary>
    /// <param name="endpoint">要校验的 endpoint 定义。</param>
    public static void ValidateEndpoint(ModbusEndpointDefinition endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.BindAddress);
        if (!IPAddress.TryParse(endpoint.BindAddress, out var bindAddress))
            throw new ArgumentException("Modbus endpoint BIND 必须是有效的 IPv4 或 IPv6 地址。", nameof(endpoint));

        ValidateTcpPort(endpoint.Port, nameof(endpoint));
        if (endpoint.MaxConnections <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(endpoint),
                endpoint.MaxConnections,
                "Modbus endpoint 最大连接数必须大于 0。");
        }

        ValidateEnum(endpoint.AddressingMode, nameof(endpoint));
        ValidateEnum(endpoint.ByteOrder, nameof(endpoint));
        ValidateEnum(endpoint.WordOrder, nameof(endpoint));
        ValidateEnum(endpoint.WritePolicy, nameof(endpoint));
        ValidateAllowlist(endpoint.AllowedClientNetworks, bindAddress, nameof(endpoint));
    }

    /// <summary>
    /// 校验单个列映射的区域、跨度、类型、位索引、缩放和访问模式。
    /// </summary>
    /// <param name="mapping">要校验的列映射。</param>
    public static void ValidateColumn(ModbusColumnMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapping.ColumnName);
        ValidateEnum(mapping.Area, nameof(mapping));
        ValidateEnum(mapping.ValueType, nameof(mapping));
        ValidateEnum(mapping.ByteOrder, nameof(mapping));
        ValidateEnum(mapping.WordOrder, nameof(mapping));
        ValidateEnum(mapping.Access, nameof(mapping));

        if (mapping.DeclaredAddress < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mapping),
                mapping.DeclaredAddress,
                "Modbus 声明地址不能为负数。");
        }
        if (mapping.Scale == 0m)
            throw new ArgumentOutOfRangeException(nameof(mapping), mapping.Scale, "Modbus SCALE 不能为 0。");

        int expectedCount = ModbusValueCodec.GetRegisterCount(mapping.ValueType, mapping.StringLength);
        if (mapping.RegisterCount != expectedCount)
        {
            throw new ArgumentException(
                $"列 '{mapping.ColumnName}' 的 count 为 {mapping.RegisterCount}，但 {mapping.ValueType} 需要 {expectedCount}。",
                nameof(mapping));
        }

        int endExclusive = mapping.PduAddress + mapping.RegisterCount;
        if (endExclusive > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mapping),
                endExclusive - 1,
                $"列 '{mapping.ColumnName}' 的 PDU 地址跨度超过 65535。");
        }

        ValidateAreaAndBit(mapping);
        if (mapping.ValueType is ModbusValueType.Bit or ModbusValueType.String
            && (mapping.Scale != 1m || mapping.Offset != 0m))
        {
            throw new ArgumentException(
                $"列 '{mapping.ColumnName}' 的 {mapping.ValueType} 类型不支持 SCALE 或 OFFSET。",
                nameof(mapping));
        }
    }

    /// <summary>
    /// 校验不依赖关系表 schema 的绑定方向、映射集合和地址冲突。
    /// </summary>
    /// <param name="binding">要校验的表绑定。</param>
    public static void ValidateBinding(ModbusTableBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.TargetName);
        ArgumentNullException.ThrowIfNull(binding.Columns);
        if (binding.Columns.Count == 0)
            throw new ArgumentException("Modbus 表绑定至少需要一个列映射。", nameof(binding));

        ValidateEnum(binding.Direction, nameof(binding));
        ValidateEnum(binding.TableMode, nameof(binding));
        ValidateEnum(binding.ErrorPolicy, nameof(binding));
        ValidateEnum(binding.ApprovedWriteAction, nameof(binding));
        ValidateOptionalColumnName(binding.SampleTimeColumn, "SAMPLE_TIME", nameof(binding));
        ValidateOptionalColumnName(binding.QualityColumn, "QUALITY", nameof(binding));
        ValidateDirectionOptions(binding);

        var columnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in binding.Columns)
        {
            ValidateColumn(mapping);
            if (!columnNames.Add(mapping.ColumnName))
            {
                throw new ArgumentException(
                    $"Modbus 表绑定中的列 '{mapping.ColumnName}' 重复。",
                    nameof(binding));
            }
        }

        ValidateAddressConflicts(binding.Columns, nameof(binding));
    }

    /// <summary>
    /// 在基础绑定校验之上检查关系表列存在性和 SQL/wire 类型兼容性。
    /// </summary>
    /// <param name="binding">要校验的表绑定。</param>
    /// <param name="schema">绑定目标关系表的 schema。</param>
    public static void ValidateBinding(ModbusTableBinding binding, TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ValidateBinding(binding);
        if (!string.Equals(binding.TableName, schema.Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Modbus 绑定表名 '{binding.TableName}' 与 schema 表名 '{schema.Name}' 不一致。",
                nameof(schema));
        }

        foreach (var mapping in binding.Columns)
        {
            TableColumn column = schema.TryGetColumn(mapping.ColumnName)
                ?? throw new ArgumentException(
                    $"表 '{schema.Name}' 不存在 Modbus 映射列 '{mapping.ColumnName}'。",
                    nameof(schema));
            TableColumnType expectedType = GetExpectedColumnType(mapping);
            if (column.DataType != expectedType)
            {
                throw new ArgumentException(
                    $"列 '{mapping.ColumnName}' 的类型为 {column.DataType}，但 {mapping.ValueType} 映射需要 {expectedType}。",
                    nameof(schema));
            }

            if (binding.Direction == ModbusMappingDirection.SourceToTable
                && binding.ErrorPolicy is ModbusErrorPolicy.Null or ModbusErrorPolicy.MarkBad
                && !column.IsNullable)
            {
                throw new ArgumentException(
                    $"ON_ERROR {binding.ErrorPolicy} 可能写入 NULL，列 '{mapping.ColumnName}' 必须允许 NULL。",
                    nameof(schema));
            }
        }

        ValidateSampleTimeColumn(binding, schema);
        ValidateQualityColumn(binding, schema);
        ValidateRowKey(binding, schema);
        ValidateSourceTableIdentity(binding, schema);
        ValidateErrorPolicyNullability(binding, schema);
    }

    /// <summary>
    /// 校验 TCP 端口范围。
    /// </summary>
    private static void ValidateTcpPort(int port, string parameterName)
    {
        if (port is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(parameterName, port, "Modbus TCP 端口必须位于 1..65535。");
    }

    /// <summary>
    /// 校验枚举值确实由当前合同定义。
    /// </summary>
    private static void ValidateEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, $"未知的 {typeof(TEnum).Name} 值。");
    }

    /// <summary>
    /// 校验 allowlist 条目，并禁止非回环监听使用空 allowlist。
    /// </summary>
    private static void ValidateAllowlist(
        IReadOnlyList<string>? allowlist,
        IPAddress bindAddress,
        string parameterName)
    {
        if ((allowlist is null || allowlist.Count == 0) && !IPAddress.IsLoopback(bindAddress))
            throw new ArgumentException("非回环 Modbus endpoint 必须配置非空 ALLOWLIST。", parameterName);
        if (allowlist is null)
            return;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(entry) || !IsValidIpOrCidr(entry))
                throw new ArgumentException($"ALLOWLIST 条目 '{entry}' 不是有效的 IP 或 CIDR。", parameterName);
            if (!seen.Add(entry))
                throw new ArgumentException($"ALLOWLIST 条目 '{entry}' 重复。", parameterName);
        }
    }

    /// <summary>
    /// 判断文本是否为有效 IP 地址或带合法前缀长度的 CIDR。
    /// </summary>
    private static bool IsValidIpOrCidr(string value)
    {
        int slashIndex = value.IndexOf('/');
        if (slashIndex < 0)
            return IPAddress.TryParse(value, out _);
        if (slashIndex == 0 || slashIndex != value.LastIndexOf('/'))
            return false;

        string addressText = value[..slashIndex];
        string prefixText = value[(slashIndex + 1)..];
        if (!IPAddress.TryParse(addressText, out var address)
            || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out int prefixLength))
        {
            return false;
        }

        int maximumPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        return prefixLength >= 0 && prefixLength <= maximumPrefix;
    }

    /// <summary>
    /// 校验区域允许的值类型、位索引和访问模式。
    /// </summary>
    private static void ValidateAreaAndBit(ModbusColumnMapping mapping)
    {
        bool bitArea = mapping.Area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput;
        bool registerArea = mapping.Area is ModbusRegisterArea.InputRegister or ModbusRegisterArea.HoldingRegister;
        if (bitArea && mapping.ValueType != ModbusValueType.Bit)
        {
            throw new ArgumentException(
                $"{mapping.Area} 只能映射 BIT 类型。",
                nameof(mapping));
        }

        if (mapping.ValueType == ModbusValueType.Bit)
        {
            if (bitArea && mapping.BitIndex is not null)
                throw new ArgumentException($"{mapping.Area} 不使用寄存器比特索引。", nameof(mapping));
            if (registerArea && mapping.BitIndex is not (>= 0 and <= 15))
                throw new ArgumentException("寄存器 BIT 映射必须指定 0..15 的 BitIndex。", nameof(mapping));
        }
        else if (mapping.BitIndex is not null)
        {
            throw new ArgumentException("非 BIT 映射的 BitIndex 必须为 null。", nameof(mapping));
        }

        if (mapping.Area is ModbusRegisterArea.DiscreteInput or ModbusRegisterArea.InputRegister
            && mapping.Access != ModbusAccessMode.Read)
        {
            throw new ArgumentException($"{mapping.Area} 只允许 ACCESS READ。", nameof(mapping));
        }
        if (registerArea && mapping.ValueType == ModbusValueType.Bit && mapping.Access != ModbusAccessMode.Read)
            throw new ArgumentException("寄存器 BIT 第一版只允许 ACCESS READ。", nameof(mapping));
    }

    /// <summary>
    /// 校验 source 与 endpoint 各自允许的绑定选项组合。
    /// </summary>
    private static void ValidateDirectionOptions(ModbusTableBinding binding)
    {
        if (binding.Direction == ModbusMappingDirection.SourceToTable)
        {
            if (binding.RowKey is not null)
                throw new ArgumentException("Source 表绑定不允许声明 ROW KEY。", nameof(binding));
            if (binding.ApprovedWriteAction != ModbusApprovedWriteAction.StageOnly)
                throw new ArgumentException("Source 表绑定不使用 endpoint 审批后动作。", nameof(binding));
            return;
        }

        if (binding.TableMode != ModbusTableMode.Latest)
            throw new ArgumentException("Endpoint 第一版只能暴露 LATEST 表模式。", nameof(binding));
        if (binding.RowKey is null)
            throw new ArgumentException("Endpoint 表绑定必须声明固定 ROW KEY。", nameof(binding));
        if (binding.ErrorPolicy != ModbusErrorPolicy.KeepLast)
            throw new ArgumentException("Endpoint 表绑定不使用 source ON_ERROR 策略。", nameof(binding));
        if (binding.StoreHistory)
            throw new ArgumentException("Endpoint 表绑定不允许启用历史采样存储。", nameof(binding));
        if (binding.SampleTimeColumn is not null || binding.QualityColumn is not null)
            throw new ArgumentException("Endpoint 表绑定不使用 source 采样时间或质量列。", nameof(binding));
    }

    /// <summary>
    /// 校验可选元数据列名不是空白文本。
    /// </summary>
    private static void ValidateOptionalColumnName(string? columnName, string optionName, string parameterName)
    {
        if (columnName is not null && string.IsNullOrWhiteSpace(columnName))
            throw new ArgumentException($"{optionName} 列名不能为空白文本。", parameterName);
    }

    /// <summary>
    /// 按四个独立地址空间检查规范化 PDU 区间是否重叠。
    /// </summary>
    private static void ValidateAddressConflicts(
        IReadOnlyList<ModbusColumnMapping> mappings,
        string parameterName)
    {
        foreach (var areaGroup in mappings.GroupBy(static mapping => mapping.Area))
        {
            ModbusColumnMapping? furthestMapping = null;
            int furthestEndExclusive = -1;
            foreach (var current in areaGroup
                         .OrderBy(static mapping => mapping.PduAddress)
                         .ThenBy(static mapping => mapping.RegisterCount))
            {
                if (furthestMapping is not null && current.PduAddress < furthestEndExclusive)
                {
                    throw new ArgumentException(
                        $"{current.Area} 中列 '{furthestMapping.ColumnName}' 与 '{current.ColumnName}' 的 PDU 地址区间重叠。",
                        parameterName);
                }

                int currentEndExclusive = current.PduAddress + current.RegisterCount;
                if (currentEndExclusive > furthestEndExclusive)
                {
                    furthestMapping = current;
                    furthestEndExclusive = currentEndExclusive;
                }
            }
        }
    }

    /// <summary>
    /// 返回 wire 类型和缩放设置要求的关系表列类型。
    /// </summary>
    private static TableColumnType GetExpectedColumnType(ModbusColumnMapping mapping)
        => mapping.ValueType switch
        {
            ModbusValueType.Bit => TableColumnType.Boolean,
            ModbusValueType.String => TableColumnType.String,
            ModbusValueType.Float32 or ModbusValueType.Float64 => TableColumnType.Float64,
            ModbusValueType.Int16 or ModbusValueType.UInt16 or ModbusValueType.Int32
                or ModbusValueType.UInt32 or ModbusValueType.Bcd16 or ModbusValueType.Bcd32
                when mapping.Scale == 1m && mapping.Offset == 0m => TableColumnType.Int64,
            ModbusValueType.Int16 or ModbusValueType.UInt16 or ModbusValueType.Int32
                or ModbusValueType.UInt32 or ModbusValueType.Bcd16 or ModbusValueType.Bcd32
                => TableColumnType.Float64,
            _ => throw new ArgumentOutOfRangeException(nameof(mapping), mapping.ValueType, "未知的 Modbus 值类型。"),
        };

    /// <summary>
    /// 校验采样时间列存在且使用 DateTime 类型。
    /// </summary>
    private static void ValidateSampleTimeColumn(ModbusTableBinding binding, TableSchema schema)
    {
        if (binding.Direction != ModbusMappingDirection.SourceToTable)
            return;
        if (binding.SampleTimeColumn is null)
            return;

        TableColumn sampleTime = schema.TryGetColumn(binding.SampleTimeColumn)
            ?? throw new ArgumentException(
                $"表 '{schema.Name}' 不存在采样时间列 '{binding.SampleTimeColumn}'。",
                nameof(schema));
        if (sampleTime.DataType != TableColumnType.DateTime)
            throw new ArgumentException("SAMPLE_TIME 列必须使用 DateTime 类型。", nameof(schema));
        if (binding.Columns.Any(mapping => string.Equals(
                mapping.ColumnName,
                binding.SampleTimeColumn,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException("SAMPLE_TIME 列不能同时映射到 Modbus 地址。", nameof(binding));
        }
    }

    /// <summary>
    /// 校验可选质量列存在且不与其他绑定角色冲突。
    /// </summary>
    private static void ValidateQualityColumn(ModbusTableBinding binding, TableSchema schema)
    {
        if (binding.QualityColumn is null)
            return;

        TableColumn quality = schema.TryGetColumn(binding.QualityColumn)
            ?? throw new ArgumentException(
                $"表 '{schema.Name}' 不存在质量列 '{binding.QualityColumn}'。",
                nameof(schema));
        if (quality.DataType != TableColumnType.Int64)
            throw new ArgumentException("QUALITY 列必须使用 Int64 类型保存质量位。", nameof(schema));
        if (binding.Columns.Any(mapping => string.Equals(
                mapping.ColumnName,
                binding.QualityColumn,
                StringComparison.Ordinal)))
        {
            throw new ArgumentException("QUALITY 列不能同时映射到 Modbus 地址。", nameof(binding));
        }
        if (string.Equals(binding.QualityColumn, binding.SampleTimeColumn, StringComparison.Ordinal))
            throw new ArgumentException("QUALITY 列不能与 SAMPLE_TIME 列相同。", nameof(binding));
    }

    /// <summary>
    /// 校验固定 ROW KEY 对应单列 Int64 主键。
    /// </summary>
    private static void ValidateRowKey(ModbusTableBinding binding, TableSchema schema)
    {
        if (binding.RowKey is null)
            return;
        if (schema.PrimaryKey.Count != 1)
            throw new ArgumentException("Modbus ROW KEY 第一版要求关系表使用单列主键。", nameof(schema));

        TableColumn primaryKey = schema.TryGetColumn(schema.PrimaryKey[0])
            ?? throw new ArgumentException("关系表 schema 的主键列不存在。", nameof(schema));
        if (primaryKey.DataType != TableColumnType.Int64)
            throw new ArgumentException("Modbus ROW KEY 第一版要求主键列使用 Int64 类型。", nameof(schema));
    }

    /// <summary>
    /// 校验 source runtime 可以确定性生成 LATEST 固定键或 HISTORY 采样键。
    /// </summary>
    private static void ValidateSourceTableIdentity(ModbusTableBinding binding, TableSchema schema)
    {
        if (binding.Direction != ModbusMappingDirection.SourceToTable)
            return;
        if (schema.PrimaryKey.Count != 1)
        {
            throw new ArgumentException(
                "Modbus source 表必须使用单列主键，以便 runtime 生成确定性的采样行标识。",
                nameof(schema));
        }

        TableColumn primaryKey = schema.TryGetColumn(schema.PrimaryKey[0])
            ?? throw new ArgumentException("关系表 schema 的主键列不存在。", nameof(schema));
        if (binding.TableMode == ModbusTableMode.Latest)
        {
            if (primaryKey.DataType != TableColumnType.Int64 || primaryKey.IsAutoIncrement)
            {
                throw new ArgumentException(
                    "Modbus LATEST 表必须使用非自增 Int64 单列主键；runtime 固定写入键 0。",
                    nameof(schema));
            }
            return;
        }

        bool generatedHistoryKey = primaryKey.DataType == TableColumnType.DateTime
                                   || (primaryKey.DataType == TableColumnType.Int64
                                       && primaryKey.IsAutoIncrement);
        if (!generatedHistoryKey)
        {
            throw new ArgumentException(
                "Modbus HISTORY 表必须使用 DateTime 单列主键或自增 Int64 单列主键。",
                nameof(schema));
        }
    }

    /// <summary>
    /// 校验会产生缺值的失败策略只能写入允许 NULL 的可读映射列。
    /// </summary>
    private static void ValidateErrorPolicyNullability(ModbusTableBinding binding, TableSchema schema)
    {
        if (binding.Direction != ModbusMappingDirection.SourceToTable
            || binding.ErrorPolicy is not (ModbusErrorPolicy.Null or ModbusErrorPolicy.MarkBad))
        {
            return;
        }

        foreach (ModbusColumnMapping mapping in binding.Columns)
        {
            if (mapping.Access == ModbusAccessMode.Write)
                continue;

            TableColumn column = schema.TryGetColumn(mapping.ColumnName)
                ?? throw new ArgumentException(
                    $"表 '{schema.Name}' 不存在映射列 '{mapping.ColumnName}'。",
                    nameof(schema));
            if (!column.IsNullable)
            {
                throw new ArgumentException(
                    $"ON_ERROR {binding.ErrorPolicy.ToString().ToUpperInvariant()} 要求可读映射列 "
                    + $"'{mapping.ColumnName}' 允许 NULL。",
                    nameof(schema));
            }
        }
    }
}
