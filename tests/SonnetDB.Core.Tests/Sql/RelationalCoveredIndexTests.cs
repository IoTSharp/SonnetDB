using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class RelationalCoveredIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-relational-covered-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("")]
    [InlineData(" AND t.rank >= -1 AND t.rank <= 1")]
    public void Execute_CoveredPrefixOrSignedRange_SkipsBaseRows(string range)
    {
        using var database = CreateDatabase();
        TableStore tasks = database.Tables.Open("cover_tasks");
        TableStore targets = database.Tables.Open("cover_targets");
        int taskDecoded = 0;
        int targetDecoded = 0;
        tasks.RowDecodedTestHook = _ => taskDecoded++;
        targets.RowDecodedTestHook = _ => targetDecoded++;
        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result = Execute(database, $"""
            SELECT t.rank, d.region
            FROM cover_tasks t JOIN cover_targets d ON t.device_id = d.device_id
            WHERE t.status = 'ready' AND d.region = 'north'{range}
            ORDER BY t.rank
            """, metrics);

        Assert.Equal(new object?[] { -1L, 0L, 1L }, result.Rows.Select(static row => row[0]));
        Assert.All(result.Rows, row => Assert.Equal("north", row[1]));
        Assert.Equal(0, taskDecoded);
        Assert.Equal(0, targetDecoded);
        Assert.Equal("secondary_index_only", metrics.Complete().AccessPath);
    }

    [Theory]
    [InlineData("t.payload", "", 3)]
    [InlineData("t.rank", " AND t.payload = 'keep'", 2)]
    [InlineData("t.rank", " AND t.rank <> 0", 2)]
    public void Execute_UncoveredColumnOrResidual_PreservesBaseRowFallback(string projection, string residual, int count)
    {
        using var database = CreateDatabase();
        int decoded = 0;
        database.Tables.Open("cover_tasks").RowDecodedTestHook = _ => decoded++;
        SelectExecutionResult result = Execute(database, $"""
            SELECT {projection}, d.region
            FROM cover_tasks t JOIN cover_targets d ON t.device_id = d.device_id
            WHERE t.status = 'ready' AND d.region = 'north'{residual}
            """);

        Assert.Equal(count, result.Rows.Count);
        Assert.True(decoded >= count);
    }

    [Fact]
    public void Execute_CoveredRangeInTransaction_SeesBufferedMutation()
    {
        using var database = CreateDatabase();
        IReadOnlyList<object?> results = SqlExecutor.ExecuteScript(database, """
            BEGIN;
            INSERT INTO cover_tasks (id, status, rank, device_id, payload) VALUES (9, 'ready', -2, 10, 'buffered');
            UPDATE cover_tasks SET rank = 2 WHERE id = 2;
            DELETE FROM cover_tasks WHERE id = 3;
            SELECT t.rank, d.region
            FROM cover_tasks t JOIN cover_targets d ON t.device_id = d.device_id
            WHERE t.status = 'ready' AND t.rank >= -2 AND t.rank <= 2 AND d.region = 'north'
            ORDER BY t.rank;
            ROLLBACK;
            """);

        Assert.Equal(new object?[] { -2L, -1L, 2L }, Assert.IsType<SelectExecutionResult>(results[4]).Rows.Select(static row => row[0]));
        Assert.Equal(new object?[] { -1L, 0L, 1L }, Execute(database, """
            SELECT t.rank FROM cover_tasks t JOIN cover_targets d ON t.device_id = d.device_id
            WHERE t.status = 'ready' AND d.region = 'north' ORDER BY t.rank
            """).Rows.Select(static row => row[0]));
    }

    [Fact]
    public void Execute_CoveredPrefixAfterReopen_MatchesUncoveredProjection()
    {
        using (var database = CreateDatabase())
            Assert.Equal(4, database.Tables.Open("cover_tasks").Scan().Count);
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var expected = Execute(reopened, """
            SELECT t.rank, t.payload FROM cover_tasks t JOIN cover_targets d ON t.device_id = d.device_id
            WHERE t.status = 'ready' AND d.region = 'north' ORDER BY t.rank
            """);
        int decoded = 0;
        reopened.Tables.Open("cover_tasks").RowDecodedTestHook = _ => decoded++;
        var actual = Execute(reopened, """
            SELECT t.rank FROM cover_tasks t JOIN cover_targets d ON t.device_id = d.device_id
            WHERE t.status = 'ready' AND d.region = 'north' ORDER BY t.rank LIMIT 2 OFFSET 1
            """);

        Assert.Equal(expected.Rows.Skip(1).Take(2).Select(static row => row[0]), actual.Rows.Select(static row => row[0]));
        Assert.Equal(0, decoded);
    }

    [Theory]
    [InlineData("ASC", "ASC")]
    [InlineData("ASC", "DESC")]
    [InlineData("DESC", "ASC")]
    [InlineData("DESC", "DESC")]
    public void Execute_OrderedCoveringWindow_MatchesBaseRowsAndStopsAtLimit(string indexDirection, string queryDirection)
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.ExecuteScript(database, $"""
            CREATE TABLE cover_events (
                id INT, tenant STRING NOT NULL, rank INT NOT NULL,
                label STRING, details STRING, PRIMARY KEY (id)
            );
            CREATE INDEX ix_events_window ON cover_events (tenant, rank {indexDirection}) INCLUDE (label);
            INSERT INTO cover_events (id, tenant, rank, label, details) VALUES
                (1, 'north', -5, 'before', 'extra'), (2, 'north', -1, 'negative', 'extra'),
                (3, 'north', 0, NULL, 'extra'), (4, 'north', 0, 'tied', 'extra'),
                (5, 'north', 1, 'positive', 'extra'), (6, 'north', 9, 'after', 'extra'),
                (7, 'south', 0, 'other', 'extra');
            """);
        TableStore store = database.Tables.Open("cover_events");
        int decoded = 0;
        store.RowDecodedTestHook = _ => decoded++;
        string window = $"""
            FROM cover_events WHERE tenant = 'north' AND rank >= -5 AND rank <= 9
            ORDER BY rank {queryDirection} LIMIT 3 OFFSET 1
            """;

        var referenceMetrics = new SqlExecutionMetrics();
        SelectExecutionResult reference = Execute(database, "SELECT rank, label, details " + window, referenceMetrics);
        Assert.Equal("secondary_index_range", referenceMetrics.Complete().AccessPath);
        Assert.Equal(4, decoded);
        decoded = 0;

        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result = Execute(database, "SELECT rank, label " + window, metrics);
        Assert.Equal(reference.Rows.Select(static row => (row[0], row[1])),
            result.Rows.Select(static row => (row[0], row[1])));
        Assert.Equal(0, decoded);
        SqlExecutionMetricsSnapshot measured = metrics.Complete();
        Assert.Equal("secondary_index_only", measured.AccessPath);
        Assert.Equal(4, measured.ExaminedRows);
        SelectExecutionResult explain = Execute(database, "EXPLAIN SELECT rank, label " + window);
        Assert.Contains(explain.Rows, row => Equals(row[0], "access_path") && Equals(row[1], "secondary_index_only"));
    }

    private Tsdb CreateDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.ExecuteScript(database, """
            CREATE TABLE cover_tasks (id INT, status STRING, rank INT, device_id INT, payload STRING, PRIMARY KEY (id));
            CREATE INDEX ix_covered ON cover_tasks (status, rank, device_id);
            CREATE TABLE cover_targets (id INT, region STRING, device_id INT, payload STRING, PRIMARY KEY (id));
            CREATE INDEX ix_covered ON cover_targets (region, device_id);
            INSERT INTO cover_tasks (id, status, rank, device_id, payload) VALUES
                (1, 'ready', -1, 10, 'keep'), (2, 'ready', 0, 10, 'skip'),
                (3, 'ready', 1, 10, 'keep'), (4, 'blocked', 2, 10, 'skip');
            INSERT INTO cover_targets (id, region, device_id, payload) VALUES
                (1, 'north', 10, 'large'), (2, 'south', 20, 'large');
            """);
        return database;
    }

    private static SelectExecutionResult Execute(Tsdb database, string sql, SqlExecutionMetrics? metrics = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, null, sql, null, null,
            new SqlExecutionOptions { Metrics = metrics, DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(20) }));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
