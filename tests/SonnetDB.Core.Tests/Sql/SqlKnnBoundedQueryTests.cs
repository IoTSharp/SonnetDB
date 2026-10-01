using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>M42 KNN 真实 SQL 入口的预算、受控并行和删除后补足回归。</summary>
public sealed class SqlKnnBoundedQueryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-m42-knn-sql-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    /// <summary>释放本测试拥有的取消计时器并删除数据库。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        string resolved = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(resolved), ignoreCase: true);
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    /// <summary>串行与受控并行必须产生相同并列结果，结束后全部 worker 和预算归还。</summary>
    [Fact]
    public void Knn_WithControlledParallelism_MatchesSerialTiesAndReleasesResources()
    {
        using var database = OpenDatabase();
        for (int series = 0; series < 4; series++)
        {
            for (int point = 0; point < 16; point++)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                SqlExecutor.Execute(database,
                    $"INSERT INTO docs (time, source, embedding) VALUES ({series * 100 + point}, 's{series}', [1,0,0])");
            }
        }

        const string sql = "SELECT * FROM knn(docs, embedding, [1,0,0], 5)";
        var metrics = new SqlExecutionMetrics();
        var parallel = Execute(database, sql, new SqlExecutionOptions
        {
            CancellationToken = _deadline.Token,
            Metrics = metrics,
            MaxDegreeOfParallelism = 2,
            ParallelismMinRows = 1,
        });
        var serial = Execute(database, sql, new SqlExecutionOptions
        {
            CancellationToken = _deadline.Token,
            EnableParallelism = false,
        });

        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, parallel.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(serial.Rows.Select(static row => (long)row[0]!), parallel.Rows.Select(static row => (long)row[0]!));
        var snapshot = metrics.Complete();
        Assert.Equal("measurement_knn", snapshot.ParallelOperator);
        Assert.True(snapshot.ParallelismEnabled);
        Assert.Equal(2, snapshot.ParallelWorkerCount);
        Assert.Equal(4, snapshot.ParallelCompletedItems);
        Assert.InRange(database.SqlParallelCoordinator.MaxObservedWorkers, 1, 2);
        Assert.Equal(0, database.SqlParallelCoordinator.ActiveWorkers);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>真实 SQL 候选预算耗尽必须显式失败，下一条较小 K 查询仍能成功。</summary>
    [Fact]
    public void Knn_WithInsufficientCandidateBudget_FailsThenAllowsSmallerQuery()
    {
        using var database = OpenDatabase();
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000,'a',[1,0,0]), (2000,'a',[0,1,0]), (3000,'a',[0,0,1])");
        var options = new SqlExecutionOptions
        {
            CancellationToken = _deadline.Token,
            EnableParallelism = false,
            BlockingOperatorMemoryLimitBytes = 256,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => Execute(database,
            "SELECT * FROM knn(docs, embedding, [1,0,0], 3)", options));
        Assert.Contains("KNN", exception.Message);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        Assert.Equal(1000L, Assert.Single(Execute(database,
            "SELECT * FROM knn(docs, embedding, [1,0,0], 1)", options).Rows)[0]);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>最优候选全部被删除后须先过滤墓碑，再填满 K；持久段和内存层都覆盖。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Knn_WithDeletedNearestCandidates_FillsKFromSurvivors(bool flush)
    {
        using var database = OpenDatabase();
        for (int point = 0; point < 32; point++)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            SqlExecutor.Execute(database,
                $"INSERT INTO docs (time, source, embedding) VALUES ({point}, 'a', [1,0,0])");
        }
        if (flush)
            Assert.NotNull(database.FlushNow());
        SqlExecutor.Execute(database, "DELETE FROM docs WHERE source = 'a' AND time < 30");
        var result = Execute(database, "SELECT * FROM knn(docs, embedding, [1,0,0], 2)",
            new SqlExecutionOptions { CancellationToken = _deadline.Token });
        Assert.Equal(new long[] { 30, 31 }, result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    private Tsdb OpenDatabase()
    {
        var database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SqlMemory = new SqlMemoryOptions
            {
                MaxParallelWorkers = 2,
                ParallelismMinRows = 1,
                ParallelWorkerMemoryBytes = 256,
                QueryLimitBytes = 8_192,
                GlobalLimitBytes = 16_384,
            },
        });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        return database;
    }

    private static SelectExecutionResult Execute(Tsdb database, string sql, SqlExecutionOptions options)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, databaseName: null, sql,
            parameters: null, controlPlane: null, options));
}
