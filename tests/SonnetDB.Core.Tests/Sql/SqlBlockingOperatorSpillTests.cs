using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M41 #379 阻塞算子预算、spill 正确性、取消与临时文件生命周期。</summary>
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

        Assert.Throws<IOException>(() => Tsdb.Open(new TsdbOptions { RootDirectory = _root }));

        Assert.True(Directory.Exists(active));
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

    private void AssertNoQueryWorkspaces()
    {
        string parent = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
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
