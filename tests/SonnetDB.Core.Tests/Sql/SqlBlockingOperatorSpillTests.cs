using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>串行运行会观察进程级 oversized 归并闸门的测试，避免其他测试污染计数。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlSpillSerialCollection
{
    public const string Name = "SQL spill serial collection";
}

/// <summary>验证 M41 #379 阻塞算子预算、spill 正确性、取消与临时文件生命周期。</summary>
[Collection(SqlSpillSerialCollection.Name)]
public sealed class SqlBlockingOperatorSpillTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-sql-spill-{Guid.NewGuid():N}");

    /// <summary>六类阻塞算子的内存与强制 spill 路径必须返回逐值一致的完整结果。</summary>
    [Theory]
    [InlineData("SELECT id, value FROM spill_items ORDER BY value DESC, id", "sort")]
    [InlineData("SELECT id, value FROM spill_items ORDER BY value DESC, id LIMIT 7", "top_n")]
    [InlineData("SELECT DISTINCT category FROM spill_items", "distinct")]
    [InlineData("SELECT category, count(*), sum(value) FROM spill_items GROUP BY category", "group")]
    [InlineData("SELECT i.id, l.label FROM spill_items i JOIN spill_lookup l ON i.category = l.join_key", "hash_join")]
    [InlineData("SELECT l.id, r.id FROM spill_hash_left l JOIN spill_hash_right r ON l.event_time = r.event_time AND l.blob_key = r.blob_key", "hash_join_temporal_blob")]
    [InlineData("SELECT id FROM spill_union WHERE bucket = 1 OR alternate = 2", "index_candidates")]
    public void Execute_ForcedSpill_MatchesInMemoryResult(string sql, string operatorName)
    {
        using Tsdb database = CreateDatabase();

        (SelectExecutionResult expected, SqlExecutionMetricsSnapshot memoryMetrics) = Execute(
            database,
            sql,
            memoryLimitBytes: 1024 * 1024);
        (SelectExecutionResult actual, SqlExecutionMetricsSnapshot spillMetrics) = Execute(
            database,
            sql,
            memoryLimitBytes: 96);

        AssertRowsEqual(expected, actual);
        Assert.Equal(0, memoryMetrics.SpillCount);
        Assert.True(spillMetrics.SpillCount > 0, $"{operatorName} 未触发 spill。");
        Assert.True(spillMetrics.SpillBytes > 0);
        Assert.InRange(spillMetrics.PeakMemoryBytes, 0, 96);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>大输入小分页在前缀可装入预算时只保留 K 行，不编码或外排其余候选。</summary>
    [Fact]
    public void TopN_LargeInputSmallFetch_RetainsOnlyBoundedPrefixWithoutSpill()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var metrics = new SqlExecutionMetrics();
        int encodeCount = 0;
        int estimateCount = 0;
        var codec = new SqlSpillCodec<object?[]>(
            row =>
            {
                encodeCount++;
                return row;
            },
            static row => row,
            row =>
            {
                estimateCount++;
                return SqlSpillRowCodec.EstimateRowBytes(row);
            });

        object?[][] result;
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 4096 }))
        using (SqlExecutionTelemetry.Enter(metrics))
        {
            result = TopN.OrderByThenPaginate(
                Enumerable.Range(0, 10_000).Select(static value => new object?[] { (long)value }),
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 3,
                fetch: 7,
                codec);
        }

        Assert.Equal(Enumerable.Range(3, 7).Select(static value => (long)value),
            result.Select(static row => (long)row[0]!));
        Assert.Equal(10, estimateCount);
        Assert.Equal(0, encodeCount);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.Equal(0, snapshot.SpillCount);
        Assert.InRange(snapshot.PeakMemoryBytes, 1, 4096);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>预算内 Top-N 对同键行必须继续按原始输入序号保持稳定。</summary>
    [Fact]
    public void TopN_BudgetedHeap_PreservesStableOrder()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        object?[][] input =
        [
            [1L, "a"],
            [1L, "b"],
            [0L, "c"],
            [1L, "d"],
        ];

        object?[][] result;
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 4096 }))
        {
            result = TopN.OrderByThenPaginate(
                input,
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: 3,
                SqlSpillCodecs.ArrayRows);
        }

        Assert.Equal(["c", "a", "b"], result.Select(static row => (string)row[1]!).ToArray());
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>已有淘汰项后遇到超预算宽行时，回退外排仍只返回精确稳定前缀。</summary>
    [Fact]
    public void TopN_WideReplacementFallsBackToSpillWithoutRestoringDiscardedRows()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        object?[][] input =
        [
            [1L, "one"],
            [2L, "two"],
            [3L, "three"],
            [100L, "discarded-100"],
            [101L, "discarded-101"],
            [0L, new string('x', 2048)],
            [-1L, "last"],
        ];
        var metrics = new SqlExecutionMetrics();

        object?[][] result;
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 512 }))
        using (SqlExecutionTelemetry.Enter(metrics))
        {
            result = TopN.OrderByThenPaginate(
                input,
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: 3,
                SqlSpillCodecs.ArrayRows);
        }

        Assert.Equal([-1L, 0L, 1L], result.Select(static row => (long)row[0]!).ToArray());
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.True(snapshot.SpillCount > 0);
        Assert.True(snapshot.SpillBytes > 0);
        Assert.InRange(snapshot.PeakMemoryBytes, 1, 512);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>极大 LIMIT 在首行外排时不得按 LIMIT 预分配结果内存。</summary>
    [Fact]
    public void TopN_HugeFetchAfterImmediateSpill_DoesNotPreallocateFetchCapacity()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var metrics = new SqlExecutionMetrics();

        object?[][] result;
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        using (SqlExecutionTelemetry.Enter(metrics))
        {
            result = TopN.OrderByThenPaginate(
                new[] { new object?[] { 1L, "only-row" } },
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: int.MaxValue,
                SqlSpillCodecs.ArrayRows);
        }

        Assert.Single(result);
        Assert.Equal(1L, result[0][0]);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();
        Assert.True(snapshot.SpillCount > 0);
        Assert.InRange(snapshot.PeakMemoryBytes, 0, 1);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>取消发生在 spill 写入后时必须抛取消异常并释放预算与查询目录。</summary>
    [Fact]
    public void Sort_CancelledDuringSpill_ReleasesBudgetAndWorkspace()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var cancellation = new CancellationTokenSource();
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = 64,
                CancellationToken = cancellation.Token,
            }))
        {
            Assert.Throws<OperationCanceledException>(() => TopN.OrderByThenPaginate(
                RowsThatCancel(cancellation),
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: null,
                SqlSpillCodecs.ArrayRows));
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>内存排序的比较阶段收到取消后必须及时停止，并完整归还预算。</summary>
    [Fact]
    public void Sort_CancelledDuringInMemoryComparison_ReleasesBudgetAndWorkspace()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var cancellation = new CancellationTokenSource();
        int comparisonCount = 0;
        var comparer = Comparer<object?[]>.Create((left, right) =>
        {
            int current = ++comparisonCount;
            if (current == 64)
                cancellation.Cancel();
            if (current > 1500)
                throw new InvalidOperationException("排序未在固定比较窗口内响应取消。");
            return ((long)left[0]!).CompareTo((long)right[0]!);
        });
        object?[][] input = Enumerable.Range(0, 10_000)
            .Select(static value => new object?[] { (long)(10_000 - value) })
            .ToArray();

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = 4 * 1024 * 1024,
                CancellationToken = cancellation.Token,
            }))
        {
            Assert.Throws<OperationCanceledException>(() => TopN.OrderByThenPaginate(
                input,
                comparer,
                offset: 0,
                fetch: null,
                SqlSpillCodecs.ArrayRows));
        }

        Assert.InRange(comparisonCount, 64, 1500);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>两个无法完整计费的大行归并必须串行进入例外区，并在完成后释放闸门与工作区。</summary>
    [Fact]
    public async Task Sort_ConcurrentOversizedMerges_AreSerializedAndReleased()
    {
        string firstRoot = Path.Combine(_root, "oversized-first");
        string secondRoot = Path.Combine(_root, "oversized-second");
        using Tsdb firstDatabase = Tsdb.Open(new TsdbOptions { RootDirectory = firstRoot });
        using Tsdb secondDatabase = Tsdb.Open(new TsdbOptions { RootDirectory = secondRoot });
        using var firstComparisonStarted = new ManualResetEventSlim();
        using var releaseFirstComparison = new ManualResetEventSlim();
        var firstComparer = Comparer<object?[]>.Create((left, right) =>
        {
            firstComparisonStarted.Set();
            releaseFirstComparison.Wait(TimeSpan.FromSeconds(15));
            return ((long)left[0]!).CompareTo((long)right[0]!);
        });
        var secondComparer = Comparer<object?[]>.Create(static (left, right) =>
            ((long)left[0]!).CompareTo((long)right[0]!));

        Task<object?[][]> firstTask = Task.Run(() => ExecuteOversizedTwoRowSort(
            firstDatabase,
            firstComparer));
        bool firstStarted = firstComparisonStarted.Wait(TimeSpan.FromSeconds(10));
        Task<object?[][]> secondTask = firstStarted
            ? Task.Run(() => ExecuteOversizedTwoRowSort(secondDatabase, secondComparer))
            : Task.FromResult(Array.Empty<object?[]>());
        bool waitingObserved = firstStarted && SpinWait.SpinUntil(
            static () => SqlSpillSorter.WaitingOversizedMergeCount > 0,
            TimeSpan.FromSeconds(10));
        int activeWhileBlocked = SqlSpillSorter.ActiveOversizedMergeCount;
        int waitingWhileBlocked = SqlSpillSorter.WaitingOversizedMergeCount;

        releaseFirstComparison.Set();
        object?[][][] results = await Task.WhenAll(firstTask, secondTask)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(firstStarted, "第一条 oversized 归并没有进入比较阶段。");
        Assert.True(waitingObserved, "第二条 oversized 归并没有在进程级闸门等待。");
        Assert.Equal(1, activeWhileBlocked);
        Assert.True(waitingWhileBlocked >= 1);
        Assert.All(results, static result =>
            Assert.Equal([1L, 2L], result.Select(static row => (long)row[0]!).ToArray()));
        Assert.Equal(0, SqlSpillSorter.ActiveOversizedMergeCount);
        Assert.Equal(0, SqlSpillSorter.WaitingOversizedMergeCount);
        Assert.Equal(0, firstDatabase.SqlMemoryBudget.ReservedBytes);
        Assert.Equal(0, secondDatabase.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces(firstRoot);
        AssertNoQueryWorkspaces(secondRoot);
    }

    /// <summary>Top-N 输入结束后的堆排序收到取消时必须及时停止并归还预算。</summary>
    [Fact]
    public void TopN_CancelledDuringFinalComparison_ReleasesBudgetAndWorkspace()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var cancellation = new CancellationTokenSource();
        bool enumerationCompleted = false;
        int finalComparisonCount = 0;
        var comparer = Comparer<object?[]>.Create((left, right) =>
        {
            if (enumerationCompleted)
            {
                int current = ++finalComparisonCount;
                if (current == 64)
                    cancellation.Cancel();
                if (current > 1500)
                    throw new InvalidOperationException("Top-N 最终排序未在固定比较窗口内响应取消。");
            }
            return ((long)left[0]!).CompareTo((long)right[0]!);
        });

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = 4 * 1024 * 1024,
                CancellationToken = cancellation.Token,
            }))
        {
            Assert.Throws<OperationCanceledException>(() => TopN.OrderByThenPaginate(
                RowsThatMarkCompletion(10_000, () => enumerationCompleted = true),
                comparer,
                offset: 0,
                fetch: 4096,
                SqlSpillCodecs.ArrayRows));
        }

        Assert.InRange(finalComparisonCount, 64, 1500);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>大量单行超预算 run 必须增量归并，并在固定文件上限内保持稳定排序。</summary>
    [Fact]
    public void Sort_ManyOversizedRows_BoundsLiveRunsAndPreservesStability()
    {
        const int rowCount = 1056;
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        int maxLiveRunFiles = 0;
        var codec = new SqlSpillCodec<object?[]>(
            row =>
            {
                maxLiveRunFiles = Math.Max(maxLiveRunFiles, CountLiveRunFiles());
                return row;
            },
            static row => row);
        object?[][] input = Enumerable.Range(0, rowCount)
            .Select(static index => new object?[]
            {
                (long)(index % 17),
                (long)index,
                new string('x', 64),
            })
            .ToArray();

        object?[][] actual;
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        {
            actual = TopN.OrderByThenPaginate(
                input,
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: null,
                codec);
        }

        long[] expectedOrdinals = Enumerable.Range(0, rowCount)
            .OrderBy(static index => index % 17)
            .Select(static index => (long)index)
            .ToArray();
        Assert.Equal(expectedOrdinals, actual.Select(static row => (long)row[1]!));
        Assert.InRange(maxLiveRunFiles, 1, SqlSpillSorter.MaxLiveRunFileCount);
        Assert.True(maxLiveRunFiles < rowCount, "run 文件未在输入生成期间增量归并。");
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>增量归并开始后收到取消信号时必须关闭游标并清理整个查询工作区。</summary>
    [Fact]
    public void Sort_CancelledDuringIncrementalMerge_ReleasesBudgetAndWorkspace()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var cancellation = new CancellationTokenSource();
        int encodeCount = 0;
        var codec = new SqlSpillCodec<object?[]>(
            row =>
            {
                if (++encodeCount == 64)
                    cancellation.Cancel();
                return row;
            },
            static row => row);

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = 1,
                CancellationToken = cancellation.Token,
            }))
        {
            Assert.Throws<OperationCanceledException>(() => TopN.OrderByThenPaginate(
                CreateOversizedRows(64),
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: null,
                codec));
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>增量归并编码失败时必须关闭输入输出文件并清理整个查询工作区。</summary>
    [Fact]
    public void Sort_EncodeFailsDuringIncrementalMerge_ReleasesBudgetAndWorkspace()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        int encodeCount = 0;
        var codec = new SqlSpillCodec<object?[]>(
            row => ++encodeCount == 65
                ? throw new InvalidDataException("测试注入的归并编码失败。")
                : row,
            static row => row);

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        {
            Assert.Throws<InvalidDataException>(() => TopN.OrderByThenPaginate(
                CreateOversizedRows(64),
                Comparer<object?[]>.Create(static (left, right) =>
                    ((long)left[0]!).CompareTo((long)right[0]!)),
                offset: 0,
                fetch: null,
                codec));
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>流式排序和分页归并正常完成后应在根查询结束前删除各自的最终 run。</summary>
    [Fact]
    public void Sort_FinalRunsAreDeletedWithinActiveRootScope_AfterSuccessfulMerges()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        long[] expectedOrdinals = Enumerable.Range(0, 64)
            .OrderBy(static index => index % 7)
            .Select(static index => (long)index)
            .ToArray();
        var comparer = Comparer<object?[]>.Create(static (left, right) =>
            ((long)left[0]!).CompareTo((long)right[0]!));

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        {
            object?[][] streamed = SqlSpillSorter.Order(
                    CreateOversizedRows(64),
                    comparer,
                    SqlSpillCodecs.ArrayRows)
                .ToArray();
            Assert.Equal(expectedOrdinals, streamed.Select(static row => (long)row[1]!));
            Assert.Equal(0, CountLiveRunFiles());

            object?[][] paged = TopN.OrderByThenPaginate(
                CreateOversizedRows(64),
                comparer,
                offset: 0,
                fetch: null,
                SqlSpillCodecs.ArrayRows);
            Assert.Equal(expectedOrdinals, paged.Select(static row => (long)row[1]!));
            Assert.Equal(0, CountLiveRunFiles());
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>最终分页归并的比较器抛错后应立即关闭游标并删除最终 run。</summary>
    [Fact]
    public void Sort_FinalMergeComparisonFails_DeletesRunsWithinActiveRootScope()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var comparer = Comparer<object?[]>.Create(static (_, _) =>
            throw new InvalidDataException("测试注入的最终归并比较失败。"));

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        {
            Assert.Throws<InvalidDataException>(() => TopN.OrderByThenPaginate(
                CreateOversizedRows(2),
                comparer,
                offset: 0,
                fetch: null,
                SqlSpillCodecs.ArrayRows));
            Assert.Equal(0, CountLiveRunFiles());
            Assert.Equal(0, SqlSpillSorter.ActiveOversizedMergeCount);
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>最终流式归并收到取消后应在根查询结束前释放闸门并删除最终 run。</summary>
    [Fact]
    public void Sort_FinalMergeCancellation_DeletesRunsWithinActiveRootScope()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using var cancellation = new CancellationTokenSource();
        var comparer = Comparer<object?[]>.Create((left, right) =>
        {
            cancellation.Cancel();
            return ((long)left[0]!).CompareTo((long)right[0]!);
        });

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = 1,
                CancellationToken = cancellation.Token,
            }))
        {
            Assert.Throws<OperationCanceledException>(() => SqlSpillSorter.Order(
                    CreateOversizedRows(2),
                    comparer,
                    SqlSpillCodecs.ArrayRows)
                .ToArray());
            Assert.Equal(0, CountLiveRunFiles());
            Assert.Equal(0, SqlSpillSorter.ActiveOversizedMergeCount);
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>调用方提前释放流式归并枚举器时应立即释放闸门并删除最终 run。</summary>
    [Fact]
    public void Sort_EarlyEnumeratorDispose_DeletesRunsWithinActiveRootScope()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var comparer = Comparer<object?[]>.Create(static (left, right) =>
            ((long)left[0]!).CompareTo((long)right[0]!));

        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        {
            IEnumerator<object?[]> enumerator = SqlSpillSorter.Order(
                    CreateOversizedRows(2),
                    comparer,
                    SqlSpillCodecs.ArrayRows)
                .GetEnumerator();
            try
            {
                Assert.True(enumerator.MoveNext());
                Assert.True(CountLiveRunFiles() > 0);
                Assert.Equal(1, SqlSpillSorter.ActiveOversizedMergeCount);
            }
            finally
            {
                enumerator.Dispose();
            }

            Assert.Equal(0, CountLiveRunFiles());
            Assert.Equal(0, SqlSpillSorter.ActiveOversizedMergeCount);
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>启动恢复只清理由 SonnetDB 标记的遗留查询目录，不触碰未拥有的目录。</summary>
    [Fact]
    public void Open_WithStaleMarkedWorkspace_CleansOnlyOwnedDirectory()
    {
        string parent = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        string stale = Path.Combine(parent, "query-stale");
        string unowned = Path.Combine(parent, "query-unowned");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(unowned);
        File.WriteAllText(Path.Combine(stale, SqlSpillWorkspace.OwnerMarkerFileName), "test");
        File.WriteAllText(Path.Combine(stale, "run.bin"), "partial");
        File.WriteAllText(Path.Combine(unowned, "keep.txt"), "keep");

        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(unowned));
    }

    /// <summary>未取得 WAL 独占租约的第二个实例不能清理仍存活实例的查询工作区。</summary>
    [Fact]
    public void Open_WhileDatabaseIsActive_DoesNotCleanActiveWorkspace()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        string active = Path.Combine(
            _root,
            SqlSpillWorkspace.DirectoryName,
            "query-active");
        Directory.CreateDirectory(active);
        File.WriteAllText(Path.Combine(active, SqlSpillWorkspace.OwnerMarkerFileName), "active");

        string equivalentRoot = Path.Combine(_root, ".");
        Assert.Throws<IOException>(() => Tsdb.Open(new TsdbOptions { RootDirectory = equivalentRoot }));

        Assert.True(Directory.Exists(active));
    }

    /// <summary>指向同一数据库的目录链接别名不能绕过进程内根目录所有权。</summary>
    [Fact]
    public void Open_ThroughDirectoryLinkAlias_WhileDatabaseIsActive_ThrowsIOException()
    {
        Directory.CreateDirectory(_root);
        string aliasParent = $"{_root}-alias";
        try
        {
            Directory.CreateSymbolicLink(aliasParent, _root);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            return;
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            string directRoot = Path.Combine(_root, "database");
            string aliasRoot = Path.Combine(aliasParent, "database");
            using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = directRoot });

            Assert.Throws<IOException>(() => Tsdb.Open(new TsdbOptions { RootDirectory = aliasRoot }));
        }
        finally
        {
            if (Directory.Exists(aliasParent))
                Directory.Delete(aliasParent);
        }
    }

    /// <summary>正常关闭数据库后必须释放进程内根目录所有权并允许重新打开。</summary>
    [Fact]
    public void Open_AfterDatabaseIsDisposed_AllowsReopen()
    {
        using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
        }

        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        Assert.Equal(_root, reopened.RootDirectory);
    }

    /// <summary>崩溃模拟关闭也必须停止后台资源、释放根目录所有权并允许恢复打开。</summary>
    [Fact]
    public void Open_AfterCrashSimulationCloseWal_AllowsReopen()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        database.CrashSimulationCloseWal();

        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        Assert.Equal(_root, reopened.RootDirectory);
    }

    /// <summary>全局预算必须拒绝超额并在释放后允许其他查询继续预留。</summary>
    [Fact]
    public void GlobalBudget_ContentionAndRelease_DoesNotOvercommit()
    {
        var budget = new SqlGlobalMemoryBudget(100);

        Assert.True(budget.TryReserve(60));
        Assert.False(budget.TryReserve(41));
        Assert.Equal(60, budget.ReservedBytes);
        budget.Release(60);
        Assert.True(budget.TryReserve(100));
        budget.Release(100);

        Assert.Equal(0, budget.ReservedBytes);
    }

    /// <summary>算子部分归还预算后，同一查询中的其他算子应能立即复用且不能超额释放。</summary>
    [Fact]
    public void OperatorReservation_PartialRelease_IsReusableAndValidated()
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 100 }))
        using (var first = SqlQueryResources.Current!.CreateReservation())
        using (var second = SqlQueryResources.Current!.CreateReservation())
        {
            Assert.True(first.TryReserve(80));
            Assert.False(second.TryReserve(21));
            first.Release(30);
            Assert.True(second.TryReserve(50));
            Assert.Throws<InvalidOperationException>(() => first.Release(51));
        }

        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>EXPLAIN ANALYZE 必须公开真实 spill 次数、字节数和预算内内存峰值。</summary>
    [Fact]
    public void ExplainAnalyze_ForcedSortSpill_ReportsResourceEvidence()
    {
        using Tsdb database = CreateDatabase();

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: null,
            "EXPLAIN ANALYZE SELECT id, value FROM spill_items ORDER BY value DESC",
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 96 }));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.True(Convert.ToInt64(values["actual_spill_count"]) > 0);
        Assert.True(Convert.ToInt64(values["actual_spill_bytes"]) > 0);
        Assert.InRange(Convert.ToInt64(values["actual_peak_memory_bytes"]), 0, 96);
        AssertNoQueryWorkspaces();
    }

    /// <summary>公开 ExecuteSelect AST 入口也必须建立数据库默认预算，不能绕过 spill 治理。</summary>
    [Fact]
    public void ExecuteSelect_DirectEntry_UsesDatabaseDefaultBudget()
    {
        using Tsdb database = CreateDatabase(queryLimitBytes: 96);
        var statement = Assert.IsType<SonnetDB.Sql.Ast.SelectStatement>(
            SonnetDB.Sql.SqlParser.Parse(
                "SELECT id, value FROM spill_items ORDER BY value DESC"));
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = SqlExecutor.ExecuteSelect(database, statement);

        Assert.Equal(40, result.Rows.Count);
        Assert.True(metrics.Complete().SpillCount > 0);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>
    /// 大 BLOB 同值范围组必须在预算内外排并逐行归并，不能把整个组绕过预算常驻内存。
    /// </summary>
    [Fact]
    public void OrderedResidualRange_LargeBlobTieGroup_SpillsWithinBudget()
    {
        using Tsdb database = CreateDatabase();
        SqlExecutor.Execute(database, """
            CREATE TABLE ordered_blob_events (
                id STRING NOT NULL,
                capture_time INT NOT NULL,
                created_at INT NOT NULL,
                attempt_count INT NOT NULL,
                payload BLOB NOT NULL,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(
            database,
            "CREATE INDEX ix_ordered_blob_events ON ordered_blob_events (capture_time, created_at, id)");
        database.Tables.Open("ordered_blob_events").InsertMany(
            Enumerable.Range(0, 64)
                .Select(static value => (IReadOnlyList<object?>)new object?[]
                {
                    $"event-{value:D3}",
                    10L,
                    (long)((value * 17) % 11),
                    value % 9 == 0 ? 1L : 0L,
                    new byte[8 * 1024],
                })
                .ToArray());
        const string query = """
            SELECT id, created_at FROM ordered_blob_events
            WHERE capture_time >= 10 AND attempt_count < 1
            ORDER BY capture_time ASC, created_at ASC, id ASC
            LIMIT 10
            """;

        (SelectExecutionResult expected, SqlExecutionMetricsSnapshot memoryMetrics) = Execute(
            database,
            query,
            memoryLimitBytes: 1024 * 1024);
        (SelectExecutionResult actual, SqlExecutionMetricsSnapshot spillMetrics) = Execute(
            database,
            query,
            memoryLimitBytes: 32 * 1024);

        AssertRowsEqual(expected, actual);
        Assert.Equal(0, memoryMetrics.SpillCount);
        Assert.True(spillMetrics.SpillCount > 0);
        Assert.True(spillMetrics.SpillBytes > 0);
        Assert.InRange(spillMetrics.PeakMemoryBytes, 0, 32 * 1024);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    private Tsdb CreateDatabase(long queryLimitBytes = 1024 * 1024)
    {
        var database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            SqlMemory = new SqlMemoryOptions
            {
                QueryLimitBytes = queryLimitBytes,
                GlobalLimitBytes = 2 * 1024 * 1024,
            },
        });
        SqlExecutor.Execute(database, "CREATE TABLE spill_items (id INT, category STRING, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE spill_lookup (id INT, join_key STRING, label STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE spill_hash_left (id INT, event_time DATETIME, blob_key BLOB, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE spill_hash_right (id INT, event_time INT, blob_key BLOB, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE TABLE spill_union (id INT, bucket INT, alternate INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "CREATE INDEX ix_spill_union_bucket ON spill_union (bucket)");
        SqlExecutor.Execute(database, "CREATE INDEX ix_spill_union_alternate ON spill_union (alternate)");

        database.Tables.Open("spill_items").InsertMany(
            Enumerable.Range(0, 40)
                .Select(static value => (IReadOnlyList<object?>)new object?[]
                {
                    (long)value,
                    $"group-{value % 5}",
                    (long)((value * 17) % 41),
                })
                .ToArray());
        database.Tables.Open("spill_lookup").InsertMany(
            Enumerable.Range(0, 5)
                .Select(static value => (IReadOnlyList<object?>)new object?[]
                {
                    (long)value,
                    $"group-{value}",
                    $"label-{value}",
                })
                .ToArray());
        DateTime hashEpoch = new(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
        database.Tables.Open("spill_hash_left").InsertMany(
            Enumerable.Range(0, 12)
                .Select(value => (IReadOnlyList<object?>)new object?[]
                {
                    (long)value,
                    value == 11 ? null : hashEpoch.AddMinutes(value),
                    value == 11 ? null : new byte[] { 0x53, 0x44, (byte)value },
                })
                .ToArray());
        database.Tables.Open("spill_hash_right").InsertMany(
            Enumerable.Range(0, 12)
                .Select(value => (IReadOnlyList<object?>)new object?[]
                {
                    (long)(100 + value),
                    value == 11
                        ? null
                        : new DateTimeOffset(hashEpoch.AddMinutes(value)).ToUnixTimeMilliseconds(),
                    value == 11 ? null : new byte[] { 0x53, 0x44, (byte)value },
                })
                .ToArray());
        database.Tables.Open("spill_union").InsertMany(
            Enumerable.Range(0, 40)
                .Select(static value => (IReadOnlyList<object?>)new object?[]
                {
                    (long)value,
                    (long)(value % 3),
                    (long)(value % 4),
                })
                .ToArray());
        return database;
    }

    private static (SelectExecutionResult Result, SqlExecutionMetricsSnapshot Metrics) Execute(
        Tsdb database,
        string sql,
        long memoryLimitBytes)
    {
        var metrics = new SqlExecutionMetrics();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            databaseName: null,
            sql,
            parameters: null,
            controlPlane: null,
            new SqlExecutionOptions
            {
                BlockingOperatorMemoryLimitBytes = memoryLimitBytes,
                Metrics = metrics,
            }));
        return (result, metrics.Complete());
    }

    private static IEnumerable<object?[]> RowsThatCancel(CancellationTokenSource cancellation)
    {
        for (int i = 0; i < 20; i++)
        {
            if (i == 5)
                cancellation.Cancel();
            yield return [(long)i, new string('x', 64)];
        }
    }

    /// <summary>生成逆序输入，并在枚举完全结束时通知测试比较器。</summary>
    private static IEnumerable<object?[]> RowsThatMarkCompletion(int count, Action onCompleted)
    {
        for (int value = count; value > 0; value--)
            yield return [(long)value];
        onCompleted();
    }

    /// <summary>在一字节预算下执行两行稳定排序，强制走 oversized 最终归并路径。</summary>
    private static object?[][] ExecuteOversizedTwoRowSort(
        Tsdb database,
        IComparer<object?[]> comparer)
    {
        using (SqlQueryResources.EnterRoot(
            database,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1 }))
        {
            return TopN.OrderByThenPaginate(
                new[]
                {
                    new object?[] { 2L, new string('x', 4096) },
                    new object?[] { 1L, new string('y', 4096) },
                },
                comparer,
                offset: 0,
                fetch: null,
                SqlSpillCodecs.ArrayRows);
        }
    }

    /// <summary>生成每行都大于一字节测试预算的排序输入。</summary>
    private static IEnumerable<object?[]> CreateOversizedRows(int count)
    {
        for (int index = 0; index < count; index++)
            yield return [(long)(index % 7), (long)index, new string('x', 64)];
    }

    /// <summary>统计当前测试数据库所有活动查询工作区中的 run 文件数。</summary>
    private int CountLiveRunFiles()
    {
        string parent = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        if (!Directory.Exists(parent))
            return 0;
        return Directory.EnumerateDirectories(parent, "query-*", SearchOption.TopDirectoryOnly)
            .Sum(static directory => Directory.EnumerateFiles(
                directory,
                "*.bin",
                SearchOption.TopDirectoryOnly).Count());
    }

    private static void AssertRowsEqual(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int rowIndex = 0; rowIndex < expected.Rows.Count; rowIndex++)
        {
            Assert.Equal(expected.Rows[rowIndex].Count, actual.Rows[rowIndex].Count);
            for (int columnIndex = 0; columnIndex < expected.Rows[rowIndex].Count; columnIndex++)
            {
                Assert.True(
                    SqlScalarComparer.ValuesEqual(
                        expected.Rows[rowIndex][columnIndex],
                        actual.Rows[rowIndex][columnIndex]),
                    $"第 {rowIndex} 行第 {columnIndex} 列不一致。");
            }
        }
    }

    /// <summary>确认当前测试根目录下没有遗留查询工作区。</summary>
    private void AssertNoQueryWorkspaces()
        => AssertNoQueryWorkspaces(_root);

    /// <summary>确认指定数据库根目录下没有遗留查询工作区。</summary>
    private static void AssertNoQueryWorkspaces(string root)
    {
        string parent = Path.Combine(root, SqlSpillWorkspace.DirectoryName);
        if (!Directory.Exists(parent))
            return;
        Assert.Empty(Directory.EnumerateDirectories(parent, "query-*", SearchOption.TopDirectoryOnly));
    }

    /// <summary>清理测试目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
