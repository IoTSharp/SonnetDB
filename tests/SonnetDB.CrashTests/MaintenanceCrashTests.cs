using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>删除真实主键时强杀必须整体恢复主键与 INCLUDE 载荷，并能重试和重开。</summary>
    [Theory]
    [InlineData("before-barrier", false)]
    [InlineData("after-barrier", false)]
    [InlineData("after-journal", false)]
    [InlineData("after-table:devices", false)]
    [InlineData("after-catalog", false)]
    [InlineData("after-complete", true)]
    public void PrimaryKeyDrop_WithIncludedIndex_HardKillRestoresWholeState(string phase, bool committed)
    {
        string root = NewScenarioRoot();
        KillMaintenancePhase(root, phase, "maintenance.ready");
        AssertDroppedPrimaryKeyRecovery(root, committed);
    }

    /// <summary>INCLUDE 和主键撤销恢复本身被强杀后仍能重复执行。</summary>
    [Fact]
    public void PrimaryKeyDrop_WithIncludedIndex_HardKillDuringRecoveryRemainsRetryable()
    {
        string root = NewScenarioRoot();
        KillMaintenancePhase(root, "after-catalog", "maintenance.ready");
        KillMaintenancePhase(root, "recovery", "recovery.ready");
        AssertDroppedPrimaryKeyRecovery(root, committed: false);
    }

    private static void KillMaintenancePhase(string root, string phase, string readyName)
    {
        string readyFile = Path.Combine(root, readyName);
        using Process child = StartChild("crash_kill9_maintenance_drop_" + phase, root, readyFile);
        try { WaitForReady(child, readyFile, TimeSpan.FromSeconds(10)); }
        finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
    }

    private static void AssertDroppedPrimaryKeyRecovery(string root, bool committed)
    {
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            TableSchema schema = db.Tables.Catalog.TryGet("devices")!;
            Assert.Equal(committed ? null : "PkDevices", schema.PrimaryKeyConstraintName);
            Assert.Equal(committed ? [] : new[] { "Id" }, schema.PrimaryKey);
            TableIndex index = Assert.Single(schema.Indexes);
            Assert.Equal([true], index.Descending);
            Assert.Equal(["Payload"], index.IncludedColumns);
            Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(
                Path.Combine(root, "tables", TableSchemaCodec.FileName)).AsSpan(8)));
            Assert.Equal(["second", "first"], MaintenancePayloads(db));
            if (!committed)
            {
                Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
                    SqlExecutor.Execute(db, "INSERT INTO devices(Id,Code,Payload) VALUES(1,'c','rejected')")).ErrorCode);
                SqlExecutor.Execute(db, "ALTER TABLE devices DROP CONSTRAINT PkDevices");
            }
            SqlExecutor.Execute(db, "INSERT INTO devices(Id,Code,Payload) VALUES(1,'c','third')");
            Assert.Equal(["third", "second", "first"], MaintenancePayloads(db));
        }
        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Null(reopened.Tables.Catalog.TryGet("devices")!.PrimaryKeyConstraintName);
        Assert.Equal(["third", "second", "first"], MaintenancePayloads(reopened));
        SqlExecutor.Execute(reopened, "UPDATE devices SET Payload='changed' WHERE Code='a'");
        SqlExecutor.Execute(reopened, "DELETE FROM devices WHERE Code='c'");
        Assert.Equal(["second", "changed"], MaintenancePayloads(reopened));
    }

    private static string[] MaintenancePayloads(Tsdb db)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT Payload FROM devices ORDER BY Code DESC")).Rows.Select(row => Assert.IsType<string>(row[0])).ToArray();

    /// <summary>关闭普通写同步时，已确认的自增维护仍须在真实进程终止后保留下一值和原数据。</summary>
    [Theory]
    [InlineData("empty", 50L, 0)]
    [InlineData("existing", 200L, 2)]
    [InlineData("deleted", 200L, 1)]
    [InlineData("rejected", 200L, 1)]
    [InlineData("maximum", long.MaxValue, 0)]
    public void AutoIncrement_MaintenanceAcknowledgedBeforeKill_RecoversNextValue(string phase, long next, int existingRows)
    {
        string root = RunKillScenario("crash_kill9_maintenance_identity_" + phase, TimeSpan.Zero);
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            Assert.Equal(existingRows, Rows(db, "SELECT Id FROM Device").Count);
            if (existingRows != 0)
                Assert.Equal("kept", Assert.Single(Rows(db, "SELECT Payload FROM Device WHERE Id=1"))[0]);
            if (phase is "deleted" or "rejected")
                Assert.Empty(Rows(db, "SELECT Id FROM Device WHERE Id=100"));
            SqlExecutor.Execute(db, "INSERT INTO Device(Payload) VALUES('after-crash')");
            Assert.Equal(next, Assert.Single(Rows(db, "SELECT Id FROM Device WHERE Payload='after-crash'"))[0]);
            if (phase == "maximum")
                Assert.ThrowsAny<Exception>(() => SqlExecutor.Execute(db, "INSERT INTO Device(Payload) VALUES('overflow')"));
        }

        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Equal(existingRows + 1, Rows(reopened, "SELECT Id FROM Device").Count);
        Assert.Equal(next, Assert.Single(Rows(reopened, "SELECT Id FROM Device WHERE Payload='after-crash'"))[0]);
        Assert.Empty(Rows(reopened, "SELECT Id FROM Device WHERE Payload='overflow'"));

        static IReadOnlyList<IReadOnlyList<object?>> Rows(Tsdb db, string sql)
            => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql)).Rows;
    }
}
