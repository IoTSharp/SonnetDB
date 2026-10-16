using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>进程强杀后只允许完整旧或新时长状态，屏障永久保留，且可重试。</summary>
    [Theory]
    [InlineData("before-barrier", false)]
    [InlineData("after-barrier", false)]
    [InlineData("after-journal", false)]
    [InlineData("after-table:durations", false)]
    [InlineData("after-catalog", false)]
    [InlineData("after-complete", true)]
    public void DurationMigration_HardKillAtEachBoundary_ReopensAndRetries(string phase, bool committed)
    {
        // Windows ready 文件名不能含冒号；保持 hook 的实际阶段名用于子进程参数。
        string root = NewScenarioRoot();
        string readyFile = Path.Combine(root, "duration.ready");
        using (Process child = StartChild("crash_duration_" + phase, root, readyFile))
        {
            try { WaitForReady(child, readyFile, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            Assert.Equal(phase == "before-barrier" ? 13 : 14, DurationVersion(root));
            AssertDurationRows(db, committed);
            if (!committed) SqlExecutor.Execute(db, "ALTER TABLE durations ALTER COLUMN elapsed TYPE DURATION");
            AssertDurationRows(db, true);
            SqlExecutor.Execute(db, "DROP INDEX ix_elapsed ON durations");
            SqlExecutor.Execute(db, "ALTER TABLE durations DROP COLUMN elapsed");
            SqlExecutor.Execute(db, "DROP TABLE durations");
            Assert.Equal(14, DurationVersion(root));
        }
        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Empty(reopened.Tables.Catalog.Snapshot());
        Assert.Equal(14, DurationVersion(root));
    }

    /// <summary>恢复期间再次强杀后必须可重复撤销，且 v14 不能下降。</summary>
    [Fact]
    public void DurationMigration_HardKillDuringRecovery_RetainsPermanentBarrier()
    {
        string root = RunKillScenario("crash_duration_after-catalog", TimeSpan.Zero);
        string readyFile = Path.Combine(root, "second.ready");
        using (Process child = StartChild("crash_duration_recovery", root, readyFile))
        {
            try { WaitForReady(child, readyFile, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        using var db = Tsdb.Open(MakeOptions(root));
        Assert.Equal(14, DurationVersion(root));
        AssertDurationRows(db, false);
        SqlExecutor.Execute(db, "ALTER TABLE durations ALTER COLUMN elapsed TYPE DURATION");
        AssertDurationRows(db, true);
    }

    private static int DurationVersion(string root) => BinaryPrimitives.ReadInt32LittleEndian(
        File.ReadAllBytes(Path.Combine(root, "tables", TableSchemaCodec.FileName)).AsSpan(8));

    private static void AssertDurationRows(Tsdb database, bool duration)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT elapsed FROM durations ORDER BY id"));
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(duration ? (object)TimeSpan.FromTicks(-1) : -1L, result.Rows[0][0]);
        Assert.Equal(duration ? (object)TimeSpan.FromTicks(972000000001) : 972000000001L, result.Rows[1][0]);
    }
}
