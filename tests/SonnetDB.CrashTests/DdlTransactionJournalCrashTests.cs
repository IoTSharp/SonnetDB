using System.Diagnostics;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>prepared/committed 决策持久后强杀，恢复 backend 后再强杀，第三次仍按同一决策完成。</summary>
    [Theory]
    [InlineData("prepared")]
    [InlineData("committed")]
    public void DdlTransactionJournal_HardKillAndSecondRecoveryKill_RepeatsDurableDecision(string decision)
    {
        string root = NewOwnedDirectory("crash_ddl_journal_" + decision);
        string journalPath = Path.Combine(root, "tables", TableDdlTransactionJournal.FileName);
        string firstReady = Path.Combine(root, "first.ready");
        using (Process child = StartChild("crash_ddl_journal_" + decision, root, firstReady))
        {
            try { WaitForReady(child, firstReady, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        TableDdlTransactionJournal durable = TableDdlTransactionJournal.Read(journalPath)!;
        Assert.Equal(decision == "prepared"
            ? TableDdlTransactionDecision.Prepared
            : TableDdlTransactionDecision.Committed, durable.Decision);

        string secondReady = Path.Combine(root, "second.ready");
        using (Process child = StartChild("crash_ddl_recovery_after-backend", root, secondReady))
        {
            try { WaitForReady(child, secondReady, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(child, TimeSpan.FromSeconds(10)); }
        }
        Assert.True(File.Exists(journalPath));
        string marker = Path.Combine(root, "tables", "ddl-recovery-" + decision + ".log");
        Assert.Single(File.ReadAllLines(marker));

        string completeReady = Path.Combine(root, "complete.ready");
        using (Process child = StartChild("crash_ddl_recovery_complete", root, completeReady))
        {
            Assert.True(child.WaitForExit(10_000));
            Assert.Equal(0, child.ExitCode);
        }
        Assert.False(File.Exists(journalPath));
        Assert.Equal(2, File.ReadAllLines(marker).Length);
    }

    private static string NewOwnedDirectory(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), name + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
