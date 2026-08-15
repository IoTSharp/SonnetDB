using SonnetDB.Engine;
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
