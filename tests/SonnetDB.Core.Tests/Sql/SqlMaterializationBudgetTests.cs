using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 M42 SQL-002 的显式关系 SELECT 物化预算和提前拒绝合同。</summary>
public sealed class SqlMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-sql-materialization-{Guid.NewGuid():N}");

    /// <summary>未启用预算的调用继续返回完整结果；精确行数边界不产生预览。</summary>
    [Fact]
    public void Execute_DefaultAndExactRowLimit_ReturnCompleteResults()
    {
        using Tsdb db = OpenItems(3);
        SelectExecutionResult baseline = Select(db, "SELECT id FROM items");
        SelectExecutionResult bounded = Select(db, "SELECT id FROM items",
            new SqlExecutionOptions { MaxMaterializedRows = 3, MaxMaterializedBytes = 288 });

        Assert.Equal(3, baseline.Rows.Count);
        Assert.Equal(baseline.Rows.Select(row => row[0]), bounded.Rows.Select(row => row[0]));
        Assert.False(bounded.Truncated);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>原始查询在追加超限结果行前停止扫描，失败后仍可执行完整查询。</summary>
    [Fact]
    public void Execute_RawRowsOverLimit_StopsScanAndReleasesBudget()
    {
        using Tsdb db = OpenItems(20);
        int decoded = 0;
        db.Tables.Open("items").RowDecodedTestHook = _ => decoded++;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT id FROM items", new SqlExecutionOptions { MaxMaterializedRows = 2 }));

        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.InRange(decoded, 1, 3);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
        db.Tables.Open("items").RowDecodedTestHook = null;
        Assert.Equal(20, Select(db, "SELECT id FROM items").Rows.Count);
    }

    /// <summary>UTF-16 字符串按估算字节判定，精确边界允许完整返回。</summary>
    [Theory]
    [InlineData(296, true)]
    [InlineData(295, false)]
    public void Execute_StringByteLimit_EnforcesExactEstimate(long bytes, bool succeeds)
    {
        using Tsdb db = Open();
        string payload = new('x', 100);
        var parameters = new SqlParameters().AddNamed("payload", payload);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = bytes };

        if (succeeds)
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db, null, "SELECT @payload AS payload", parameters, null, options));
            Assert.Equal(payload, Assert.Single(result.Rows)[0]);
            Assert.False(result.Truncated);
        }
        else
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
                db, null, "SELECT @payload AS payload", parameters, null, options));
            Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>二进制值的字节载荷计入预算，行数上限不会掩盖大值。</summary>
    [Fact]
    public void Execute_BinaryValueOverByteLimit_RejectsSingleWideRow()
    {
        using Tsdb db = Open();
        var parameters = new SqlParameters().AddNamed("payload", new byte[1_024]);
        Execute(db, "CREATE TABLE blobs (id INT, payload BLOB, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, null, "INSERT INTO blobs (id, payload) VALUES (1, @payload)", parameters);
        Assert.Equal(1_024, Assert.IsType<byte[]>(Assert.Single(Select(db, "SELECT payload FROM blobs").Rows)[0]).Length);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(
            db, "SELECT payload FROM blobs",
            new SqlExecutionOptions { MaxMaterializedRows = 100, MaxMaterializedBytes = 1_000 }));

        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>无效预算先于 DML 分发拒绝，目标数据保持不变。</summary>
    [Theory]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(null, 0L)]
    [InlineData(null, -1L)]
    public void Execute_ZeroOrNegativeLimit_RejectsBeforeWrite(long? rows, long? bytes)
    {
        using Tsdb db = OpenItems(1);

        Assert.Throws<ArgumentOutOfRangeException>(() => Execute(db,
            "INSERT INTO items (id, category, value, payload) VALUES (2, 0, 2, 'new') RETURNING id",
            new SqlExecutionOptions { MaxMaterializedRows = rows, MaxMaterializedBytes = bytes }));

        Assert.Equal(1L, Assert.Single(Select(db, "SELECT id FROM items").Rows)[0]);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>当前预算仅用于只读关系查询，自动提交与显式事务 RETURNING 都在写入前拒绝。</summary>
    [Theory]
    [InlineData("INSERT INTO items (id, category, value, payload) VALUES (2, 0, 2, 'new') RETURNING id", false)]
    [InlineData("UPDATE items SET value = 999 WHERE id = 1 RETURNING id, value", false)]
    [InlineData("DELETE FROM items WHERE id = 1 RETURNING id", false)]
    [InlineData("INSERT INTO items (id, category, value, payload) VALUES (2, 0, 2, 'new') RETURNING id", true)]
    [InlineData("UPDATE items SET value = 999 WHERE id = 1 RETURNING id, value", true)]
    [InlineData("DELETE FROM items WHERE id = 1 RETURNING id", true)]
    public void Execute_ReturningWithBudget_RejectsBeforeMutation(string sql, bool explicitTransaction)
    {
        using Tsdb db = OpenItems(1);
        SqlTransactionContext? transaction = explicitTransaction
            ? Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN")) : null;

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Execute(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100, MaxMaterializedBytes = 1_000_000 }, transaction));

        Assert.Contains("RETURNING", error.Message, StringComparison.Ordinal);
        Assert.Equal(new object?[] { 1L, 1L }, Assert.Single(Select(db,
            "SELECT id, value FROM items", transaction: transaction).Rows));
        if (transaction is not null)
            Execute(db, "COMMIT", transaction: transaction);
        Assert.Equal(new object?[] { 1L, 1L }, Assert.Single(Select(db, "SELECT id, value FROM items").Rows));
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>SELECT 超限不会撤销显式事务中此前合法的缓冲写入。</summary>
    [Fact]
    public void Execute_ReadYourWritesOverLimit_PreservesExistingTransaction()
    {
        using Tsdb db = OpenItems(1);
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(db, "BEGIN"));
        Execute(db, "INSERT INTO items (id, category, value, payload) VALUES (2, 0, 2, 'new')",
            transaction: transaction);

        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT id FROM items",
            new SqlExecutionOptions { MaxMaterializedRows = 1 }, transaction));

        Assert.Equal(2, Select(db, "SELECT id FROM items", transaction: transaction).Rows.Count);
        Execute(db, "COMMIT", transaction: transaction);
        Assert.Equal(2, Select(db, "SELECT id FROM items").Rows.Count);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>嵌套派生表、CTE 和集合运算不重置调用链预算。</summary>
    [Theory]
    [InlineData("SELECT id FROM (SELECT id FROM items LIMIT 2) AS source", 2, 2)]
    [InlineData("WITH source AS (SELECT id FROM items LIMIT 2) SELECT id FROM source", 2, 2)]
    [InlineData("SELECT 1 AS value UNION ALL SELECT 2 AS value", 2, 2)]
    [InlineData("WITH RECURSIVE r (id) AS (SELECT 1 AS id UNION ALL SELECT id + 1 FROM r WHERE id < 4) SELECT id FROM r", 4, 4)]
    public void Execute_NestedBranches_ShareRootBudget(string sql, long smallLimit, int expectedRows)
    {
        using Tsdb db = OpenItems(3);
        var generous = new SqlExecutionOptions { MaxMaterializedRows = 1_000, MaxMaterializedBytes = 1_000_000 };
        Assert.Equal(expectedRows, Select(db, sql, generous).Rows.Count);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = smallLimit }));

        Assert.Contains("SQL 执行器", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    /// <summary>阻塞阶段同样受累计物化预算限制，即使最终 SQL LIMIT 只需一行。</summary>
    [Theory]
    [InlineData("SELECT id FROM items ORDER BY value DESC LIMIT 1")]
    [InlineData("SELECT category, count(*) FROM items GROUP BY category")]
    [InlineData("SELECT DISTINCT category FROM items")]
    [InlineData("SELECT i.id, l.id FROM items AS i JOIN items AS l ON i.category = l.category")]
    [InlineData("SELECT id, row_number() OVER (ORDER BY value) AS position FROM items")]
    public void Execute_BlockingOperators_RejectAndReleaseSharedBudget(string sql)
    {
        using Tsdb db = OpenItems(12);
        SelectExecutionResult baseline = Select(db, sql);
        SelectExecutionResult bounded = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 10_000, MaxMaterializedBytes = 1_000_000 });
        Assert.Equal(baseline.Rows.Count, bounded.Rows.Count);
        for (int i = 0; i < baseline.Rows.Count; i++)
            Assert.Equal(baseline.Rows[i], bounded.Rows[i]);

        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 1 }));

        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>spilled 排序依然受物化预算约束，超限后删除查询临时工作区。</summary>
    [Fact]
    public void Execute_SpilledSortOverByteLimit_RejectsAndCleansWorkspace()
    {
        using Tsdb db = OpenItems(12);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT payload, value AS sort_key FROM items ORDER BY sort_key DESC",
            new SqlExecutionOptions
            {
                MaxMaterializedBytes = 1_000,
                BlockingOperatorMemoryLimitBytes = 400,
                Metrics = metrics,
            }));

        Assert.True(metrics.Complete().SpillCount > 0);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        AssertNoQueryWorkspaces();
    }

    /// <summary>每次根调用均有独立预算，顺序执行不会累计上一条 SELECT 的计费。</summary>
    [Fact]
    public void Execute_SeparateRootCalls_DoNotShareCounters()
    {
        using Tsdb db = OpenItems(1);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1 };

        Assert.Single(Select(db, "SELECT id FROM items", options).Rows);
        Assert.Single(Select(db, "SELECT id FROM items", options).Rows);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>其它模型和系统结果路径在预算模式下明确拒绝。</summary>
    [Theory]
    [InlineData("SELECT * FROM information_schema.tables")]
    [InlineData("SELECT * FROM json_each('[1,2]')")]
    public void Execute_UnsupportedSourceWithBudget_RejectsExplicitly(string sql)
    {
        using Tsdb db = OpenItems(1);
        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));

        Assert.Single(Select(db, "SELECT id FROM items").Rows);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>EXPLAIN 的完整结果受预算约束；ANALYZE 实际执行使用同一预算。</summary>
    [Theory]
    [InlineData("EXPLAIN SELECT id FROM items")]
    [InlineData("EXPLAIN ANALYZE SELECT id FROM items")]
    public void Execute_ExplainSelect_UsesMaterializationBudget(string sql)
    {
        using Tsdb db = OpenItems(3);
        Assert.NotEmpty(Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 1_000, MaxMaterializedBytes = 1_000_000 }).Rows);

        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 1 }));

        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>EXPLAIN 与 ANALYZE 的 Graph 源在查找目标图之前明确拒绝预算模式。</summary>
    [Theory]
    [InlineData("EXPLAIN SELECT * FROM graph_nodes(missing_graph)")]
    [InlineData("EXPLAIN ANALYZE SELECT * FROM graph_nodes(missing_graph)")]
    public void Execute_ExplainGraphWithBudget_RejectsBeforeTargetDispatch(string sql)
    {
        using Tsdb db = Open();

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));

        Assert.Contains("Graph", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>取消错误优先于新物化计费，并保持已有例程错误码。</summary>
    [Fact]
    public void Execute_CancelledBudgetedQuery_PreservesCancellationContract()
    {
        using Tsdb db = OpenItems(3);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db,
            "SELECT id FROM items", new SqlExecutionOptions
            {
                MaxMaterializedRows = 1,
                CancellationToken = cancellation.Token,
            }));

        Assert.Equal("routine_cancelled", error.Code);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    /// <summary>并发 worker 不能通过竞争条件超过同一根调用的行数预算。</summary>
    [Fact]
    public void Retain_ConcurrentWorkers_EnforcesSingleRootBudget()
    {
        using Tsdb db = Open();
        var options = new SqlExecutionOptions { MaxMaterializedRows = 7 };
        int accepted = 0;
        using (SqlQueryResources.EnterRoot(db, options))
        using (var budget = SqlRowRetentionBudget.EnterExecution(options))
        {
            Assert.NotNull(budget);
            Parallel.For(0, 32, _ =>
            {
                try
                {
                    budget!.Retain([1L]);
                    Interlocked.Increment(ref accepted);
                }
                catch (InvalidOperationException)
                {
                    // 并发预算拒绝是该回归的预期结果。
                }
            });
            Assert.Equal(7, accepted);
        }
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    /// <summary>预算模式在扫描和用户回调前验证整个查询树。</summary>
    [Fact]
    public void Execute_UnsupportedNestedSourceOrUserFunction_RejectsBeforeScanAndCallback()
    {
        using Tsdb db = OpenItems(3);
        int decoded = 0;
        int called = 0;
        db.Tables.Open("items").RowDecodedTestHook = _ => decoded++;
        db.Functions.RegisterScalar("read_callback", _ => { called++; return 1L; });
        var options = new SqlExecutionOptions { MaxMaterializedRows = 100 };

        Assert.Throws<NotSupportedException>(() => Select(db,
            "SELECT i.id FROM items AS i WHERE EXISTS (SELECT * FROM information_schema.tables)", options));
        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT read_callback() FROM items", options));

        Assert.Equal(0, decoded);
        Assert.Equal(0, called);
        db.Tables.Open("items").RowDecodedTestHook = null;
        Assert.Equal(3, Select(db, "SELECT read_callback() FROM items").Rows.Count);
        Assert.Equal(3, called);
    }

    /// <summary>清理测试数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private Tsdb OpenItems(int count)
    {
        Tsdb db = Open();
        Execute(db, "CREATE TABLE items (id INT, category INT, value INT, payload STRING, PRIMARY KEY (id))");
        string payload = new('x', 100);
        string rows = string.Join(", ", Enumerable.Range(1, count)
            .Select(id => $"({id}, {id % 3}, {id}, '{payload}')"));
        Execute(db, $"INSERT INTO items (id, category, value, payload) VALUES {rows}");
        return db;
    }

    private void AssertNoQueryWorkspaces()
    {
        string spillRoot = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        if (Directory.Exists(spillRoot))
            Assert.Empty(Directory.EnumerateDirectories(spillRoot));
    }

    private static object? Execute(Tsdb db, string sql, SqlExecutionOptions? options = null,
        SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction,
            options ?? SqlExecutionOptions.Default);

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null,
        SqlTransactionContext? transaction = null)
        => Assert.IsType<SelectExecutionResult>(Execute(db, sql, options, transaction));
}
