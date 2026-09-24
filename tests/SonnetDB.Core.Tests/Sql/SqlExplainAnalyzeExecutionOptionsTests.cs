using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 EXPLAIN ANALYZE 复用 SQL 根调用的取消令牌和绝对截止时间。
/// </summary>
public sealed class SqlExplainAnalyzeExecutionOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-explain-options-" + Guid.NewGuid().ToString("N"));

    public SqlExplainAnalyzeExecutionOptionsTests()
        => Directory.CreateDirectory(_root);

    /// <summary>已取消的根调用必须在 EXPLAIN ANALYZE 触发数据执行前返回稳定取消错误。</summary>
    [Fact]
    public void ExplainAnalyze_PreCancelledOptions_ReturnsStableCancellationCode()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var exception = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(
                db,
                databaseName: "options",
                sql: "EXPLAIN ANALYZE SELECT usage FROM cpu WHERE host = 'h1'",
                parameters: null,
                controlPlane: null,
                options: new SqlExecutionOptions { CancellationToken = cancelled.Token }));

        Assert.Equal(RoutineErrorCodes.Cancelled, exception.Code);
    }

    /// <summary>已过期的绝对截止时间必须转换为同一稳定取消错误，并且不能执行查询主体。</summary>
    [Fact]
    public void ExplainAnalyze_ExpiredDeadline_ReturnsStableCancellationCode()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");

        var exception = Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(
                db,
                databaseName: "options",
                sql: "EXPLAIN ANALYZE SELECT usage FROM cpu WHERE host = 'h1'",
                parameters: null,
                controlPlane: null,
                options: new SqlExecutionOptions
                {
                    DeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(-1),
                }));

        Assert.Equal(RoutineErrorCodes.Cancelled, exception.Code);
        Assert.IsType<TimeoutException>(exception.InnerException);
    }

    /// <summary>普通 EXPLAIN 只读取规划元数据，不因为提供截止时间而产生实际指标。</summary>
    [Fact]
    public void Explain_WithFutureDeadline_RemainsEstimateOnly()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(
                db,
                databaseName: "options",
                sql: "EXPLAIN SELECT usage FROM cpu WHERE host = 'h1'",
                parameters: null,
                controlPlane: null,
                options: new SqlExecutionOptions
                {
                    DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                }));

        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        Assert.Null(values["actual_rows"]);
        Assert.Null(values["actual_execution_ms"]);
    }

    /// <summary>EXPLAIN ANALYZE 必须把实际执行证据写入根调用提供的诊断收集器。</summary>
    [Fact]
    public void ExplainAnalyze_ReusesRootDiagnosticsMetrics()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE readings (id INT, status STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO readings (id, status) VALUES (1, 'ready'), (2, 'cold')");
        var metrics = new SqlExecutionMetrics();

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(
                db,
                databaseName: "options",
                sql: "EXPLAIN ANALYZE SELECT id FROM readings WHERE status = 'ready'",
                parameters: null,
                controlPlane: null,
                options: new SqlExecutionOptions { Metrics = metrics }));

        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        var snapshot = metrics.Complete();
        Assert.Equal(1L, Convert.ToInt64(values["actual_rows"]));
        Assert.True(snapshot.ExecutionElapsedMs >= 0);
        Assert.Equal("table_scan", snapshot.AccessPath);
        Assert.Equal(2L, snapshot.CandidateRows);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 测试数据库可能仍持有短暂文件句柄；测试结果不依赖清理重试。
        }
    }
}
