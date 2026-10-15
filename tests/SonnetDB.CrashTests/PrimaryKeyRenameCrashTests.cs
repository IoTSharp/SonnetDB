using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrimaryKeyRename_HardKillAtCatalogCommit_ReopensConsistentKeysReferencesAndGeneratedState(bool beforeCatalog)
    {
        string scenario = "crash_kill9_primary_key_rename_" + (beforeCatalog ? "before_catalog" : "after_catalog");
        string root = RunKillScenario(scenario, TimeSpan.Zero);
        string key = beforeCatalog ? "id" : "Identifier";
        using var reopened = Tsdb.Open(MakeOptions(root));
        var parent = reopened.Tables.Catalog.TryGet("parents")!;
        Assert.Equal([key], parent.PrimaryKey);
        Assert.Equal([key], Assert.Single(reopened.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
        Assert.Equal([key, "label"], parent.Indexes[0].Columns);
        Assert.True(parent.TryGetColumn(key)!.IsAutoIncrement);
        Assert.True(parent.TryGetColumn("version")!.IsRowVersion);
        Assert.Equal(2, Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, $"SELECT {key} FROM parents")).Rows.Count);
        Assert.Equal(TableConstraintException.ForeignKeyViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(reopened, "INSERT INTO children (id, parent_id) VALUES (11, 99)")).ErrorCode);
        Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(reopened, $"INSERT INTO parents ({key}, label) VALUES (1, 'duplicate')")).ErrorCode);
        Assert.Equal(TableConstraintException.UniqueViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(reopened, $"INSERT INTO parents ({key}, label) VALUES (9, 'two')")).ErrorCode);
        Assert.Equal(TableConstraintException.CheckViolation, Assert.Throws<TableConstraintException>(() =>
            SqlExecutor.Execute(reopened, $"INSERT INTO parents ({key}, label) VALUES (-1, 'negative')")).ErrorCode);
        SqlExecutor.Execute(reopened, "INSERT INTO parents DEFAULT VALUES");
        var row = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened,
            $"SELECT {key}, version FROM parents WHERE label = 'base'")).Rows);
        Assert.True((long)row[0]! > 2L);
        Assert.Equal(1L, row[1]);
        if (beforeCatalog)
        {
            SqlExecutor.Execute(reopened, "ALTER TABLE parents RENAME COLUMN id TO Identifier");
            Assert.Equal(["Identifier"], reopened.Tables.Catalog.TryGet("parents")!.PrimaryKey);
            Assert.Equal(["Identifier"], Assert.Single(reopened.Tables.Catalog.TryGet("children")!.ForeignKeys).PrincipalColumns);
        }
    }
}
