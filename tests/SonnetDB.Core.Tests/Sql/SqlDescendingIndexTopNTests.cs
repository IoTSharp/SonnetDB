using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M41 #371 双向索引 cursor 与倒序 Top-N 的安全下推和回退边界。</summary>
public sealed class SqlDescendingIndexTopNTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-desc-index-topn-{Guid.NewGuid():N}");

    /// <summary>无 WHERE 的非空 Int64 索引应跨符号边界倒序读取并只检查 OFFSET+LIMIT 行。</summary>
    [Fact]
    public void Select_OrderByDescendingLimit_UsesReverseIndexCursorAndStopsAtWindow()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("topn_events");
        long scansBefore = store.FullScanCount;
        var pushedLimits = new List<int>();
        store.RangeScanLimitTestHook = pushedLimits.Add;
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            "SELECT id, occurred_at FROM topn_events ORDER BY occurred_at DESC LIMIT 3 OFFSET 1",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));

        Assert.Equal([5L, 4L, 3L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal([50L, 10L, 0L], result.Rows.Select(static row => (long)row[1]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Contains(4, pushedLimits);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.Equal("secondary_index_range", snapshot.AccessPath);
        Assert.Equal("ix_topn_occurred", snapshot.IndexName);
        Assert.Equal(4, snapshot.CandidateRows);
        Assert.Equal(4, snapshot.ExaminedRows);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "EXPLAIN SELECT id FROM topn_events ORDER BY occurred_at DESC LIMIT 3 OFFSET 1"));
        var explainValues = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Equal("secondary_index_range", explainValues["access_path"]);
        Assert.Equal("ix_topn_occurred", explainValues["index_name"]);
        Assert.Equal(4L, explainValues["estimated_scanned_rows"]);
    }

    /// <summary>联合索引等值前缀后的时间列应倒序读取，LIMIT 在过滤前安全下推。</summary>
    [Fact]
    public void Select_EqualityPrefixOrderByDescending_UsesCompositeIndexWindow()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("topn_events");
        long scansBefore = store.FullScanCount;
        var pushedLimits = new List<int>();
        store.RangeScanLimitTestHook = pushedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM topn_events
            WHERE tenant = 'north'
            ORDER BY occurred_at DESC LIMIT 2
            """));

        Assert.Equal([5L, 3L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Contains(2, pushedLimits);
    }

    /// <summary>非覆盖残余谓词不得在过滤前截断，必须读取完整索引范围后走现有 Top-N。</summary>
    [Fact]
    public void Select_DescendingWithResidual_DoesNotPushCandidateLimit()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("topn_events");
        var pushedLimits = new List<int>();
        store.RangeScanLimitTestHook = pushedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM topn_events
            WHERE tenant = 'north' AND status = 'ready'
            ORDER BY occurred_at DESC LIMIT 1
            """));

        Assert.Equal([3L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.DoesNotContain(1, pushedLimits);
        Assert.Empty(pushedLimits);
    }

    /// <summary>nullable 排序键无法覆盖 NULL 行时必须回退现有排序路径。</summary>
    [Fact]
    public void Select_NullableOrderColumn_FallsBackWithoutReverseIndexLimit()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("topn_nullable");
        long scansBefore = store.FullScanCount;
        var pushedLimits = new List<int>();
        store.RangeScanLimitTestHook = pushedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM topn_nullable ORDER BY occurred_at DESC LIMIT 2"));

        Assert.Equal([3L, 1L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Equal(scansBefore + 1, store.FullScanCount);
        Assert.Empty(pushedLimits);
    }

    /// <summary>事务写集可能改变排序窗口，必须回退并让 buffered row 参与 Top-N。</summary>
    [Fact]
    public void Select_DescendingWithTransactionOverlay_FallsBackAndSeesBufferedRow()
    {
        using var db = CreateDatabase();
        var store = db.Tables.Open("topn_events");
        long scansBefore = store.FullScanCount;
        var pushedLimits = new List<int>();
        store.RangeScanLimitTestHook = pushedLimits.Add;

        var results = SqlExecutor.ExecuteScript(db, """
            BEGIN;
            INSERT INTO topn_events (id, tenant, status, occurred_at)
                VALUES (7, 'north', 'ready', 1000);
            SELECT id FROM topn_events ORDER BY occurred_at DESC LIMIT 1;
            ROLLBACK;
            """);
        var selected = Assert.IsType<SelectExecutionResult>(results[2]);

        Assert.Equal(7L, Assert.Single(Assert.Single(selected.Rows)));
        Assert.Equal(scansBefore + 1, store.FullScanCount);
        Assert.Empty(pushedLimits);
    }

    /// <summary>删除测试使用的临时数据库目录。</summary>
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

    /// <summary>创建覆盖负数/零/正数、联合前缀、残余谓词和 nullable 回退的固定数据。</summary>
    private Tsdb CreateDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE topn_events (
                id INT,
                tenant STRING,
                status STRING,
                occurred_at INT NOT NULL,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE INDEX ix_topn_occurred ON topn_events (occurred_at)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_topn_tenant_occurred ON topn_events (tenant, occurred_at)");
        SqlExecutor.Execute(db, """
            INSERT INTO topn_events (id, tenant, status, occurred_at) VALUES
                (1, 'south', 'ready', -50),
                (2, 'north', 'blocked', -10),
                (3, 'north', 'ready', 0),
                (4, 'south', 'ready', 10),
                (5, 'north', 'blocked', 50),
                (6, 'south', 'ready', 100)
            """);
        SqlExecutor.Execute(db, """
            CREATE TABLE topn_nullable (
                id INT,
                occurred_at INT NULL,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE INDEX ix_topn_nullable ON topn_nullable (occurred_at)");
        SqlExecutor.Execute(db, "INSERT INTO topn_nullable (id, occurred_at) VALUES (1, 10), (2, NULL), (3, 20)");
        return db;
    }
}
