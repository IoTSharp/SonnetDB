using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>
/// 验证级联删除按批复用反向 FK 元数据、持久索引和临时哈希索引。
/// </summary>
public sealed class CascadeDeletePerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-cascade-performance-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ApplyTransaction_MultipleParentsWithoutIndex_ScansChildTableOnce()
    {
        using var db = Open();
        CreateCascadeTables(db);
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1), (2), (3)");
        SqlExecutor.Execute(db,
            "INSERT INTO children (id, parent_id, label) VALUES "
            + "(10, 1, 'a'), (11, 1, 'b'), (20, 2, 'c'), (30, 3, 'keep')");

        var metrics = new CascadeDeleteExecutionMetrics();
        int affected = db.Tables.ApplyTransaction(DeleteParents(1, 2), metrics);

        Assert.Equal(5, affected);
        Assert.Equal(1, metrics.CatalogSnapshotCount);
        Assert.Equal(0, metrics.PersistentIndexLookupCount);
        Assert.Equal(1, metrics.FallbackScanCount);
        Assert.Equal(4, metrics.FallbackDecodedRowCount);

        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, parent_id FROM children ORDER BY id"));
        Assert.Equal(new object?[] { 30L, 3L }, Assert.Single(rows.Rows));
    }

    [Fact]
    public void ApplyTransaction_ExactForeignKeyIndex_UsesOneLookupPerParentWithoutFallbackScan()
    {
        using var db = Open();
        CreateCascadeTables(db);
        SqlExecutor.Execute(db, "CREATE INDEX idx_children_parent ON children (parent_id)");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1), (2), (3)");
        SqlExecutor.Execute(db,
            "INSERT INTO children (id, parent_id, label) VALUES "
            + "(10, 1, 'a'), (11, 1, 'b'), (20, 2, 'c'), (30, 3, 'keep')");

        var metrics = new CascadeDeleteExecutionMetrics();
        int affected = db.Tables.ApplyTransaction(DeleteParents(1, 2), metrics);

        Assert.Equal(5, affected);
        Assert.Equal(1, metrics.CatalogSnapshotCount);
        Assert.Equal(2, metrics.PersistentIndexLookupCount);
        Assert.Equal(0, metrics.FallbackScanCount);
        Assert.Equal(0, metrics.FallbackDecodedRowCount);
    }

    [Fact]
    public void ApplyTransaction_IndexWithAdditionalColumns_FallsBackToSingleScan()
    {
        using var db = Open();
        CreateCascadeTables(db);
        SqlExecutor.Execute(db, "CREATE INDEX idx_children_parent_label ON children (parent_id, label)");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1), (2)");
        SqlExecutor.Execute(db,
            "INSERT INTO children (id, parent_id, label) VALUES (10, 1, 'a'), (20, 2, 'b')");

        var metrics = new CascadeDeleteExecutionMetrics();
        _ = db.Tables.ApplyTransaction(DeleteParents(1, 2), metrics);

        Assert.Equal(0, metrics.PersistentIndexLookupCount);
        Assert.Equal(1, metrics.FallbackScanCount);
        Assert.Equal(2, metrics.FallbackDecodedRowCount);
    }

    [Fact]
    public void ApplyTransaction_ExplicitChildUpdateAndParentDelete_DoesNotCreateDuplicateMutation()
    {
        using var db = Open();
        CreateCascadeTables(db);
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "INSERT INTO children (id, parent_id, label) VALUES (10, 1, 'move')");

        var mutations = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            ["parents"] = [new TableRowMutation([1L], NewValues: null)],
            ["children"] = [new TableRowMutation([10L], [10L, 2L, "move"])],
        };

        int affected = db.Tables.ApplyTransaction(mutations, new CascadeDeleteExecutionMetrics());

        Assert.Equal(2, affected);
        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id, parent_id FROM children"));
        Assert.Equal(new object?[] { 10L, 2L }, Assert.Single(rows.Rows));
    }

    [Fact]
    public void ApplyTransaction_PreparationFailureAfterCascadeExpansion_LeavesAllRowsUnchanged()
    {
        using var db = Open();
        CreateCascadeTables(db);
        SqlExecutor.Execute(db, "CREATE TABLE guard_rows (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO parents (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO children (id, parent_id, label) VALUES (10, 1, 'keep')");
        SqlExecutor.Execute(db, "INSERT INTO guard_rows (id) VALUES (1)");

        var mutations = new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            ["parents"] = [new TableRowMutation([1L], NewValues: null)],
            ["guard_rows"] = [new TableRowMutation(PrimaryKeyValues: null, [1L])],
        };

        Assert.Throws<InvalidOperationException>(() =>
            db.Tables.ApplyTransaction(mutations, new CascadeDeleteExecutionMetrics()));

        Assert.Single(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM parents")).Rows);
        Assert.Single(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM children")).Rows);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open()
        => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateCascadeTables(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE parents (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "CREATE TABLE children (id INT, parent_id INT, label STRING, PRIMARY KEY (id), "
            + "FOREIGN KEY (parent_id) REFERENCES parents (id) ON DELETE CASCADE)");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TableRowMutation>> DeleteParents(params long[] ids)
        => new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            ["parents"] = ids
                .Select(static id => new TableRowMutation([id], NewValues: null))
                .ToArray(),
        };
}
