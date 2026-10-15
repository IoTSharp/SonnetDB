using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    [Theory]
    [InlineData("before_barrier", false)]
    [InlineData("after_barrier", false)]
    [InlineData("after_journal", false)]
    [InlineData("after_page", false)]
    [InlineData("after_parent", false)]
    [InlineData("after_catalog", false)]
    [InlineData("after_complete", true)]
    public void PrimaryKeyMigration_HardKillAtDurableBoundary_RestoresWholeOldOrNewState(string phase, bool committed)
    {
        string root = RunKillScenario("crash_kill9_primary_key_migration_" + phase, TimeSpan.Zero);
        AssertMigrationState(root, committed, phase == "after_page" ? 1032 : 2);
        int version = BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(
            Path.Combine(root, "tables", TableSchemaCodec.FileName)).AsSpan(8));
        Assert.Equal(phase == "before_barrier" ? 11 : 13, version);
    }

    [Theory]
    [InlineData("after_catalog", false)]
    [InlineData("after_complete", true)]
    public void PrimaryKeyTypeMigration_HardKill_RestoresMatchingChildTypeAndValues(string phase, bool committed)
    {
        string root = RunKillScenario("crash_kill9_primary_key_migration_type_" + phase, TimeSpan.Zero);
        using var db = Tsdb.Open(MakeOptions(root));
        TableColumnType expected = committed ? TableColumnType.Decimal : TableColumnType.Int64;
        Assert.Equal(expected, db.Tables.Catalog.TryGet("parents")!.TryGetColumn("code")!.DataType);
        Assert.Equal(expected, db.Tables.Catalog.TryGet("children")!.TryGetColumn("parent_id")!.DataType);
        object? child = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT parent_id FROM children WHERE id=10")).Rows)[0];
        if (committed) Assert.Equal(2m, Assert.IsType<decimal>(child));
        else Assert.Equal(2L, Assert.IsType<long>(child));
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO children(id,parent_id) VALUES(11,99)")).ErrorCode);
    }

    [Fact]
    public void PrimaryKeyMigration_HardKillAgainAfterRecoveryCatalog_RepeatsUndoWithoutLoweringBarrier()
    {
        string root = RunKillScenario("crash_kill9_primary_key_migration_after_catalog", TimeSpan.Zero);
        string readyFile = Path.Combine(root, "second-recovery.ready");
        using (Process process = StartChild("crash_kill9_primary_key_migration_recovery_after_catalog", root, readyFile))
        {
            try { WaitForReady(process, readyFile, TimeSpan.FromSeconds(10)); }
            finally { TerminateAndWait(process, TimeSpan.FromSeconds(10)); }
        }
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(
            Path.Combine(root, "tables", TableSchemaCodec.FileName)).AsSpan(8)));
        AssertMigrationState(root, committed: false, expectedRows: 2);
        using (var db = Tsdb.Open(MakeOptions(root)))
            SqlExecutor.Execute(db, "UPDATE parents SET label='after-recovery' WHERE id=1");
        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Equal("after-recovery", Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened,
            "SELECT label FROM parents WHERE id=1")).Rows)[0]);
    }

    private static void AssertMigrationState(string root, bool committed, int expectedRows)
    {
        using var db = Tsdb.Open(MakeOptions(root));
        string key = committed ? "code" : "id";
        Assert.Equal([key], db.Tables.Catalog.TryGet("parents")!.PrimaryKey);
        Assert.Equal([key], Assert.Single(db.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
        Assert.Equal(expectedRows, Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT id FROM parents")).Rows.Count);
        Assert.Equal(committed ? 2L : 1L, Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT parent_id FROM children WHERE id=10")).Rows)[0]);
        Assert.Equal("one", Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            $"SELECT label FROM parents WHERE {key}={(committed ? 2 : 1)}")).Rows)[0]);
        Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO parents(code,label) VALUES(9999,'two')")).ErrorCode);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO children(id,parent_id) VALUES(11,99999)")).ErrorCode);
        Assert.Equal(TableConstraintException.CheckViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(db, "INSERT INTO parents(code,label) VALUES(-1,'negative')")).ErrorCode);
        SqlExecutor.Execute(db, "INSERT INTO parents(code) VALUES(9999)");
        IReadOnlyList<object?> inserted = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id,version FROM parents WHERE label='base'")).Rows);
        Assert.True(Assert.IsType<long>(inserted[0]) > expectedRows);
        Assert.Equal(1L, inserted[1]);
    }
}
