using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Modbus;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 Modbus Phase A DDL、catalog 元数据和本地只读执行合同。
/// </summary>
public sealed class ModbusSqlExecutorTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// 为每个测试创建独立数据库目录。
    /// </summary>
    public ModbusSqlExecutorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-modbus-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// 清理测试数据库目录。
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Windows 上延迟释放的文件句柄不应遮蔽测试断言。
        }
        catch (UnauthorizedAccessException)
        {
            // 测试进程结束后临时目录仍可由系统清理。
        }
    }

    /// <summary>
    /// 验证 source 表会保存声明地址、规范化 PDU 地址和继承后的有效顺序。
    /// </summary>
    [Fact]
    public void Execute_CreateSourceTable_ResolvesAddressAndOrderDefaults()
    {
        using var database = OpenDatabase();
        CreateSource(database, "line_source", host: "192.0.2.10", byteOrder: "LITTLE_ENDIAN", wordOrder: "BIG_ENDIAN");

        _ = SqlExecutor.Execute(database, """
            CREATE TABLE source_values (
                id INT NOT NULL,
                temperature FLOAT
                    FROM MODBUS INPUT_REGISTER(300001)
                    AS INT16 SCALE 0.1,
                flow FLOAT
                    FROM MODBUS HOLDING_REGISTER(40010, 2)
                    AS FLOAT32 WORD_ORDER LITTLE_ENDIAN,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE line_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """);

        ModbusTableBinding binding = Assert.IsType<ModbusTableBinding>(
            database.Modbus.Catalog.TryGetBinding("source_values"));
        Assert.Equal(ModbusMappingDirection.SourceToTable, binding.Direction);
        Assert.True(binding.Enabled);
        Assert.Equal(2, binding.Columns.Count);

        ModbusColumnMapping temperature = binding.Columns[0];
        Assert.Equal(300001, temperature.DeclaredAddress);
        Assert.Equal((ushort)0, temperature.PduAddress);
        Assert.Equal(ModbusByteOrder.LittleEndian, temperature.ByteOrder);
        Assert.Equal(ModbusWordOrder.BigEndian, temperature.WordOrder);

        ModbusColumnMapping flow = binding.Columns[1];
        Assert.Equal(40010, flow.DeclaredAddress);
        Assert.Equal((ushort)9, flow.PduAddress);
        Assert.Equal(ModbusByteOrder.LittleEndian, flow.ByteOrder);
        Assert.Equal(ModbusWordOrder.LittleEndian, flow.WordOrder);
    }

    /// <summary>
    /// 验证 endpoint 表固定行、审批动作以及四个独立地址空间可共享 PDU 偏移。
    /// </summary>
    [Fact]
    public void Execute_CreateEndpointTable_PersistsFixedRowAndIndependentAreas()
    {
        using var database = OpenDatabase();
        CreateEndpoint(database, "line_endpoint");

        _ = SqlExecutor.Execute(database, """
            CREATE TABLE endpoint_values (
                id INT NOT NULL,
                running BOOL
                    EXPOSE AS MODBUS COIL(1)
                    AS BIT ACCESS READ_WRITE,
                speed INT
                    EXPOSE AS MODBUS HOLDING_REGISTER(40001)
                    AS UINT16 ACCESS READ_WRITE,
                PRIMARY KEY (id)
            )
            USING MODBUS ENDPOINT line_endpoint
            WITH (ROW KEY 1, ON_EXTERNAL_WRITE UPDATE_TABLE)
            """);

        ModbusTableBinding binding = Assert.IsType<ModbusTableBinding>(
            database.Modbus.Catalog.TryGetBinding("endpoint_values"));
        Assert.Equal(ModbusMappingDirection.TableToEndpoint, binding.Direction);
        Assert.Equal(1L, binding.RowKey);
        Assert.Equal(ModbusApprovedWriteAction.UpdateTable, binding.ApprovedWriteAction);
        Assert.All(binding.Columns, mapping => Assert.Equal((ushort)0, mapping.PduAddress));
        Assert.Equal(
            [ModbusRegisterArea.Coil, ModbusRegisterArea.HoldingRegister],
            binding.Columns.Select(static mapping => mapping.Area));
    }

    /// <summary>
    /// 验证 SHOW、DESCRIBE 与 EXPLAIN 返回稳定的本地 catalog 元数据。
    /// </summary>
    [Fact]
    public void Execute_ShowDescribeAndExplain_ReturnStableLocalMetadata()
    {
        using var database = OpenDatabase();
        CreateSource(database, "metadata_source");
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE metadata_values (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE metadata_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """);

        var sources = ExecuteSelect(database, "SHOW MODBUS SOURCES");
        Assert.Equal(
            ["name", "transport", "endpoint", "unit_id", "addressing", "byte_order", "word_order"],
            sources.Columns.Take(7));
        IReadOnlyList<object?> sourceRow = Assert.Single(sources.Rows);
        Assert.Equal("metadata_source", sourceRow[0]);
        Assert.Equal("TCP", sourceRow[1]);
        Assert.Equal("192.0.2.10:502", sourceRow[2]);
        Assert.False(Assert.IsType<bool>(sourceRow[10]));
        Assert.Equal("disabled", sourceRow[11]);

        var describedSource = ExecuteSelect(database, "DESCRIBE MODBUS SOURCE metadata_source");
        Assert.Single(describedSource.Rows);
        Assert.Equal(sourceRow.Take(10), describedSource.Rows[0].Take(10));

        var describedTable = ExecuteSelect(database, "DESCRIBE MODBUS TABLE metadata_values");
        Assert.Equal(
            ["column_name", "direction", "area", "declared_address", "pdu_address", "register_count"],
            describedTable.Columns.Take(6));
        IReadOnlyList<object?> mappingRow = Assert.Single(describedTable.Rows);
        Assert.Equal("value", mappingRow[0]);
        Assert.Equal("FROM", mappingRow[1]);
        Assert.Equal("HOLDING_REGISTER", mappingRow[2]);
        Assert.Equal(40001L, mappingRow[3]);
        Assert.Equal(0L, mappingRow[4]);

        var explain = ExecuteSelect(database, "EXPLAIN DESCRIBE MODBUS TABLE metadata_values");
        var plan = explain.Rows.ToDictionary(
            static row => Assert.IsType<string>(row[0]),
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Equal("describe_modbus_table", plan["statement_type"]);
        Assert.Equal("catalog", plan["access_path"]);
        Assert.Equal(1L, plan["estimated_scanned_rows"]);
        Assert.Equal(0, plan["estimated_segment_count"]);
    }

    /// <summary>验证 source runtime 状态只影响瞬时元数据，不推进 catalog 修订号。</summary>
    [Fact]
    public void Execute_ShowSources_WithRuntimeStatus_ReturnsLiveStateWithoutCatalogMutation()
    {
        using var database = OpenDatabase();
        CreateSource(database, "runtime_source", enabled: true);
        long revision = database.Modbus.Revision;
        var succeededAt = new DateTimeOffset(2026, 8, 9, 1, 2, 3, TimeSpan.Zero);

        database.Modbus.ReportSourceRuntimeStatus(
            "runtime_source",
            new ModbusSourceRuntimeStatus(
                RuntimeEnabled: true,
                ModbusSourceRuntimeHealth.Healthy,
                succeededAt));

        var sources = ExecuteSelect(database, "SHOW MODBUS SOURCES");
        IReadOnlyList<object?> row = Assert.Single(sources.Rows);
        Assert.True(Assert.IsType<bool>(row[10]));
        Assert.Equal("healthy", row[11]);
        Assert.Equal(succeededAt.ToString("O"), row[12]);
        Assert.Null(row[13]);
        Assert.Equal(revision, database.Modbus.Revision);
    }

    /// <summary>
    /// 验证并发修改目录时，SHOW 与 DESCRIBE 的行内容和 revision 始终来自同一份目录快照。
    /// </summary>
    [Fact]
    public async Task MetadataQueries_ConcurrentCatalogMutation_UseOneCapturedState()
    {
        var catalog = new ModbusCatalog();
        catalog.AddSource(new ModbusSourceDefinition("anchor", "127.0.0.1"));
        var changing = new ModbusSourceDefinition("changing", "127.0.0.2");
        using var start = new ManualResetEventSlim(initialState: false);

        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < 5_000; iteration++)
            {
                catalog.AddSource(changing);
                Thread.Yield();
                Assert.True(catalog.RemoveSource(changing.Name));
                Thread.Yield();
            }
        });

        Task reader = Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < 5_000; iteration++)
            {
                SelectExecutionResult shown = ModbusSqlExecutor.ShowSources(catalog);
                int revisionColumn = shown.Columns.Count - 1;
                Assert.Equal("catalog_revision", shown.Columns[revisionColumn]);
                long revision = Assert.IsType<long>(shown.Rows[0][revisionColumn]);
                Assert.All(shown.Rows, row => Assert.Equal(revision, Assert.IsType<long>(row[revisionColumn])));
                Assert.Equal(revision % 2 == 0 ? 2 : 1, shown.Rows.Count);

                try
                {
                    SelectExecutionResult described = ModbusSqlExecutor.DescribeSource(catalog, changing.Name);
                    IReadOnlyList<object?> row = Assert.Single(described.Rows);
                    long describedRevision = Assert.IsType<long>(row[revisionColumn]);
                    Assert.Equal(0, describedRevision % 2);
                }
                catch (InvalidOperationException ex)
                {
                    Assert.Contains("不存在", ex.Message, StringComparison.Ordinal);
                }
            }
        });

        start.Set();
        await Task.WhenAll(writer, reader);
    }

    /// <summary>
    /// 验证关闭并重开数据库后仍能读取 source、endpoint 和表映射。
    /// </summary>
    [Fact]
    public void Reopen_WithModbusCatalog_RestoresDefinitionsAndBinding()
    {
        using (var database = OpenDatabase())
        {
            CreateSource(database, "persisted_source");
            CreateEndpoint(database, "persisted_endpoint");
            _ = SqlExecutor.Execute(database, """
                CREATE TABLE persisted_values (
                    id INT NOT NULL,
                    value INT FROM MODBUS HOLDING_REGISTER(400001) AS UINT16,
                    PRIMARY KEY (id)
                )
                USING MODBUS SOURCE persisted_source
                WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
                """);
        }

        using var reopened = OpenDatabase();
        Assert.NotNull(reopened.Modbus.Catalog.TryGetSource("persisted_source"));
        Assert.NotNull(reopened.Modbus.Catalog.TryGetEndpoint("persisted_endpoint"));
        ModbusTableBinding binding = Assert.IsType<ModbusTableBinding>(
            reopened.Modbus.Catalog.TryGetBinding("persisted_values"));
        Assert.Equal((ushort)0, Assert.Single(binding.Columns).PduAddress);
        Assert.Single(ExecuteSelect(reopened, "SHOW MODBUS SOURCES").Rows);
        Assert.Single(ExecuteSelect(reopened, "SHOW MODBUS ENDPOINTS").Rows);
        Assert.Single(ExecuteSelect(reopened, "DESCRIBE MODBUS TABLE persisted_values").Rows);
    }

    /// <summary>
    /// 验证缺失 target 与重复对象名会失败且不会留下半创建的关系表。
    /// </summary>
    [Fact]
    public void Execute_MissingTargetOrDuplicateName_RejectsWithoutPartialTable()
    {
        using var database = OpenDatabase();
        var missingTarget = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, """
            CREATE TABLE orphan_values (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE missing_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """));
        Assert.Contains("不存在", missingTarget.Message, StringComparison.Ordinal);
        Assert.Null(database.Tables.Catalog.TryGet("orphan_values"));

        CreateSource(database, "duplicate_source");
        var duplicate = Assert.Throws<InvalidOperationException>(() => CreateSource(database, "duplicate_source"));
        Assert.Contains("已存在", duplicate.Message, StringComparison.Ordinal);
        Assert.Single(database.Modbus.Catalog.ListSources());
    }

    /// <summary>
    /// 验证映射表只落下关系 schema 的崩溃中间态不会被 IF NOT EXISTS 静默当作成功。
    /// </summary>
    [Fact]
    public void Execute_IfNotExistsWithMissingBinding_RejectsIncompleteTable()
    {
        using var database = OpenDatabase();
        CreateSource(database, "recovery_source");
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE recovery_values (
                id INT NOT NULL,
                value INT,
                PRIMARY KEY (id)
            )
            """);

        var incomplete = Assert.Throws<InvalidDataException>(() => SqlExecutor.Execute(database, """
            CREATE TABLE IF NOT EXISTS recovery_values (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE recovery_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """));

        Assert.Contains("缺少 MODBUS 绑定", incomplete.Message, StringComparison.Ordinal);
        Assert.Null(database.Modbus.Catalog.TryGetBinding("recovery_values"));
    }

    /// <summary>
    /// 验证 rowstore 生命周期租约冲突时不会发布关系表 schema 或 Modbus 绑定，重启后可同名重试。
    /// </summary>
    [Fact]
    public void Execute_RowStoreOpenFailure_DoesNotPublishMappedTable()
    {
        const string tableName = "open_failure_values";
        const string sourceName = "open_failure_source";
        using (var database = OpenDatabase())
        {
            CreateSource(database, sourceName);
            string rowStoreRoot = Path.Combine(
                _root,
                "tables",
                "rowstore",
                EncodeTableName(tableName));
            using (KvKeyspace blocker = KvKeyspace.Open("test-blocker", rowStoreRoot, KvOptions.Default))
            {
                IOException error = Assert.Throws<IOException>(() =>
                    SqlExecutor.Execute(database, MappedTableSql(tableName, sourceName)));
                Assert.Contains("already owned", error.Message, StringComparison.Ordinal);
            }

            Assert.Null(database.Tables.Catalog.TryGet(tableName));
            Assert.Null(database.Modbus.Catalog.TryGetBinding(tableName));
        }

        using var reopened = OpenDatabase();
        Assert.NotNull(reopened.Modbus.Catalog.TryGetSource(sourceName));
        Assert.Null(reopened.Tables.Catalog.TryGet(tableName));
        Assert.Null(reopened.Modbus.Catalog.TryGetBinding(tableName));

        _ = SqlExecutor.Execute(reopened, MappedTableSql(tableName, sourceName));
        Assert.NotNull(reopened.Tables.Catalog.TryGet(tableName));
        Assert.NotNull(reopened.Modbus.Catalog.TryGetBinding(tableName));
    }

    /// <summary>
    /// 验证表 catalog 持久化失败会释放本次打开的 rowstore，并在重启后允许同名映射表重试。
    /// </summary>
    [Fact]
    public void Execute_TableCatalogPersistFailure_RollsBackOpenedStore()
    {
        const string tableName = "persist_failure_values";
        const string sourceName = "persist_failure_source";
        using (var database = OpenDatabase())
        {
            CreateSource(database, sourceName);
            string catalogTempPath = Path.Combine(
                _root,
                "tables",
                TableSchemaCodec.FileName + ".tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(catalogTempPath)!);
            using (var blocker = new FileStream(
                       catalogTempPath,
                       FileMode.OpenOrCreate,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                _ = Assert.Throws<IOException>(() =>
                    SqlExecutor.Execute(database, MappedTableSql(tableName, sourceName)));
            }

            Assert.Null(database.Tables.Catalog.TryGet(tableName));
            Assert.Null(database.Modbus.Catalog.TryGetBinding(tableName));
        }

        using var reopened = OpenDatabase();
        Assert.NotNull(reopened.Modbus.Catalog.TryGetSource(sourceName));
        Assert.Null(reopened.Tables.Catalog.TryGet(tableName));
        Assert.Null(reopened.Modbus.Catalog.TryGetBinding(tableName));

        _ = SqlExecutor.Execute(reopened, MappedTableSql(tableName, sourceName));
        Assert.NotNull(reopened.Tables.Catalog.TryGet(tableName));
        Assert.NotNull(reopened.Modbus.Catalog.TryGetBinding(tableName));
    }

    /// <summary>
    /// 验证关系表候选 schema 已落盘但尚未发布时，无锁 catalog 读取仍看不到半成品映射表。
    /// </summary>
    [Fact]
    public void Execute_TableCandidatePersistedBeforePublish_KeepsMappedTableInvisible()
    {
        const string tableName = "publish_order_values";
        const string sourceName = "publish_order_source";
        using var database = OpenDatabase();
        CreateSource(database, sourceName);
        var hookInvoked = false;
        database.Tables.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            hookInvoked = true;
            Assert.Null(database.Tables.Catalog.TryGet(tableName));
            Assert.Null(database.Modbus.Catalog.TryGetBinding(tableName));
        };

        _ = SqlExecutor.Execute(database, MappedTableSql(tableName, sourceName));

        Assert.True(hookInvoked);
        Assert.NotNull(database.Tables.Catalog.TryGet(tableName));
        Assert.NotNull(database.Modbus.Catalog.TryGetBinding(tableName));
    }

    /// <summary>
    /// 验证两个公开 catalog 是独立无锁快照，绑定发布前可短暂观察到已提交的关系表。
    /// </summary>
    [Fact]
    public void Execute_BindingCandidatePersistedBeforePublish_ExposesDocumentedCatalogWindow()
    {
        const string tableName = "catalog_window_values";
        const string sourceName = "catalog_window_source";
        using var database = OpenDatabase();
        CreateSource(database, sourceName);
        var hookInvoked = false;
        database.Modbus.AfterCatalogPersistedBeforePublishTestHook = () =>
        {
            hookInvoked = true;
            Assert.NotNull(database.Tables.Catalog.TryGet(tableName));
            Assert.Null(database.Modbus.Catalog.TryGetBinding(tableName));
        };

        _ = SqlExecutor.Execute(database, MappedTableSql(tableName, sourceName));

        Assert.True(hookInvoked);
        Assert.NotNull(database.Tables.Catalog.TryGet(tableName));
        Assert.NotNull(database.Modbus.Catalog.TryGetBinding(tableName));
    }

    /// <summary>
    /// 验证关系表候选文件落盘后的发布异常会恢复旧 schema，关闭重开后仍可安全重试。
    /// </summary>
    [Fact]
    public void Execute_TablePublishFailure_RollsBackSchemaFileBeforeReopen()
    {
        const string tableName = "failed_publish_values";
        const string sourceName = "failed_publish_source";
        using (var database = OpenDatabase())
        {
            CreateSource(database, sourceName);
            database.Tables.AfterCatalogPersistedBeforePublishTestHook = static () =>
                throw new InvalidOperationException("injected table publish failure");

            var error = Assert.Throws<InvalidOperationException>(() =>
                SqlExecutor.Execute(database, MappedTableSql(tableName, sourceName)));
            Assert.Contains("injected table publish failure", error.Message, StringComparison.Ordinal);
            Assert.Null(database.Tables.Catalog.TryGet(tableName));
            Assert.Null(database.Modbus.Catalog.TryGetBinding(tableName));
        }

        using var reopened = OpenDatabase();
        Assert.Null(reopened.Tables.Catalog.TryGet(tableName));
        Assert.Null(reopened.Modbus.Catalog.TryGetBinding(tableName));
        _ = SqlExecutor.Execute(reopened, MappedTableSql(tableName, sourceName));
        Assert.NotNull(reopened.Tables.Catalog.TryGet(tableName));
        Assert.NotNull(reopened.Modbus.Catalog.TryGetBinding(tableName));
    }

    /// <summary>
    /// 验证 SQL/wire 类型不兼容和同区域 PDU 跨度冲突会在表发布前失败。
    /// </summary>
    [Fact]
    public void Execute_TypeMismatchOrOverlap_RejectsBeforePublishingTable()
    {
        using var database = OpenDatabase();
        CreateSource(database, "validation_source");

        var typeMismatch = Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(database, """
            CREATE TABLE invalid_type (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS INT16 SCALE 0.1,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE validation_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """));
        Assert.Contains("Float64", typeMismatch.Message, StringComparison.Ordinal);
        Assert.Null(database.Tables.Catalog.TryGet("invalid_type"));

        var overlap = Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(database, """
            CREATE TABLE invalid_overlap (
                id INT NOT NULL,
                first FLOAT FROM MODBUS HOLDING_REGISTER(40001, 2) AS FLOAT32,
                second INT FROM MODBUS HOLDING_REGISTER(40002) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE validation_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """));
        Assert.Contains("重叠", overlap.Message, StringComparison.Ordinal);
        Assert.Null(database.Tables.Catalog.TryGet("invalid_overlap"));
    }

    /// <summary>
    /// 验证普通 SELECT 只读取映射表本地行，不连接已配置的外部 source。
    /// </summary>
    [Fact]
    public void Execute_SelectMappedTable_ReadsLocalRowsWithoutProtocolIo()
    {
        using var database = OpenDatabase();
        CreateSource(database, "offline_source", host: "203.0.113.1", enabled: true);
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE local_shadow (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE offline_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """);

        var selected = ExecuteSelect(database, "SELECT id, value FROM local_shadow WHERE id = 1");
        Assert.Empty(selected.Rows);
        Assert.False(Assert.IsType<bool>(ExecuteSelect(database, "SHOW MODBUS SOURCES").Rows[0][10]));
    }

    /// <summary>
    /// 验证普通 INSERT（包括省略映射列）和 UPDATE 都不能伪造 source 映射值。
    /// </summary>
    [Fact]
    public void Execute_DirectDmlAgainstSourceMappedState_RejectsLocalWriteBypass()
    {
        using var database = OpenDatabase();
        CreateSource(database, "guarded_source");
        _ = SqlExecutor.Execute(database, """
            CREATE TABLE guarded_values (
                id INT NOT NULL,
                note STRING NULL,
                value INT NULL FROM MODBUS HOLDING_REGISTER(40001) AS UINT16 ACCESS READ_WRITE,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE guarded_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """);

        string[] deniedSql =
        [
            "INSERT INTO guarded_values (id, value) VALUES (1, 42)",
            "INSERT INTO guarded_values (id, note) VALUES (1, 'local-only')",
            "UPDATE guarded_values SET value = 42 WHERE id = 1",
            "IMPORT JSON 'does-not-exist.json' INTO guarded_values",
        ];
        foreach (string sql in deniedSql)
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                SqlExecutor.Execute(database, sql));
            Assert.Contains("WRITE MODBUS", error.Message, StringComparison.Ordinal);
        }

        Assert.Empty(database.Tables.Open("guarded_values").Scan());
    }

    /// <summary>验证嵌入式执行器不会绕过 Server 的令牌、审计和网络边界。</summary>
    [Fact]
    public void Execute_ModbusRuntimeStatementsEmbedded_RejectsWithoutSideEffects()
    {
        using var database = OpenDatabase();

        Assert.Throws<NotSupportedException>(() =>
            SqlExecutor.Execute(database, "WRITE MODBUS controls SET value = 1 DRY RUN"));
        Assert.Throws<NotSupportedException>(() =>
            SqlExecutor.Execute(database, "SHOW MODBUS WRITE AUDIT"));
    }

    /// <summary>
    /// 验证缺少当前数据库的 Admin 权限时拒绝全部 Modbus DDL，但仍允许只读 metadata 查询。
    /// </summary>
    [Fact]
    public void Execute_CanAdministerFalse_RejectsDdlAndAllowsShow()
    {
        using var database = OpenDatabase();
        CreateSource(database, "existing_source");
        var noAdministration = new SqlExecutionOptions
        {
            Caller = "writer",
            CanWrite = true,
            CanAdminister = false,
        };

        string[] deniedStatements =
        [
            SourceSql("denied_source"),
            EndpointSql("denied_endpoint"),
            """
            CREATE TABLE denied_values (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE existing_source
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """,
        ];

        foreach (string sql in deniedStatements)
        {
            var denied = Assert.Throws<InvalidOperationException>(() =>
                ExecuteWithOptions(database, sql, noAdministration));
            Assert.Contains("当前数据库的 Admin 权限", denied.Message, StringComparison.Ordinal);
        }

        var readOnly = noAdministration with { CanWrite = false, Caller = "reader" };
        var sources = Assert.IsType<SelectExecutionResult>(
            ExecuteWithOptions(database, "SHOW MODBUS SOURCES", readOnly));
        Assert.Equal("existing_source", Assert.Single(sources.Rows)[0]);
    }

    /// <summary>
    /// 打开当前测试目录中的数据库。
    /// </summary>
    private Tsdb OpenDatabase() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    /// <summary>
    /// 创建具有可控默认顺序和不可达测试地址的 source。
    /// </summary>
    private static void CreateSource(
        Tsdb database,
        string name,
        string host = "192.0.2.10",
        string byteOrder = "BIG_ENDIAN",
        string wordOrder = "BIG_ENDIAN",
        bool enabled = false)
        => _ = SqlExecutor.Execute(database, SourceSql(name, host, byteOrder, wordOrder, enabled));

    /// <summary>
    /// 构造完整且不会触发网络访问的 source DDL。
    /// </summary>
    private static string SourceSql(
        string name,
        string host = "192.0.2.10",
        string byteOrder = "BIG_ENDIAN",
        string wordOrder = "BIG_ENDIAN",
        bool enabled = false)
        => $"""
            CREATE MODBUS SOURCE {name}
            WITH (
                TRANSPORT TCP,
                ENDPOINT '{host}:502',
                UNIT_ID 1,
                POLL_INTERVAL '1s',
                TIMEOUT '500ms',
                RETRY 1,
                ADDRESSING MODICON,
                BYTE_ORDER {byteOrder},
                WORD_ORDER {wordOrder},
                ENABLED {(enabled ? "TRUE" : "FALSE")}
            )
            """;

    /// <summary>构造指向指定 source 的最小 Modbus 映射表 DDL。</summary>
    private static string MappedTableSql(string tableName, string sourceName)
        => $"""
            CREATE TABLE {tableName} (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE {sourceName}
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """;

    /// <summary>按关系表 rowstore 的 UTF-8 小写十六进制规则编码表名。</summary>
    private static string EncodeTableName(string tableName)
        => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(tableName)).ToLowerInvariant();

    /// <summary>
    /// 创建仅监听回环地址并带 allowlist 的 endpoint。
    /// </summary>
    private static void CreateEndpoint(Tsdb database, string name)
        => _ = SqlExecutor.Execute(database, EndpointSql(name));

    /// <summary>
    /// 构造安全的 endpoint DDL。
    /// </summary>
    private static string EndpointSql(string name)
        => $"""
            CREATE MODBUS ENDPOINT {name}
            WITH (
                TRANSPORT TCP,
                BIND '127.0.0.1:1502',
                UNIT_ID 1,
                ADDRESSING MODICON,
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER LITTLE_ENDIAN,
                ALLOWLIST ('127.0.0.1'),
                MAX_CONNECTIONS 8,
                WRITE_POLICY STAGED
            )
            """;

    /// <summary>
    /// 执行 SQL 并断言返回关系结果集。
    /// </summary>
    private static SelectExecutionResult ExecuteSelect(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));

    /// <summary>
    /// 使用显式治理选项执行 SQL。
    /// </summary>
    private static object? ExecuteWithOptions(
        Tsdb database,
        string sql,
        SqlExecutionOptions options)
        => SqlExecutor.Execute(
            database,
            databaseName: "test",
            sql,
            parameters: null,
            controlPlane: null,
            options);
}
