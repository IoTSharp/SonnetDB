using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>用固定分层语料逐行验证完整流式排序及资源回收。</summary>
[Collection(StreamingOrderByCapacityTests.CollectionName)]
public sealed class StreamingOrderByCapacityTests(ITestOutputHelper output) : IDisposable
{
    internal const string CollectionName = "Streaming ORDER BY capacity";
    private const int SeriesCount = 8;
    private const int Seed = 440;
    private const int BatchSize = 2_048;
    private const string Projection = "SELECT time, host, value FROM ordered_samples ORDER BY time";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-stream-order-{Guid.NewGuid():N}");

    /// <summary>小语料对拍稳定升降序、边界分页与结果终态。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallCorpus_MatchesBufferedOrderAndPagination(bool descending)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using Tsdb database = CreateDatabase(1_024, deadline.Token);
        string sql = Projection + (descending ? " DESC" : " ASC");
        var buffered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));

        using var stream = OpenStream(database, sql, new SqlExecutionOptions { CancellationToken = deadline.Token });
        Assert.Equal(buffered.Columns, stream.Columns);
        for (int i = 0; i < buffered.Rows.Count; i++)
        {
            Assert.True(stream.MoveNext());
            Assert.Equal(buffered.Rows[i], stream.Current);
        }
        Assert.Equal(1_024, buffered.Rows.Count);
        Assert.False(stream.MoveNext());
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);

        var page = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql + " LIMIT 5 OFFSET 6"));
        Assert.Equal(5, page.Rows.Count);
        for (int i = 0; i < page.Rows.Count; i++)
            Assert.Equal(buffered.Rows[i + 6], page.Rows[i]);
    }

    /// <summary>验证 10 万行输入的时间排序及完整输出。</summary>
    [OrderCapacityFact]
    public void Capacity_100000Rows() => VerifyCapacity(100_000);

    /// <summary>验证接近百万行输入的时间排序及完整输出。</summary>
    [OrderCapacityFact]
    public void Capacity_999992Rows() => VerifyCapacity(999_992);

    /// <summary>短读、主动取消和绝对截止时间到期后都可重新执行。</summary>
    [Theory]
    [InlineData("dispose")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task OrderedStream_Interruption_ReleasesAndAllowsNextQuery(string interruption)
    {
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using Tsdb database = CreateDatabase(32, testDeadline.Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testDeadline.Token);
        using (var stream = OpenStream(database, Projection, new SqlExecutionOptions
        {
            CancellationToken = cancellation.Token,
            DeadlineUtc = interruption == "deadline" ? DateTimeOffset.UtcNow.AddSeconds(2) : null,
        }))
        {
            Assert.True(stream.MoveNext());
            if (interruption == "cancel")
            {
                cancellation.Cancel();
                Assert.ThrowsAny<OperationCanceledException>(() => stream.MoveNext());
            }
            else if (interruption == "deadline")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => Task.Delay(TimeSpan.FromSeconds(6), stream.CancellationToken).WaitAsync(testDeadline.Token));
                Assert.True(stream.CancellationToken.IsCancellationRequested);
                Assert.ThrowsAny<OperationCanceledException>(() => stream.MoveNext());
            }
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        using var next = OpenStream(database, Projection, new SqlExecutionOptions { CancellationToken = testDeadline.Token });
        for (int i = 0; i < 32; i++)
            Assert.True(next.MoveNext());
        Assert.False(next.MoveNext());
    }

    /// <summary>强制流式外排，逐行比较稳定顺序，并验证短读只释放自己的工作区。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelationalSpill_CompletedOrAbandoned_ReclaimsRuns(bool shortRead)
    {
        const long runBudget = 256 * 1024;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using Tsdb database = CreateSpillDatabase();
        const string sql = "SELECT id, sort_key, payload FROM ordered_records ORDER BY sort_key DESC";
        var buffered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
        var metrics = new SqlExecutionMetrics();
        using (var stream = OpenStream(database, sql, new SqlExecutionOptions
        {
            CancellationToken = deadline.Token,
            BlockingOperatorMemoryLimitBytes = 512,
            SortSpillMaxBytes = runBudget,
            Metrics = metrics,
        }))
        {
            Assert.True(stream.MoveNext());
            Assert.Equal(buffered.Rows[0], stream.Current);
            Assert.True(Directory.Exists(SpillDirectory));
            Assert.NotEmpty(Directory.EnumerateDirectories(SpillDirectory, "query-*"));
            if (!shortRead)
            {
                for (int i = 1; i < buffered.Rows.Count; i++)
                {
                    Assert.True(stream.MoveNext());
                    Assert.Equal(buffered.Rows[i], stream.Current);
                }
                Assert.False(stream.MoveNext());
            }
        }

        AssertNoSpillRuns(database);
        SqlExecutionMetricsSnapshot result = metrics.Complete();
        Assert.True(result.SpillBytes > 0);
        Assert.True(result.SortComparisons > 0);
        Assert.InRange(result.PeakMemoryBytes, 0, 512);
        Assert.InRange(result.PeakSortSpillBytes, 1, runBudget);
        Assert.InRange(result.PeakSortReaders, 1, 32);
        output.WriteLine(FormattableString.Invariant(
            $"shortRead={shortRead} rows={buffered.Rows.Count} operatorPeakBytes={result.PeakMemoryBytes} peakLiveSpillBytes={result.PeakSortSpillBytes} cumulativeSpillBytes={result.SpillBytes} comparisons={result.SortComparisons} peakRunReaders={result.PeakSortReaders}"));
    }

    /// <summary>临时空间配额耗尽及工作区路径不可写都必须报错、释放预算并允许下一条语句运行。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelationalSpill_QuotaOrUnusablePath_ReleasesResources(bool pathFailure)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using Tsdb database = CreateSpillDatabase();
        const string sql = "SELECT id, payload FROM ordered_records ORDER BY sort_key";
        if (pathFailure)
            File.WriteAllText(SpillDirectory, "a regular file blocks creation of the spill directory");

        var options = new SqlExecutionOptions
        {
            CancellationToken = deadline.Token,
            BlockingOperatorMemoryLimitBytes = 512,
            SortSpillMaxBytes = pathFailure ? null : 128,
        };
        if (pathFailure)
        {
            Assert.ThrowsAny<IOException>(ConsumeFirstRow);
            Assert.Equal("a regular file blocks creation of the spill directory", File.ReadAllText(SpillDirectory));
        }
        else
        {
            Assert.Throws<SqlSortSpillBudgetExceededException>(ConsumeFirstRow);
        }

        AssertNoSpillRuns(database);
        Assert.Equal(64, Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql)).Rows.Count);

        void ConsumeFirstRow()
        {
            using var stream = OpenStream(database, sql, options);
            _ = stream.MoveNext();
        }
    }

    /// <summary>短时反复运行完整读取、提前退出、取消和配额拒绝，核对每轮资源回到初始值。</summary>
    [Fact]
    public void RelationalSpill_BoundedSoak_AlwaysReturnsResources()
    {
        bool extended = string.Equals(Environment.GetEnvironmentVariable("SONNETDB_ORDER_BY_SOAK"), "1", StringComparison.Ordinal);
        int rounds = extended ? 512 : 12;
        using var deadline = new CancellationTokenSource(extended ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(45));
        using Tsdb database = CreateSpillDatabase();
        const string sql = "SELECT id, payload FROM ordered_records ORDER BY sort_key DESC";
        var elapsed = Stopwatch.StartNew();
        long peakBytes = 0;
        long peakLiveSpill = 0;
        long spillBytes = 0;
        for (int round = 0; round < rounds; round++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            int outcome = round % 4;
            var metrics = new SqlExecutionMetrics();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var options = new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = 512,
                SortSpillMaxBytes = outcome == 3 ? 128 : 256 * 1024,
                CancellationToken = cancellation.Token,
                Metrics = metrics,
            };
            using (var stream = OpenStream(database, sql, options))
            {
                if (outcome == 3)
                {
                    Assert.Throws<SqlSortSpillBudgetExceededException>(() => stream.MoveNext());
                }
                else
                {
                    Assert.True(stream.MoveNext());
                    if (outcome == 0)
                    {
                        for (int row = 1; row < 64; row++)
                            Assert.True(stream.MoveNext());
                        Assert.False(stream.MoveNext());
                    }
                    else if (outcome == 2)
                    {
                        cancellation.Cancel();
                        Assert.ThrowsAny<OperationCanceledException>(() => stream.MoveNext());
                    }
                }
            }

            SqlExecutionMetricsSnapshot result = metrics.Complete();
            peakBytes = Math.Max(peakBytes, result.PeakMemoryBytes);
            peakLiveSpill = Math.Max(peakLiveSpill, result.PeakSortSpillBytes);
            spillBytes += result.SpillBytes;
            AssertNoSpillRuns(database);
            if ((round + 1) % 64 == 0)
                output.WriteLine(FormattableString.Invariant($"soakCompleted={round + 1} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F2}"));
        }

        output.WriteLine(FormattableString.Invariant(
            $"soakRounds={rounds} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F2} peakOperatorBytes={peakBytes} peakLiveSpillBytes={peakLiveSpill} cumulativeSpillBytes={spillBytes}"));
    }

    private void VerifyCapacity(int rowCount)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var setup = Stopwatch.StartNew();
        using Tsdb database = CreateDatabase(rowCount, deadline.Token);
        setup.Stop();

        // 小语料与旧执行器逐行对拍。大语料直接用生成公式验证，无需在消费者保留一份完整结果。
        var metrics = new SqlExecutionMetrics();
        using var process = Process.GetCurrentProcess();
        long processPeakBefore = process.PeakWorkingSet64;
        var elapsed = Stopwatch.StartNew();
        using var stream = OpenStream(database, Projection, new SqlExecutionOptions
        {
            CancellationToken = deadline.Token,
            Metrics = metrics,
        });

        Assert.Equal(["time", "host", "value"], stream.Columns);
        double firstRowMs = 0;
        string[] seriesOrder = new string[SeriesCount];
        for (int index = 0; index < rowCount; index++)
        {
            if (!stream.MoveNext())
                Assert.Fail($"EOF at {index.ToString(CultureInfo.InvariantCulture)}");
            if (index == 0)
                firstRowMs = elapsed.Elapsed.TotalMilliseconds;

            int seriesPosition = index % SeriesCount;
            int time = index / SeriesCount;
            IReadOnlyList<object?> row = stream.Current;
            Assert.Equal((long)time, row[0]);
            string host = Assert.IsType<string>(row[1]);
            if (time == 0)
                seriesOrder[seriesPosition] = host;
            Assert.Equal(seriesOrder[seriesPosition], host);
            int series = host[^1] - '0';
            Assert.InRange(series, 0, SeriesCount - 1);
            Assert.Equal(ValueAt(time, series), row[2]);
        }

        Assert.Equal(SeriesCount, seriesOrder.Distinct(StringComparer.Ordinal).Count());
        Assert.False(stream.MoveNext());
        elapsed.Stop();
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);

        SqlExecutionMetricsSnapshot result = metrics.Complete();
        process.Refresh();
        output.WriteLine(FormattableString.Invariant(
            $"tierRows={rowCount} seed={Seed} series={SeriesCount} batchSize={BatchSize} machine={Environment.MachineName} runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription} cpuCount={Environment.ProcessorCount}"));
        output.WriteLine(FormattableString.Invariant(
            $"setupMs={setup.Elapsed.TotalMilliseconds:F2} firstRowMs={firstRowMs:F2} fullDeliveryMs={elapsed.Elapsed.TotalMilliseconds:F2} processLifetimePeakBeforeBytes={processPeakBefore} processLifetimePeakAfterBytes={process.PeakWorkingSet64} candidateRows={result.CandidateRows} operatorPeakBytes={result.PeakMemoryBytes} spillBytesWritten={result.SpillBytes} peakLiveSpillBytes={result.PeakSortSpillBytes} comparisons={result.SortComparisons} peakRunReaders={result.PeakSortReaders} allocatedBytes={result.AllocatedBytes}"));
    }

    private Tsdb CreateDatabase(int rowCount, CancellationToken cancellationToken)
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT ordered_samples (host TAG, value FIELD INT)");
            var tags = new Dictionary<string, string>[SeriesCount];
            for (int series = 0; series < SeriesCount; series++)
                tags[series] = new Dictionary<string, string> { ["host"] = $"node{series}" };

            Point[] batch = new Point[Math.Min(BatchSize, rowCount)];
            for (int offset = 0; offset < rowCount; offset += batch.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(batch.Length, rowCount - offset);
                for (int i = 0; i < count; i++)
                {
                    int index = offset + i;
                    int time = index / SeriesCount;
                    int series = index % SeriesCount;
                    batch[i] = Point.Create("ordered_samples", time, tags[series], new Dictionary<string, FieldValue>
                    {
                        ["value"] = FieldValue.FromLong(ValueAt(time, series)),
                    });
                }
                Assert.Equal(count, database.WriteMany(batch.AsSpan(0, count), cancellationToken));

                // 固定分段并保留最新一批在 MemTable，以覆盖两类读源。
                if (offset + count < rowCount && (offset + count) % 32_768 == 0)
                    database.FlushNow();
            }
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static long ValueAt(int time, int series) => ((long)time * 31 + series * 17 + Seed) % 1_009;

    private Tsdb CreateSpillDatabase()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(database, "CREATE TABLE ordered_records (id INT, sort_key INT, payload STRING, PRIMARY KEY (id))");
            const string payload = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            string values = string.Join(",", Enumerable.Range(0, 64).Select(
                static id => FormattableString.Invariant($"({id},{id % 7},'{payload}')")));
            SqlExecutor.Execute(database, "INSERT INTO ordered_records (id, sort_key, payload) VALUES " + values);
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private string SpillDirectory => Path.Combine(_root, SqlSpillWorkspace.DirectoryName);

    private void AssertNoSpillRuns(Tsdb database)
    {
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        if (Directory.Exists(SpillDirectory))
            Assert.Empty(Directory.EnumerateDirectories(SpillDirectory, "query-*"));
    }

    private static SqlStreamingSelectResult OpenStream(Tsdb database, string sql, SqlExecutionOptions options) =>
        Assert.IsType<SqlStreamingSelectResult>(SqlExecutor.Execute(
            database, databaseName: null, sql, parameters: null, controlPlane: null, options with { StreamResults = true }));

    /// <summary>清理测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class OrderCapacityFactAttribute : FactAttribute
    {
        public OrderCapacityFactAttribute()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("SONNETDB_ORDER_BY_CAPACITY"), "1", StringComparison.Ordinal))
                Skip = "Set SONNETDB_ORDER_BY_CAPACITY=1 to run the bounded capacity tier.";
        }
    }
}

/// <summary>容量用例与同一程序集中的其他测试串行执行。</summary>
[CollectionDefinition(StreamingOrderByCapacityTests.CollectionName, DisableParallelization = true)]
public sealed class StreamingOrderByCapacityCollection;
