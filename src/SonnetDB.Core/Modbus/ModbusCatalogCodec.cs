using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace SonnetDB.Modbus;

/// <summary>
/// Modbus 独立目录文件（<c>modbus/modbus.sdbmodbus</c>）的版本化二进制编解码器。
/// </summary>
internal static class ModbusCatalogCodec
{
    /// <summary>Modbus 目录文件名。</summary>
    public const string FileName = "modbus.sdbmodbus";

    private const int FormatVersion = 1;
    private const int HeaderSize = 40;
    private const int FooterSize = 16;
    private const int MaxSourceCount = 10_000;
    private const int MaxEndpointCount = 10_000;
    private const int MaxBindingCount = 100_000;
    private const int MaxColumnsPerBinding = 65_535;
    private const int MaxAllowedNetworkCount = 4_096;
    private const int MaxNameBytes = 1_024;
    private const int MaxAddressBytes = 4_096;
    private const int MaxFixedStringBytes = 131_072;
    private static readonly byte[] Magic = "SDBMODB1"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// 从指定文件加载 Modbus 目录；文件不存在时返回修订号为 0 的空目录。
    /// </summary>
    /// <param name="path">Modbus 目录文件路径。</param>
    /// <returns>完成校验的 Modbus 目录。</returns>
    /// <exception cref="InvalidDataException">格式版本、长度、枚举、CRC 或目录内容无效时抛出。</exception>
    public static ModbusCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return new ModbusCatalog();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(stream);
    }

    /// <summary>
    /// 将 Modbus 目录写入同目录临时文件，刷盘后原子替换正式文件。
    /// </summary>
    /// <param name="path">Modbus 目录文件路径。</param>
    /// <param name="catalog">待保存目录。</param>
    /// <param name="tempSuffix">同目录临时文件后缀，不能为空。</param>
    public static void Save(
        string path,
        ModbusCatalog catalog,
        string tempSuffix = ".tmp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrEmpty(tempSuffix);

        ModbusCatalogState state = catalog.CaptureState();
        ValidateStateCounts(state);

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + tempSuffix;
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var buffered = new BufferedStream(file, 65_536))
            {
                Save(state, buffered);
                buffered.Flush();
                file.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch
        {
            // 正式文件在 File.Move 前始终保持不变；清理失败不能覆盖原始写入异常。
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    /// <summary>读取并校验一个完整目录流。</summary>
    private static ModbusCatalog Load(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("ModbusCatalog: invalid magic in header.");

        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (version != FormatVersion)
            throw new InvalidDataException($"ModbusCatalog: unsupported format version {version}.");
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4));
        if (headerSize != HeaderSize)
            throw new InvalidDataException($"ModbusCatalog: unexpected header size {headerSize}.");

        long revision = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(16, 8));
        if (revision < 0)
            throw new InvalidDataException($"ModbusCatalog: invalid revision {revision}.");
        int sourceCount = ReadBoundedCount(header.Slice(24, 4), MaxSourceCount, "source");
        int endpointCount = ReadBoundedCount(header.Slice(28, 4), MaxEndpointCount, "endpoint");
        int bindingCount = ReadBoundedCount(header.Slice(32, 4), MaxBindingCount, "binding");
        int reserved = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(36, 4));
        if (reserved != 0)
            throw new InvalidDataException("ModbusCatalog: header reserved bytes are not zero.");

        var crc = new Crc32();
        crc.Append(header);
        var sources = new List<ModbusSourceDefinition>(sourceCount);
        var endpoints = new List<ModbusEndpointDefinition>(endpointCount);
        var bindings = new List<ModbusTableBinding>(bindingCount);
        for (var i = 0; i < sourceCount; i++)
            sources.Add(ReadSource(source, crc, i));
        for (var i = 0; i < endpointCount; i++)
            endpoints.Add(ReadEndpoint(source, crc, i));
        for (var i = 0; i < bindingCount; i++)
            bindings.Add(ReadBinding(source, crc, i));

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("ModbusCatalog: invalid magic in footer.");
        int footerVersion = BinaryPrimitives.ReadInt32LittleEndian(footer.Slice(12, 4));
        if (footerVersion != version)
            throw new InvalidDataException("ModbusCatalog: header and footer versions do not match.");
        uint actualCrc = crc.GetCurrentHashAsUInt32();
        if (storedCrc != actualCrc)
        {
            throw new InvalidDataException(
                $"ModbusCatalog: CRC32 mismatch (expected 0x{storedCrc:X8}, got 0x{actualCrc:X8}).");
        }
        if (source.ReadByte() != -1)
            throw new InvalidDataException("ModbusCatalog: unexpected trailing data.");

        return new ModbusCatalog(new ModbusCatalogState(revision, sources, endpoints, bindings));
    }

    /// <summary>按固定顺序写出目录头、三类定义与校验尾。</summary>
    private static void Save(ModbusCatalogState state, Stream destination)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.Slice(16, 8), state.Revision);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(24, 4), state.Sources.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(28, 4), state.Endpoints.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(32, 4), state.Bindings.Count);
        destination.Write(header);

        var crc = new Crc32();
        crc.Append(header);
        foreach (var definition in state.Sources)
            WriteSource(destination, crc, definition);
        foreach (var definition in state.Endpoints)
            WriteEndpoint(destination, crc, definition);
        foreach (var binding in state.Bindings)
            WriteBinding(destination, crc, binding);

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer.Slice(4, Magic.Length));
        BinaryPrimitives.WriteInt32LittleEndian(footer.Slice(12, 4), FormatVersion);
        destination.Write(footer);
    }

    /// <summary>读取并校验一项 source 定义。</summary>
    private static ModbusSourceDefinition ReadSource(Stream source, Crc32 crc, int index)
    {
        string name = ReadRequiredString(source, crc, MaxNameBytes, $"source {index} name");
        string host = ReadRequiredString(source, crc, MaxAddressBytes, $"source {index} host");
        int port = ReadInt32(source, crc, $"source '{name}' port");
        byte unitId = ReadByte(source, crc, $"source '{name}' unit id");
        ModbusAddressingMode addressingMode = ReadAddressingMode(source, crc, $"source '{name}' addressing mode");
        int pollInterval = ReadInt32(source, crc, $"source '{name}' poll interval");
        int timeout = ReadInt32(source, crc, $"source '{name}' timeout");
        int retryCount = ReadInt32(source, crc, $"source '{name}' retry count");
        ModbusByteOrder byteOrder = ReadByteOrder(source, crc, $"source '{name}' byte order");
        ModbusWordOrder wordOrder = ReadWordOrder(source, crc, $"source '{name}' word order");
        bool enabled = ReadBoolean(source, crc, $"source '{name}' enabled");

        var definition = new ModbusSourceDefinition(
            name,
            host,
            port,
            unitId,
            addressingMode,
            pollInterval,
            timeout,
            retryCount,
            byteOrder,
            wordOrder,
            enabled);
        ValidateSource(definition);
        return definition;
    }

    /// <summary>写出一项 source 定义。</summary>
    private static void WriteSource(Stream destination, Crc32 crc, ModbusSourceDefinition definition)
    {
        ValidateSource(definition);
        WriteRequiredString(destination, crc, definition.Name, MaxNameBytes, "source name");
        WriteRequiredString(destination, crc, definition.Host, MaxAddressBytes, $"source '{definition.Name}' host");
        WriteInt32(destination, crc, definition.Port);
        WriteByte(destination, crc, definition.UnitId);
        WriteByte(destination, crc, (byte)definition.AddressingMode);
        WriteInt32(destination, crc, definition.PollIntervalMilliseconds);
        WriteInt32(destination, crc, definition.TimeoutMilliseconds);
        WriteInt32(destination, crc, definition.RetryCount);
        WriteByte(destination, crc, (byte)definition.ByteOrder);
        WriteByte(destination, crc, (byte)definition.WordOrder);
        WriteBoolean(destination, crc, definition.Enabled);
    }

    /// <summary>读取并校验一项 endpoint 定义。</summary>
    private static ModbusEndpointDefinition ReadEndpoint(Stream source, Crc32 crc, int index)
    {
        string name = ReadRequiredString(source, crc, MaxNameBytes, $"endpoint {index} name");
        string bindAddress = ReadRequiredString(source, crc, MaxAddressBytes, $"endpoint {index} bind address");
        int port = ReadInt32(source, crc, $"endpoint '{name}' port");
        byte unitId = ReadByte(source, crc, $"endpoint '{name}' unit id");
        int maxConnections = ReadInt32(source, crc, $"endpoint '{name}' max connections");
        int networkCount = ReadInt32(source, crc, $"endpoint '{name}' allowed network count");
        IReadOnlyList<string>? networks;
        if (networkCount == -1)
        {
            networks = null;
        }
        else
        {
            if (networkCount is < 0 or > MaxAllowedNetworkCount)
            {
                throw new InvalidDataException(
                    $"ModbusCatalog: invalid endpoint '{name}' allowed network count {networkCount}.");
            }

            var values = new string[networkCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = ReadRequiredString(
                    source,
                    crc,
                    MaxAddressBytes,
                    $"endpoint '{name}' allowed network {i}");
            }
            networks = Array.AsReadOnly(values);
        }

        ModbusAddressingMode addressingMode = ReadAddressingMode(source, crc, $"endpoint '{name}' addressing mode");
        ModbusByteOrder byteOrder = ReadByteOrder(source, crc, $"endpoint '{name}' byte order");
        ModbusWordOrder wordOrder = ReadWordOrder(source, crc, $"endpoint '{name}' word order");
        ModbusEndpointWritePolicy writePolicy = ReadWritePolicy(source, crc, $"endpoint '{name}' write policy");
        bool enabled = ReadBoolean(source, crc, $"endpoint '{name}' enabled");

        var definition = new ModbusEndpointDefinition(
            name,
            bindAddress,
            port,
            unitId,
            maxConnections,
            networks,
            addressingMode,
            byteOrder,
            wordOrder,
            writePolicy,
            enabled);
        ValidateEndpoint(definition);
        return definition;
    }

    /// <summary>写出一项 endpoint 定义。</summary>
    private static void WriteEndpoint(Stream destination, Crc32 crc, ModbusEndpointDefinition definition)
    {
        ValidateEndpoint(definition);
        WriteRequiredString(destination, crc, definition.Name, MaxNameBytes, "endpoint name");
        WriteRequiredString(destination, crc, definition.BindAddress, MaxAddressBytes, $"endpoint '{definition.Name}' bind address");
        WriteInt32(destination, crc, definition.Port);
        WriteByte(destination, crc, definition.UnitId);
        WriteInt32(destination, crc, definition.MaxConnections);
        if (definition.AllowedClientNetworks is null)
        {
            WriteInt32(destination, crc, -1);
        }
        else
        {
            WriteInt32(destination, crc, definition.AllowedClientNetworks.Count);
            foreach (string network in definition.AllowedClientNetworks)
            {
                WriteRequiredString(
                    destination,
                    crc,
                    network,
                    MaxAddressBytes,
                    $"endpoint '{definition.Name}' allowed network");
            }
        }

        WriteByte(destination, crc, (byte)definition.AddressingMode);
        WriteByte(destination, crc, (byte)definition.ByteOrder);
        WriteByte(destination, crc, (byte)definition.WordOrder);
        WriteByte(destination, crc, (byte)definition.WritePolicy);
        WriteBoolean(destination, crc, definition.Enabled);
    }

    /// <summary>读取并校验一项关系表绑定。</summary>
    private static ModbusTableBinding ReadBinding(Stream source, Crc32 crc, int index)
    {
        string tableName = ReadRequiredString(source, crc, MaxNameBytes, $"binding {index} table name");
        ModbusMappingDirection direction = ReadDirection(source, crc, $"binding '{tableName}' direction");
        string targetName = ReadRequiredString(source, crc, MaxNameBytes, $"binding '{tableName}' target name");
        long? rowKey = ReadBoolean(source, crc, $"binding '{tableName}' row key flag")
            ? ReadInt64(source, crc, $"binding '{tableName}' row key")
            : null;
        ModbusTableMode tableMode = ReadTableMode(source, crc, $"binding '{tableName}' table mode");
        ModbusErrorPolicy errorPolicy = ReadErrorPolicy(source, crc, $"binding '{tableName}' error policy");
        ModbusApprovedWriteAction approvedWriteAction = ReadApprovedWriteAction(
            source,
            crc,
            $"binding '{tableName}' approved write action");
        bool storeHistory = ReadBoolean(source, crc, $"binding '{tableName}' store history");
        string? sampleTimeColumn = ReadOptionalString(
            source,
            crc,
            MaxNameBytes,
            $"binding '{tableName}' sample time column");
        string? qualityColumn = ReadOptionalString(
            source,
            crc,
            MaxNameBytes,
            $"binding '{tableName}' quality column");
        bool enabled = ReadBoolean(source, crc, $"binding '{tableName}' enabled");
        int columnCount = ReadInt32(source, crc, $"binding '{tableName}' column count");
        if (columnCount is <= 0 or > MaxColumnsPerBinding)
        {
            throw new InvalidDataException(
                $"ModbusCatalog: invalid binding '{tableName}' column count {columnCount}.");
        }

        var columns = new ModbusColumnMapping[columnCount];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = ReadColumn(source, crc, tableName, i);

        var binding = new ModbusTableBinding(
            tableName,
            direction,
            targetName,
            Array.AsReadOnly(columns),
            rowKey,
            tableMode,
            errorPolicy,
            approvedWriteAction,
            storeHistory,
            sampleTimeColumn,
            qualityColumn,
            enabled);
        ValidateBindingStorage(binding);
        return binding;
    }

    /// <summary>写出一项关系表绑定。</summary>
    private static void WriteBinding(Stream destination, Crc32 crc, ModbusTableBinding binding)
    {
        ValidateBindingStorage(binding);
        WriteRequiredString(destination, crc, binding.TableName, MaxNameBytes, "binding table name");
        WriteByte(destination, crc, (byte)binding.Direction);
        WriteRequiredString(destination, crc, binding.TargetName, MaxNameBytes, $"binding '{binding.TableName}' target name");
        WriteBoolean(destination, crc, binding.RowKey.HasValue);
        if (binding.RowKey.HasValue)
            WriteInt64(destination, crc, binding.RowKey.Value);
        WriteByte(destination, crc, (byte)binding.TableMode);
        WriteByte(destination, crc, (byte)binding.ErrorPolicy);
        WriteByte(destination, crc, (byte)binding.ApprovedWriteAction);
        WriteBoolean(destination, crc, binding.StoreHistory);
        WriteOptionalString(destination, crc, binding.SampleTimeColumn, MaxNameBytes, $"binding '{binding.TableName}' sample time column");
        WriteOptionalString(destination, crc, binding.QualityColumn, MaxNameBytes, $"binding '{binding.TableName}' quality column");
        WriteBoolean(destination, crc, binding.Enabled);
        WriteInt32(destination, crc, binding.Columns.Count);
        foreach (var mapping in binding.Columns)
            WriteColumn(destination, crc, binding.TableName, mapping);
    }

    /// <summary>读取一个列映射。</summary>
    private static ModbusColumnMapping ReadColumn(Stream source, Crc32 crc, string tableName, int index)
    {
        string columnName = ReadRequiredString(
            source,
            crc,
            MaxNameBytes,
            $"binding '{tableName}' column {index} name");
        ModbusRegisterArea area = ReadArea(source, crc, $"binding '{tableName}' column '{columnName}' area");
        int declaredAddress = ReadInt32(source, crc, $"binding '{tableName}' column '{columnName}' declared address");
        ushort pduAddress = ReadUInt16(source, crc, $"binding '{tableName}' column '{columnName}' PDU address");
        ModbusValueType valueType = ReadValueType(source, crc, $"binding '{tableName}' column '{columnName}' value type");
        int registerCount = ReadInt32(source, crc, $"binding '{tableName}' column '{columnName}' register count");
        int stringLength = ReadInt32(source, crc, $"binding '{tableName}' column '{columnName}' string length");
        int storedBitIndex = ReadInt32(source, crc, $"binding '{tableName}' column '{columnName}' bit index");
        int? bitIndex = storedBitIndex == -1 ? null : storedBitIndex;
        ModbusByteOrder byteOrder = ReadByteOrder(source, crc, $"binding '{tableName}' column '{columnName}' byte order");
        ModbusWordOrder wordOrder = ReadWordOrder(source, crc, $"binding '{tableName}' column '{columnName}' word order");
        decimal scale = ReadDecimal(source, crc, $"binding '{tableName}' column '{columnName}' scale");
        decimal offset = ReadDecimal(source, crc, $"binding '{tableName}' column '{columnName}' offset");
        ModbusAccessMode access = ReadAccess(source, crc, $"binding '{tableName}' column '{columnName}' access");

        var mapping = new ModbusColumnMapping(
            columnName,
            area,
            declaredAddress,
            pduAddress,
            valueType,
            registerCount,
            stringLength,
            bitIndex,
            byteOrder,
            wordOrder,
            scale,
            offset,
            access);
        ValidateColumnStorage(tableName, mapping);
        return mapping;
    }

    /// <summary>写出一个列映射。</summary>
    private static void WriteColumn(
        Stream destination,
        Crc32 crc,
        string tableName,
        ModbusColumnMapping mapping)
    {
        ValidateColumnStorage(tableName, mapping);
        WriteRequiredString(destination, crc, mapping.ColumnName, MaxNameBytes, $"binding '{tableName}' column name");
        WriteByte(destination, crc, (byte)mapping.Area);
        WriteInt32(destination, crc, mapping.DeclaredAddress);
        WriteUInt16(destination, crc, mapping.PduAddress);
        WriteByte(destination, crc, (byte)mapping.ValueType);
        WriteInt32(destination, crc, mapping.RegisterCount);
        WriteInt32(destination, crc, mapping.StringLength);
        WriteInt32(destination, crc, mapping.BitIndex ?? -1);
        WriteByte(destination, crc, (byte)mapping.ByteOrder);
        WriteByte(destination, crc, (byte)mapping.WordOrder);
        WriteDecimal(destination, crc, mapping.Scale);
        WriteDecimal(destination, crc, mapping.Offset);
        WriteByte(destination, crc, (byte)mapping.Access);
    }

    /// <summary>校验目录规模，防止异常数量进入文件头。</summary>
    private static void ValidateStateCounts(ModbusCatalogState state)
    {
        if (state.Revision < 0)
            throw new InvalidDataException($"ModbusCatalog: invalid revision {state.Revision}.");
        ValidateCount(state.Sources.Count, MaxSourceCount, "source");
        ValidateCount(state.Endpoints.Count, MaxEndpointCount, "endpoint");
        ValidateCount(state.Bindings.Count, MaxBindingCount, "binding");
    }

    /// <summary>校验 source 中直接影响持久化可靠性的字段。</summary>
    private static void ValidateSource(ModbusSourceDefinition definition)
    {
        ValidateRequiredText(definition.Name, "source name");
        ValidateRequiredText(definition.Host, $"source '{definition.Name}' host");
        ValidatePort(definition.Port, $"source '{definition.Name}'");
        if (definition.PollIntervalMilliseconds <= 0)
            throw new InvalidDataException($"ModbusCatalog: source '{definition.Name}' poll interval 必须大于 0。");
        if (definition.TimeoutMilliseconds <= 0)
            throw new InvalidDataException($"ModbusCatalog: source '{definition.Name}' timeout 必须大于 0。");
        if (definition.RetryCount < 0)
            throw new InvalidDataException($"ModbusCatalog: source '{definition.Name}' retry count 不能为负数。");
        ValidateEnum(definition.AddressingMode, $"source '{definition.Name}' addressing mode");
        ValidateEnum(definition.ByteOrder, $"source '{definition.Name}' byte order");
        ValidateEnum(definition.WordOrder, $"source '{definition.Name}' word order");
    }

    /// <summary>校验 endpoint 中直接影响持久化可靠性的字段。</summary>
    private static void ValidateEndpoint(ModbusEndpointDefinition definition)
    {
        ValidateRequiredText(definition.Name, "endpoint name");
        ValidateRequiredText(definition.BindAddress, $"endpoint '{definition.Name}' bind address");
        ValidatePort(definition.Port, $"endpoint '{definition.Name}'");
        if (definition.MaxConnections <= 0)
            throw new InvalidDataException($"ModbusCatalog: endpoint '{definition.Name}' max connections 必须大于 0。");
        if (definition.AllowedClientNetworks is { Count: > MaxAllowedNetworkCount })
        {
            throw new InvalidDataException(
                $"ModbusCatalog: endpoint '{definition.Name}' allowed network 数量超过 {MaxAllowedNetworkCount}。");
        }
        if (definition.AllowedClientNetworks is not null)
        {
            foreach (string network in definition.AllowedClientNetworks)
                ValidateRequiredText(network, $"endpoint '{definition.Name}' allowed network");
        }
        ValidateEnum(definition.AddressingMode, $"endpoint '{definition.Name}' addressing mode");
        ValidateEnum(definition.ByteOrder, $"endpoint '{definition.Name}' byte order");
        ValidateEnum(definition.WordOrder, $"endpoint '{definition.Name}' word order");
        ValidateEnum(definition.WritePolicy, $"endpoint '{definition.Name}' write policy");
    }

    /// <summary>校验绑定及其列集合能安全写入当前格式。</summary>
    private static void ValidateBindingStorage(ModbusTableBinding binding)
    {
        ValidateRequiredText(binding.TableName, "binding table name");
        ValidateRequiredText(binding.TargetName, $"binding '{binding.TableName}' target name");
        ArgumentNullException.ThrowIfNull(binding.Columns);
        if (binding.Columns.Count is <= 0 or > MaxColumnsPerBinding)
        {
            throw new InvalidDataException(
                $"ModbusCatalog: binding '{binding.TableName}' column count 必须在 1..{MaxColumnsPerBinding} 之间。");
        }
        ValidateEnum(binding.Direction, $"binding '{binding.TableName}' direction");
        ValidateEnum(binding.TableMode, $"binding '{binding.TableName}' table mode");
        ValidateEnum(binding.ErrorPolicy, $"binding '{binding.TableName}' error policy");
        ValidateEnum(binding.ApprovedWriteAction, $"binding '{binding.TableName}' approved write action");
        if (binding.SampleTimeColumn is not null)
            ValidateRequiredText(binding.SampleTimeColumn, $"binding '{binding.TableName}' sample time column");
        if (binding.QualityColumn is not null)
            ValidateRequiredText(binding.QualityColumn, $"binding '{binding.TableName}' quality column");

        var columnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in binding.Columns)
        {
            ValidateColumnStorage(binding.TableName, mapping);
            if (!columnNames.Add(mapping.ColumnName))
            {
                throw new InvalidDataException(
                    $"ModbusCatalog: binding '{binding.TableName}' duplicate column '{mapping.ColumnName}'。");
            }
        }
    }

    /// <summary>校验列映射的持久化边界。</summary>
    private static void ValidateColumnStorage(string tableName, ModbusColumnMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ValidateRequiredText(mapping.ColumnName, $"binding '{tableName}' column name");
        ValidateEnum(mapping.Area, $"binding '{tableName}' column '{mapping.ColumnName}' area");
        ValidateEnum(mapping.ValueType, $"binding '{tableName}' column '{mapping.ColumnName}' value type");
        ValidateEnum(mapping.ByteOrder, $"binding '{tableName}' column '{mapping.ColumnName}' byte order");
        ValidateEnum(mapping.WordOrder, $"binding '{tableName}' column '{mapping.ColumnName}' word order");
        ValidateEnum(mapping.Access, $"binding '{tableName}' column '{mapping.ColumnName}' access");
        if (mapping.DeclaredAddress < 0)
            throw new InvalidDataException($"ModbusCatalog: column '{mapping.ColumnName}' declared address 不能为负数。");
        if (mapping.RegisterCount <= 0
            || mapping.RegisterCount > ushort.MaxValue + 1
            || (long)mapping.PduAddress + mapping.RegisterCount > ushort.MaxValue + 1L)
            throw new InvalidDataException($"ModbusCatalog: column '{mapping.ColumnName}' address span 超出 PDU 范围。");
        if (mapping.StringLength is < 0 or > MaxFixedStringBytes)
        {
            throw new InvalidDataException(
                $"ModbusCatalog: column '{mapping.ColumnName}' string length 必须在 0..{MaxFixedStringBytes} 之间。");
        }
        if (mapping.BitIndex is < 0 or > 15)
            throw new InvalidDataException($"ModbusCatalog: column '{mapping.ColumnName}' bit index 必须在 0..15 之间。");
        if (mapping.Scale == 0m)
            throw new InvalidDataException($"ModbusCatalog: column '{mapping.ColumnName}' scale 不能为 0。");
    }

    /// <summary>校验必填字符串。</summary>
    private static void ValidateRequiredText(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"ModbusCatalog: {description} 不能为空。");
    }

    /// <summary>校验 TCP 端口范围。</summary>
    private static void ValidatePort(int port, string description)
    {
        if (port is <= 0 or > ushort.MaxValue)
            throw new InvalidDataException($"ModbusCatalog: {description} port 必须在 1..65535 之间。");
    }

    /// <summary>校验枚举值属于当前格式已知范围。</summary>
    private static void ValidateEnum<TEnum>(TEnum value, string description)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidDataException($"ModbusCatalog: invalid {description} '{value}'。");
    }

    /// <summary>从文件头读取受上限约束的数量。</summary>
    private static int ReadBoundedCount(ReadOnlySpan<byte> bytes, int maximum, string description)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        ValidateCount(count, maximum, description);
        return count;
    }

    /// <summary>校验集合数量。</summary>
    private static void ValidateCount(int count, int maximum, string description)
    {
        if (count is < 0 || count > maximum)
            throw new InvalidDataException($"ModbusCatalog: invalid {description} count {count}.");
    }

    /// <summary>读取必填 UTF-8 字符串。</summary>
    private static string ReadRequiredString(
        Stream source,
        Crc32 crc,
        int maximumBytes,
        string description)
    {
        string? value = ReadString(source, crc, maximumBytes, description, optional: false);
        return value!;
    }

    /// <summary>读取可空 UTF-8 字符串。</summary>
    private static string? ReadOptionalString(
        Stream source,
        Crc32 crc,
        int maximumBytes,
        string description)
        => ReadString(source, crc, maximumBytes, description, optional: true);

    /// <summary>读取带 Int32 长度前缀的 UTF-8 字符串。</summary>
    private static string? ReadString(
        Stream source,
        Crc32 crc,
        int maximumBytes,
        string description,
        bool optional)
    {
        int length = ReadInt32(source, crc, description + " length");
        if (optional && length == -1)
            return null;
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException($"ModbusCatalog: invalid {description} length {length}.");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> content = buffer.AsSpan(0, length);
            ReadExact(source, content, description);
            crc.Append(content);
            try
            {
                return Utf8.GetString(content);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"ModbusCatalog: {description} is not valid UTF-8.", exception);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>写出必填 UTF-8 字符串。</summary>
    private static void WriteRequiredString(
        Stream destination,
        Crc32 crc,
        string value,
        int maximumBytes,
        string description)
    {
        ValidateRequiredText(value, description);
        WriteString(destination, crc, value, maximumBytes, description);
    }

    /// <summary>写出可空 UTF-8 字符串。</summary>
    private static void WriteOptionalString(
        Stream destination,
        Crc32 crc,
        string? value,
        int maximumBytes,
        string description)
    {
        if (value is null)
        {
            WriteInt32(destination, crc, -1);
            return;
        }
        ValidateRequiredText(value, description);
        WriteString(destination, crc, value, maximumBytes, description);
    }

    /// <summary>写出带 Int32 长度前缀的 UTF-8 字符串。</summary>
    private static void WriteString(
        Stream destination,
        Crc32 crc,
        string value,
        int maximumBytes,
        string description)
    {
        int length = Utf8.GetByteCount(value);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException($"ModbusCatalog: {description} 长度超过 {maximumBytes} 字节。");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Span<byte> content = buffer.AsSpan(0, length);
            int written = Utf8.GetBytes(value, content);
            if (written != length)
                throw new InvalidDataException("ModbusCatalog: UTF-8 encoded length mismatch.");
            WriteInt32(destination, crc, length);
            crc.Append(content);
            destination.Write(content);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>读取一个原始字节并纳入 CRC。</summary>
    private static byte ReadByte(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        return buffer[0];
    }

    /// <summary>写出一个原始字节并纳入 CRC。</summary>
    private static void WriteByte(Stream destination, Crc32 crc, byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        buffer[0] = value;
        crc.Append(buffer);
        destination.Write(buffer);
    }

    /// <summary>读取严格的 0/1 布尔值。</summary>
    private static bool ReadBoolean(Stream source, Crc32 crc, string description)
    {
        byte value = ReadByte(source, crc, description);
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"ModbusCatalog: invalid {description} boolean value {value}."),
        };
    }

    /// <summary>写出 0/1 布尔值。</summary>
    private static void WriteBoolean(Stream destination, Crc32 crc, bool value)
        => WriteByte(destination, crc, value ? (byte)1 : (byte)0);

    /// <summary>读取 little-endian Int32。</summary>
    private static int ReadInt32(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    /// <summary>写出 little-endian Int32。</summary>
    private static void WriteInt32(Stream destination, Crc32 crc, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        crc.Append(buffer);
        destination.Write(buffer);
    }

    /// <summary>读取 little-endian UInt16。</summary>
    private static ushort ReadUInt16(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }

    /// <summary>写出 little-endian UInt16。</summary>
    private static void WriteUInt16(Stream destination, Crc32 crc, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        crc.Append(buffer);
        destination.Write(buffer);
    }

    /// <summary>读取 little-endian Int64。</summary>
    private static long ReadInt64(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    /// <summary>写出 little-endian Int64。</summary>
    private static void WriteInt64(Stream destination, Crc32 crc, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        crc.Append(buffer);
        destination.Write(buffer);
    }

    /// <summary>读取 decimal 的四个 little-endian 位段。</summary>
    private static decimal ReadDecimal(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[16];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        int low = BinaryPrimitives.ReadInt32LittleEndian(buffer[..4]);
        int middle = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(4, 4));
        int high = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(8, 4));
        int flags = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(12, 4));
        int scale = (flags >> 16) & 0xFF;
        if ((flags & 0x7F00FFFF) != 0 || scale > 28)
            throw new InvalidDataException($"ModbusCatalog: invalid decimal flags for {description}.");
        return new decimal(low, middle, high, flags < 0, (byte)scale);
    }

    /// <summary>写出 decimal 的四个 little-endian 位段。</summary>
    private static void WriteDecimal(Stream destination, Crc32 crc, decimal value)
    {
        Span<int> bits = stackalloc int[4];
        decimal.GetBits(value, bits);
        Span<byte> buffer = stackalloc byte[16];
        for (var i = 0; i < bits.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(i * 4, 4), bits[i]);
        crc.Append(buffer);
        destination.Write(buffer);
    }

    /// <summary>读取地址表示枚举。</summary>
    private static ModbusAddressingMode ReadAddressingMode(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusAddressingMode)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取字节序枚举。</summary>
    private static ModbusByteOrder ReadByteOrder(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusByteOrder)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取字序枚举。</summary>
    private static ModbusWordOrder ReadWordOrder(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusWordOrder)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取寄存器区枚举。</summary>
    private static ModbusRegisterArea ReadArea(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusRegisterArea)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取值类型枚举。</summary>
    private static ModbusValueType ReadValueType(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusValueType)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取访问模式枚举。</summary>
    private static ModbusAccessMode ReadAccess(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusAccessMode)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取绑定方向枚举。</summary>
    private static ModbusMappingDirection ReadDirection(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusMappingDirection)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取表模式枚举。</summary>
    private static ModbusTableMode ReadTableMode(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusTableMode)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取错误策略枚举。</summary>
    private static ModbusErrorPolicy ReadErrorPolicy(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusErrorPolicy)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取 endpoint 写入策略枚举。</summary>
    private static ModbusEndpointWritePolicy ReadWritePolicy(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusEndpointWritePolicy)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>读取获批写动作枚举。</summary>
    private static ModbusApprovedWriteAction ReadApprovedWriteAction(Stream source, Crc32 crc, string description)
    {
        var value = (ModbusApprovedWriteAction)ReadByte(source, crc, description);
        ValidateEnum(value, description);
        return value;
    }

    /// <summary>完整读取固定长度内容，遇到 EOF 时报告具体截断位置。</summary>
    private static void ReadExact(Stream source, Span<byte> destination, string description)
    {
        var read = 0;
        while (read < destination.Length)
        {
            int current = source.Read(destination[read..]);
            if (current == 0)
                throw new InvalidDataException($"ModbusCatalog: {description} is truncated.");
            read += current;
        }
    }

    /// <summary>尽力删除失败写入留下的临时文件。</summary>
    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // 临时文件清理是尽力而为，不能覆盖目录写入的原始异常。
        }
        catch (UnauthorizedAccessException)
        {
            // 同上；下次保存会以 FileMode.Create 覆盖该临时文件。
        }
    }
}
