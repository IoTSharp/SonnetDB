using System.Diagnostics;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>prepared 撤销副本归属损坏时，在删除当前已发布数据前拒绝恢复并保留 journal。</summary>
    [Fact]
    public async Task EfMigration_CorruptUndoOwner_RejectsBeforeChangingPublishedCatalog()
    {
        string root = NewScenarioRoot();
        KillEfChild(root, "published");
        string tables = Path.Combine(root, "database", "tables");
        string journalPath = Path.Combine(tables, TableDdlTransactionJournal.FileName);
        var journal = TableDdlTransactionJournal.Read(journalPath)!;
        string ownerPath = Path.GetFullPath(Path.Combine(tables, journal.UndoRelativePath, ".ddl-owner"));
        Assert.StartsWith(Path.GetFullPath(tables) + Path.DirectorySeparatorChar, ownerPath, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(ownerPath, new string('0', 32));
        string schemaPath = Path.Combine(tables, TableSchemaCodec.FileName);
        byte[] published = File.ReadAllBytes(schemaPath);
        using Process child = StartChild("ef_migration_verify-old", root, Path.Combine(root, "reject.ready"));
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.True(child.WaitForExit(60_000));
            Assert.NotEqual(0, child.ExitCode);
            Assert.Contains("owner", await error, StringComparison.Ordinal);
        }
        finally
        {
            TerminateAndWait(child, TimeSpan.FromSeconds(10));
            KeepEfEvidence(root, "corrupt-undo-owner", child, output, error);
        }
        Assert.Equal(published, File.ReadAllBytes(schemaPath));
        Assert.Equal(TableDdlTransactionDecision.Prepared, TableDdlTransactionJournal.Read(journalPath)!.Decision);
        Assert.True(File.Exists(ownerPath));
    }
}
