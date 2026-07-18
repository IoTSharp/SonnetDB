using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class TableDeleteInSubqueryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-delete-in-subquery-{Guid.NewGuid():N}");

    [Fact]
    public void Delete_InOrderedLimitedSubquery_WithParameter_DeletesExactlyTenRows()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, """
            CREATE TABLE T_Acquisitions (
                guid STRING,
                uploadTime DATETIME,
                captureTime DATETIME,
                payload STRING,
                PRIMARY KEY (guid)
            )
            """);

        var start = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 15; i++)
        {
            var parameters = new SqlParameters()
                .AddNamed("guid", $"g{i:D2}")
                .AddNamed("upload", i < 12 ? start : start.AddHours(2))
                .AddNamed("capture", start.AddMinutes(i))
                .AddNamed("payload", new string('x', 4096));
            SqlExecutor.Execute(
                db,
                databaseName: null,
                """
                INSERT INTO T_Acquisitions (guid, uploadTime, captureTime, payload)
                VALUES (@guid, @upload, @capture, @payload)
                """,
                parameters,
                controlPlane: null);
        }

        var store = db.Tables.Open("T_Acquisitions");
        long scansBefore = store.FullScanCount;
        long pointReadsBefore = store.PrimaryKeyLookupCount;
        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            """
            DELETE FROM "T_Acquisitions"
            WHERE "guid" IN (
                SELECT "guid"
                FROM "T_Acquisitions"
                WHERE "uploadTime" < @cutoff
                ORDER BY "captureTime"
                LIMIT 10
            )
            """,
            new SqlParameters().AddNamed("cutoff", start.AddHours(1)),
            controlPlane: null));

        Assert.Equal(10, result.SeriesAffected);
        Assert.Equal(scansBefore + 1, store.FullScanCount);
        Assert.Equal(pointReadsBefore + 10, store.PrimaryKeyLookupCount);
        var remaining = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT guid FROM T_Acquisitions ORDER BY guid"));
        Assert.Equal(
            ["g10", "g11", "g12", "g13", "g14"],
            remaining.Rows.Select(static row => (string)row[0]!));
    }

    [Fact]
    public void Delete_InAndNotInSubquery_PreserveNullThreeValuedLogic()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, code INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, code) VALUES (1, 1), (2, 2), (3, NULL)");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id, code) VALUES (1, 2), (2, NULL)");

        var inResult = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE code IN (SELECT code FROM lookup ORDER BY id)"));
        Assert.Equal(1, inResult.SeriesAffected);
        Assert.Equal([1L, 3L], ReadIds(db));

        var notInWithNull = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE code NOT IN (SELECT code FROM lookup ORDER BY id)"));
        Assert.Equal(0, notInWithNull.SeriesAffected);
        Assert.Equal([1L, 3L], ReadIds(db));

        SqlExecutor.Execute(db, "DELETE FROM lookup WHERE id = 2");
        var notInWithoutNull = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE code NOT IN (SELECT code FROM lookup ORDER BY id)"));
        Assert.Equal(1, notInWithoutNull.SeriesAffected);
        Assert.Equal([3L], ReadIds(db));
    }

    [Fact]
    public void Delete_InAndNotInEmptySubquery_UseEmptySetSemantics()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, code INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, code) VALUES (1, 1), (2, NULL)");

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id FROM targets WHERE code NOT IN (SELECT code FROM lookup) ORDER BY id"));
        Assert.Equal([1L, 2L], selected.Rows.Select(static row => (long)row[0]!));

        var inResult = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE code IN (SELECT code FROM lookup)"));
        Assert.Equal(0, inResult.SeriesAffected);

        var notInResult = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE code NOT IN (SELECT code FROM lookup)"));
        Assert.Equal(2, notInResult.SeriesAffected);
        Assert.Empty(ReadIds(db));
    }

    [Fact]
    public void Delete_InSubqueryReturningMultipleColumns_IsRejectedExplicitly()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, code INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, code) VALUES (1, 10)");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id, code) VALUES (1, 10)");

        var lookupStore = db.Tables.Open("lookup");
        long scansBefore = lookupStore.FullScanCount;
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE id IN (SELECT id, code FROM lookup)"));

        Assert.Contains("必须只返回一列", exception.Message, StringComparison.Ordinal);
        Assert.Equal(scansBefore, lookupStore.FullScanCount);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_StarSubqueryExpandingToMultipleColumns_IsRejectedBeforeScan()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, payload STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id, payload) VALUES (1, 'large')");
        var lookupStore = db.Tables.Open("lookup");
        long scansBefore = lookupStore.FullScanCount;

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE id IN (SELECT * FROM lookup)"));

        Assert.Contains("必须只返回一列", exception.Message, StringComparison.Ordinal);
        Assert.Equal(scansBefore, lookupStore.FullScanCount);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_UnionBranchReturningMultipleColumns_IsRejectedBeforeScan()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, payload STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id) VALUES (1)");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id, payload) VALUES (1, 'large')");
        var lookupStore = db.Tables.Open("lookup");
        long scansBefore = lookupStore.FullScanCount;

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            """
            DELETE FROM targets
            WHERE id IN (SELECT id FROM lookup UNION SELECT id, payload FROM lookup)
            """));

        Assert.Contains("必须只返回一列", exception.Message, StringComparison.Ordinal);
        Assert.Equal(scansBefore, lookupStore.FullScanCount);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_DocumentSubquery_IsRejectedAsUnsupportedRatherThanCorrelated()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION target_docs");
        SqlExecutor.Execute(db, "INSERT INTO targets (id) VALUES (1)");

        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE id IN (SELECT id FROM target_docs)"));

        Assert.Contains("只支持普通关系表", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("相关子查询", exception.Message, StringComparison.Ordinal);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_WrappedTableValuedFunctionSubquery_IsRejectedExplicitly()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id) VALUES (1)");

        var exception = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db,
            """
            DELETE FROM targets
            WHERE id IN (
                SELECT id FROM (
                    SELECT id FROM json_each('missing.json')
                ) AS j
            )
            """));

        Assert.Contains("只支持普通关系表", exception.Message, StringComparison.Ordinal);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_CorrelatedInSubquery_IsRejectedEvenWhenInnerTableIsEmpty()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, target_code INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, code) VALUES (1, 10)");

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            """
            DELETE FROM targets
            WHERE id IN (
                SELECT l.id FROM lookup l WHERE l.target_code = targets.code
            )
            """));

        Assert.Contains("相关子查询", exception.Message, StringComparison.Ordinal);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_NonCorrelatedSubqueryOrderByProjectionAlias_IsAccepted()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, code INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO targets (id, code) VALUES (1, 10), (2, 20)");
        SqlExecutor.Execute(db, "INSERT INTO lookup (id) VALUES (2)");

        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE id IN (SELECT id AS code FROM lookup ORDER BY code)"));

        Assert.Equal(1, result.SeriesAffected);
        Assert.Equal([1L], ReadIds(db));
    }

    [Fact]
    public void Delete_Int64PrimaryKeyAboveDoublePrecision_DoesNotMatchAdjacentValue()
    {
        using var db = Tsdb.Open(Options());
        const long existing = 9_007_199_254_740_992L;
        const long adjacent = 9_007_199_254_740_993L;
        SqlExecutor.Execute(db, "CREATE TABLE targets (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, candidate INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, $"INSERT INTO targets (id) VALUES ({existing})");
        SqlExecutor.Execute(db, $"INSERT INTO lookup (id, candidate) VALUES (1, {adjacent})");

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id FROM targets WHERE id IN (SELECT candidate FROM lookup)"));
        Assert.Empty(selected.Rows);

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM targets WHERE id IN (SELECT candidate FROM lookup)"));
        Assert.Equal(0, deleted.SeriesAffected);
        Assert.Equal([existing], ReadIds(db));
    }

    [Fact]
    public void Delete_DateTimePrimaryKeyWithOutOfRangeUnixCandidate_FallsBackWithoutThrowing()
    {
        using var db = Tsdb.Open(Options());
        var timestamp = new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
        SqlExecutor.Execute(db, "CREATE TABLE events (at DATETIME, payload STRING, PRIMARY KEY (at))");
        SqlExecutor.Execute(db, "CREATE TABLE lookup (id INT, candidate INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(
            db,
            databaseName: null,
            "INSERT INTO events (at, payload) VALUES (@at, 'kept')",
            new SqlParameters().AddNamed("at", timestamp),
            controlPlane: null);
        SqlExecutor.Execute(db, $"INSERT INTO lookup (id, candidate) VALUES (1, {long.MaxValue})");

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            "DELETE FROM events WHERE at IN (SELECT candidate FROM lookup)"));

        Assert.Equal(0, deleted.SeriesAffected);
        var remaining = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT payload FROM events"));
        Assert.Equal("kept", Assert.Single(remaining.Rows)[0]);
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

    private TsdbOptions Options() => new() { RootDirectory = _root };

    private static long[] ReadIds(Tsdb db)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id FROM targets ORDER BY id"));
        return result.Rows.Select(static row => (long)row[0]!).ToArray();
    }
}
