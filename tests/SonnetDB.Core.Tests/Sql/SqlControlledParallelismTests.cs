using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>M41 #380 受控并行、预算门控、取消和运行时反馈回归测试。</summary>
public sealed class SqlControlledParallelismTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-m41-parallel-" + Guid.NewGuid().ToString("N"));

    /// <summary>清理测试数据库目录。</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>measurement scan 并行结果必须与串行结果逐行一致，并记录估算反馈。</summary>
    [Fact]
    public void Execute_ParallelMeasurementScan_MatchesSerialAndRecordsFeedback()
    {
        using Tsdb database = CreateMeasurementDatabase();
        const string sql = "SELECT time, host, value FROM cpu";
        var parallelMetrics = new SqlExecutionMetrics();
        var parallel = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: "m41",
            sql,
            parameters: null,
            controlPlane: null,
            ParallelOptions(parallelMetrics)));

        var serial = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: "m41",
            sql,
            parameters: null,
            controlPlane: null,
            ParallelOptions(metrics: null) with { EnableParallelism = false }));

        Assert.Equal(RowKeys(serial), RowKeys(parallel));
        SqlExecutionMetricsSnapshot snapshot = parallelMetrics.Complete();
        Assert.True(snapshot.ParallelismEnabled);
        Assert.Equal(2, snapshot.ParallelWorkerCount);
        Assert.Equal(4, snapshot.ParallelCompletedItems);
        Assert.InRange(database.SqlParallelCoordinator.MaxObservedWorkers, 1, 2);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);

        string fingerprint = SqlStatementFingerprint.Create(SqlParser.Parse(sql));
        Assert.True(database.SqlRuntimeFeedback.TryGet(fingerprint, out SqlRuntimeFeedbackSnapshot feedback));
        Assert.True(feedback.SampleCount >= 1);
        Assert.True(feedback.EstimatedRows > 0);
        Assert.True(feedback.ActualRows > 0);
        Assert.True(feedback.ActualToEstimatedRatio > 0);
    }

    /// <summary>缺少线程安全合同的用户函数必须保持串行调用顺序。</summary>
    [Fact]
    public void Execute_ParallelMeasurementScan_WithUserFunction_FallsBackSerial()
    {
        using Tsdb database = CreateMeasurementDatabase();
        database.Functions.RegisterScalar(
            "observe",
            static args => args[0],
            minArgumentCount: 1,
            maxArgumentCount: 1);
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: "m41",
            "SELECT time, observe(value) FROM cpu",
            parameters: null,
            controlPlane: null,
            ParallelOptions(metrics)));

        Assert.Equal(64, result.Rows.Count);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.False(snapshot.ParallelismEnabled);
        Assert.Contains("user_defined_function", snapshot.ParallelFallbackReason);
    }

    /// <summary>按 series 并行的 legacy aggregate 必须保持串行结果和数值语义。</summary>
    [Fact]
    public void Execute_ParallelAggregate_MatchesSerialResult()
    {
        using Tsdb database = CreateMeasurementDatabase();
        const string sql = "SELECT sum(value) AS total FROM cpu";
        var parallelMetrics = new SqlExecutionMetrics();
        var parallel = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: "m41",
            sql,
            parameters: null,
            controlPlane: null,
            ParallelOptions(parallelMetrics)));
        var serial = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: "m41",
            sql,
            parameters: null,
            controlPlane: null,
            ParallelOptions(metrics: null) with { EnableParallelism = false }));

        Assert.Equal(serial.Rows.Count, parallel.Rows.Count);
        Assert.Equal(serial.Rows[0][0], parallel.Rows[0][0]);
        SqlExecutionMetricsSnapshot snapshot = parallelMetrics.Complete();
        Assert.True(snapshot.ParallelismEnabled);
        Assert.Equal("aggregate_scan", snapshot.ParallelOperator);
    }

    /// <summary>物化子查询 probe 的 Hash JOIN 并行必须保持 LEFT JOIN 的 NULL 扩展和输入顺序。</summary>
    [Fact]
    public void Execute_ParallelHashJoin_PreservesLeftJoinNullsAndOrder()
    {
        using Tsdb database = OpenDatabase();
        SqlExecutor.Execute(database, "CREATE TABLE join_left (id INT, join_key INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE join_right (id INT, join_key INT NULL, PRIMARY KEY (id))");
        database.Tables.Open("join_left").InsertMany(
            Enumerable.Range(0, 16).Select(static id => (IReadOnlyList<object?>)[(long)id, (long)(id % 8)]).ToArray());
        database.Tables.Open("join_right").InsertMany(
            Enumerable.Range(0, 8).Select(static id => (IReadOnlyList<object?>)[(long)(100 + id), (long)id]).ToArray());

        const string sql = """
            SELECT l.id, r.id
            FROM (SELECT id, join_key FROM join_left) l
            LEFT JOIN (SELECT id, join_key FROM join_right) r ON l.join_key = r.join_key
            ORDER BY l.id
            """;
        var metrics = new SqlExecutionMetrics();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: "m41",
            sql,
            parameters: null,
            controlPlane: null,
            ParallelOptions(metrics)));

        Assert.Equal(16, result.Rows.Count);
        Assert.Equal(Enumerable.Range(0, 16).Select(static id => (long)id), result.Rows.Select(static row => (long)row[0]!));
        Assert.All(result.Rows, static row => Assert.NotNull(row[1]));
        Assert.True(metrics.Complete().ParallelismEnabled);
        Assert.Equal("hash_join", metrics.Complete().ParallelOperator);
    }

    /// <summary>并行 worker 不得突破数据库上限，输出索引必须保持稳定顺序。</summary>
    [Fact]
    public void MapOrdered_RespectsWorkerUpperBoundAndStableOrder()
    {
        using Tsdb database = OpenDatabase();
        var metrics = new SqlExecutionMetrics();
        var options = ParallelOptions(metrics) with { ParallelismMinRows = 1 };
        using var resources = SqlQueryResources.EnterRoot(database, options);
        using var telemetry = SqlExecutionTelemetry.Enter(metrics);
        int active = 0;
        int observed = 0;

        IReadOnlyList<int> result = SqlParallelExecution.MapOrdered(
            Enumerable.Range(0, 64).ToArray(),
            value =>
            {
                int current = Interlocked.Increment(ref active);
                while (current > Volatile.Read(ref observed)
                    && Interlocked.CompareExchange(ref observed, current, Volatile.Read(ref observed)) != Volatile.Read(ref observed))
                {
                }
                Thread.SpinWait(20_000);
                Interlocked.Decrement(ref active);
                return value;
            },
            "unit_scan",
            estimatedRows: 64);

        Assert.Equal(Enumerable.Range(0, 64), result);
        Assert.InRange(observed, 1, 2);
        Assert.InRange(database.SqlParallelCoordinator.MaxObservedWorkers, 1, 2);
    }

    /// <summary>查询预算不足以容纳两个 worker 时应回退串行且释放全部预算。</summary>
    [Fact]
    public void MapOrdered_WhenQueryBudgetCompetes_FallsBackWithoutLeakingBudget()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SqlMemory = new SqlMemoryOptions
            {
                QueryLimitBytes = 1_024,
                GlobalLimitBytes = 1_024,
                MaxParallelWorkers = 4,
                ParallelismMinRows = 1,
                ParallelWorkerMemoryBytes = 1_024,
            },
        });
        var metrics = new SqlExecutionMetrics();
        var options = new SqlExecutionOptions
        {
            Metrics = metrics,
            MaxDegreeOfParallelism = 4,
            ParallelismMinRows = 1,
        };
        using (SqlQueryResources.EnterRoot(database, options))
        using (SqlExecutionTelemetry.Enter(metrics))
        {
            IReadOnlyList<int> result = SqlParallelExecution.MapOrdered(
                Enumerable.Range(0, 8).ToArray(), static value => value, "budget_scan", 8);
            Assert.Equal(Enumerable.Range(0, 8), result);
        }

        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.False(snapshot.ParallelismEnabled);
        Assert.Contains("parallel_permit_or_memory_unavailable", snapshot.ParallelFallbackReason);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        Assert.Equal(0, database.SqlParallelCoordinator.ActiveWorkers);
    }

    /// <summary>取消并行查询后必须释放 worker 槽位和查询级/全局预算。</summary>
    [Fact]
    public void MapOrdered_WhenCancelled_ReleasesWorkersAndBudget()
    {
        using Tsdb database = OpenDatabase();
        using var cancelled = new CancellationTokenSource();
        var options = ParallelOptions(metrics: null) with
        {
            CancellationToken = cancelled.Token,
            ParallelismMinRows = 1,
        };
        using var resources = SqlQueryResources.EnterRoot(database, options);

        Assert.ThrowsAny<OperationCanceledException>(() => SqlParallelExecution.MapOrdered(
            Enumerable.Range(0, 1_000).ToArray(),
            value =>
            {
                if (value == 0)
                    cancelled.Cancel();
                Thread.SpinWait(10_000);
                return value;
            },
            "cancel_scan",
            1_000));

        Assert.Equal(0, database.SqlParallelCoordinator.ActiveWorkers);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>worker 异常必须保持串行异常类型，并释放所有并行资源。</summary>
    [Fact]
    public void MapOrdered_WhenSelectorFails_ReleasesWorkersAndBudget()
    {
        using Tsdb database = OpenDatabase();
        var options = ParallelOptions(metrics: null) with { ParallelismMinRows = 1 };
        using var resources = SqlQueryResources.EnterRoot(database, options);

        Assert.Throws<InvalidOperationException>(() => SqlParallelExecution.MapOrdered(
            Enumerable.Range(0, 64).ToArray(),
            static value => value == 3
                ? throw new InvalidOperationException("selector failure")
                : value,
            "exception_scan",
            64));

        Assert.Equal(0, database.SqlParallelCoordinator.ActiveWorkers);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>存在事务 ambient 时必须禁用并行，避免破坏 read-your-writes 语义。</summary>
    [Fact]
    public void MapOrdered_WithTransactionOverlay_DisablesParallelism()
    {
        using Tsdb database = OpenDatabase();
        var metrics = new SqlExecutionMetrics();
        var options = ParallelOptions(metrics) with { ParallelismMinRows = 1 };
        using var resources = SqlQueryResources.EnterRoot(database, options);
        using var telemetry = SqlExecutionTelemetry.Enter(metrics);
        using var transaction = SqlTransactionContext.EnterScope(new SqlTransactionContext());

        IReadOnlyList<int> result = SqlParallelExecution.MapOrdered(
            Enumerable.Range(0, 8).ToArray(), static value => value, "transaction_scan", 8);

        Assert.Equal(Enumerable.Range(0, 8), result);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.False(snapshot.ParallelismEnabled);
        Assert.Contains("benefit_or_resource_gate", snapshot.ParallelFallbackReason);
    }

    /// <summary>反馈 fingerprint 只描述 AST 形状，不携带字面量值，并区分 DISTINCT/HAVING/UNION 语义。</summary>
    [Fact]
    public void Fingerprint_DistinguishesShape_WithoutLiteralValues()
    {
        string first = SqlStatementFingerprint.Create(
            SqlParser.Parse("SELECT value FROM cpu WHERE value = 1"));
        string sameShapeDifferentLiteral = SqlStatementFingerprint.Create(
            SqlParser.Parse("SELECT value FROM cpu WHERE value = 999"));
        string distinct = SqlStatementFingerprint.Create(
            SqlParser.Parse("SELECT DISTINCT value FROM cpu WHERE value = 1"));
        string having = SqlStatementFingerprint.Create(
            SqlParser.Parse("SELECT value, count(*) FROM cpu GROUP BY value HAVING count(*) > 1"));
        string union = SqlStatementFingerprint.Create(
            SqlParser.Parse("SELECT value FROM cpu UNION SELECT value FROM cpu"));

        Assert.Equal(first, sameShapeDifferentLiteral);
        Assert.NotEqual(first, distinct);
        Assert.NotEqual(first, having);
        Assert.NotEqual(first, union);
    }

    private Tsdb CreateMeasurementDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SqlMemory = new SqlMemoryOptions
            {
                MaxParallelWorkers = 2,
                ParallelismMinRows = 1,
                ParallelWorkerMemoryBytes = 1_024,
                QueryLimitBytes = 128 * 1024,
                GlobalLimitBytes = 256 * 1024,
            },
        });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT cpu (host TAG, value FIELD INT)");
        for (int series = 0; series < 4; series++)
        {
            for (int point = 0; point < 16; point++)
            {
                SqlExecutor.Execute(
                    database,
                    $"INSERT INTO cpu (time, host, value) VALUES ({series * 10_000L + point}, 'h{series}', {point})");
            }
        }

        return database;
    }

    private Tsdb OpenDatabase()
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SqlMemory = new SqlMemoryOptions
            {
                MaxParallelWorkers = 2,
                ParallelismMinRows = 1,
                ParallelWorkerMemoryBytes = 1_024,
                QueryLimitBytes = 128 * 1024,
                GlobalLimitBytes = 256 * 1024,
            },
        });

    private static SqlExecutionOptions ParallelOptions(SqlExecutionMetrics? metrics)
        => new()
        {
            Metrics = metrics,
            MaxDegreeOfParallelism = 2,
            ParallelismMinRows = 1,
        };

    private static IEnumerable<string> RowKeys(SelectExecutionResult result)
        => result.Rows.Select(static row => string.Join("|", row.Select(value => value?.ToString() ?? "NULL")));
}
