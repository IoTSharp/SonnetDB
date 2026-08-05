using System.Buffers.Binary;
using System.IO.Hashing;
using SonnetDB.Backup;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Modbus;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Modbus;

/// <summary>
/// 验证 Modbus 独立目录的版本、原子持久化、引用边界和备份恢复。
/// </summary>
public sealed class ModbusCatalogTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.ModbusCatalog.Tests.{Guid.NewGuid():N}");

    /// <summary>创建当前测试实例的隔离根目录。</summary>
    public ModbusCatalogTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>
    /// 验证 source、endpoint 和绑定重启后保持有效，逻辑 revision 只随成功 DDL 推进。
    /// </summary>
    [Fact]
    public void Manager_CreateDefinitionsAndBinding_ReopensWithMonotonicRevision()
    {
        string root = Path.Combine(_rootDirectory, "roundtrip");
        var tables = new TableCatalog();
        tables.Add(CreateTableSchema("telemetry"));
        var manager = new ModbusManager(root, tables);

        manager.CreateSource(CreateSource());
        manager.CreateEndpoint(CreateEndpoint());
        manager.CreateBinding(CreateSourceBinding("telemetry"));

        Assert.Equal(3, manager.Revision);
        Assert.Equal(1, manager.Catalog.SourceCount);
        Assert.Equal(1, manager.Catalog.EndpointCount);
        Assert.Equal(1, manager.Catalog.BindingCount);

        var reopened = new ModbusManager(root, tables);
        Assert.Equal(3, reopened.Revision);
        ModbusSourceDefinition source = reopened.Catalog.TryGetSource("line1")!;
        Assert.Equal("plc.local", source.Host);
        Assert.Equal(1_502, source.Port);
        Assert.Equal((byte)17, source.UnitId);
        Assert.Equal(250, source.PollIntervalMilliseconds);
        Assert.Equal(750, source.TimeoutMilliseconds);
        Assert.Equal(5, source.RetryCount);
        Assert.Equal(ModbusByteOrder.LittleEndian, source.ByteOrder);
        Assert.Equal(ModbusWordOrder.LittleEndian, source.WordOrder);
        Assert.False(source.Enabled);

        ModbusEndpointDefinition endpoint = reopened.Catalog.TryGetEndpoint("shadow")!;
        Assert.Equal("127.0.0.1", endpoint.BindAddress);
        Assert.Equal(1_503, endpoint.Port);
        Assert.Equal((byte)9, endpoint.UnitId);
        Assert.Equal(12, endpoint.MaxConnections);
        Assert.Equal(["127.0.0.1/32", "::1/128"], endpoint.AllowedClientNetworks);
        Assert.Equal(ModbusAddressingMode.OneBased, endpoint.AddressingMode);
        Assert.Equal(ModbusByteOrder.LittleEndian, endpoint.ByteOrder);
        Assert.Equal(ModbusWordOrder.LittleEndian, endpoint.WordOrder);
        Assert.Equal(ModbusEndpointWritePolicy.Reject, endpoint.WritePolicy);
        Assert.False(endpoint.Enabled);

        ModbusTableBinding binding = reopened.Catalog.TryGetBinding("telemetry")!;
        Assert.Equal(ModbusMappingDirection.SourceToTable, binding.Direction);
        Assert.Equal("line1", binding.TargetName);
        Assert.Null(binding.RowKey);
        Assert.Equal(ModbusTableMode.Latest, binding.TableMode);
        Assert.Equal(ModbusErrorPolicy.KeepLast, binding.ErrorPolicy);
        ModbusColumnMapping mapping = Assert.Single(binding.Columns);
        Assert.Equal(40_001, mapping.DeclaredAddress);
        Assert.Equal((ushort)0, mapping.PduAddress);
        Assert.Equal(ModbusValueType.UInt16, mapping.ValueType);
        Assert.Equal(1m, mapping.Scale);
        Assert.Equal(0m, mapping.Offset);

        Assert.Throws<InvalidOperationException>(() => reopened.DropSource("line1"));
        Assert.Equal(3, reopened.Revision);
        Assert.True(reopened.DropEndpoint("shadow"));
        Assert.Equal(4, reopened.Revision);
        Assert.False(reopened.DropEndpoint("shadow"));
        Assert.Equal(4, reopened.Revision);
        Assert.True(reopened.DropBinding("telemetry"));
        Assert.Equal(5, reopened.Revision);
        Assert.True(reopened.DropSource("line1"));
        Assert.Equal(6, reopened.Revision);

        var afterDrops = new ModbusManager(root, tables);
        Assert.Equal(6, afterDrops.Revision);
        Assert.Equal(0, afterDrops.Catalog.SourceCount);
        Assert.Equal(0, afterDrops.Catalog.EndpointCount);
        Assert.Equal(0, afterDrops.Catalog.BindingCount);
    }

    /// <summary>
    /// 验证未来格式版本、CRC 损坏以及头部和尾部截断均被明确拒绝。
    /// </summary>
    [Fact]
    public void Codec_FutureVersionCrcAndTruncation_RejectsCorruptFiles()
    {
        string root = Path.Combine(_rootDirectory, "corruption");
        var manager = new ModbusManager(root, new TableCatalog());
        manager.CreateSource(CreateSource());
        byte[] valid = File.ReadAllBytes(manager.CatalogPath);

        string futurePath = Path.Combine(root, "future.sdbmodbus");
        byte[] future = (byte[])valid.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(future.AsSpan(8, 4), 2);
        File.WriteAllBytes(futurePath, future);
        var futureError = Assert.Throws<InvalidDataException>(() => ModbusCatalogCodec.Load(futurePath));
        Assert.Contains("unsupported format version 2", futureError.Message, StringComparison.Ordinal);

        string crcPath = Path.Combine(root, "crc.sdbmodbus");
        byte[] badCrc = (byte[])valid.Clone();
        badCrc[16] ^= 0x40;
        File.WriteAllBytes(crcPath, badCrc);
        var crcError = Assert.Throws<InvalidDataException>(() => ModbusCatalogCodec.Load(crcPath));
        Assert.Contains("CRC32 mismatch", crcError.Message, StringComparison.Ordinal);

        string headerPath = Path.Combine(root, "header-truncated.sdbmodbus");
        File.WriteAllBytes(headerPath, valid[..20]);
        var headerError = Assert.Throws<InvalidDataException>(() => ModbusCatalogCodec.Load(headerPath));
        Assert.Contains("header is truncated", headerError.Message, StringComparison.Ordinal);

        string footerPath = Path.Combine(root, "footer-truncated.sdbmodbus");
        File.WriteAllBytes(footerPath, valid[..^1]);
        var footerError = Assert.Throws<InvalidDataException>(() => ModbusCatalogCodec.Load(footerPath));
        Assert.Contains("footer is truncated", footerError.Message, StringComparison.Ordinal);

        string decimalRoot = Path.Combine(_rootDirectory, "decimal-corruption");
        var decimalTables = new TableCatalog();
        decimalTables.Add(CreateTableSchema("telemetry"));
        var decimalManager = new ModbusManager(decimalRoot, decimalTables);
        decimalManager.CreateSource(CreateSource());
        decimalManager.CreateBinding(CreateSourceBinding("telemetry"));
        byte[] badDecimal = File.ReadAllBytes(decimalManager.CatalogPath);
        int footerOffset = badDecimal.Length - 16;
        int scaleFlagsOffset = footerOffset - 21;
        badDecimal[scaleFlagsOffset + 2] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(
            badDecimal.AsSpan(footerOffset, 4),
            Crc32.HashToUInt32(badDecimal.AsSpan(0, footerOffset)));
        string decimalPath = Path.Combine(decimalRoot, "invalid-decimal.sdbmodbus");
        File.WriteAllBytes(decimalPath, badDecimal);
        var decimalError = Assert.Throws<InvalidDataException>(() => ModbusCatalogCodec.Load(decimalPath));
        Assert.Contains("invalid decimal flags", decimalError.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 验证重复定义、缺失关系表或目标，以及声明地址和 PDU 地址不一致时都不推进 revision。
    /// </summary>
    [Fact]
    public void Manager_InvalidReferencesAndDuplicates_RejectWithoutRevisionChange()
    {
        string root = Path.Combine(_rootDirectory, "references");
        var tables = new TableCatalog();
        tables.Add(CreateTableSchema("telemetry"));
        var manager = new ModbusManager(root, tables);

        var missingTarget = CreateSourceBinding("telemetry", targetName: "missing");
        Assert.Throws<InvalidOperationException>(() => manager.CreateBinding(missingTarget));
        Assert.Equal(0, manager.Revision);

        manager.CreateSource(CreateSource());
        Assert.Equal(1, manager.Revision);
        Assert.Throws<InvalidOperationException>(() => manager.CreateSource(CreateSource()));
        Assert.Equal(1, manager.Revision);

        var missingTable = CreateSourceBinding("missing_table");
        Assert.Throws<InvalidOperationException>(() => manager.CreateBinding(missingTable));
        Assert.Equal(1, manager.Revision);

        ModbusTableBinding mismatched = CreateSourceBinding("telemetry") with
        {
            Columns =
            [
                CreateSourceMapping() with { PduAddress = 1 },
            ],
        };
        Assert.Throws<ArgumentException>(() => manager.CreateBinding(mismatched));
        Assert.Equal(1, manager.Revision);

        manager.CreateBinding(CreateSourceBinding("telemetry"));
        Assert.Equal(2, manager.Revision);
        Assert.Throws<InvalidOperationException>(() => manager.CreateBinding(CreateSourceBinding("telemetry")));
        Assert.Equal(2, manager.Revision);
    }

    /// <summary>
    /// 验证 endpoint 与绑定只枚举一次调用方集合，后续校验、发布和重开均使用同一不可变快照。
    /// </summary>
    [Fact]
    public void Manager_MutableCollectionInputs_SnapshotsBeforeValidationAndPersistence()
    {
        string root = Path.Combine(_rootDirectory, "mutable-inputs");
        var tables = new TableCatalog();
        tables.Add(CreateTableSchema("telemetry"));
        var manager = new ModbusManager(root, tables);

        var changingNetworks = new ChangingReadOnlyList<string>(
            "127.0.0.1/32",
            "not-an-ip-or-cidr");
        manager.CreateEndpoint(CreateEndpoint() with
        {
            AllowedClientNetworks = changingNetworks,
        });
        Assert.Equal(1, changingNetworks.EnumerationCount);
        Assert.Equal(
            ["127.0.0.1/32"],
            manager.Catalog.TryGetEndpoint("shadow")!.AllowedClientNetworks);

        manager.CreateSource(CreateSource());
        var changingMappings = new ChangingReadOnlyList<ModbusColumnMapping>(
            CreateSourceMapping(),
            CreateSourceMapping() with { PduAddress = 1 });
        manager.CreateBinding(CreateSourceBinding("telemetry") with
        {
            Columns = changingMappings,
        });
        Assert.Equal(1, changingMappings.EnumerationCount);
        Assert.Equal(
            (ushort)0,
            Assert.Single(manager.Catalog.TryGetBinding("telemetry")!.Columns).PduAddress);

        var reopened = new ModbusManager(root, tables);
        Assert.Equal(
            ["127.0.0.1/32"],
            reopened.Catalog.TryGetEndpoint("shadow")!.AllowedClientNetworks);
        Assert.Equal(
            (ushort)0,
            Assert.Single(reopened.Catalog.TryGetBinding("telemetry")!.Columns).PduAddress);
    }

    /// <summary>
    /// 验证候选文件已经落盘但尚未发布时，catalog 读者仍只能观察旧修订号和旧定义。
    /// </summary>
    [Fact]
    public void Manager_CandidatePersistedBeforePublish_KeepsOldSnapshotVisible()
    {
        string root = Path.Combine(_rootDirectory, "persist-before-publish");
        var manager = new ModbusManager(root, new TableCatalog());
        var hookInvoked = false;
        manager.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            hookInvoked = true;
            Assert.Equal(0, manager.Revision);
            Assert.Null(manager.Catalog.TryGetSource("line1"));
        };

        manager.CreateSource(CreateSource());

        Assert.True(hookInvoked);
        Assert.Equal(1, manager.Revision);
        Assert.NotNull(manager.Catalog.TryGetSource("line1"));
    }

    /// <summary>
    /// 验证候选文件落盘后的发布异常会恢复旧文件，API 报错后重开不会出现幽灵定义。
    /// </summary>
    [Fact]
    public void Manager_PublishFailure_RollsBackPersistedCandidateBeforeReopen()
    {
        string root = Path.Combine(_rootDirectory, "publish-failure-rollback");
        var tables = new TableCatalog();
        var manager = new ModbusManager(root, tables);
        manager.AfterCatalogPersistedBeforePublishTestHook = static () =>
            throw new InvalidOperationException("injected publish failure");

        var error = Assert.Throws<InvalidOperationException>(() => manager.CreateSource(CreateSource()));
        Assert.Contains("injected publish failure", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, manager.Revision);
        Assert.Null(manager.Catalog.TryGetSource("line1"));

        var reopened = new ModbusManager(root, tables);
        Assert.Equal(0, reopened.Revision);
        Assert.Null(reopened.Catalog.TryGetSource("line1"));
    }

    /// <summary>
    /// 验证临时文件创建失败时保留旧文件，并回退新增定义和逻辑 revision。
    /// </summary>
    [Fact]
    public void Manager_SaveFailure_PreservesOldFileAndRollsBackRevision()
    {
        string root = Path.Combine(_rootDirectory, "save-failure");
        var tables = new TableCatalog();
        var manager = new ModbusManager(root, tables);
        manager.CreateSource(CreateSource());
        byte[] original = File.ReadAllBytes(manager.CatalogPath);

        string blockingTemporaryPath = manager.CatalogPath + ".tmp";
        Directory.CreateDirectory(blockingTemporaryPath);
        Exception? error = Record.Exception(() => manager.CreateEndpoint(CreateEndpoint()));

        Assert.NotNull(error);
        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"预期文件系统异常，实际为 {error.GetType().FullName}: {error.Message}");
        Assert.Equal(1, manager.Revision);
        Assert.Null(manager.Catalog.TryGetEndpoint("shadow"));
        Assert.Equal(original, File.ReadAllBytes(manager.CatalogPath));

        Directory.Delete(blockingTemporaryPath);
        var reopened = new ModbusManager(root, tables);
        Assert.Equal(1, reopened.Revision);
        Assert.NotNull(reopened.Catalog.TryGetSource("line1"));
        Assert.Null(reopened.Catalog.TryGetEndpoint("shadow"));
    }

    /// <summary>
    /// 验证绑定会阻止关系表 schema 变更，但不会误伤同名 document collection。
    /// </summary>
    [Fact]
    public void BoundTable_SchemaMutationRejectsWithoutBlockingSameNameDocument()
    {
        string root = Path.Combine(_rootDirectory, "dependency-guards");
        using var database = OpenDatabase(root);
        database.Modbus.CreateSource(CreateSource());
        database.Tables.Create(CreateTableSchema("shared"));
        database.Modbus.CreateBinding(CreateSourceBinding("shared"));
        database.Documents.Create(DocumentCollectionSchema.Create("shared"));

        var alterError = Assert.Throws<InvalidOperationException>(() => database.Tables.AlterTableAddColumn(
            "shared",
            "extra",
            TableColumnType.String,
            isNullable: true,
            defaultValue: null));
        Assert.Contains("MODBUS 绑定", alterError.Message, StringComparison.Ordinal);

        var dropError = Assert.Throws<InvalidOperationException>(() => database.Tables.Drop("shared"));
        Assert.Contains("MODBUS 绑定", dropError.Message, StringComparison.Ordinal);
        Assert.NotNull(database.Tables.Catalog.TryGet("shared"));

        Assert.True(database.Documents.Drop("shared"));
        Assert.Null(database.Documents.Catalog.TryGet("shared"));
    }

    /// <summary>
    /// 验证受数据库管理的 TableCatalog 拒绝绕过 schema 锁和 Modbus 依赖检查的直接变更。
    /// </summary>
    [Fact]
    public void ManagedTableCatalog_DirectMutationRejectsAndPreservesBinding()
    {
        string root = Path.Combine(_rootDirectory, "managed-table-catalog-guard");
        using var database = OpenDatabase(root);
        database.Modbus.CreateSource(CreateSource());
        database.Tables.Create(CreateTableSchema("telemetry"));
        database.Modbus.CreateBinding(CreateSourceBinding("telemetry"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            database.Tables.Catalog.Remove("telemetry"));
        Assert.Contains("TableManager", error.Message, StringComparison.Ordinal);
        Assert.NotNull(database.Tables.Catalog.TryGet("telemetry"));
        Assert.NotNull(database.Modbus.Catalog.TryGetBinding("telemetry"));
    }

    /// <summary>
    /// 验证备份把 Modbus catalog 作为 schema 保存，恢复后的数据库可重新加载全部定义和绑定。
    /// </summary>
    [Fact]
    public void Backup_CreateRestoreReopen_PreservesModbusCatalog()
    {
        string databaseRoot = Path.Combine(_rootDirectory, "backup-source");
        string backupRoot = Path.Combine(_rootDirectory, "backup-artifact");
        string restoredRoot = Path.Combine(_rootDirectory, "backup-restored");

        using (var database = OpenDatabase(databaseRoot))
        {
            database.Modbus.CreateSource(CreateSource());
            database.Modbus.CreateEndpoint(CreateEndpoint());
            database.Tables.Create(CreateTableSchema("telemetry"));
            database.Modbus.CreateBinding(CreateSourceBinding("telemetry"));

            BackupManifest manifest = new BackupService().Create(database, new BackupCreateOptions
            {
                DestinationDirectory = backupRoot,
            });
            Assert.Contains(manifest.Files, static file =>
                file.Kind == BackupFileKind.Schema
                && string.Equals(file.Path, "modbus/modbus.sdbmodbus", StringComparison.Ordinal));
        }

        new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupRoot,
            TargetDirectory = restoredRoot,
        });

        using var restored = OpenDatabase(restoredRoot);
        Assert.Equal(3, restored.Modbus.Revision);
        Assert.NotNull(restored.Modbus.Catalog.TryGetSource("line1"));
        Assert.NotNull(restored.Modbus.Catalog.TryGetEndpoint("shadow"));
        ModbusTableBinding binding = restored.Modbus.Catalog.TryGetBinding("telemetry")!;
        Assert.Equal("line1", binding.TargetName);
        Assert.Equal((ushort)0, Assert.Single(binding.Columns).PduAddress);
        Assert.NotNull(restored.Tables.Catalog.TryGet("telemetry"));
    }

    /// <summary>删除当前测试实例创建的隔离目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    /// <summary>按测试所需的稳定选项打开数据库。</summary>
    private static Tsdb OpenDatabase(string rootDirectory)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = rootDirectory,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        });

    /// <summary>创建测试 source。</summary>
    private static ModbusSourceDefinition CreateSource()
        => new(
            "line1",
            "plc.local",
            Port: 1_502,
            UnitId: 17,
            AddressingMode: ModbusAddressingMode.Modicon,
            PollIntervalMilliseconds: 250,
            TimeoutMilliseconds: 750,
            RetryCount: 5,
            ByteOrder: ModbusByteOrder.LittleEndian,
            WordOrder: ModbusWordOrder.LittleEndian,
            Enabled: false);

    /// <summary>创建仅监听回环地址的测试 endpoint。</summary>
    private static ModbusEndpointDefinition CreateEndpoint()
        => new(
            "shadow",
            BindAddress: "127.0.0.1",
            Port: 1_503,
            UnitId: 9,
            MaxConnections: 12,
            AllowedClientNetworks: ["127.0.0.1/32", "::1/128"],
            AddressingMode: ModbusAddressingMode.OneBased,
            ByteOrder: ModbusByteOrder.LittleEndian,
            WordOrder: ModbusWordOrder.LittleEndian,
            WritePolicy: ModbusEndpointWritePolicy.Reject,
            Enabled: false);

    /// <summary>创建包含 Int64 主键和数值列的关系表 schema。</summary>
    private static TableSchema CreateTableSchema(string tableName)
        => TableSchema.Create(
            tableName,
            [
                ("id", TableColumnType.Int64, false),
                ("value", TableColumnType.Int64, false),
            ],
            ["id"]);

    /// <summary>创建一项规范化为 PDU 0 的保持寄存器映射。</summary>
    private static ModbusColumnMapping CreateSourceMapping()
        => new(
            "value",
            ModbusRegisterArea.HoldingRegister,
            DeclaredAddress: 40_001,
            PduAddress: 0,
            ModbusValueType.UInt16);

    /// <summary>创建 source 到关系表的只读最新值绑定。</summary>
    private static ModbusTableBinding CreateSourceBinding(
        string tableName,
        string targetName = "line1")
        => new(
            tableName,
            ModbusMappingDirection.SourceToTable,
            targetName,
            [CreateSourceMapping()]);

    /// <summary>
    /// 第一次枚举返回合法值、后续枚举返回另一值，用于证明生产入口不会校验后再次读取调用方集合。
    /// </summary>
    private sealed class ChangingReadOnlyList<T>(T firstValue, T laterValue) : IReadOnlyList<T>
    {
        private int _enumerationCount;

        /// <summary>当前已开始的枚举次数。</summary>
        public int EnumerationCount => Volatile.Read(ref _enumerationCount);

        /// <summary>测试集合固定只包含一个逻辑元素。</summary>
        public int Count => 1;

        /// <summary>按索引读取首次值；生产代码应通过单次快照固化该值。</summary>
        public T this[int index] => index == 0
            ? firstValue
            : throw new ArgumentOutOfRangeException(nameof(index));

        /// <summary>创建一次可观测的枚举，并在第二次起返回替代值。</summary>
        public IEnumerator<T> GetEnumerator()
        {
            int current = Interlocked.Increment(ref _enumerationCount);
            yield return current == 1 ? firstValue : laterValue;
        }

        /// <summary>通过泛型枚举器实现非泛型集合合同。</summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}
