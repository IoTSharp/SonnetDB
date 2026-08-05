using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Modbus;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Modbus;

/// <summary>
/// 验证关系表、Modbus catalog、备份和关闭路径共享的数据库级锁边界。
/// </summary>
public sealed class ModbusConcurrencyTests : IDisposable
{
    private static readonly TimeSpan _eventTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _operationTimeout = TimeSpan.FromSeconds(10);
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.ModbusConcurrency.Tests.{Guid.NewGuid():N}");

    /// <summary>
    /// 验证 DROP 通过依赖检查前会阻塞并发绑定创建，重启后不会留下悬空引用。
    /// </summary>
    [Fact]
    public async Task CreateBinding_ConcurrentTableDrop_SerializesWithoutDanglingCatalog()
    {
        string databaseRoot = Path.Combine(_rootDirectory, "binding-drop-database");
        using (Tsdb database = OpenDatabase(databaseRoot))
        using (var tableLockHeld = new ManualResetEventSlim())
        using (var releaseTableLock = new ManualResetEventSlim())
        using (var dropHasSchemaLock = new ManualResetEventSlim())
        using (var bindingStarted = new ManualResetEventSlim())
        {
            database.Modbus.CreateSource(CreateSource());
            database.Tables.Create(CreateTableSchema("telemetry"));

            Task tableLockHolder = Task.Run(() => database.Tables.ExecuteLocked(() =>
            {
                tableLockHeld.Set();
                if (!releaseTableLock.Wait(_operationTimeout))
                    throw new TimeoutException("测试未能按时释放关系表内部锁。");
                return true;
            }));

            Task<bool>? dropTask = null;
            Task? bindingTask = null;
            try
            {
                Assert.True(tableLockHeld.Wait(_eventTimeout), "未能取得关系表内部锁。");
                database.Tables.SchemaMutationLockAcquiredTestHook = operation =>
                {
                    if (string.Equals(operation, "DROP TABLE", StringComparison.Ordinal))
                        dropHasSchemaLock.Set();
                };

                dropTask = Task.Run(() => database.Tables.Drop("telemetry"));
                Assert.True(dropHasSchemaLock.Wait(_eventTimeout), "DROP 未能取得数据库级 schema 锁。");

                bindingTask = Task.Run(() =>
                {
                    bindingStarted.Set();
                    database.Modbus.CreateBinding(CreateSourceBinding("telemetry"));
                });
                Assert.True(bindingStarted.Wait(_eventTimeout), "绑定创建任务未能按时启动。");
                Task firstCompleted = await Task.WhenAny(
                    bindingTask,
                    Task.Delay(TimeSpan.FromMilliseconds(250)));
                Assert.NotSame(bindingTask, firstCompleted);
            }
            finally
            {
                database.Tables.SchemaMutationLockAcquiredTestHook = null;
                releaseTableLock.Set();
            }

            await tableLockHolder.WaitAsync(_operationTimeout);
            Assert.NotNull(dropTask);
            Assert.True(await dropTask.WaitAsync(_operationTimeout));
            Assert.NotNull(bindingTask);
            var bindingError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await bindingTask.WaitAsync(_operationTimeout));
            Assert.Contains("不存在", bindingError.Message, StringComparison.Ordinal);
        }

