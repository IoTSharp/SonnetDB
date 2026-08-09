using SonnetDB.Modbus;
using Xunit;

namespace SonnetDB.Tests.Modbus;

/// <summary>
/// 验证 Modbus 控制写审计的持久化、数据库隔离和损坏拒绝合同。
/// </summary>
public sealed class ModbusWriteAuditStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-modbus-write-audit-" + Guid.NewGuid().ToString("N"));

    /// <summary>初始化独立审计目录。</summary>
    public ModbusWriteAuditStoreTests() => Directory.CreateDirectory(_root);

    /// <summary>清理测试审计目录。</summary>
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

    /// <summary>验证审计重启恢复、数据库过滤和载荷脱敏。</summary>
    [Fact]
    public void Reopen_WithPersistedEntries_RestoresFilteredMetadataWithoutValuePayload()
    {
        var store = new FileModbusWriteAuditStore(_root);
        ModbusWriteAuditEntry expected = CreateEntry("factory", "remote_succeeded");
        store.Append(expected);
        store.Append(CreateEntry("other", "failed"));

        var reopened = new FileModbusWriteAuditStore(_root);
        ModbusWriteAuditEntry actual = Assert.Single(reopened.List("factory", maxEntries: 10));
        Assert.Equal(expected, actual);
        Assert.Equal(expected, Assert.Single(reopened.List("FACTORY", maxEntries: 10)));

        string persisted = File.ReadAllText(Path.Combine(_root, "modbus-write-audit.ndjson"));
        Assert.DoesNotContain("normalizedValue", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("encodedValues", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("confirmationToken", persisted, StringComparison.Ordinal);
    }

    /// <summary>验证损坏的审计行在重启时被明确拒绝。</summary>
    [Fact]
    public void Reopen_WithCorruptedLine_ThrowsInvalidDataException()
    {
        var store = new FileModbusWriteAuditStore(_root);
        store.Append(CreateEntry("factory", "started"));
        File.AppendAllText(Path.Combine(_root, "modbus-write-audit.ndjson"), "{broken\n");

        Assert.Throws<InvalidDataException>(() => new FileModbusWriteAuditStore(_root));
    }

    /// <summary>验证 JSON 语法合法但必需字段缺失的审计行同样被拒绝。</summary>
    [Fact]
    public void Reopen_WithSemanticallyInvalidLine_ThrowsInvalidDataException()
    {
        File.WriteAllText(Path.Combine(_root, "modbus-write-audit.ndjson"), "{}\n");

        Assert.Throws<InvalidDataException>(() => new FileModbusWriteAuditStore(_root));
    }

    private static ModbusWriteAuditEntry CreateEntry(string database, string result)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new DateTimeOffset(2026, 8, 9, 1, 2, 3, TimeSpan.Zero),
            "credential:0123456789ABCDEF",
            database,
            "plc",
            "controls",
            "setpoint",
            UnitId: 1,
            FunctionCode: 0x06,
            DeclaredAddress: 40001,
            PduAddress: 0,
            EventType: "confirm",
            Result: result,
            ErrorCode: null,
            ApprovalId: Guid.NewGuid(),
            CatalogRevision: 2);
}
