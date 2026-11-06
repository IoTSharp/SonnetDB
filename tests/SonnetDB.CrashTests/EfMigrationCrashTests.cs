using System.Diagnostics;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>实际 EF 多命令业务迁移在提交边界被父进程强杀，两次重开及幂等迁移/回退保持完整状态。</summary>
    /// <param name="phase">真实提交阶段。</param>
    /// <param name="committed">恢复后的持久提交决定。</param>
    [Theory]
    [InlineData("before-commit", false)]
    [InlineData("undo-durable", false)]
    [InlineData("prepared-durable", false)]
    [InlineData("published", false)]
    [InlineData("committed-durable", true)]
    [InlineData("cleanup-complete", true)]
    [InlineData("after-ack", true)]
    public void EfMigration_HardKillAtCommitBoundary_RecoversAtomicBusinessState(string phase, bool committed)
        => VerifyEfMigrationRecovery(phase, committed, killRecovery: false);

    /// <summary>实际关系恢复在补偿/清理后再次强杀，重试仍按原 durable decision 恢复。</summary>
    /// <param name="phase">第一次强杀的提交阶段。</param>
    /// <param name="committed">期望恢复的持久决定。</param>
    [Theory]
    [InlineData("published", false)]
    [InlineData("committed-durable", true)]
    public void EfMigration_HardKillAgainDuringRecovery_RepeatsSameDecision(string phase, bool committed)
        => VerifyEfMigrationRecovery(phase, committed, killRecovery: true);

    private void VerifyEfMigrationRecovery(string phase, bool committed, bool killRecovery)
    {
        string root = NewScenarioRoot();
        string journal = Path.Combine(root, "database", "tables", TableDdlTransactionJournal.FileName);
        KillEfChild(root, phase);
        if (phase is "prepared-durable" or "published" or "committed-durable" or "cleanup-complete")
            Assert.Equal(committed ? TableDdlTransactionDecision.Committed : TableDdlTransactionDecision.Prepared,
                TableDdlTransactionJournal.Read(journal)!.Decision);
        else Assert.False(File.Exists(journal));

        if (killRecovery)
        {
            KillEfChild(root, committed ? "recover-new" : "recover-old");
            Assert.True(File.Exists(journal));
        }

        // Every reopen is a new OS process, not a second context sharing an embedded database.
        for (int reopen = 0; reopen < 2; reopen++)
            CompleteEfChild(root, committed ? "verify-new" : "verify-old", "reopen-" + reopen);
        Assert.False(File.Exists(journal));
        CompleteEfChild(root, "roundtrip", "retry-business-downgrade-upgrade");
        CompleteEfChild(root, "verify-new", "final-reopen");
    }

    private static void KillEfChild(string root, string phase)
    {
        string ready = Path.Combine(root, phase + ".ready");
        using Process child = StartChild("ef_migration_" + phase, root, ready);
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        DateTime started = child.StartTime.ToUniversalTime();
        try
        {
            WaitForEfReady(child, ready);
            Assert.Equal(phase, File.ReadAllText(ready));
            Assert.False(child.HasExited);
            Assert.Equal(started, child.StartTime.ToUniversalTime());
            File.AppendAllText(Path.Combine(root, "parent-confirmations.log"),
                $"OBSERVED {phase} | pid={child.Id} | start={started:O} | parent={Environment.ProcessId}\n");
        }
        finally
        {
            TerminateAndWait(child, TimeSpan.FromSeconds(10));
            File.AppendAllText(Path.Combine(root, "parent-confirmations.log"),
                $"KILLED {phase} | pid={child.Id} | exit={child.ExitCode}\n");
            KeepEfEvidence(root, phase, child, output, error);
        }
        Assert.NotEqual(0, child.ExitCode);
    }

    private static void CompleteEfChild(string root, string action, string receipt)
    {
        using Process child = StartChild("ef_migration_" + action, root, Path.Combine(root, receipt + ".ready"));
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(child.WaitForExit(60_000), "EF child timeout: " + action);
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            TerminateAndWait(child, TimeSpan.FromSeconds(10));
            KeepEfEvidence(root, receipt, child, output, error);
        }
    }

    private static void WaitForEfReady(Process child, string path)
    {
        var elapsed = Stopwatch.StartNew();
        for (int attempt = 0; attempt < 1_800 && elapsed.Elapsed < TimeSpan.FromSeconds(45); attempt++)
        {
            if (File.Exists(path)) return;
            if (child.HasExited) throw new InvalidOperationException("EF child exited before boundary; see retained child log.");
            Thread.Sleep(25);
        }
        throw new TimeoutException("EF child did not reach boundary within 45 seconds.");
    }

    private static void KeepEfEvidence(string root, string label, Process child, Task<string> outputTask, Task<string> errorTask)
    {
        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();
        string? destination = Environment.GetEnvironmentVariable("SONNETDB_EF_CRASH_EVIDENCE");
        if (string.IsNullOrWhiteSpace(destination)) return;
        string owned = Path.Combine(Path.GetFullPath(destination), Path.GetFileName(root));
        Directory.CreateDirectory(owned);
        File.WriteAllText(Path.Combine(owned, label + ".log"),
            $"pid={child.Id}; exit={child.ExitCode}\n" + File.ReadAllText(Path.Combine(root, "child-process.txt"))
            + "\nSTDOUT\n" + output + "\nSTDERR\n" + error);
        if (File.Exists(Path.Combine(root, "parent-confirmations.log")))
            File.Copy(Path.Combine(root, "parent-confirmations.log"), Path.Combine(owned, "parent-confirmations.log"), overwrite: true);
    }
}
