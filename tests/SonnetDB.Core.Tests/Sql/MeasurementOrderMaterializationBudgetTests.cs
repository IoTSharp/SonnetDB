using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证直接 measurement 标量多键排序的候选、键及最终页累计物化预算。</summary>
public sealed class MeasurementOrderMaterializationBudgetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-measurement-sort-{Guid.NewGuid():N}");

    /// <summary>小页仍先准入所有排序候选和键，最终页也占用累计行数预算。</summary>
    [Theory]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void Execute_LimitOne_ChargesCandidatesKeysAndOutput(long rows, bool succeeds)
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        const string sql = "SELECT time FROM readings ORDER BY value LIMIT 1";
        var options = new SqlExecutionOptions { MaxMaterializedRows = rows };

        if (succeeds)
        {
            SelectExecutionResult result = Select(db, sql, options);
            Assert.Equal(2L, Assert.Single(result.Rows)[0]);
            Assert.False(result.Truncated);
        }
        else
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db, sql, options));
            Assert.Contains("累计物化", error.Message, StringComparison.Ordinal);
        }
        AssertReleased(db);
    }

    /// <summary>三条单数值候选、三个单数值键及一条输出精确计入 672 估算字节。</summary>
    [Theory]
    [InlineData(671, false)]
    [InlineData(672, true)]
    public void Execute_NumericSort_EnforcesExactCumulativeByteBoundary(long bytes, bool succeeds)
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        var options = new SqlExecutionOptions { MaxMaterializedRows = 7, MaxMaterializedBytes = bytes };

        if (succeeds)
            Assert.Equal(2L, Assert.Single(Select(db,
                "SELECT time FROM readings ORDER BY value LIMIT 1", options).Rows)[0]);
        else
            Assert.Throws<InvalidOperationException>(() => Select(db,
                "SELECT time FROM readings ORDER BY value LIMIT 1", options));
        AssertReleased(db);
    }

    /// <summary>未投影长字符串和第二排序键的载荷及引用槽位不能漏计。</summary>
    [Theory]
    [InlineData(519, false)]
    [InlineData(520, true)]
    public void Execute_UnprojectedStringAndSecondKey_ChargesExactByteBoundary(long bytes, bool succeeds)
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db, $"INSERT INTO readings (time, host, value, label) VALUES (1, 'a', 7, '{new string('x', 100)}')");
        const string sql = "SELECT time FROM readings ORDER BY label, value DESC";
        var options = new SqlExecutionOptions { MaxMaterializedRows = 3, MaxMaterializedBytes = bytes };

        if (succeeds)
            Assert.Equal(1L, Assert.Single(Select(db, sql, options).Rows)[0]);
        else
            Assert.Throws<InvalidOperationException>(() => Select(db, sql, options));
        AssertReleased(db);
    }

    /// <summary>WHERE 先剔除候选，多键排序使用未投影字段，分页最后应用。</summary>
    [Theory]
    [InlineData("LIMIT 2 OFFSET 1")]
    [InlineData("OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY")]
    public void Execute_FilterAndMultipleUnprojectedKeys_ReturnsCompleteSqlPage(string pagination)
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value, label) VALUES (1, 'a', 10, 'B'), (2, 'a', 20, 'A'), (3, 'a', 30, 'A'), (4, 'a', 40, 'A'), (5, 'a', 50, 'B')");

        SelectExecutionResult result = Select(db,
            $"SELECT time FROM readings WHERE value >= 20 ORDER BY LOWER(label), value DESC {pagination}",
            new SqlExecutionOptions { MaxMaterializedRows = 10 });

        Assert.Equal([3L, 2L], result.Rows.Select(static row => (long)row[0]!));
        Assert.False(result.Truncated);
        AssertReleased(db);
    }

    /// <summary>相同排序键保持跨 series 的自然输入顺序，第二键明确排序时再打破平局。</summary>
    [Fact]
    public void Execute_EqualKeysAcrossSeries_PreservesStableInputOrder()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value) VALUES (1, 'a', 7), (3, 'a', 7), (2, 'b', 7), (4, 'b', 7)");
        SelectExecutionResult natural = Select(db, "SELECT time, host FROM readings");

        SelectExecutionResult tied = Select(db, "SELECT time, host FROM readings ORDER BY value",
            new SqlExecutionOptions { MaxMaterializedRows = 12 });

        Assert.Equal(natural.Rows.Select(static row => (long)row[0]!), tied.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(natural.Rows.Select(static row => (string)row[1]!), tied.Rows.Select(static row => (string)row[1]!));
        Assert.Equal([4L, 3L, 2L, 1L], Select(db,
            "SELECT time FROM readings ORDER BY value, time DESC", new SqlExecutionOptions { MaxMaterializedRows = 12 })
            .Rows.Select(static row => (long)row[0]!));
        AssertReleased(db);
    }

    /// <summary>稀疏未投影字段加入合并时间轴，升序 NULL 在前且降序 NULL 在后。</summary>
    [Fact]
    public void Execute_SparseFields_UsesUnionTimelineAndSqlNullOrdering()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db, "INSERT INTO readings (time, host, value) VALUES (1, 'a', 10), (3, 'a', 20)");
        SqlExecutor.Execute(db, "INSERT INTO readings (time, host, label) VALUES (2, 'a', 'only-key')");
        var options = new SqlExecutionOptions { MaxMaterializedRows = 9 };

        SelectExecutionResult ascending = Select(db, "SELECT time, label FROM readings ORDER BY value", options);
        SelectExecutionResult descending = Select(db, "SELECT time, label FROM readings ORDER BY value DESC", options);

        Assert.Equal([2L, 1L, 3L], ascending.Rows.Select(static row => (long)row[0]!));
        Assert.Equal([3L, 1L, 2L], descending.Rows.Select(static row => (long)row[0]!));
        Assert.Null(ascending.Rows[1][1]);
        AssertReleased(db);
    }

    /// <summary>未投影缺失 TAG 解析为 NULL，不能因排序额外报未知列。</summary>
    [Fact]
    public void Execute_MissingTagKey_ReturnsNullFirst()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db, "INSERT INTO readings (time, value) VALUES (1, 10)");
        SqlExecutor.Execute(db, "INSERT INTO readings (time, host, value) VALUES (2, 'a', 20)");

        SelectExecutionResult result = Select(db, "SELECT time FROM readings ORDER BY host",
            new SqlExecutionOptions { MaxMaterializedRows = 6 });

        Assert.Equal([1L, 2L], result.Rows.Select(static row => (long)row[0]!));
        AssertReleased(db);
    }

    /// <summary>排序别名引用原投影值，保留原名并遵守双引号精确绑定。</summary>
    [Fact]
    public void Execute_QuotedAliasAndMixedCaseColumns_PreservesNameBinding()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Devices (Host TAG, Value FIELD INT)");
        SqlExecutor.Execute(db, "INSERT INTO Devices (time, Host, Value) VALUES (1, 'a', 7), (2, 'a', 9)");

        SelectExecutionResult result = Select(db,
            "SELECT d.VALUE + 1 AS NextValue FROM devices AS d ORDER BY \"NextValue\" DESC, d.VALUE",
            new SqlExecutionOptions { MaxMaterializedRows = 6 });

        Assert.Equal(["NextValue"], result.Columns);
        Assert.Equal([10L, 8L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT Value + 1 AS NextValue FROM Devices ORDER BY \"nextvalue\"",
            new SqlExecutionOptions { MaxMaterializedRows = 6 }));
        AssertReleased(db);
    }

    /// <summary>同名别名优先于星号展开的原列，排序必须复用实际别名投影。</summary>
    [Fact]
    public void Execute_AliasShadowsExpandedStarColumn_UsesAliasProjection()
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);

        SelectExecutionResult result = Select(db, "SELECT *, -value AS value FROM readings ORDER BY value",
            new SqlExecutionOptions { MaxMaterializedRows = 9 });

        Assert.Equal([1L, 3L, 2L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal([-30L, -20L, -10L], result.Rows.Select(static row => (long)row[4]!));
        AssertReleased(db);
    }

    /// <summary>已有 now() 时间窗下推和内建标量残差在无排序及排序预算路径继续可用。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ORDER BY value")]
    public void Execute_TimeNowPushdownAndScalarResidual_PreservesExistingRawContract(string order)
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);

        SelectExecutionResult result = Select(db,
            "SELECT time FROM readings WHERE time < now() AND LOWER(label) = 'a'" + order,
            new SqlExecutionOptions { MaxMaterializedRows = 3 });

        Assert.Equal(2L, Assert.Single(result.Rows)[0]);
        AssertReleased(db);
    }

    /// <summary>大整数排序复用精确数值比较，避免 double 舍入引起顺序错误。</summary>
    [Fact]
    public void Execute_Int64KeysBeyondDoublePrecision_PreservesExactOrdering()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value) VALUES (1, 'a', 9007199254740993), (2, 'a', 9007199254740992)");

        SelectExecutionResult result = Select(db, "SELECT time FROM readings ORDER BY value",
            new SqlExecutionOptions { MaxMaterializedRows = 6 });

        Assert.Equal([2L, 1L], result.Rows.Select(static row => (long)row[0]!));
        AssertReleased(db);
    }

    /// <summary>布尔键使用统一标量比较，并用时间第二键确定同值顺序。</summary>
    [Fact]
    public void Execute_BooleanKeys_UsesSharedScalarOrdering()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db, "CREATE MEASUREMENT switches (state FIELD BOOL)");
        SqlExecutor.Execute(db, "INSERT INTO switches (time, state) VALUES (1, TRUE), (2, FALSE), (3, TRUE)");

        SelectExecutionResult result = Select(db, "SELECT time FROM switches ORDER BY state ASC, time DESC",
            new SqlExecutionOptions { MaxMaterializedRows = 9 });

        Assert.Equal([2L, 3L, 1L], result.Rows.Select(static row => (long)row[0]!));
        AssertReleased(db);
    }

    /// <summary>没有匹配行时不产生候选、键或页保留。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_NoMatchedCandidates_ReturnsEmptyWithinMinimalBudget(bool hasPoints)
    {
        using Tsdb db = OpenReadings();
        if (hasPoints)
            InsertThree(db);

        SelectExecutionResult result = Select(db, "SELECT time FROM readings WHERE value < 0 ORDER BY label, value",
            new SqlExecutionOptions { MaxMaterializedRows = 1, MaxMaterializedBytes = 1 });

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        AssertReleased(db);
    }

    /// <summary>查询共享内存预留也约束候选、键和输出，失败后不保留预留字节。</summary>
    [Fact]
    public void Execute_BlockingMemoryLimit_RejectsAndReleasesReservation()
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT time FROM readings ORDER BY value LIMIT 1", new SqlExecutionOptions
            {
                MaxMaterializedRows = 7,
                BlockingOperatorMemoryLimitBytes = 671,
            }));

        Assert.Contains("内存预算", error.Message, StringComparison.Ordinal);
        AssertReleased(db);
        Assert.Equal(2L, Assert.Single(Select(db, "SELECT time FROM readings ORDER BY value LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 7 }).Rows)[0]);
        AssertReleased(db);
    }

    /// <summary>空页仍完成阻塞输入准入；超大 OFFSET 和 LIMIT 0 不绕过候选及键预算。</summary>
    [Theory]
    [InlineData("LIMIT 0")]
    [InlineData("OFFSET 100")]
    public void Execute_EmptyPage_StillAdmitsBlockingInput(string pagination)
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        string sql = $"SELECT time FROM readings ORDER BY value {pagination}";

        Assert.Throws<InvalidOperationException>(() => Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 5 }));
        SelectExecutionResult result = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 6 });

        Assert.Empty(result.Rows);
        Assert.False(result.Truncated);
        AssertReleased(db);
    }

    /// <summary>未知键、聚合、窗口、子查询、非标准值和无效标量参数在读取任何点前拒绝。</summary>
    [Theory]
    [InlineData("SELECT time FROM readings ORDER BY unknown_key", false)]
    [InlineData("SELECT time FROM readings ORDER BY SUM(value)", true)]
    [InlineData("SELECT time FROM readings ORDER BY moving_average(value, 2)", true)]
    [InlineData("SELECT time FROM readings ORDER BY ABS(value) OVER (ORDER BY time)", true)]
    [InlineData("SELECT time FROM readings ORDER BY (SELECT 1)", true)]
    [InlineData("SELECT time FROM readings ORDER BY [1, 2]", true)]
    [InlineData("SELECT time FROM readings ORDER BY ABS()", true)]
    [InlineData("SELECT time FROM readings WHERE SUM(value) > 0 ORDER BY time", true)]
    public void Execute_UnsupportedSortExpression_RejectsBeforePointRead(string sql, bool notSupported)
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        var metrics = new SqlExecutionMetrics();
        var options = new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics };

        if (notSupported)
            Assert.Throws<NotSupportedException>(() => Select(db, sql, options));
        else
            Assert.Throws<InvalidOperationException>(() => Select(db, sql, options));
        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>空 measurement 的非标准排序字段也预检拒绝，不依赖第一条数据触发错误。</summary>
    [Fact]
    public void Execute_EmptyGeoSortKey_RejectsBeforeScan()
    {
        using Tsdb db = OpenReadings();
        SqlExecutor.Execute(db, "CREATE MEASUREMENT locations (value FIELD INT, position FIELD GEOPOINT)");
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT value FROM locations ORDER BY position",
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, metrics.Complete().CandidateRows);
        AssertReleased(db);
    }

    /// <summary>排序键中的用户函数不得进入回调；拒绝后下一次排序仍可正常执行。</summary>
    [Fact]
    public void Execute_UserFunctionKey_RejectsWithoutInvocation()
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        int called = 0;
        db.Functions.RegisterScalar("sort_callback", _ => { called++; return 0L; });
        var metrics = new SqlExecutionMetrics();

        Assert.Throws<NotSupportedException>(() => Select(db,
            "SELECT time FROM readings ORDER BY sort_callback()",
            new SqlExecutionOptions { MaxMaterializedRows = 100, Metrics = metrics }));

        Assert.Equal(0, called);
        Assert.Equal(0, metrics.Complete().CandidateRows);
        Assert.Equal(2L, Assert.Single(Select(db, "SELECT time FROM readings ORDER BY value LIMIT 1",
            new SqlExecutionOptions { MaxMaterializedRows = 7 }).Rows)[0]);
        AssertReleased(db);
    }

    /// <summary>已取消根调用保留取消错误码并回收查询预算。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_CancelledSort_ReleasesResources(bool expiredDeadline)
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        using var cancellation = new CancellationTokenSource();
        if (!expiredDeadline)
            cancellation.Cancel();

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() => Select(db,
            "SELECT time FROM readings ORDER BY value", new SqlExecutionOptions
            {
                MaxMaterializedRows = 9,
                CancellationToken = cancellation.Token,
                DeadlineUtc = expiredDeadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : null,
            }));

        Assert.Equal("routine_cancelled", error.Code);
        AssertReleased(db);
        Assert.Equal(3, Select(db, "SELECT time FROM readings ORDER BY value",
            new SqlExecutionOptions { MaxMaterializedRows = 9 }).Rows.Count);
        AssertReleased(db);
    }

    /// <summary>段流排序预算拒绝不修改持久数据，重开后仍返回完整排序页。</summary>
    [Fact]
    public void Execute_SegmentBudgetRejectionAndReopen_PreservesPersistentPoints()
    {
        using (Tsdb db = OpenReadings())
        {
            InsertThree(db);
            db.FlushNow();
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT time FROM readings ORDER BY value LIMIT 1",
                new SqlExecutionOptions { MaxMaterializedRows = 5 }));
            AssertReleased(db);
        }
        using Tsdb reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });

        SelectExecutionResult result = Select(reopened, "SELECT time FROM readings ORDER BY value",
            new SqlExecutionOptions { MaxMaterializedRows = 9 });

        Assert.Equal([2L, 3L, 1L], result.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(3, Select(reopened, "SELECT * FROM readings").Rows.Count);
        AssertReleased(reopened);
    }

    /// <summary>默认调用仍遵守既有 time 排序能力，预算路径与既有结果一致。</summary>
    [Fact]
    public void Execute_TimeSort_PreservesDefaultResult()
    {
        using Tsdb db = OpenReadings();
        InsertThree(db);
        const string sql = "SELECT time, value FROM readings ORDER BY time DESC LIMIT 2 OFFSET 1";

        SelectExecutionResult expected = Select(db, sql);
        SelectExecutionResult actual = Select(db, sql, new SqlExecutionOptions { MaxMaterializedRows = 8 });

        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Select(static row => (long)row[0]!), actual.Rows.Select(static row => (long)row[0]!));
        Assert.Equal(expected.Rows.Select(static row => (long)row[1]!), actual.Rows.Select(static row => (long)row[1]!));
        Assert.False(actual.Truncated);
        AssertReleased(db);
    }

    /// <summary>清理本测试创建的独占数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        Assert.StartsWith("sndb-measurement-sort-", Path.GetFileName(target), StringComparison.Ordinal);
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenReadings()
    {
        Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT readings (host TAG, value FIELD INT, label FIELD STRING)");
        return db;
    }

    private static void InsertThree(Tsdb db)
        => SqlExecutor.Execute(db,
            "INSERT INTO readings (time, host, value, label) VALUES (1, 'a', 30, 'C'), (2, 'a', 10, 'A'), (3, 'a', 20, 'B')");

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, null, null,
            options ?? SqlExecutionOptions.Default));

    private static void AssertReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }
}
