using System.Globalization;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证直接 measurement SELECT 的执行期物化预算、过滤和取消合同。</summary>
public sealed class SqlMeasurementMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-measurement-budget-{Guid.NewGuid():N}");

    /// <summary>大量候选点遇到小结果预算时仅消费前沿点，不先物化完整 SQL 结果。</summary>
    [Fact]
    public void Execute_ManyPointsOverRowBudget_StopsBeforeFullResultAndReleasesResources()
    {
        using Tsdb db = OpenPoints(4_096);
        var metrics = new SqlExecutionMetrics();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT value FROM readings", new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        Assert.InRange(metrics.Complete().CandidateRows, 3, 4);
        AssertReleased(db);
        Assert.Equal(4_096, Select(db, "SELECT value FROM readings").Rows.Count);
    }

    /// <summary>只计入过滤后的结果，长扫描可在精确结果预算内完成。</summary>
    [Fact]
    public void Execute_FieldAndTagFilters_CountsOnlyRetainedRows()
    {
        using Tsdb db = OpenPoints(4_096);

        SelectExecutionResult result = Select(db,
            "SELECT value FROM readings WHERE host = 'h1' AND value >= 4094",
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 192 });

        Assert.Equal([4094L, 4095L], result.Rows.Select(static row => (long)row[0]!));
        Assert.False(result.Truncated);
        AssertReleased(db);
    }

    /// <summary>UTF-16 字符串、时间及多列载荷共用准确的内部估算边界。</summary>
    [Theory]
    [InlineData(360, true)]
    [InlineData(359, false)]
    public void Execute_StringAndMultipleColumns_EnforcesEstimatedByteBoundary(long bytes, bool succeeds)
    {
        using Tsdb db = OpenPoints(1);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = bytes };

        if (succeeds)
        {
            SelectExecutionResult result = Select(db, "SELECT time, value, label FROM readings", options);
            Assert.Equal(new string('x', 100), Assert.Single(result.Rows)[2]);
            Assert.False(result.Truncated);
        }
        else
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
                "SELECT time, value, label FROM readings", options));
            Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        }
        AssertReleased(db);
    }

    /// <summary>LIMIT 和 OFFSET 在过滤后下推，跳过的行不计入结果预算。</summary>
    [Theory]
    [InlineData("SELECT time, value FROM readings WHERE value >= 4 LIMIT 2 OFFSET 3", 7L, 8L)]
    [InlineData("SELECT time, value FROM readings WHERE value >= 4 OFFSET 3 ROWS FETCH NEXT 2 ROWS ONLY", 7L, 8L)]
    [InlineData("SELECT time, value FROM readings OFFSET 8", 8L, 9L)]
    public void Execute_PaginationWithFilter_PreservesCompleteSqlPage(string sql, long first, long second)
    {
        using Tsdb db = OpenPoints(10);

        SelectExecutionResult bounded = Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 2, MaxMaterializedBytes = 256 });

        Assert.Equal([first, second], bounded.Rows.Select(static row => (long)row[1]!));
        AssertEquivalent(Select(db, sql), bounded);
        AssertReleased(db);
    }

    /// <summary>LIMIT 0 不启动点流，预算不足以容纳一行时仍可成功返回空页。</summary>
    [Fact]
    public void Execute_ZeroLimit_DoesNotReadPoints()
    {
        using Tsdb db = OpenPoints(10);
        var metrics = new SqlExecutionMetrics();

        SelectExecutionResult result = Select(db, "SELECT * FROM readings LIMIT 0",
            new SqlExecutionOptions { MaxMaterializedBytes = 1, Metrics = metrics });

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>跨 series 使用同一根预算，不能在切换 series 时重置累计计数。</summary>
    [Fact]
    public void Execute_MultipleSeries_SharesRootBudgetAndPreservesNaturalOrder()
    {
        using Tsdb db = OpenPoints(2);
        SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value, label) VALUES (0, 'h2', 10, 'a'), (1, 'h2', 11, 'b')");
        const string sql = "SELECT time, host, value FROM readings";

        Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 3 }));
        AssertEquivalent(Select(db, sql), Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 4 }));
        AssertReleased(db);
    }

    /// <summary>段文件点流在超限退出时释放租约，后续预算查询仍读取完整指定 SQL 页。</summary>
    [Fact]
    public void Execute_FlushedSegmentOverRowBudget_ReleasesPointStreamAndAllowsNextQuery()
    {
        using Tsdb db = OpenPoints(32);
        db.FlushNow();
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT value FROM readings",
            new SqlExecutionOptions { MaxMaterializedRows = 2, Metrics = metrics }));

        Assert.InRange(metrics.Complete().CandidateRows, 3, 4);
        AssertReleased(db);
        Assert.Equal([0L, 1L], Select(db, "SELECT value FROM readings LIMIT 2",
            new SqlExecutionOptions { MaxMaterializedRows = 2 }).Rows.Select(static row => (long)row[0]!));
        AssertReleased(db);
    }

    /// <summary>未出现在某个 series 中的 TAG 仍投影为 NULL，不创造额外 series 或字段。</summary>
    [Fact]
    public void Execute_MissingTag_ReturnsNullWithDefaultEquivalentResult()
    {
        using Tsdb db = OpenPoints(0);
        SqlExecutor.Execute(db, "CREATE MEASUREMENT sparse_tags (host TAG, region TAG, value FIELD INT)");
        SqlExecutor.Execute(db, "INSERT INTO sparse_tags (time, host, value) VALUES (1, 'h1', 7)");
        const string sql = "SELECT time, host, region, value FROM sparse_tags";

        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 1 });

        AssertEquivalent(Select(db, sql), result);
        Assert.Null(Assert.Single(result.Rows)[2]);
        AssertReleased(db);
    }

    /// <summary>多字段稀疏时间轴逐点合并，并保持缺失字段的 NULL 和标量投影语义。</summary>
    [Fact]
    public void Execute_SparseFieldsAndScalarProjection_PreservesUnionTimelineAndNulls()
    {
        using Tsdb db = OpenPoints(0);
        SqlExecutor.Execute(db, "INSERT INTO readings (time, host, value) VALUES (1, 'h1', 2), (3, 'h1', 4)");
        SqlExecutor.Execute(db, "INSERT INTO readings (time, host, label) VALUES (2, 'h1', 'middle')");
        const string sql = "SELECT time, value, label, value + 1 AS next_value FROM readings";

        SelectExecutionResult bounded = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 3 });

        AssertEquivalent(Select(db, sql), bounded);
        Assert.Equal([1L, 2L, 3L], bounded.Rows.Select(static row => (long)row[0]!));
        Assert.Null(bounded.Rows[1][1]);
        Assert.Null(bounded.Rows[0][2]);
        Assert.Null(bounded.Rows[1][3]);
        AssertEquivalent(Select(db, "SELECT time, label FROM readings WHERE value IS NULL OR value IN (4)"),
            Select(db, "SELECT time, label FROM readings WHERE value IS NULL OR value IN (4)",
                new SqlExecutionOptions { MaxMaterializedRows = 2 }));
        AssertReleased(db);
    }

    /// <summary>字符串和内置标量 WHERE 逐点求值，投影值及 NULL 传播与默认路径一致。</summary>
    [Fact]
    public void Execute_StringAndScalarFilter_PreservesDefaultResult()
    {
        using Tsdb db = OpenPoints(0);
        SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value, label) VALUES (1, 'h1', 1, 'ready'), (2, 'h1', 2, 'skip'), (3, 'h1', 3, 'READY')");
        const string sql = "SELECT time, UPPER(label) AS state, value * 2 AS doubled FROM readings WHERE UPPER(label) = 'READY' AND value + 1 > 1 LIMIT 2";

        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 2 });

        AssertEquivalent(Select(db, sql), result);
        Assert.Equal([1L, 3L], result.Rows.Select(static row => (long)row[0]!));
        AssertReleased(db);
    }

    /// <summary>预算路径只计算 SQL 页内投影，LIMIT 后的 CAST 错误仍可由完整查询暴露。</summary>
    [Fact]
    public void Execute_LimitBeforeInvalidLaterProjection_ReturnsCompletePageWithoutEvaluatingLaterRow()
    {
        using Tsdb db = OpenPoints(0);
        SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value, label) VALUES (1, 'h1', 1, '7'), (2, 'h1', 2, 'invalid')");
        var options = new SqlExecutionOptions { MaxMaterializedRows = 1 };

        SelectExecutionResult result = Select(db, "SELECT CAST(label AS INT) AS parsed FROM readings LIMIT 1", options);

        Assert.Equal(7L, Assert.Single(result.Rows)[0]);
        Assert.False(result.Truncated);
        AssertEquivalent(Select(db, "SELECT CAST(label AS INT) AS parsed FROM readings WHERE value = 1 LIMIT 1"), result);
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT CAST(label AS INT) AS parsed FROM readings"));
        AssertReleased(db);
    }

    /// <summary>保留 measurement 和列名称的原始拼写，并遵守未引用名称不区分大小写的绑定合同。</summary>
    [Fact]
    public void Execute_MixedCaseIdentifiers_PreservesBoundNames()
    {
        using Tsdb db = OpenPoints(0);
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Devices (Host TAG, Value FIELD INT)");
        SqlExecutor.Execute(db, "INSERT INTO Devices (time, Host, Value) VALUES (1, 'h1', 7)");
        const string sql = "SELECT d.Host, d.Value FROM devices AS d WHERE d.VALUE = 7";

        SelectExecutionResult bounded = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 1 });

        Assert.Equal(["Host", "Value"], bounded.Columns);
        AssertEquivalent(Select(db, sql), bounded);
        AssertEquivalent(Select(db, "SELECT \"Value\" FROM \"Devices\""),
            Select(db, "SELECT \"Value\" FROM \"Devices\"", new SqlExecutionOptions { MaxMaterializedRows = 1 }));
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT \"value\" FROM \"Devices\"",
            new SqlExecutionOptions { MaxMaterializedRows = 1 }));
        AssertReleased(db);
    }

    /// <summary>无法保证执行预算的阻塞或混合查询在读取任何点之前明确拒绝。</summary>
    [Theory]
    [InlineData("SELECT SUM(value) FROM readings")]
    [InlineData("SELECT time, SUM(value) FROM readings GROUP BY time(1s)")]
    [InlineData("SELECT DISTINCT value FROM readings")]
    [InlineData("SELECT moving_average(value, 2) FROM readings")]
    [InlineData("SELECT value FROM readings UNION ALL SELECT 1")]
    [InlineData("SELECT 1 UNION ALL SELECT value FROM readings")]
    [InlineData("SELECT value FROM (SELECT value FROM readings) AS source")]
    [InlineData("WITH source AS (SELECT value FROM readings) SELECT value FROM source")]
    [InlineData("SELECT value FROM readings WHERE EXISTS (SELECT 1)")]
    public void Execute_UnsupportedMeasurementShape_RejectsBeforePointRead(string sql)
    {
        using Tsdb db = OpenPoints(4);
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db, sql,
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>用户函数在扫描和回调前拒绝，默认调用仍保留既有能力。</summary>
    [Fact]
    public void Execute_UserCallback_RejectsBeforeInvocation()
    {
        using Tsdb db = OpenPoints(2);
        int called = 0;
        db.Functions.RegisterScalar("read_callback", _ => { called++; return 1L; });

        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT read_callback() FROM readings",
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));

        Assert.Equal(0, called);
        Assert.Equal(2, Select(db, "SELECT read_callback() FROM readings").Rows.Count);
        Assert.Equal(2, called);
        AssertReleased(db);
    }

    /// <summary>不支持的 FIELD 类型在空 measurement 上也拒绝，避免到第一条值才发现合同缺口。</summary>
    [Fact]
    public void Execute_GeoField_RejectsBeforeScan()
    {
        using Tsdb db = OpenPoints(0);
        SqlExecutor.Execute(db, "CREATE MEASUREMENT locations (host TAG, position FIELD GEOPOINT)");

        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT position FROM locations",
            new SqlExecutionOptions { MaxMaterializedRows = 100 }));

        AssertReleased(db);
    }

    /// <summary>取消令牌及已过期截止时间保持根调用取消错误码，之后可重新执行。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_CancelledRoot_ReleasesBudgetAndAllowsNextQuery(bool expiredDeadline)
    {
        using Tsdb db = OpenPoints(10);
        using var cancellation = new CancellationTokenSource();
        if (!expiredDeadline)
            cancellation.Cancel();
        var options = new SqlExecutionOptions
        {
            MaxMaterializedRows = 1,
            CancellationToken = cancellation.Token,
            DeadlineUtc = expiredDeadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : null,
        };

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db,
            "SELECT value FROM readings", options));

        Assert.Equal("routine_cancelled", error.Code);
        AssertReleased(db);
        Assert.Single(Select(db, "SELECT value FROM readings LIMIT 1", new SqlExecutionOptions { MaxMaterializedRows = 1 }).Rows);
    }

    /// <summary>超预算失败只影响查询，重开数据库仍返回全部持久数据。</summary>
    [Fact]
    public void Execute_BudgetRejection_DoesNotModifyPersistentPoints()
    {
        using (Tsdb db = OpenPoints(20))
        {
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT time, value, label FROM readings",
                new SqlExecutionOptions { MaxMaterializedBytes = 360 }));
            AssertReleased(db);
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(20, Select(reopened, "SELECT * FROM readings").Rows.Count);
        AssertReleased(reopened);
    }

    /// <summary>未设置物化预算时原有完整结果、排序及聚合能力继续可用。</summary>
    [Fact]
    public void Execute_DefaultOptions_PreservesFullSortAndAggregateResults()
    {
        using Tsdb db = OpenPoints(100);

        SelectExecutionResult sorted = Select(db, "SELECT time, value FROM readings ORDER BY time DESC");

        Assert.Equal(100, sorted.Rows.Count);
        Assert.Equal(99L, sorted.Rows[0][0]);
        Assert.False(sorted.Truncated);
        Assert.Equal(100L, Assert.Single(Select(db, "SELECT COUNT(value) FROM readings").Rows)[0]);
        AssertReleased(db);
    }

    /// <summary>清理本测试创建的独占数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        Assert.StartsWith("sndb-measurement-budget-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenPoints(int count)
    {
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT readings (host TAG, value FIELD INT, label FIELD STRING)");
        if (count == 0)
            return db;
        var sql = new StringBuilder("INSERT INTO readings (time, host, value, label) VALUES ");
        string payload = new('x', 100);
        for (int i = 0; i < count; i++)
        {
            if (i != 0)
                sql.Append(',');
            sql.Append('(').Append(i.ToString(CultureInfo.InvariantCulture)).Append(", 'h1', ")
                .Append(i.ToString(CultureInfo.InvariantCulture)).Append(", '").Append(payload).Append("')");
        }
        SqlExecutor.Execute(db, sql.ToString());
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null,
            options ?? SqlExecutionOptions.Default));

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    private static void AssertEquivalent(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
            Assert.Equal(expected.Rows[i], actual.Rows[i]);
        Assert.False(actual.Truncated);
    }
}
