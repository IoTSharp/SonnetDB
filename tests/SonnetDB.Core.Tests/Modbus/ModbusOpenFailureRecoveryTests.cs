using SonnetDB.Engine;
using SonnetDB.Modbus;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Modbus;

/// <summary>
/// 验证 Modbus catalog 导致数据库打开失败时，启动阶段已创建的资源会被完整释放。
/// </summary>
public sealed class ModbusOpenFailureRecoveryTests
{
    /// <summary>
    /// 验证损坏的 Modbus catalog 被修复后，同一进程可以立即重新打开数据库。
    /// </summary>
    [Fact]
    public void Open_CorruptModbusCatalogThenRestore_ReopensInSameProcess()
    {
        string rootDirectory = Path.Combine(
            Path.GetTempPath(),
            $"SonnetDB.ModbusOpenFailure.Tests.{Guid.NewGuid():N}");

        try
        {
            string catalogPath;
            byte[] originalCatalog;
            using (Tsdb database = OpenDatabase(rootDirectory))
            {
                database.Modbus.CreateSource(CreateSource());
                database.Tables.Create(CreateTableSchema());
                database.Modbus.CreateBinding(CreateBinding());
                catalogPath = database.Modbus.CatalogPath;
                originalCatalog = File.ReadAllBytes(catalogPath);
            }

            // 只破坏 footer 中保存的 CRC，确保失败来自 catalog 完整性校验而非业务字段解析。
            byte[] corruptedCatalog = (byte[])originalCatalog.Clone();
            corruptedCatalog[^16] ^= 0x01;
            File.WriteAllBytes(catalogPath, corruptedCatalog);

            try
            {
                InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                {
                    using Tsdb unexpected = OpenDatabase(rootDirectory);
                });
                Assert.Contains("CRC32 mismatch", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                // 首次打开失败后立即恢复原字节，为同进程重试保留完全相同的数据库状态。
                File.WriteAllBytes(catalogPath, originalCatalog);
            }

            // 成功重开说明失败路径没有遗留占用 active WAL 的 writer 或未释放的启动资源。
            using Tsdb reopened = OpenDatabase(rootDirectory);
            Assert.NotNull(reopened.Modbus.Catalog.TryGetSource("line1"));
            Assert.NotNull(reopened.Modbus.Catalog.TryGetBinding("telemetry"));
            Assert.NotNull(reopened.Tables.Catalog.TryGet("telemetry"));
        }
        finally
        {
            TryDeleteDirectory(rootDirectory);
        }
    }

    /// <summary>
    /// 使用关闭后台维护任务的稳定选项打开测试数据库。
    /// </summary>
    private static Tsdb OpenDatabase(string rootDirectory)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = rootDirectory,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        });

    /// <summary>
    /// 创建用于持久化测试的禁用状态 Modbus source。
    /// </summary>
    private static ModbusSourceDefinition CreateSource()
        => new(
            "line1",
            "127.0.0.1",
            AddressingMode: ModbusAddressingMode.Modicon,
            Enabled: false);

    /// <summary>
    /// 创建包含主键和一个 Modbus 数值列的关系表 schema。
    /// </summary>
    private static TableSchema CreateTableSchema()
        => TableSchema.Create(
            "telemetry",
            [
                ("id", TableColumnType.Int64, false),
                ("value", TableColumnType.Int64, false),
            ],
            ["id"]);

    /// <summary>
    /// 创建指向 source 的单列保持寄存器绑定。
    /// </summary>
    private static ModbusTableBinding CreateBinding()
        => new(
            "telemetry",
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

    /// <summary>
    /// 尽力删除测试目录；当前回归失败时可能仍有 WAL 句柄占用，不能让清理异常覆盖主断言。
    /// </summary>
    private static void TryDeleteDirectory(string rootDirectory)
    {
        try
        {
            if (Directory.Exists(rootDirectory))
                Directory.Delete(rootDirectory, recursive: true);
        }
        catch (IOException)
        {
            // 失败用例可能正是因为启动资源未释放，保留目录供诊断。
        }
        catch (UnauthorizedAccessException)
        {
            // 文件系统拒绝清理时同样保留目录，不覆盖测试结果。
        }
    }
}
