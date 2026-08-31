using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>M41 #368 单语句访问路径、读写、分配与等待证据测试。</summary>
public sealed class SqlExecutionMetricsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-m41-sql-metrics-" + Guid.NewGuid().ToString("N"));

    /// <summary>清理测试数据库。</summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>二级索引查询应报告真实访问路径及有界候选/检查行数。</summary>
    [Fact]
    public void Execute_SecondaryIndexWithResidual_ReportsActualAccessEvidence()
    {
        using var db = CreateDatabase();
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: "m41_metrics",
            "SELECT id FROM audits WHERE idempotency_key = 'key-2' AND status = 'ready'",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(2L, Assert.Single(result.Rows)[0]);
        Assert.Equal("secondary_index", snapshot.AccessPath);
        Assert.Equal("ux_audits_idempotency", snapshot.IndexName);
        Assert.Null(snapshot.FallbackReason);
        Assert.Equal(1, snapshot.CandidateRows);
        Assert.Equal(1, snapshot.ExaminedRows);
        Assert.Equal(1, snapshot.LogicalReads);
        Assert.True(snapshot.ExecutionElapsedMs >= 0);
        Assert.True(snapshot.AllocatedBytes >= 0);
    }

    /// <summary>无可索引谓词时应明确报告扫描回退，且检查量与表规模一致。</summary>
    [Fact]
    public void Execute_UnindexedPredicate_ReportsScanFallbackAndAmplification()
    {
        using var db = CreateDatabase();
        var metrics = new SqlExecutionMetrics();

        _ = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: "m41_metrics",
            "SELECT id FROM audits WHERE status = 'missing'",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal("table_scan", snapshot.AccessPath);
        Assert.Equal("no_sargable_predicate", snapshot.FallbackReason);
        Assert.Equal(3, snapshot.CandidateRows);
        Assert.Equal(3, snapshot.ExaminedRows);
        Assert.Equal(3, snapshot.LogicalReads);
    }

    /// <summary>主键 UPDATE 应报告点读路径及单行候选，不再表现为无证据的高分配写入。</summary>
    [Fact]
    public void Execute_PrimaryKeyUpdate_ReportsPointMutationEvidence()
    {
        using var db = CreateDatabase();
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: "m41_metrics",
            "UPDATE audits AS a SET status = 'done' WHERE a.id = 2 AND a.status = 'ready'",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(1, result.RowsAffected);
        Assert.Equal("primary_key", snapshot.AccessPath);
        Assert.Equal("primary", snapshot.IndexName);
        Assert.Equal(1, snapshot.CandidateRows);
        Assert.Equal(1, snapshot.ExaminedRows);
        Assert.Equal(1, snapshot.LogicalReads);
    }

    /// <summary>无索引 DELETE 应明确报告候选扫描放大，便于从 Top Query 定位写路径。</summary>
    [Fact]
    public void Execute_UnindexedDelete_ReportsMutationScanEvidence()
    {
        using var db = CreateDatabase();
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: "m41_metrics",
            "DELETE FROM audits WHERE status = 'missing'",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics }));
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(0, result.SeriesAffected);
        Assert.Equal("table_scan", snapshot.AccessPath);
        Assert.Equal("no_sargable_predicate", snapshot.FallbackReason);
        Assert.Equal(3, snapshot.CandidateRows);
        Assert.Equal(3, snapshot.ExaminedRows);
        Assert.Equal(3, snapshot.LogicalReads);
    }

    /// <summary>并发物理读取应通过原子累计完整保留读取次数和 payload 字节数。</summary>
    [Fact]
    public void RecordPhysicalRead_ConcurrentCalls_AccumulatesAllValues()
    {
        const int workerCount = 8;
        const int readsPerWorker = 10_000;
        const int bytesPerRead = 17;
        var metrics = new SqlExecutionMetrics();

        Parallel.For(0, workerCount, _ =>
        {
            for (var index = 0; index < readsPerWorker; index++)
                metrics.RecordPhysicalRead(bytesPerRead);
        });
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        long expectedReads = workerCount * readsPerWorker;
        Assert.Equal(expectedReads, snapshot.PhysicalReads);
        Assert.Equal(expectedReads * bytesPerRead, snapshot.PhysicalReadBytes);
    }

    /// <summary>并发读取尚未全部结束时冻结指标，也必须返回来自同一稳定点的次数与字节数。</summary>
    [Fact]
    public async Task Complete_ConcurrentPhysicalReads_ReturnsConsistentPair()
    {
        const int workerCount = 8;
        const int readsPerWorker = 20_000;
        const int bytesPerRead = 19;
        var metrics = new SqlExecutionMetrics();
        using var firstReadsCompleted = new CountdownEvent(workerCount);

        Task[] writers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                metrics.RecordPhysicalRead(bytesPerRead);
                firstReadsCompleted.Signal();
                for (var index = 1; index < readsPerWorker; index++)
                    metrics.RecordPhysicalRead(bytesPerRead);
            }))
            .ToArray();

        firstReadsCompleted.Wait();
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        await Task.WhenAll(writers);

        Assert.True(snapshot.PhysicalReads >= workerCount);
        Assert.Equal(snapshot.PhysicalReads * bytesPerRead, snapshot.PhysicalReadBytes);
    }

    /// <summary>溢出回滚与成功 writer 交错时，失败样本不得回绕字节数或污染读取次数。</summary>
    [Fact]
    public void RecordPhysicalRead_ByteCountOverflow_FailsWithoutMutatingTotals()
    {
        const int workerCount = 8;
        const int callsPerWorker = 2_000;
        var metrics = new SqlExecutionMetrics();
        metrics.RecordPhysicalRead(long.MaxValue);

        Parallel.For(0, workerCount, worker =>
        {
            for (var index = 0; index < callsPerWorker; index++)
            {
                if ((worker & 1) == 0)
                    metrics.RecordPhysicalRead(0);
                else
                    Assert.Throws<OverflowException>(() => metrics.RecordPhysicalRead(1));
            }
        });
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        long successfulReads = 1L + (workerCount / 2L * callsPerWorker);
        Assert.Equal(successfulReads, snapshot.PhysicalReads);
        Assert.Equal(long.MaxValue, snapshot.PhysicalReadBytes);
    }

    /// <summary>事务已有同表写入时，完整复合主键 UPDATE 仍应只合并目标键而不扫描全表。</summary>
    [Fact]
    public void QueueUpdate_CompositePrimaryKeyWithBufferedWrite_UsesPointLookup()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE attribute_latest (
                catalog INT,
                device_id STRING,
                key_name STRING,
                value INT,
                PRIMARY KEY (catalog, device_id, key_name))
            """);
        var store = db.Tables.Open("attribute_latest");
        for (var index = 0; index < 8_192; index++)
            store.Upsert([1L, $"device-{index:D5}", "last_activity", 0L]);

        var transaction = new SqlTransactionContext();
        using var transactionScope = SqlTransactionContext.EnterScope(transaction);
        TableSqlExecutor.QueueUpdate(
            transaction,
            db,
            Assert.IsType<UpdateStatement>(SqlParser.Parse("""
                UPDATE attribute_latest
                SET value = 1
                WHERE catalog = 1 AND device_id = 'device-00001' AND key_name = 'last_activity'
                """)));

        var targetUpdate = Assert.IsType<UpdateStatement>(SqlParser.Parse("""
            UPDATE attribute_latest
            SET value = 2
            WHERE catalog = 1 AND device_id = 'device-04096' AND key_name = 'last_activity'
            """));
        long scansBefore = store.FullScanCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;
        var metrics = new SqlExecutionMetrics();
        RowsAffectedExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = TableSqlExecutor.QueueUpdate(transaction, db, targetUpdate);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(lookupsBefore + 1, store.PrimaryKeyLookupCount);
        Assert.Equal("primary_key", snapshot.AccessPath);
        Assert.Equal("primary", snapshot.IndexName);
        Assert.Null(snapshot.FallbackReason);
        Assert.Equal(1, snapshot.CandidateRows);
        Assert.Equal(1, snapshot.ExaminedRows);
        Assert.True(
            snapshot.AllocatedBytes < 1024 * 1024,
            $"事务内复合主键 UPDATE 分配了 {snapshot.AllocatedBytes:N0} bytes。");

        Assert.Equal(2, TableSqlExecutor.CommitTransaction(db, transaction).RowsAffected);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT value FROM attribute_latest
            WHERE catalog = 1 AND device_id = 'device-04096' AND key_name = 'last_activity'
            """));
        Assert.Equal(2L, Assert.Single(selected.Rows)[0]);
    }

    /// <summary>事务内同键 UPDATE 后按复合主键 DELETE 应合并目标 mutation，不得扫描全表。</summary>
    [Fact]
    public void QueueDelete_CompositePrimaryKeyAfterBufferedUpdate_UsesPointLookup()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE delete_latest (
                catalog INT,
                device_id STRING,
                key_name STRING,
                value INT,
                PRIMARY KEY (catalog, device_id, key_name))
            """);
        SqlExecutor.Execute(db, """
            INSERT INTO delete_latest (catalog, device_id, key_name, value)
            VALUES (1, 'device-00001', 'last_activity', 0)
            """);
        var schema = db.Tables.Catalog.TryGet("delete_latest")!;
        var store = db.Tables.Open(schema.Name);
        var transaction = new SqlTransactionContext();
        using var transactionScope = SqlTransactionContext.EnterScope(transaction);
        TableSqlExecutor.QueueUpdate(
            transaction,
            db,
            Assert.IsType<UpdateStatement>(SqlParser.Parse("""
                UPDATE delete_latest
                SET value = 1
                WHERE catalog = 1 AND device_id = 'device-00001' AND key_name = 'last_activity'
                """)));

        var targetDelete = Assert.IsType<DeleteStatement>(SqlParser.Parse("""
            DELETE FROM delete_latest
            WHERE catalog = 1 AND device_id = 'device-00001' AND key_name = 'last_activity'
            """));
        long scansBefore = store.FullScanCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;
        var metrics = new SqlExecutionMetrics();
        RowsAffectedExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = TableSqlExecutor.QueueDelete(transaction, db, targetDelete, schema);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(1, result.RowsAffected);
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(lookupsBefore + 1, store.PrimaryKeyLookupCount);
        Assert.Equal("primary_key", snapshot.AccessPath);
        Assert.Equal("primary", snapshot.IndexName);
        Assert.Null(snapshot.FallbackReason);
        Assert.Equal(1, snapshot.CandidateRows);
        Assert.Equal(1, snapshot.ExaminedRows);

        Assert.Equal(1, TableSqlExecutor.CommitTransaction(db, transaction).RowsAffected);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT value FROM delete_latest
            WHERE catalog = 1 AND device_id = 'device-00001' AND key_name = 'last_activity'
            """));
        Assert.Empty(selected.Rows);
    }

    /// <summary>事务内复合主键 EXISTS 应读取缓冲后的目标值，并保持单次主键点查。</summary>
    [Fact]
    public void Exists_CompositePrimaryKeyWithBufferedUpdate_UsesPointLookup()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE exists_latest (
                catalog INT,
                device_id STRING,
                key_name STRING,
                value INT,
                PRIMARY KEY (catalog, device_id, key_name))
            """);
        SqlExecutor.Execute(db, """
            INSERT INTO exists_latest (catalog, device_id, key_name, value)
            VALUES (1, 'device-00001', 'last_activity', 0)
            """);
        var store = db.Tables.Open("exists_latest");
        var transaction = new SqlTransactionContext();
        using var transactionScope = SqlTransactionContext.EnterScope(transaction);
        TableSqlExecutor.QueueUpdate(
            transaction,
            db,
            Assert.IsType<UpdateStatement>(SqlParser.Parse("""
                UPDATE exists_latest
                SET value = 1
                WHERE catalog = 1 AND device_id = 'device-00001' AND key_name = 'last_activity'
                """)));

        long scansBefore = store.FullScanCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;
        var metrics = new RelationalSelectExecutionMetrics();
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT EXISTS (
                SELECT 1 FROM exists_latest
                WHERE catalog = 1
                    AND device_id = 'device-00001'
                    AND key_name = 'last_activity'
                    AND value = 1)
            """));
        var result = RelationalSelectExecutor.Execute(db, statement, metrics);

        Assert.True(Assert.IsType<bool>(Assert.Single(result.Rows)[0]));
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Equal(lookupsBefore + 1, store.PrimaryKeyLookupCount);
        Assert.Equal(1, metrics.ExistsRowsExamined);
        Assert.Equal(1, metrics.ExistsEarlyExitCount);
        Assert.Equal("primary_key", metrics.LastExistsAccessPath);
        Assert.Equal("primary", metrics.LastExistsIndexName);
        Assert.Null(metrics.LastExistsFallbackReason);
    }

    /// <summary>关系写入应把实际 WAL record 写入及 fsync 等待归属到当前 SQL。</summary>
    [Fact]
    public void Execute_Insert_ReportsPhysicalWalWrites()
    {
        using var db = CreateDatabase();
        var metrics = new SqlExecutionMetrics();

        _ = SqlExecutor.Execute(
            db,
            databaseName: "m41_metrics",
            "INSERT INTO audits (id, idempotency_key, status) VALUES (4, 'key-4', 'ready')",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { Metrics = metrics });
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.True(snapshot.PhysicalWrites >= 1);
        Assert.True(snapshot.PhysicalWriteBytes > 0);
        Assert.True(snapshot.KvLockWaitMs >= 0);
    }

    private Tsdb CreateDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE audits (id INT, idempotency_key STRING, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE UNIQUE INDEX ux_audits_idempotency ON audits (idempotency_key)");
        SqlExecutor.Execute(db, "INSERT INTO audits (id, idempotency_key, status) VALUES "
            + "(1, 'key-1', 'done'), (2, 'key-2', 'ready'), (3, 'key-3', 'ready')");
        return db;
    }
}
