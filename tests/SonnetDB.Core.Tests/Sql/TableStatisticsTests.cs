using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>M41 #375/#376/#377 统计、成本规划和执行证据回归。</summary>
public sealed class TableStatisticsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-table-statistics-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AnalyzeTable_PersistsStatisticsAcrossRestart_WithoutRawValues()
    {
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            SqlExecutor.Execute(db, "CREATE TABLE events (id INT, tenant STRING, value INT, PRIMARY KEY (id))");
            SqlExecutor.Execute(db, "CREATE INDEX ix_events_tenant ON events (tenant)");
            SqlExecutor.Execute(db, "INSERT INTO events (id, tenant, value) VALUES "
                + "(1, 'north', 10), (2, 'north', 20), (3, 'south', 30), (4, NULL, 40)");

            var result = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(db, "ANALYZE TABLE events"));
            Assert.Equal(4L, result.Rows[0][1]);
            Assert.Equal(4, result.Rows[0][4]);

            TableStatistics statistics = Assert.IsType<TableStatistics>(db.Tables.Open("events").Statistics);
            Assert.Equal(4, statistics.RowCount);
            Assert.Equal(0.25, statistics.TryGetColumn("tenant")!.NullFraction, precision: 6);
            Assert.NotEmpty(statistics.TryGetColumn("tenant")!.MostCommonValues);
            Assert.NotEmpty(statistics.TryGetColumn("value")!.Histogram);
            Assert.Equal(4, statistics.TryGetIndex("ix_events_tenant")!.RowCount);
            Assert.DoesNotContain(
                statistics.TryGetColumn("tenant")!.MostCommonValues,
                value => value.ToString()!.Contains("north", StringComparison.OrdinalIgnoreCase));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = reopened.Tables.Open("events");
        Assert.NotNull(store.Statistics);
        Assert.False(store.AreStatisticsStale);
    }

    [Fact]
    public void Explain_ReadsStatisticsMetadata_WithoutScanningBusinessRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE audits (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_audits_status ON audits (status)");
        InsertSelectiveRows(db, "audits");
        TableStore store = db.Tables.Open("audits");
        _ = store.RefreshStatistics();
        long scansBefore = store.FullScanCount;

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN SELECT id FROM audits WHERE status = 'ready'"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal("refreshed", values["estimate_source"]);
        Assert.Equal("secondary_index", values["access_path"]);
        Assert.NotNull(values["estimated_cost"]);
        Assert.NotNull(values["candidate_plans"]);
    }

    [Fact]
    public void ExplainAnalyze_Select_ReportsEstimatedAndActualEvidence()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE readings (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_readings_status ON readings (status)");
        InsertSelectiveRows(db, "readings");
        _ = SqlExecutor.Execute(db, "ANALYZE TABLE readings");

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN ANALYZE SELECT id FROM readings WHERE status = 'ready'"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal("refreshed", values["estimate_source"]);
        Assert.Equal(1L, Convert.ToInt64(values["actual_rows"]));
        Assert.Equal(1L, Convert.ToInt64(values["actual_candidate_rows"]));
        Assert.Equal(1L, Convert.ToInt64(values["actual_examined_rows"]));
        Assert.Equal(1L, Convert.ToInt64(values["actual_loops"]));
        Assert.NotNull(values["actual_execution_ms"]);
        Assert.Equal(0L, Convert.ToInt64(values["actual_spill_count"]));
        Assert.Equal("secondary_index", values["actual_access_path"]);
    }

    [Fact]
    public void CostPlanner_FreshStatistics_SelectsSelectiveIndexAndRejectsNonSelectiveIndex()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE cost_events (id INT, tenant STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_cost_tenant ON cost_events (tenant)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_cost_status ON cost_events (status)");
        TableStore store = db.Tables.Open("cost_events");
        store.InsertMany(Enumerable.Range(1, 2_000)
            .Select(id => (IReadOnlyList<object?>)new object?[]
            {
                (long)id,
                "common",
                id == 1 ? "rare" : "common",
            })
            .ToArray());
        _ = store.RefreshStatistics();

        var selective = Explain(db, "SELECT id FROM cost_events WHERE tenant = 'common' AND status = 'rare'");
        Assert.Equal("ix_cost_status", selective["index_name"]);
        Assert.Equal("secondary_index", selective["access_path"]);
        Assert.Contains("ix_cost_tenant", (string)selective["candidate_plans"]!);
        Assert.Contains("ix_cost_status", (string)selective["candidate_plans"]!);

        var nonSelective = Explain(db, "SELECT id FROM cost_events WHERE status = 'common'");
        Assert.Equal("table_scan", nonSelective["access_path"]);
        Assert.Equal("cost_model_table_scan", nonSelective["fallback_reason"]);
    }

    [Fact]
    public void ExplainExists_FreshStatisticsMatchesRuntimeCostPlan_WithoutScanningBusinessRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE exists_cost (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_exists_cost_status ON exists_cost (status)");
        TableStore store = db.Tables.Open("exists_cost");
        InsertSkewedRows(store);
        _ = store.RefreshStatistics();
        long scansBefore = store.FullScanCount;

        var explain = Explain(db, "SELECT EXISTS (SELECT 1 FROM exists_cost WHERE status = 'common')");
        Assert.Equal("table_scan", explain["access_path"]);
        Assert.Equal("cost_model_table_scan", explain["fallback_reason"]);
        Assert.Equal("refreshed", explain["estimate_source"]);
        Assert.NotNull(explain["estimated_cost"]);
        Assert.Equal(scansBefore, store.FullScanCount);

        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT EXISTS (SELECT 1 FROM exists_cost WHERE status = 'common')"));
        var metrics = new RelationalSelectExecutionMetrics();
        _ = RelationalSelectExecutor.Execute(db, statement, metrics);
        Assert.Equal(explain["access_path"], metrics.LastExistsAccessPath);
        Assert.Equal(explain["fallback_reason"], metrics.LastExistsFallbackReason);
    }

    [Fact]
    public void ExplainJoin_FreshStatisticsReportsTableCostEvidence_WithoutScanningBusinessRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT join_cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE join_hosts (id INT, host STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_join_hosts_status ON join_hosts (status)");
        TableStore store = db.Tables.Open("join_hosts");
        InsertSkewedRows(store, includeHost: true);
        _ = store.RefreshStatistics();
        long scansBefore = store.FullScanCount;

        var explain = Explain(db, """
            SELECT c.time, h.host
            FROM join_cpu c
            JOIN join_hosts h ON c.host = h.host
            WHERE h.status = 'rare'
            """);

        Assert.Contains("table:secondary_index", (string)explain["access_path"]!);
        Assert.Equal("join_hosts.ix_join_hosts_status", explain["index_name"]);
        Assert.Equal("refreshed", explain["estimate_source"]);
        Assert.NotNull(explain["estimated_cost"]);
        Assert.Contains("ix_join_hosts_status", (string)explain["candidate_plans"]!);
        Assert.Equal(scansBefore, store.FullScanCount);
    }

    [Fact]
    public void Parse_AnalyzeTable_AcceptsOptionalTableKeyword()
    {
        Assert.Equal("events", Assert.IsType<AnalyzeTableStatement>(SqlParser.Parse("ANALYZE TABLE events")).TableName);
        Assert.Equal("events", Assert.IsType<AnalyzeTableStatement>(SqlParser.Parse("ANALYZE events")).TableName);
    }

    [Fact]
    public void AnalyzeTable_Int64Extremes_PreservesHistogramBounds()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE extremes (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(
            db,
            databaseName: null,
            "INSERT INTO extremes (id, value) VALUES (1, @minimum), (2, @maximum)",
            new SqlParameters().AddNamed("minimum", long.MinValue).AddNamed("maximum", long.MaxValue),
            controlPlane: null);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "ANALYZE extremes"));
        Assert.Equal(2L, result.Rows[0][1]);
        var histogram = db.Tables.Open("extremes").Statistics!.TryGetColumn("value")!.Histogram;
        Assert.Contains(histogram, bucket => bucket.Int64UpperBound == long.MinValue);
        Assert.Contains(histogram, bucket => bucket.Int64UpperBound == long.MaxValue);
    }

    /// <summary>取消统计刷新后必须释放快照，并允许同一表继续成功刷新。</summary>
    [Fact]
    public void RefreshStatistics_PreCanceled_ReleasesSnapshotAndRemainsUsable()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE canceled_stats (id INT, value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_canceled_stats_value ON canceled_stats (value)");
        SqlExecutor.Execute(db, "INSERT INTO canceled_stats (id, value) VALUES (1, 'ready')");
        TableStore store = db.Tables.Open("canceled_stats");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => store.RefreshStatistics(cancellationToken: cancellation.Token));

        TableStatistics statistics = store.RefreshStatistics();
        Assert.Equal(1, statistics.RowCount);
        Assert.Equal(1, statistics.TryGetIndex("ix_canceled_stats_value")!.RowCount);
    }

    [Fact]
    public void Estimate_MissingStatistics_DoesNotSampleOnPlanningThread()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE first_query (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_first_status ON first_query (status)");
        TableStore store = db.Tables.Open("first_query");
        InsertSkewedRows(store);
        int planningThread = Environment.CurrentManagedThreadId;
        int foregroundSnapshots = 0;
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            if (Environment.CurrentManagedThreadId == planningThread)
                Interlocked.Increment(ref foregroundSnapshots);
        };

        _ = TableCostPlanner.Estimate(store, store.Schema, null, allowAutomaticRefresh: true);

        Assert.Equal(0, Volatile.Read(ref foregroundSnapshots));
    }

    [Fact]
    public async Task Estimate_RefreshRunning_CoalescesAndUsesHeuristicsWithoutAmbientContext()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "coalesced");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var ambient = new AsyncLocal<string?> { Value = "foreground-query" };
        string? capturedContext = "not-run";
        int refreshes = 0;
        store.AutomaticStatisticsRefreshStartedTestHook = token =>
        {
            Interlocked.Increment(ref refreshes);
            capturedContext = ambient.Value;
            entered.SetResult();
            release.Wait(token);
        };

        try
        {
            var explainBefore = Explain(db, "SELECT id FROM coalesced WHERE status = 'rare'");
            Assert.Equal("idle", explainBefore["statistics_refresh_state"]);
            _ = store.TryAutomaticStatisticsRefresh();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task completion = store.AutomaticStatisticsRefreshCompletion;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (int request = 0; request < 32; request++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Assert.Null(store.TryAutomaticStatisticsRefresh());
            }
            var explain = Explain(db, "SELECT id FROM coalesced WHERE status = 'rare'");
            Assert.Equal("running", explain["statistics_refresh_state"]);
            Assert.Equal("statistics_missing", explain["estimate_source"]);
            Assert.Equal("secondary_index", explain["access_path"]);
            Assert.Null(capturedContext);
            Assert.Equal(1, Volatile.Read(ref refreshes));

            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("completed", store.AutomaticStatisticsRefreshStatus.State);
            Assert.False(store.AreStatisticsStale);
            Assert.NotNull(store.Statistics);
        }
        finally
        {
            release.Set();
            await store.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Estimate_TwoTables_SharesDatabaseBudgetAndRetriesDeferredWork()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore first = CreateRefreshTable(db, "first_budget");
        TableStore second = CreateRefreshTable(db, "second_budget");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        first.AutomaticStatisticsRefreshStartedTestHook = token =>
        {
            entered.SetResult();
            release.Wait(token);
        };
        try
        {
            _ = first.TryAutomaticStatisticsRefresh();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(second.TryAutomaticStatisticsRefresh());
            Assert.Equal(new TableStatisticsRefreshStatus("deferred", "statistics_refresh_busy"),
                second.AutomaticStatisticsRefreshStatus);
            Assert.Null(second.Statistics);

            Task firstCompletion = first.AutomaticStatisticsRefreshCompletion;
            release.Set();
            await firstCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            _ = second.TryAutomaticStatisticsRefresh();
            await second.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("completed", second.AutomaticStatisticsRefreshStatus.State);
            Assert.NotNull(second.Statistics);
        }
        finally
        {
            release.Set();
            await first.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Estimate_StatisticsIoFailure_ReportsFailureAndBacksOffWithoutFailingQuery()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "failed_refresh");
        int attempts = 0;
        store.AutomaticStatisticsRefreshStartedTestHook = _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("injected maintenance failure");
        };

        _ = store.TryAutomaticStatisticsRefresh();
        await store.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new TableStatisticsRefreshStatus("failed", "statistics_refresh_io_error"),
            store.AutomaticStatisticsRefreshStatus);
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT id FROM failed_refresh WHERE status = 'rare'"));
        Assert.Equal(1L, Assert.Single(result.Rows)[0]);
        Assert.Equal(1, Volatile.Read(ref attempts));
        var explain = Explain(db, "SELECT id FROM failed_refresh WHERE status = 'rare'");
        Assert.Equal("failed", explain["statistics_refresh_state"]);
        Assert.Equal("statistics_refresh_io_error", explain["statistics_refresh_error_code"]);
    }

    [Fact]
    public async Task Dispose_RefreshRunning_CancelsAndReleasesDatabaseForReopen()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "closing_refresh");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.AutomaticStatisticsRefreshStartedTestHook = token =>
        {
            entered.SetResult();
            release.Wait(token);
        };

        _ = store.TryAutomaticStatisticsRefresh();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task completion = store.AutomaticStatisticsRefreshCompletion;
        db.Dispose();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", store.AutomaticStatisticsRefreshStatus.State);
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(2_000, reopened.Tables.Open("closing_refresh").RowCount);
    }

    [Fact]
    public async Task Dispose_RefreshRetainsDiskSnapshot_ReleasesAllWritersBeforeWorkerExits()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "delayed_close");
        TableStore other = CreateRefreshTable(db, "other_close");
        store.Compact();
        other.Compact();
        using TableReadSnapshot retainedSnapshot = store.AcquireTableReadSnapshot();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("test did not release the retained statistics snapshot");
        };

        Task completion = Task.CompletedTask;
        try
        {
            _ = store.TryAutomaticStatisticsRefresh();
            completion = store.AutomaticStatisticsRefreshCompletion;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            db.Dispose();
            Assert.False(completion.IsCompleted);

            using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
            TableStore reopenedStore = reopened.Tables.Open("delayed_close");
            Assert.Equal(2_000, reopenedStore.RowCount);
            Assert.Equal(2_000, reopened.Tables.Open("other_close").RowCount);
            using var cursor = retainedSnapshot.Snapshot.OpenRangeCursor(new SonnetDB.Kv.KvRangeScanOptions
            {
                Prefix = new byte[] { (byte)'r' },
                PageSize = 1,
            });
            Assert.Single(cursor.ReadNextPage());
            TableStatistics newStatistics = reopenedStore.RefreshStatistics();

            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("cancelled", store.AutomaticStatisticsRefreshStatus.State);
            Assert.Same(newStatistics, reopenedStore.Statistics);
        }
        finally
        {
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Drop_RefreshRetainsDiskSnapshot_ReleasesWriterAndMaintenanceBudget()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "delayed_ddl");
        TableStore other = CreateRefreshTable(db, "other_ddl");
        store.Compact();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("test did not release the retained statistics snapshot");
        };

        Task completion = Task.CompletedTask;
        try
        {
            _ = store.TryAutomaticStatisticsRefresh();
            completion = store.AutomaticStatisticsRefreshCompletion;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(db.Tables.Drop("delayed_ddl"));
            Assert.Null(db.Tables.Catalog.TryGet("delayed_ddl"));
            Assert.False(completion.IsCompleted);
            Assert.Null(other.TryAutomaticStatisticsRefresh());
            Assert.Equal("deferred", other.AutomaticStatisticsRefreshStatus.State);

            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("cancelled", store.AutomaticStatisticsRefreshStatus.State);
            _ = other.TryAutomaticStatisticsRefresh();
            await other.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(other.Statistics);
            Assert.Equal("completed", other.AutomaticStatisticsRefreshStatus.State);
        }
        finally
        {
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            await other.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Rename_RefreshRetainsDiskSnapshot_CancelsBeforeMovingDirectory()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "delayed_rename");
        TableStore other = CreateRefreshTable(db, "other_rename");
        store.Compact();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        CancellationToken maintenanceToken = default;
        store.AutomaticStatisticsRefreshStartedTestHook = token => maintenanceToken = token;
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20), maintenanceToken))
                throw new TimeoutException("test did not cancel the retained statistics snapshot");
        };

        Task completion = Task.CompletedTask;
        try
        {
            _ = store.TryAutomaticStatisticsRefresh();
            completion = store.AutomaticStatisticsRefreshCompletion;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            db.Tables.RenameTable("delayed_rename", "renamed_table");
            Assert.True(completion.IsCompleted);
            Assert.Equal("cancelled", store.AutomaticStatisticsRefreshStatus.State);
            Assert.Null(db.Tables.Catalog.TryGet("delayed_rename"));
            Assert.Equal(2_000, db.Tables.Open("renamed_table").RowCount);

            _ = other.TryAutomaticStatisticsRefresh();
            await other.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(other.Statistics);
            Assert.Equal("completed", other.AutomaticStatisticsRefreshStatus.State);
        }
        finally
        {
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            await other.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Rename_RefreshIgnoresCancellation_TimeoutPreservesOriginalTableForRetry()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "blocked_rename");
        store.Compact();
        store.AutomaticStatisticsRefreshRenameTimeoutTestOverride = TimeSpan.Zero;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("test did not release the retained statistics snapshot");
        };

        Task completion = Task.CompletedTask;
        try
        {
            _ = store.TryAutomaticStatisticsRefresh();
            completion = store.AutomaticStatisticsRefreshCompletion;
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<TimeoutException>(() => db.Tables.RenameTable("blocked_rename", "retried_table"));
            Assert.False(completion.IsCompleted);
            Assert.Same(store, db.Tables.Open("blocked_rename"));
            Assert.NotNull(db.Tables.Catalog.TryGet("blocked_rename"));
            Assert.Null(db.Tables.Catalog.TryGet("retried_table"));
            Assert.NotNull(store.GetByPrimaryKey(new object?[] { 1L }));
            store.Insert(new object?[] { 2_001L, "after_timeout" });
            Assert.Equal("after_timeout", store.GetByPrimaryKey(new object?[] { 2_001L })!.Values[1]);

            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("cancelled", store.AutomaticStatisticsRefreshStatus.State);
            db.Tables.RenameTable("blocked_rename", "retried_table");
            Assert.Null(db.Tables.Catalog.TryGet("blocked_rename"));
            TableStore renamed = db.Tables.Open("retried_table");
            Assert.Equal(2_001, renamed.RowCount);
            Assert.Equal("after_timeout", renamed.GetByPrimaryKey(new object?[] { 2_001L })!.Values[1]);
        }
        finally
        {
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshStatistics_OlderBackgroundSample_DoesNotReplaceNewerAnalyze(bool writeDuringSampling)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "newer_analyze");
        store.InsertMany(Enumerable.Range(2_001, 3_000)
            .Select(id => (IReadOnlyList<object?>)new object?[] { (long)id, "common" })
            .ToArray());
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int maintenanceThread = 0;
        CancellationToken maintenanceToken = default;
        store.AutomaticStatisticsRefreshStartedTestHook = token =>
        {
            maintenanceThread = Environment.CurrentManagedThreadId;
            maintenanceToken = token;
        };
        store.ReadSnapshotAcquiredTestHook = () =>
        {
            if (Environment.CurrentManagedThreadId != maintenanceThread)
                return;
            sampled.SetResult();
            release.Wait(maintenanceToken);
        };

        try
        {
            _ = store.TryAutomaticStatisticsRefresh();
            await sampled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (writeDuringSampling)
                store.Insert(new object?[] { 5_001L, "rare" });
            TableStatistics explicitStatistics = store.RefreshStatistics();
            Assert.Equal(writeDuringSampling ? 5_001 : 5_000, explicitStatistics.RowCount);
            Assert.True(explicitStatistics.IsComplete);
            Task completion = store.AutomaticStatisticsRefreshCompletion;
            release.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(explicitStatistics, store.Statistics);
            Assert.False(store.AreStatisticsStale);
        }
        finally
        {
            release.Set();
            await store.AutomaticStatisticsRefreshCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static TableStore CreateRefreshTable(Tsdb db, string name)
    {
        SqlExecutor.Execute(db, $"CREATE TABLE {name} (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, $"CREATE INDEX ix_{name}_status ON {name} (status)");
        TableStore store = db.Tables.Open(name);
        InsertSkewedRows(store);
        return store;
    }

    [Fact]
    public void Analyze_CancelledAfterSnapshot_DoesNotPublishStatistics()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        TableStore store = CreateRefreshTable(db, "cancel_analyze");
        using var cancellation = new CancellationTokenSource();
        store.ReadSnapshotAcquiredTestHook = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            SqlParser.Parse("ANALYZE cancel_analyze"),
            controlPlane: null,
            transaction: null,
            new SqlExecutionOptions { CancellationToken = cancellation.Token }));

        Assert.Null(store.Statistics);
        store.ReadSnapshotAcquiredTestHook = null;
        Assert.Equal(2_000, store.RefreshStatistics().RowCount);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void InsertSelectiveRows(Tsdb db, string tableName)
    {
        string values = string.Join(", ", Enumerable.Range(1, 100)
            .Select(id => $"({id}, '{(id == 1 ? "ready" : "blocked")}')"));
        SqlExecutor.Execute(db, $"INSERT INTO {tableName} (id, status) VALUES {values}");
    }

    private static void InsertSkewedRows(TableStore store, bool includeHost = false)
    {
        store.InsertMany(Enumerable.Range(1, 2_000)
            .Select(id => (IReadOnlyList<object?>)(includeHost
                ? new object?[] { (long)id, $"host-{id}", id == 1 ? "rare" : "common" }
                : new object?[] { (long)id, id == 1 ? "rare" : "common" }))
            .ToArray());
    }

    private static IReadOnlyDictionary<string, object?> Explain(Tsdb db, string sql)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + sql));
        return result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
    }
}
