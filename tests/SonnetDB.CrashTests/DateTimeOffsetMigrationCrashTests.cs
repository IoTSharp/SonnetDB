using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>进程强杀后只允许完整旧或新时刻状态，屏障永久保留，且可重试。</summary>
    [Theory]
    [InlineData("before-barrier", false)]
    [InlineData("after-barrier", false)]
    [InlineData("after-journal", false)]
    [InlineData("after-table:offsets", false)]
    [InlineData("after-catalog", false)]
    [InlineData("after-complete", true)]
    public void DateTimeOffsetMigration_HardKillAtEachBoundary_ReopensAndRetries(string phase, bool committed)
    {
        // Windows ready 文件名不能含冒号；保持 hook 的实际阶段名用于子进程参数。
        string root = NewScenarioRoot();
        string readyFile = Path.Combine(root, "offset.ready");
        using (Process child = StartChild("crash_offset_" + phase, root, readyFile))
        {
            try { WaitForReady(child, readyFile, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            Assert.Equal(phase == "before-barrier" ? 13 : 15, DateTimeOffsetVersion(root));
            AssertDateTimeOffsetRows(db, committed);
            if (!committed) SqlExecutor.Execute(db, "ALTER TABLE offsets ALTER COLUMN stamp TYPE DATETIMEOFFSET");
            AssertDateTimeOffsetRows(db, true);
            SqlExecutor.Execute(db, "DROP INDEX ix_stamp ON offsets");
            SqlExecutor.Execute(db, "ALTER TABLE offsets DROP COLUMN stamp");
            SqlExecutor.Execute(db, "DROP TABLE offsets");
            Assert.Equal(15, DateTimeOffsetVersion(root));
        }
        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Empty(reopened.Tables.Catalog.Snapshot());
        Assert.Equal(15, DateTimeOffsetVersion(root));
    }

    /// <summary>恢复期间再次强杀后必须可重复撤销，且 v15 不能下降。</summary>
    [Fact]
    public void DateTimeOffsetMigration_HardKillDuringRecovery_RetainsPermanentBarrier()
    {
        string root = RunKillScenario("crash_offset_after-catalog", TimeSpan.Zero);
        string readyFile = Path.Combine(root, "second.ready");
        using (Process child = StartChild("crash_offset_recovery", root, readyFile))
        {
            try { WaitForReady(child, readyFile, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        using var db = Tsdb.Open(MakeOptions(root));
        Assert.Equal(15, DateTimeOffsetVersion(root));
        AssertDateTimeOffsetRows(db, false);
        SqlExecutor.Execute(db, "ALTER TABLE offsets ALTER COLUMN stamp TYPE DATETIMEOFFSET");
        AssertDateTimeOffsetRows(db, true);
    }

    private static int DateTimeOffsetVersion(string root) => BinaryPrimitives.ReadInt32LittleEndian(
        File.ReadAllBytes(Path.Combine(root, "tables", TableSchemaCodec.FileName)).AsSpan(8));

    private static void AssertDateTimeOffsetRows(Tsdb database, bool offset)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT stamp FROM offsets ORDER BY id"));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(offset ? (object)new DateTimeOffset(1, TimeSpan.Zero) : 1L, result.Rows[0][0]);
        Assert.Equal(offset ? (object)new DateTimeOffset(639270596961230001, TimeSpan.Zero) : 639270596961230001L, result.Rows[1][0]);
    }
}
