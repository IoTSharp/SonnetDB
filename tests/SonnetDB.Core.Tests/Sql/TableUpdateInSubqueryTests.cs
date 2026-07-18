using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class TableUpdateInSubqueryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-update-in-subquery-{Guid.NewGuid():N}");

    [Fact]
    public void Update_InOrderedLimitedSubquery_WithParameters_UpdatesOnlySelectedRows()
    {
        using var db = OpenTargets();
        SqlExecutor.Execute(db, "CREATE TABLE scores (id INT, target_id INT, score INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO scores (id, target_id, score) VALUES
                (1, 1, 10), (2, 2, 30), (3, 3, 20)
            """);

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            """
            UPDATE targets SET marker = 7
            WHERE id IN (
                SELECT target_id FROM scores
                WHERE score >= @minimum
                ORDER BY score DESC
                LIMIT @take
            )
            """,
            new SqlParameters().AddNamed("minimum", 10).AddNamed("take", 2),
            controlPlane: null));

        Assert.Equal(2, result.RowsAffected);
        Assert.Equal([(1L, 0L), (2L, 7L), (3L, 7L)], ReadMarkers(db));
    }

    [Fact]
    public void Update_InAndNotInSubquery_PreserveNullAndEmptySetSemantics()
    {
        using var db = OpenTargets(includeNullCode: true);
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, code INT NULL, PRIMARY KEY (id))");

        var inEmpty = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            "UPDATE targets SET marker = 1 WHERE code IN (SELECT code FROM lookup)"));
        Assert.Equal(0, inEmpty.RowsAffected);

        var notInEmpty = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            "UPDATE targets SET marker = 2 WHERE code NOT IN (SELECT code FROM lookup)"));
        Assert.Equal(3, notInEmpty.RowsAffected);
        Assert.Equal([(1L, 2L), (2L, 2L), (3L, 2L)], ReadMarkers(db));

        SqlExecutor.Execute(db, "UPDATE targets SET marker = 0 WHERE TRUE");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id, code) VALUES (1, 20), (2, NULL)");

        var inWithNull = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            "UPDATE targets SET marker = 1 WHERE code IN (SELECT code FROM lookup ORDER BY id)"));
        Assert.Equal(1, inWithNull.RowsAffected);

        var notInWithNull = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            "UPDATE targets SET marker = 2 WHERE code NOT IN (SELECT code FROM lookup ORDER BY id)"));
        Assert.Equal(0, notInWithNull.RowsAffected);
        Assert.Equal([(1L, 0L), (2L, 1L), (3L, 0L)], ReadMarkers(db));
    }

    [Fact]
    public void Update_CorrelatedInSubquery_IsRejectedBeforeScanningEmptyInnerTable()
    {
        using var db = OpenTargets();
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, target_code INT, PRIMARY KEY (id))");

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            """
            UPDATE targets SET marker = 9
            WHERE id IN (
                SELECT l.id FROM lookup l WHERE l.target_code = targets.code
            )
            """));

        Assert.Contains("相关子查询", exception.Message, StringComparison.Ordinal);
        Assert.Equal([(1L, 0L), (2L, 0L), (3L, 0L)], ReadMarkers(db));
    }

    [Fact]
    public void Update_InSubqueryReturningMultipleColumns_IsRejectedExplicitly()
    {
        using var db = OpenTargets();
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, code INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id, code) VALUES (1, 10)");

        var lookupStore = db.Tables.Open("lookup");
        long scansBefore = lookupStore.FullScanCount;
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "UPDATE targets SET marker = 9 WHERE id IN (SELECT id, code FROM lookup)"));

        Assert.Contains("必须只返回一列", exception.Message, StringComparison.Ordinal);
        Assert.Equal(scansBefore, lookupStore.FullScanCount);
        Assert.Equal([(1L, 0L), (2L, 0L), (3L, 0L)], ReadMarkers(db));
    }

    [Fact]
    public void UpdateAndDelete_InSubqueries_WorkInLightTransactionQueue()
    {
        using var db = OpenTargets();
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id) VALUES (1), (2)");

        SqlExecutor.ExecuteScript(db, """
            BEGIN;
            UPDATE targets SET marker = 5 WHERE id IN (SELECT id FROM lookup);
            COMMIT;
            BEGIN;
            DELETE FROM targets WHERE id NOT IN (SELECT id FROM lookup);
            COMMIT;
            """);

        Assert.Equal([(1L, 5L), (2L, 5L)], ReadMarkers(db));
    }

    [Theory]
    [InlineData("UPDATE targets SET marker = 8 WHERE id IN (SELECT id FROM targets WHERE id = 4)")]
    [InlineData("DELETE FROM targets WHERE id IN (SELECT id FROM targets WHERE id = 4)")]
    public void MutationSubquery_WithPriorBufferedTargetWrite_IsRejectedExplicitly(string sql)
    {
        using var db = OpenTargets();
        var transaction = new SqlTransactionContext();
        SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            SqlParser.Parse("INSERT INTO targets (id, code, marker) VALUES (4, 40, 0)"),
            controlPlane: null,
            transaction);

        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            SqlParser.Parse(sql),
            controlPlane: null,
            transaction));

        Assert.Contains("已有缓冲写", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private Tsdb OpenTargets(bool includeNullCode = false)
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT NULL, marker INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, includeNullCode
            ? "INSERT INTO targets (id, code, marker) VALUES (1, 10, 0), (2, 20, 0), (3, NULL, 0)"
            : "INSERT INTO targets (id, code, marker) VALUES (1, 10, 0), (2, 20, 0), (3, 30, 0)");
        return db;
    }

    private static (long Id, long Marker)[] ReadMarkers(Tsdb db)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id, marker FROM targets ORDER BY id"));
        return result.Rows
            .Select(static row => ((long)row[0]!, (long)row[1]!))
            .ToArray();
    }
}
