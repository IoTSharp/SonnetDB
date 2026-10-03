using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 measurement KNN 候选和最终标量页的累计物化准入。</summary>
public sealed class KnnMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-knn-materialization-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));
    private const string Source = "FROM knn(docs, embedding, [0,0], 3, 'l2')";

    /// <summary>实际保留的候选和最终结果使用同一根行数预算。</summary>
    [Theory]
    [InlineData(9, true)]
    [InlineData(8, false)]
    public void Execute_CandidatesAndOutput_SharesCumulativeRowBudget(long rows, bool succeeds)
    {
        using var database = OpenPoints(3);
        var options = Options(rows);
        const string sql = "SELECT time, distance " + Source;

        if (succeeds)
            AssertEquivalent(Select(database, sql), Select(database, sql, options));
        else
        {
            var error = Assert.Throws<InvalidOperationException>(() => Select(database, sql, options));
            Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        }
        AssertReleased(database);
    }

    /// <summary>候选拒绝发生在进入保留堆前，已有候选保持不变。</summary>
    [Fact]
    public void CandidateSet_RowBudgetExhausted_RejectsBeforeEnqueue()
    {
        using var database = OpenPoints(0);
        using (var resources = SqlQueryResources.EnterRoot(database, Options(2)))
        using (var candidates = new KnnExecutor.BoundedCandidateSet(3, "embedding", database.Tombstones))
        {
            using (var budget = SqlRowRetentionBudget.EnterExecution(Options(2)))
            {
                candidates.Add(1, 1, 1);
                candidates.Add(2, 2, 1);
                Assert.Throws<InvalidOperationException>(() => candidates.Add(3, 3, 1));
                Assert.Equal(2, candidates.Count);
            }
            Assert.Equal([1L, 2L], candidates.GetResults().Select(static hit => hit.Timestamp));
        }
        AssertReleased(database);
    }

    /// <summary>替换候选也累计收费，超限不能先替换最差候选再抛错。</summary>
    [Fact]
    public void CandidateSet_ReplacementExceedsBudget_PreservesPreviouslyRetainedCandidate()
    {
        using var database = OpenPoints(0);
        using (var resources = SqlQueryResources.EnterRoot(database, Options(1)))
        using (var candidates = new KnnExecutor.BoundedCandidateSet(1, "embedding", database.Tombstones))
        {
            using (var budget = SqlRowRetentionBudget.EnterExecution(Options(1)))
            {
                candidates.Add(3, 1, 1);
                Assert.Throws<InvalidOperationException>(() => candidates.Add(2, 2, 1));
            }
            Assert.Equal(1L, Assert.Single(candidates.GetResults()).Timestamp);
        }
        AssertReleased(database);
    }

    /// <summary>有序 snapshot 必须在分配数组和消耗堆之前获得完整预算。</summary>
    [Fact]
    public void CandidateSet_SnapshotExceedsByteBudget_PreservesHeapBeforeAllocation()
    {
        using var database = OpenPoints(0);
        using (var resources = SqlQueryResources.EnterRoot(database, Options(2, 319)))
        using (var candidates = new KnnExecutor.BoundedCandidateSet(1, "embedding", database.Tombstones))
        {
            using (var budget = SqlRowRetentionBudget.EnterExecution(Options(2, 319)))
            {
                candidates.Add(1, 1, 1);
                Assert.Throws<InvalidOperationException>(() => candidates.GetResults());
                Assert.Equal(1, candidates.Count);
            }
            Assert.Equal(1L, Assert.Single(candidates.GetResults()).Timestamp);
        }
        AssertReleased(database);
    }

    /// <summary>执行期累计预算同时承担共享内存预留，候选不重复预留旧工作集。</summary>
    [Fact]
    public void CandidateSet_ExecutionBudget_ReservesEachRetentionStageOnce()
    {
        using var database = OpenPoints(0);
        var options = Options(2, 320);
        using (var resources = SqlQueryResources.EnterRoot(database, options))
        using (var budget = SqlRowRetentionBudget.EnterExecution(options))
        using (var candidates = new KnnExecutor.BoundedCandidateSet(1, "embedding", database.Tombstones,
            resources: SqlQueryResources.Current))
        {
            candidates.Add(1, 1, 1);
            Assert.Equal(160, database.SqlMemoryBudget.ReservedBytes);
            Assert.Equal(1L, Assert.Single(candidates.GetResults()).Timestamp);
            Assert.Equal(320, database.SqlMemoryBudget.ReservedBytes);
        }
        AssertReleased(database);
    }

    /// <summary>未启用累计预算时仍执行原有每候选 256 字节共享工作集准入。</summary>
    [Fact]
    public void CandidateSet_DefaultOptions_PreservesWorkingSetReservation()
    {
        using var database = OpenPoints(0);
        using (var resources = SqlQueryResources.EnterRoot(database, new SqlExecutionOptions()))
        using (var candidates = new KnnExecutor.BoundedCandidateSet(1, "embedding", database.Tombstones,
            resources: SqlQueryResources.Current))
        {
            candidates.Add(1, 1, 1);
            Assert.Equal(256, database.SqlMemoryBudget.ReservedBytes);
            Assert.Equal(1L, Assert.Single(candidates.GetResults()).Timestamp);
            Assert.Equal(256, database.SqlMemoryBudget.ReservedBytes);
        }
        AssertReleased(database);
    }

    /// <summary>被删除及不优于 Top-K 的扫描点不占累计保留行数。</summary>
    [Fact]
    public void CandidateSet_DeletedAndRejectedPoints_DoesNotChargeUnretainedCandidates()
    {
        using var database = OpenPoints(3);
        SqlExecutor.Execute(database, "DELETE FROM docs WHERE host = 'a' AND time = 0");
        ulong seriesId = Assert.Single(database.Catalog.Find("docs", null)).Id;
        using (var resources = SqlQueryResources.EnterRoot(database, Options(2)))
        using (var budget = SqlRowRetentionBudget.EnterExecution(Options(2)))
        using (var candidates = new KnnExecutor.BoundedCandidateSet(1, "embedding", database.Tombstones))
        {
            candidates.Add(0, 0, seriesId);
            candidates.Add(1, 1, seriesId);
            candidates.Add(2, 2, seriesId);
            Assert.Equal(1L, Assert.Single(candidates.GetResults()).Timestamp);
        }
        AssertReleased(database);
    }

    /// <summary>分页只保留完整 SQL 页，但扫描期 Top-K 候选仍进入累计预算。</summary>
    [Theory]
    [InlineData("LIMIT 1 OFFSET 1")]
    [InlineData("OFFSET 1 ROWS FETCH NEXT 1 ROWS ONLY")]
    public void Execute_Pagination_ChargesCandidatesAndOnlyRequestedOutput(string pagination)
    {
        using var database = OpenPoints(3);
        string sql = "SELECT time " + Source + " " + pagination;

        SelectExecutionResult result = Select(database, sql, Options(7, 1056));

        Assert.Equal(1L, Assert.Single(result.Rows)[0]);
        AssertEquivalent(Select(database, sql), result);
        Assert.Throws<InvalidOperationException>(() => Select(database, sql, Options(6)));
        AssertReleased(database);
    }

    /// <summary>零页不拉取候选，极小预算仍返回完整空结果。</summary>
    [Fact]
    public void Execute_ZeroLimit_DoesNotStartKnnScan()
    {
        using var database = OpenPoints(3);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(database, "SELECT time " + Source + " LIMIT 0",
            Options(1, 1) with { Metrics = metrics });

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        Assert.Null(metrics.Complete().ParallelOperator);
        AssertReleased(database);
    }

    /// <summary>候选三标量估算与最终时间列的精确字节边界共同生效。</summary>
    [Theory]
    [InlineData(416, true)]
    [InlineData(415, false)]
    public void Execute_TimeProjection_EnforcesCombinedEstimatedByteBoundary(long bytes, bool succeeds)
    {
        using var database = OpenPoints(1);
        const string sql = "SELECT time FROM knn(docs, embedding, [0,0], 1, 'l2')";

        if (succeeds)
            Assert.Equal(0L, Assert.Single(Select(database, sql, Options(3, bytes)).Rows)[0]);
        else
            Assert.Throws<InvalidOperationException>(() => Select(database, sql, Options(3, bytes)));
        AssertReleased(database);
    }

    /// <summary>仅回填引用字段；字符串缓存和最终结果都进入估算字节预算。</summary>
    [Theory]
    [InlineData(839, true)]
    [InlineData(838, false)]
    public void Execute_StringProjection_ChargesValueBeforeCachingAndOutput(long bytes, bool succeeds)
    {
        using var database = OpenPoints(1);
        const string sql = "SELECT label FROM knn(docs, embedding, [0,0], 1, 'l2')";

        if (succeeds)
            Assert.Equal(new string('x', 100), Assert.Single(Select(database, sql, Options(3, bytes)).Rows)[0]);
        else
            Assert.Throws<InvalidOperationException>(() => Select(database, sql, Options(3, bytes)));
        AssertReleased(database);
        Assert.Single(Select(database, "SELECT time FROM knn(docs, embedding, [0,0], 1, 'l2')", Options(3, 416)).Rows);
        AssertReleased(database);
    }

    /// <summary>TAG、时间筛选以及基础标量表达式保持默认 KNN 结果语义。</summary>
    [Fact]
    public void Execute_TagTimeAndScalarProjection_MatchesDefaultResults()
    {
        using var database = OpenPoints(4);
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, host, embedding, value, label) VALUES (10, 'b', [0,0], 9, 'excluded')");
        const string sql = "SELECT docs.time, docs.host, value + 1 AS next_value, UPPER(label) AS title, "
            + "CASE WHEN value > 1 THEN distance ELSE NULL END AS selected_distance "
            + Source + " WHERE docs.host = 'a' AND time >= 1 AND time <= 3";

        SelectExecutionResult result = Select(database, sql, Options(9));

        AssertEquivalent(Select(database, sql), result);
        Assert.Equal([1L, 2L, 3L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(["time", "host", "next_value", "title", "selected_distance"], result.Columns);
        AssertReleased(database);
    }

    /// <summary>多序列 worker 共用累计预算，失败及成功都释放 worker 和内存。</summary>
    [Fact]
    public void Execute_ParallelSeries_SharesBudgetAcrossWorkersAndReleasesResources()
    {
        using var database = OpenPoints(3);
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, host, embedding, value) VALUES (10, 'b', [0,0], 10), (11, 'b', [1,0], 11)");
        var metrics = new SqlExecutionMetrics();
        var options = Options(32) with
        {
            EnableParallelism = true,
            MaxDegreeOfParallelism = 2,
            ParallelismMinRows = 1,
            Metrics = metrics,
        };
        const string sql = "SELECT time, host FROM knn(docs, embedding, [0,0], 2, 'l2')";

        AssertEquivalent(Select(database, sql), Select(database, sql, options));
        Assert.True(metrics.Complete().ParallelismEnabled);
        AssertReleased(database);
        Assert.Throws<InvalidOperationException>(() => Select(database, sql,
            options with { MaxMaterializedRows = 3, Metrics = null }));
        AssertReleased(database);
    }

    /// <summary>保留原始字段名，并按引号精确解析列名和来源限定符。</summary>
    [Theory]
    [InlineData("SELECT Vectors.Label FROM knn(Vectors, Embedding, [0,0], 1, 'l2')", true)]
    [InlineData("SELECT vectors.label FROM knn(vectors, embedding, [0,0], 1, 'l2')", true)]
    [InlineData("SELECT \"Vectors\".\"Label\" FROM knn(\"Vectors\", \"Embedding\", [0,0], 1, 'l2')", true)]
    [InlineData("SELECT \"vectors\".\"Label\" FROM knn(Vectors, Embedding, [0,0], 1, 'l2')", false)]
    [InlineData("SELECT \"label\" FROM knn(Vectors, Embedding, [0,0], 1, 'l2')", false)]
    public void Execute_QuotedIdentifiers_PreservesSpellingAndExactBinding(string sql, bool succeeds)
    {
        using var database = OpenPoints(0);
        SqlExecutor.Execute(database, "CREATE MEASUREMENT Vectors (Embedding FIELD VECTOR(2), Label FIELD STRING)");
        SqlExecutor.Execute(database, "INSERT INTO Vectors (time, Embedding, Label) VALUES (1, [0,0], 'ready')");
        if (succeeds)
        {
            var result = Select(database, sql, Options(3));
            Assert.Equal(["Label"], result.Columns);
            Assert.Equal("ready", Assert.Single(result.Rows)[0]);
        }
        else
            Assert.Throws<InvalidOperationException>(() => Select(database, sql, Options(3)));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("SELECT Label FROM knn(Vectors, Embedding, [0,0], 1, 'l2') AS v"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse("SELECT Label FROM knn(Vectors, Embedding, [0,0], 1, 'l2') v"));
        AssertReleased(database);
    }

    /// <summary>已有 now/duration 时间下推保持可用，不误当作用户函数。</summary>
    [Fact]
    public void Execute_NowTimeFilter_PreservesExistingSafeFilter()
    {
        using var database = OpenPoints(1);
        const string sql = "SELECT time " + Source + " WHERE time < now() - 1s";
        AssertEquivalent(Select(database, sql), Select(database, sql, Options(3)));
        AssertReleased(database);
    }

    /// <summary>锁争用时能响应调用方取消，取消后的查询仍可正常执行。</summary>
    [Fact]
    public async Task Execute_ContendedReplacementLock_CancelsWithinBoundedWait()
    {
        using var database = OpenPoints(1);
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        object gate = database.VectorReplacements.SyncRoot;
        Task<Exception>? attempt = null;
        Task holder = Task.Run(() =>
        {
            lock (gate)
            {
                acquired.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("测试持锁等待超过十秒。");
            }
        });
        try
        {
            await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
            attempt = Task.Run(() => Record.Exception(() => Select(database, "SELECT time " + Source,
                Options(3) with { CancellationToken = cancellation.Token })));
            var error = Assert.IsType<RoutineExecutionException>(await attempt.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(RoutineErrorCodes.Cancelled, error.Code);
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            if (attempt is not null)
                await attempt.WaitAsync(TimeSpan.FromSeconds(5));
            await holder.WaitAsync(TimeSpan.FromSeconds(5));
        }
        AssertReleased(database);
        Assert.Single(Select(database, "SELECT time " + Source, Options(3)).Rows);
        AssertReleased(database);
    }

    /// <summary>稀疏字段在命中时间戳不存在时保留 NULL。</summary>
    [Fact]
    public void Execute_SparseScalarField_PreservesNull()
    {
        using var database = OpenPoints(0);
        SqlExecutor.Execute(database, "INSERT INTO docs (time, host, embedding) VALUES (1, 'a', [1,0])");
        const string sql = "SELECT time, value, label " + Source;

        SelectExecutionResult result = Select(database, sql, Options(3));

        AssertEquivalent(Select(database, sql), result);
        Assert.Null(Assert.Single(result.Rows)[1]);
        Assert.Null(result.Rows[0][2]);
        AssertReleased(database);
    }

    /// <summary>持久段重开及墓碑过滤后仍填满 K，失败查询后预算全部归还。</summary>
    [Fact]
    public void Execute_ReopenedSegmentWithTombstone_PreservesSurvivorsAndReleasesResources()
    {
        using (var database = OpenPoints(4))
        {
            Assert.NotNull(database.FlushNow());
            SqlExecutor.Execute(database, "DELETE FROM docs WHERE host = 'a' AND time = 0");
        }
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Throws<InvalidOperationException>(() => Select(reopened, "SELECT time " + Source, Options(2)));
        AssertReleased(reopened);
        var result = Select(reopened, "SELECT time " + Source, Options(9));
        Assert.Equal([1L, 2L, 3L], result.Rows.Select(static row => (long)row[0]!));
        AssertEquivalent(Select(reopened, "SELECT time " + Source), result);
        AssertReleased(reopened);
    }

    /// <summary>输出向量、复杂来源和未安全执行的 WHERE 在扫描前拒绝。</summary>
    [Theory]
    [InlineData("SELECT * " + Source)]
    [InlineData("SELECT embedding " + Source)]
    [InlineData("SELECT [1,2] " + Source)]
    [InlineData("SELECT time " + Source + " ORDER BY time")]
    [InlineData("SELECT DISTINCT time " + Source)]
    [InlineData("SELECT COUNT(*) " + Source)]
    [InlineData("SELECT ROW_NUMBER() OVER (ORDER BY time) " + Source)]
    [InlineData("SELECT time " + Source + " WHERE value > 1")]
    [InlineData("SELECT time " + Source + " WHERE host = 'a' OR host = 'b'")]
    [InlineData("SELECT time " + Source + " WHERE time IN (SELECT 1)")]
    [InlineData("SELECT time FROM (SELECT time " + Source + ") AS source")]
    [InlineData("WITH source AS (SELECT time " + Source + ") SELECT time FROM source")]
    [InlineData("SELECT time " + Source + " UNION ALL SELECT 1")]
    [InlineData("SELECT 1 UNION ALL SELECT time " + Source)]
    [InlineData("SELECT value FROM forecast(docs, value, 2, 'linear')")]
    public void Execute_UnsupportedShape_RejectsBeforeCandidateResources(string sql)
    {
        using var database = OpenPoints(3);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(database, sql, Options(100) with { Metrics = metrics }));

        Assert.Null(metrics.Complete().ParallelOperator);
        AssertReleased(database);
    }

    /// <summary>用户标量与覆盖内置 KNN 的用户 TVF 都在执行回调前拒绝。</summary>
    [Fact]
    public void Execute_UserCallbacks_RejectsWithoutInvokingCallback()
    {
        using var database = OpenPoints(1);
        int callbacks = 0;
        database.Functions.RegisterScalar("custom_value", _ => { callbacks++; return 7L; }, 0, 0);
        Assert.Throws<NotSupportedException>(() => Select(database, "SELECT custom_value() " + Source, Options(10)));
        Assert.Equal(0, callbacks);
        Assert.Equal(7L, Assert.Single(Select(database, "SELECT custom_value() " + Source).Rows)[0]);
        Assert.Equal(1, callbacks);

        database.Functions.RegisterTableValuedFunction("knn", (_, _) =>
        {
            callbacks++;
            return new SelectExecutionResult(["custom"], [[8L]]);
        });
        Assert.Throws<NotSupportedException>(() => Select(database, "SELECT time " + Source, Options(10)));
        Assert.Equal(1, callbacks);
        Assert.Equal(8L, Assert.Single(Select(database, "SELECT time " + Source).Rows)[0]);
        Assert.Equal(2, callbacks);
        AssertReleased(database);
    }

    /// <summary>默认无物化预算调用继续返回向量结果。</summary>
    [Fact]
    public void Execute_DefaultOptions_PreservesVectorOutput()
    {
        using var database = OpenPoints(1);
        var result = Select(database, "SELECT embedding " + Source);
        Assert.Equal([0f, 0f], Assert.IsType<float[]>(Assert.Single(result.Rows)[0]));
        AssertReleased(database);
    }

    /// <summary>取消请求不泄漏 SQL 内存预算和 worker。</summary>
    [Fact]
    public void Execute_PreCancelledRequest_ReleasesAllResources()
    {
        using var database = OpenPoints(3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = Assert.Throws<RoutineExecutionException>(() => Select(database, "SELECT time " + Source,
            Options(6) with { CancellationToken = cancellation.Token }));
        Assert.Equal(RoutineErrorCodes.Cancelled, error.Code);
        AssertReleased(database);
    }

    /// <summary>释放仅本测试创建的计时器和临时数据库目录。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        string resolved = Path.GetFullPath(_root);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetDirectoryName(resolved), ignoreCase: true);
        Assert.StartsWith("sndb-knn-materialization-", Path.GetFileName(resolved), StringComparison.Ordinal);
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    private Tsdb OpenPoints(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 64);
        var database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SqlMemory = new SqlMemoryOptions { MaxParallelWorkers = 2, ParallelismMinRows = 1 },
        });
        try
        {
            SqlExecutor.Execute(database,
                "CREATE MEASUREMENT docs (host TAG, embedding FIELD VECTOR(2), value FIELD INT, label FIELD STRING)");
            for (int point = 0; point < count; point++)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                SqlExecutor.Execute(database,
                    $"INSERT INTO docs (time, host, embedding, value, label) VALUES ({point}, 'a', [{point},0], {point}, '{new string('x', 100)}')");
            }
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private SqlExecutionOptions Options(long rows, long? bytes = null) => new()
    {
        MaxMaterializedRows = rows,
        MaxMaterializedBytes = bytes,
        EnableParallelism = false,
        CancellationToken = _deadline.Token,
    };

    private static SelectExecutionResult Select(Tsdb database, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, databaseName: null, sql,
            parameters: null, controlPlane: null, options ?? new SqlExecutionOptions()));

    private static void AssertEquivalent(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        Assert.InRange(expected.Rows.Count, 0, 64);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        for (int row = 0; row < expected.Rows.Count; row++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            Assert.Equal(expected.Rows[row], actual.Rows[row]);
        }
        Assert.False(actual.Truncated);
    }

    private static void AssertReleased(Tsdb database)
    {
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        Assert.Equal(0, database.SqlParallelCoordinator.ActiveWorkers);
    }
}