        using Tsdb reopened = OpenDatabase(databaseRoot);
        Assert.Null(reopened.Tables.Catalog.TryGet("telemetry"));
        Assert.Null(reopened.Modbus.Catalog.TryGetBinding("telemetry"));
    }

    /// <summary>
    /// 验证真实文件复制期间 mapped CREATE 被阻塞，恢复后不会出现跨 catalog 半次 DDL。
    /// </summary>
    [Fact]
    public async Task Backup_DuringMappedCreate_RestoresSingleSchemaSnapshot()
    {
        string databaseRoot = Path.Combine(_rootDirectory, "backup-create-database");
        string backupRoot = Path.Combine(_rootDirectory, "backup-create-artifact");
        string restoredRoot = Path.Combine(_rootDirectory, "backup-create-restored");
        using var database = OpenDatabase(databaseRoot);
        database.Modbus.CreateSource(CreateSource());
        database.Tables.Create(CreateTableSchema("baseline"));

        var backupService = new BackupService();
        using var modbusCatalogCopied = new ManualResetEventSlim();
        using var releaseFileCopy = new ManualResetEventSlim();
        using var ddlBeforeSchemaLock = new ManualResetEventSlim();
        using var ddlHasSchemaLock = new ManualResetEventSlim();
        backupService.AfterFileCopiedTestHook = relativePath =>
        {
            if (!string.Equals(
                    relativePath,
                    "modbus/modbus.sdbmodbus",
                    StringComparison.Ordinal))
            {
                return;
            }

            modbusCatalogCopied.Set();
            if (!releaseFileCopy.Wait(_operationTimeout))
                throw new TimeoutException("测试未能按时恢复备份文件复制。");
        };

        Task<BackupManifest> backupTask = Task.Run(() => backupService.Create(
            database,
            new BackupCreateOptions { DestinationDirectory = backupRoot }));
        Task<object?>? ddlTask = null;
        try
        {
            Assert.True(modbusCatalogCopied.Wait(_eventTimeout), "备份未复制到 Modbus catalog 同步点。");
            Assert.True(File.Exists(Path.Combine(backupRoot, "modbus", ModbusCatalogCodec.FileName)));
            Assert.False(File.Exists(Path.Combine(backupRoot, "tables", TableSchemaCodec.FileName)));

            database.BeforeSchemaMutationLockTestHook = ddlBeforeSchemaLock.Set;
            database.SchemaMutationLockAcquiredTestHook = ddlHasSchemaLock.Set;
            ddlTask = Task.Run(() => SqlExecutor.Execute(database, MappedTableSql("mapped_values")));

            Assert.True(ddlBeforeSchemaLock.Wait(_eventTimeout), "映射表 DDL 未到达 schema 锁入口。");
            Assert.False(
                ddlHasSchemaLock.Wait(TimeSpan.FromMilliseconds(250)),
                "备份复制期间映射表 DDL 不应取得 schema 锁。");
            Assert.Null(database.Tables.Catalog.TryGet("mapped_values"));
        }
        finally
        {
            database.BeforeSchemaMutationLockTestHook = null;
            database.SchemaMutationLockAcquiredTestHook = null;
            releaseFileCopy.Set();
        }

        BackupManifest manifest = await backupTask.WaitAsync(_operationTimeout);
        Assert.NotNull(ddlTask);
        _ = await ddlTask.WaitAsync(_operationTimeout);
        Assert.Contains(manifest.Files, static file =>
            string.Equals(file.Path, "modbus/modbus.sdbmodbus", StringComparison.Ordinal));
        Assert.NotNull(database.Tables.Catalog.TryGet("mapped_values"));
        Assert.NotNull(database.Modbus.Catalog.TryGetBinding("mapped_values"));

        BackupVerificationResult verification = backupService.Verify(backupRoot);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));
        backupService.Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupRoot,
            TargetDirectory = restoredRoot,
        });

        using Tsdb restored = OpenDatabase(restoredRoot);
        Assert.NotNull(restored.Modbus.Catalog.TryGetSource("line1"));
        Assert.NotNull(restored.Tables.Catalog.TryGet("baseline"));
        Assert.Null(restored.Tables.Catalog.TryGet("mapped_values"));
        Assert.Null(restored.Modbus.Catalog.TryGetBinding("mapped_values"));
    }

    /// <summary>
    /// 验证备份复制 Modbus catalog 期间 Dispose 会等待，释放后备份与关闭都能成功结束。
    /// </summary>
    [Fact]
    public async Task Backup_DuringCatalogCopy_BlocksDisposeThenBothComplete()
    {
        string databaseRoot = Path.Combine(_rootDirectory, "backup-dispose-database");
        string backupRoot = Path.Combine(_rootDirectory, "backup-dispose-artifact");
        Tsdb database = OpenDatabase(databaseRoot);
        database.Modbus.CreateSource(CreateSource());
        database.Tables.Create(CreateTableSchema("baseline"));

        using var modbusCatalogCopied = new ManualResetEventSlim();
        using var releaseFileCopy = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        var backupService = new BackupService();
        backupService.AfterFileCopiedTestHook = relativePath =>
        {
            if (!string.Equals(
                    relativePath,
                    "modbus/modbus.sdbmodbus",
                    StringComparison.Ordinal))
            {
                return;
            }

            modbusCatalogCopied.Set();
            if (!releaseFileCopy.Wait(_operationTimeout))
                throw new TimeoutException("测试未能按时恢复备份文件复制。");
        };

        Task<BackupManifest> backupTask = Task.Run(() => backupService.Create(
            database,
            new BackupCreateOptions { DestinationDirectory = backupRoot }));
        Task? disposeTask = null;
        try
        {
            Assert.True(modbusCatalogCopied.Wait(_eventTimeout), "备份未复制到 Modbus catalog 同步点。");
            disposeTask = Task.Run(() =>
            {
                disposeStarted.Set();
                database.Dispose();
            });
            Assert.True(disposeStarted.Wait(_eventTimeout), "Dispose 任务未能按时启动。");

            Task firstCompleted = await Task.WhenAny(
                disposeTask,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(disposeTask, firstCompleted);
        }
        finally
        {
            backupService.AfterFileCopiedTestHook = null;
            releaseFileCopy.Set();
        }

        BackupManifest manifest = await backupTask.WaitAsync(_operationTimeout);
        Assert.NotNull(disposeTask);
        await disposeTask.WaitAsync(_operationTimeout);
        Assert.Contains(manifest.Files, static file =>
            string.Equals(file.Path, "modbus/modbus.sdbmodbus", StringComparison.Ordinal));
        BackupVerificationResult verification = backupService.Verify(backupRoot);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));
    }

    /// <summary>
    /// 验证 Dispose 等待持有 schema 锁的映射 DDL，关闭后 Modbus 管理器拒绝继续发布目录变更。
    /// </summary>
    [Fact]
    public async Task Dispose_DuringMappedCreate_WaitsAndRejectsLaterModbusMutation()
    {
        string databaseRoot = Path.Combine(_rootDirectory, "dispose-create-database");
        Tsdb database = OpenDatabase(databaseRoot);
        database.Modbus.CreateSource(CreateSource());

        using var ddlHasSchemaLock = new ManualResetEventSlim();
        using var releaseDdl = new ManualResetEventSlim();
        using var disposeStarted = new ManualResetEventSlim();
        database.SchemaMutationLockAcquiredTestHook = () =>
        {
            ddlHasSchemaLock.Set();
            if (!releaseDdl.Wait(_operationTimeout))
                throw new TimeoutException("测试未能按时恢复映射表 DDL。");
        };

        Task<object?> ddlTask = Task.Run(() => SqlExecutor.Execute(database, MappedTableSql("mapped_values")));
        Task? disposeTask = null;
        try
        {
            Assert.True(ddlHasSchemaLock.Wait(_eventTimeout), "映射表 DDL 未取得 schema 锁。");
            disposeTask = Task.Run(() =>
            {
                disposeStarted.Set();
                database.Dispose();
            });
            Assert.True(disposeStarted.Wait(_eventTimeout), "Dispose 任务未能按时启动。");

            Task firstCompleted = await Task.WhenAny(
                disposeTask,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(disposeTask, firstCompleted);
        }
        finally
        {
            database.SchemaMutationLockAcquiredTestHook = null;
            releaseDdl.Set();
        }

        _ = await ddlTask.WaitAsync(_operationTimeout);
        Assert.NotNull(disposeTask);
        await disposeTask.WaitAsync(_operationTimeout);
        Assert.Throws<ObjectDisposedException>(() =>
            database.Modbus.CreateSource(CreateSource("late_source")));

        using Tsdb reopened = OpenDatabase(databaseRoot);
        Assert.NotNull(reopened.Tables.Catalog.TryGet("mapped_values"));
        Assert.NotNull(reopened.Modbus.Catalog.TryGetBinding("mapped_values"));
        Assert.Null(reopened.Modbus.Catalog.TryGetSource("late_source"));
    }

    /// <summary>删除当前测试实例创建的隔离目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootDirectory))
                Directory.Delete(_rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 并发失败可能暂时保留文件句柄，不能让清理异常覆盖主断言。
        }
        catch (UnauthorizedAccessException)
        {
            // 文件系统拒绝清理时保留目录供诊断。
        }
    }

    /// <summary>使用关闭后台维护任务的稳定选项打开测试数据库。</summary>
    private static Tsdb OpenDatabase(string rootDirectory)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = rootDirectory,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        });

    /// <summary>创建禁用状态、Modicon 寻址的测试 source。</summary>
    private static ModbusSourceDefinition CreateSource(string name = "line1")
        => new(
            name,
            "127.0.0.1",
            AddressingMode: ModbusAddressingMode.Modicon,
            Enabled: false);

    /// <summary>创建含主键和单个数值列的关系表 schema。</summary>
    private static TableSchema CreateTableSchema(string tableName)
        => TableSchema.Create(
            tableName,
            [
                ("id", TableColumnType.Int64, false),
                ("value", TableColumnType.Int64, false),
            ],
            ["id"]);

    /// <summary>创建指向测试 source 的保持寄存器绑定。</summary>
    private static ModbusTableBinding CreateSourceBinding(string tableName)
        => new(
            tableName,
            ModbusMappingDirection.SourceToTable,
            "line1",
            [
                new ModbusColumnMapping(
                    "value",
                    ModbusRegisterArea.HoldingRegister,
                    DeclaredAddress: 40_001,
                    PduAddress: 0,
                    ModbusValueType.UInt16),
            ]);

    /// <summary>构造用于并发备份测试的完整映射表 DDL。</summary>
    private static string MappedTableSql(string tableName)
        => $"""
            CREATE TABLE {tableName} (
                id INT NOT NULL,
                value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
                PRIMARY KEY (id)
            )
            USING MODBUS SOURCE line1
            WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
            """;
}
