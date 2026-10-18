using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>强杀真实子进程后，CTAS 提交点两侧只可见完整旧状态或完整新表。</summary>
    [Theory]
    [InlineData("stage-created", false)]
    [InlineData("stage-durable", false)]
    [InlineData("rowstore-moved", false)]
    [InlineData("catalog-durable", true)]
    public void CreateTableAs_HardKillAtDurableBoundary_ReopensWholeTableOrNothing(string phase, bool committed)
    {
        string root = RunKillScenario("crash_ctas_" + phase, TimeSpan.Zero);
        for (int reopen = 0; reopen < 2; reopen++)
        {
            using var database = Tsdb.Open(MakeOptions(root));
            Assert.Equal(1_024, database.Tables.Open("Source").RowCount);
            if (committed)
            {
                Assert.Equal(1_024, database.Tables.Open("Copied").RowCount);
                Assert.Equal(1_049_600L, Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
                    "SELECT SUM(Value) FROM Copied")).Rows)[0]);
            }
            else Assert.Null(database.Tables.Catalog.TryGet("Copied"));
            Assert.False(File.Exists(Path.Combine(root, "tables", TableCreationJournal.FileName)));
            Assert.Empty(Directory.EnumerateDirectories(Path.Combine(root, "tables"), "ctas-*", SearchOption.TopDirectoryOnly));
        }
    }

    /// <summary>准备文件写断保留原始证据；完整同步但尚未 move 的准备日志可安全回收。</summary>
    [Theory]
    [InlineData("temporary-created", false)]
    [InlineData("temporary-durable", true)]
    public void CreateTableAs_HardKillDuringPrepare_PreservesUnknownOrCleansVerifiedTemporary(string phase, bool complete)
    {
        string root = RunKillScenario("crash_ctas_" + phase, TimeSpan.Zero);
        string path = Path.Combine(root, "tables", TableCreationJournal.FileName + ".tmp");
        byte[] before = File.ReadAllBytes(path);
        using var database = Tsdb.Open(MakeOptions(root));
        Assert.Equal(1_024, database.Tables.Open("Source").RowCount);
        Assert.Null(database.Tables.Catalog.TryGet("Copied"));
        if (complete)
        {
            Assert.False(File.Exists(path));
            SqlExecutor.Execute(database, "CREATE TABLE Copied AS SELECT Id,Value FROM Source");
            Assert.Equal(1_024, database.Tables.Open("Copied").RowCount);
        }
        else
        {
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => SqlExecutor.Execute(database, "CREATE TABLE Copied AS SELECT Id,Value FROM Source"));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
    }

    /// <summary>恢复回收删完 owner 但尚未删空目录时再次强杀，下一次仍能恢复且允许同名创建。</summary>
    [Fact]
    public void CreateTableAs_HardKillAgainDuringRecovery_ReopensAndAllowsSameNameCreate()
    {
        string root = RunKillScenario("crash_ctas_rowstore-moved", TimeSpan.Zero);
        string ready = Path.Combine(root, "second-recovery.ready");
        using (Process child = StartChild("crash_ctas_recovery-after-owner-removed", root, ready))
        {
            try { WaitForReady(child, ready, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        string[] rowstores = Directory.GetDirectories(Path.Combine(root, "tables", "rowstore"));
        Assert.Contains(rowstores, directory => !Directory.EnumerateFileSystemEntries(directory).Any());
        using var database = Tsdb.Open(MakeOptions(root));
        Assert.Null(database.Tables.Catalog.TryGet("Copied"));
        Assert.Equal(1_024, database.Tables.Open("Source").RowCount);
        SqlExecutor.Execute(database, "CREATE TABLE Copied AS SELECT Id,Value FROM Source");
        Assert.Equal(1_024, database.Tables.Open("Copied").RowCount);
    }
}
