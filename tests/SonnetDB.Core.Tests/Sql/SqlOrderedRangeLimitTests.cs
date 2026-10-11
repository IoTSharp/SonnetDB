using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证关系表复合有序范围查询的候选上限下推及安全回退。
/// </summary>
public sealed class SqlOrderedRangeLimitTests : IDisposable
{
    private readonly string _root;
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// 为每个测试创建独立数据库目录。
    /// </summary>
    public SqlOrderedRangeLimitTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "sndb-ordered-range-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    /// <summary>
    /// 清理测试数据库目录。
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不应覆盖原始断言结果。
        }
    }

    /// <summary>
    /// 验证等值前缀后的复合升序排序会下推，并在分页边界处读完整时间并列组。
    /// </summary>
    [Fact]
    public void CompositeRange_WithEqualityPrefixAndAscendingOrder_CompletesBoundaryTieGroup()
    {
        using var db = CreateDatabase(
            "Lane, CaptureTime, Id",
            "('z', 'north', 10, 'keep'),"
            + "('aa', 'north', 10, 'keep'),"
            + "('b', 'north', 10, 'keep'),"
            + "('cccc', 'north', 10, 'keep'),"
            + "('later', 'north', 20, 'keep'),"
            + "('other', 'south', 1, 'keep')");
        var observedLimits = new List<int>();
        var observedContinuations = new List<bool>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;
        db.Tables.Open("ordered_captures").RangeScanContinuationTestHook = observedContinuations.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE Lane = 'north' AND CaptureTime >= 10
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 2 OFFSET 1
            """));

        Assert.Equal(["b", "cccc"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal([4, 8], observedLimits);
        Assert.Equal([false, true], observedContinuations);
    }

    /// <summary>
    /// 验证非唯一范围索引可匹配隐式主键，并修正长度前缀物理顺序与 SQL 字符串顺序的差异。
    /// </summary>
    [Fact]
    public void RangeWithImplicitPrimaryKey_LimitOne_UsesSqlStringOrder()
    {
        using var db = CreateDatabase(
            "CaptureTime",
            "('z', 'north', 10, 'keep'),"
            + "('aa', 'north', 10, 'keep'),"
            + "('b', 'north', 10, 'keep'),"
            + "('later', 'north', 20, 'keep')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 1
            """));

        Assert.Equal("aa", Assert.Single(result.Rows)[0]);
        Assert.Equal([2, 4], observedLimits);
    }

    /// <summary>
    /// 验证分页边界落在时间组末尾时只需一次前视，并能跨多个时间组返回正确结果。
    /// </summary>
    [Fact]
    public void CompositeRange_BoundaryBetweenGroups_UsesSingleLookahead()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('a', 'north', 10, 'keep'),"
            + "('c', 'north', 20, 'keep'),"
            + "('b', 'north', 20, 'keep'),"
            + "('d', 'north', 30, 'keep')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 3
            """));

        Assert.Equal(["a", "b", "c"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal([4], observedLimits);
    }

    /// <summary>
    /// 验证超大同值组按指数扩展，累计扫描量保持线性而且不会截断组内 SQL 排序。
    /// </summary>
    [Fact]
    public void CompositeRange_LargeBoundaryTieGroup_ExpandsGeometrically()
    {
        string values = string.Join(
            ",",
            Enumerable.Range(0, 129).Select(static value => $"('{value}', 'north', 10, 'keep')"));
        using var db = CreateDatabase("CaptureTime, Id", values);
        var observedLimits = new List<int>();
        var observedContinuations = new List<bool>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;
        db.Tables.Open("ordered_captures").RangeScanContinuationTestHook = observedContinuations.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 1
            """));

        Assert.Equal("0", Assert.Single(result.Rows)[0]);
        Assert.Equal([2, 4, 8, 16, 32, 64, 128], observedLimits);
        Assert.Equal([false, true, true, true, true, true, true], observedContinuations);
    }

    /// <summary>
    /// 验证有符号 Int64 范围从 -1 跨到 0 时，复合排序分页仍以逻辑数值顺序返回候选行。
    /// </summary>
    [Fact]
    public void CompositeRange_SignedInt64CrossingNegativeOneAndZero_PreservesLimitOffsetOrder()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('neg-a', 'north', -1, 'keep'),"
            + "('neg-b', 'north', -1, 'keep'),"
            + "('zero-a', 'north', 0, 'keep'),"
            + "('zero-b', 'north', 0, 'keep'),"
            + "('zero-c', 'north', 0, 'keep'),"
            + "('later', 'north', 1, 'keep')");
        var observedLimits = new List<int>();
        var observedContinuations = new List<bool>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;
        db.Tables.Open("ordered_captures").RangeScanContinuationTestHook = observedContinuations.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= -1
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 2 OFFSET 1
            """));

        Assert.Equal(["neg-b", "zero-a"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal([4, 4], observedLimits);
        Assert.Equal([false, false], observedContinuations);
    }

    /// <summary>
    /// 验证 DATETIME 范围值的并列组跨索引页续读，避免分页边界漏掉同一时刻的 SQL 排序行。
    /// </summary>
    [Fact]
    public void CompositeRange_DateTimeBoundaryTieGroup_ContinuesToNextPage()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE ordered_events (
                Id STRING NOT NULL,
                OccurredAt DATETIME NOT NULL,
                PRIMARY KEY (Id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE INDEX idx_ordered_events ON ordered_events (OccurredAt, Id)");
        SqlExecutor.Execute(db, """
            INSERT INTO ordered_events (Id, OccurredAt) VALUES
                ('z', 0), ('aa', 0), ('b', 0), ('later', 1000)
            """);
        var observedLimits = new List<int>();
        var observedContinuations = new List<bool>();
        db.Tables.Open("ordered_events").RangeScanLimitTestHook = observedLimits.Add;
        db.Tables.Open("ordered_events").RangeScanContinuationTestHook = observedContinuations.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, OccurredAt FROM ordered_events
            WHERE OccurredAt >= 0
            ORDER BY OccurredAt ASC, Id ASC
            LIMIT 1 OFFSET 1
            """));

        Assert.Equal("b", Assert.Single(result.Rows)[0]);
        Assert.Equal([3, 6], observedLimits);
        Assert.Equal([false, true], observedContinuations);
    }

    /// <summary>
    /// 验证 OFFSET + LIMIT 恰好达到 Int32 上限时可表达，超过上限时必须放弃下推。
    /// </summary>
    [Fact]
    public void PaginationCandidateLimit_OffsetAndFetchOverflow_ReturnsFalse()
    {
        Assert.True(TableSqlExecutor.TryGetPaginationCandidateLimit(
            new PaginationSpec(int.MaxValue - 1, 1),
            out int maximum));
        Assert.Equal(int.MaxValue, maximum);

        Assert.False(TableSqlExecutor.TryGetPaginationCandidateLimit(
            new PaginationSpec(int.MaxValue, 1),
            out int overflowed));
        Assert.Equal(0, overflowed);
    }

    /// <summary>
    /// 验证降序复合排序按反向范围读取并在完整边界组之后停止。
    /// </summary>
    [Fact]
    public void CompositeRange_DescendingOrder_PushesCandidateLimit()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('a', 'north', 10, 'keep'),('b', 'north', 20, 'keep'),('c', 'north', 30, 'keep')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10
            ORDER BY CaptureTime DESC, Id DESC
            LIMIT 1
            """));

        Assert.Equal("c", Assert.Single(result.Rows)[0]);
        Assert.Equal([2], observedLimits);
    }

    /// <summary>
    /// 验证 ORDER BY 跳过范围列后的索引中间列时不能使用候选截断。
    /// </summary>
    [Fact]
    public void CompositeRange_SkippedIndexColumnSequence_DoesNotPushCandidateLimit()
    {
        using var db = CreateDatabase(
            "CaptureTime, Marker, Id",
            "('z', 'north', 10, 'x'),('aa', 'north', 10, 'z'),('b', 'north', 20, 'a')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 1
            """));

        Assert.Equal("aa", Assert.Single(result.Rows)[0]);
        Assert.Equal([int.MaxValue], observedLimits);
    }

    /// <summary>
    /// 验证索引计划未覆盖的残余谓词必须在完整候选集中过滤后再分页。
    /// </summary>
    [Fact]
    public void CompositeRange_WithResidualPredicate_DoesNotPushCandidateLimit()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('a', 'north', 10, 'drop'),"
            + "('b', 'north', 10, 'keep'),"
            + "('c', 'north', 20, 'keep')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10 AND Marker = 'keep'
            ORDER BY CaptureTime ASC, Id ASC
            LIMIT 1
            """));

        Assert.Equal("b", Assert.Single(result.Rows)[0]);
        Assert.Equal([int.MaxValue], observedLimits);
    }

    /// <summary>
    /// 验证前段候选全部被残余谓词拒绝时，仍会继续读取后续有效行，并在满足 OFFSET + LIMIT 后立即停止。
    /// </summary>
    [Fact]
    public void OrderedResidualRange_RejectedPrefix_StopsAfterQualifyingWindow()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('tail', 'north', 70, 'keep'),"
            + "('drop-2', 'north', 20, 'drop'),"
            + "('keep-2', 'north', 50, 'keep'),"
            + "('drop-1', 'north', 10, 'drop'),"
            + "('keep-1', 'north', 40, 'keep'),"
            + "('drop-3', 'north', 30, 'drop'),"
            + "('keep-3', 'north', 60, 'keep')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;
        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result;

        using (SqlExecutionTelemetry.Enter(metrics))
        {
            result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
                SELECT Id, CaptureTime FROM ordered_captures
                WHERE CaptureTime >= 10 AND Marker = 'keep'
                ORDER BY CaptureTime ASC
                LIMIT 2 OFFSET 1
                """));
        }
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(["keep-2", "keep-3"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal([50L, 60L], result.Rows.Select(static row => (long)row[1]!).ToArray());
        Assert.Equal([int.MaxValue], observedLimits);
        Assert.Equal(6, snapshot.CandidateRows);
        Assert.Equal(6, snapshot.ExaminedRows);
    }

    /// <summary>验证降序有序残余范围沿反向索引读取，并在取得足够有效行后停止。</summary>
    [Fact]
    public void OrderedResidualRange_Descending_PreservesOrderAndStopsEarly()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('old', 'north', 10, 'keep'),"
            + "('keep-2', 'north', 20, 'keep'),"
            + "('drop-2', 'north', 30, 'drop'),"
            + "('keep-1', 'north', 40, 'keep'),"
            + "('drop-1', 'north', 50, 'drop')");
        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result;

        using (SqlExecutionTelemetry.Enter(metrics))
        {
            result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
                SELECT Id, CaptureTime FROM ordered_captures
                WHERE CaptureTime >= 10 AND Marker = 'keep'
                ORDER BY CaptureTime DESC
                LIMIT 2
                """));
        }
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(["keep-1", "keep-2"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal([40L, 20L], result.Rows.Select(static row => (long)row[1]!).ToArray());
        Assert.Equal(4, snapshot.CandidateRows);
    }

    /// <summary>验证缺少 LIMIT 时不会选择有序残余范围早停，仍完整过滤并排序全部候选。</summary>
    [Fact]
    public void OrderedResidualRange_WithoutLimit_DoesNotEnableEarlyStop()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('later', 'north', 30, 'keep'),"
            + "('drop', 'north', 20, 'drop'),"
            + "('earlier', 'north', 10, 'keep')");
        var schema = db.Tables.Catalog.TryGet("ordered_captures")!;
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10 AND Marker = 'keep'
            ORDER BY CaptureTime ASC
            """));

        Assert.False(TableSqlExecutor.TryChooseOrderedResidualRangeAccessPlan(
            schema,
            statement,
            out _,
            out _));

        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = TableSqlExecutor.ExecuteSelect(db, statement, schema);
        SqlExecutionMetricsSnapshot snapshot = metrics.Complete();

        Assert.Equal(["earlier", "later"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal(3, snapshot.CandidateRows);
    }

    /// <summary>验证 OR、IN 与不匹配排序不会进入有序残余范围早停路径。</summary>
    [Theory]
    [InlineData("SELECT Id FROM ordered_captures WHERE CaptureTime >= 10 AND (Marker = 'keep' OR Marker = 'hold') ORDER BY CaptureTime LIMIT 1")]
    [InlineData("SELECT Id FROM ordered_captures WHERE CaptureTime >= 10 AND Marker IN ('keep', 'hold') ORDER BY CaptureTime LIMIT 1")]
    [InlineData("SELECT Id FROM ordered_captures WHERE CaptureTime >= 10 AND Marker = 'keep' ORDER BY Marker LIMIT 1")]
    public void OrderedResidualRange_UnsupportedShape_DoesNotChoosePlan(string sql)
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('one', 'north', 10, 'keep')");
        var schema = db.Tables.Catalog.TryGet("ordered_captures")!;
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));

        Assert.False(TableSqlExecutor.TryChooseOrderedResidualRangeAccessPlan(
            schema,
            statement,
            out _,
            out _));
    }

    /// <summary>
    /// 验证没有 LIMIT 时不启用候选上限，仍返回完整有序结果。
    /// </summary>
    [Fact]
    public void CompositeRange_WithoutLimit_DoesNotPushCandidateLimit()
    {
        using var db = CreateDatabase(
            "CaptureTime, Id",
            "('z', 'north', 10, 'keep'),('aa', 'north', 10, 'keep'),('b', 'north', 20, 'keep')");
        var observedLimits = new List<int>();
        db.Tables.Open("ordered_captures").RangeScanLimitTestHook = observedLimits.Add;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT Id, CaptureTime FROM ordered_captures
            WHERE CaptureTime >= 10
            ORDER BY CaptureTime ASC, Id ASC
            """));

        Assert.Equal(["aa", "z", "b"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal([int.MaxValue], observedLimits);
    }

    /// <summary>
    /// 验证同一 WHERE 等值前缀存在多个索引时，ORDER BY + LIMIT 会选择可反向早停的时间索引。
    /// </summary>
    [Fact]
    public void OrderedLimit_EquivalentEqualityPrefixes_SelectsOrderCompatibleIndex()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE governance_events (
                Id INT NOT NULL,
                Category STRING NOT NULL,
                ActionCode STRING NOT NULL,
                OccurredAt DATETIME NOT NULL,
                PRIMARY KEY (Id)
            )
            """);
        // 先创建不可满足排序的索引，复现目录顺序曾经覆盖 ORDER BY 选择的问题。
        SqlExecutor.Execute(
            db,
            "CREATE INDEX ix_governance_category_action ON governance_events (Category, ActionCode)");
        SqlExecutor.Execute(
            db,
            "CREATE INDEX ix_governance_category_occurred ON governance_events (Category, OccurredAt)");
        DateTimeOffset epoch = DateTimeOffset.UnixEpoch;
        db.Tables.Open("governance_events").InsertMany(
            Enumerable.Range(1, 200)
                .Select(id => (IReadOnlyList<object?>)new object?[]
                {
                    (long)id,
                    "scheduler_execution",
                    $"action-{id:D3}",
                    epoch.AddMilliseconds(id),
                })
                .ToArray());

        const string query = """
            SELECT Id FROM governance_events
            WHERE Category = 'scheduler_execution'
            ORDER BY OccurredAt DESC
            LIMIT 5
            """;
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, query));
        var explain = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN ANALYZE " + query));
        var values = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal([200L, 199L, 198L, 197L, 196L], result.Rows.Select(static row => row[0]).ToArray());
        Assert.Equal("ix_governance_category_occurred", values["actual_index_name"]);
        Assert.Equal(5L, Convert.ToInt64(values["actual_candidate_rows"]));
        Assert.Equal(5L, Convert.ToInt64(values["actual_examined_rows"]));
    }

    /// <summary>
    /// 验证零前缀和短等值前缀下，有序宽范围都不能替换成本模型选中的高选择性范围索引。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedAlternativeRange_BroaderThanSelectivePlan_KeepsSelectiveIndex(
        bool useEqualityPrefix)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE plan_guard_events (
                Id INT NOT NULL,
                Tenant STRING NOT NULL,
                SelectivityKey INT NOT NULL,
                OrderedKey INT NOT NULL,
                Marker STRING NOT NULL,
                PRIMARY KEY (Id)
            )
            """);
        string selectiveColumns = useEqualityPrefix ? "Tenant, SelectivityKey" : "SelectivityKey";
        string orderedColumns = useEqualityPrefix ? "Tenant, OrderedKey" : "OrderedKey";
        SqlExecutor.Execute(
            db,
            $"CREATE INDEX ix_plan_guard_selective ON plan_guard_events ({selectiveColumns})");
        SqlExecutor.Execute(
            db,
            $"CREATE INDEX ix_plan_guard_ordered ON plan_guard_events ({orderedColumns})");
        db.Tables.Open("plan_guard_events").InsertMany(
            Enumerable.Range(0, 2_048)
                .Select(static value => (IReadOnlyList<object?>)new object?[]
                {
                    (long)value,
                    "shared",
                    (long)value,
                    (long)value,
                    "keep",
                })
                .ToArray());
        _ = db.Tables.Open("plan_guard_events").RefreshStatistics();

        string tenantPredicate = useEqualityPrefix ? "Tenant = 'shared' AND " : string.Empty;
        string query = $"""
            SELECT Id FROM plan_guard_events
            WHERE {tenantPredicate}SelectivityKey >= 2040 AND OrderedKey >= 0 AND Marker = 'keep'
            ORDER BY OrderedKey ASC
            LIMIT 3
            """;
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, query));
        var planned = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN " + query));
        var explain = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN ANALYZE " + query));
        var plannedValues = planned.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
        var values = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal([2040L, 2041L, 2042L], result.Rows.Select(static row => row[0]).ToArray());
        Assert.Equal("ix_plan_guard_selective", plannedValues["index_name"]);
        Assert.Equal("ix_plan_guard_selective", values["actual_index_name"]);
        Assert.Equal(8L, Convert.ToInt64(values["actual_candidate_rows"]));
        Assert.Equal(8L, Convert.ToInt64(values["actual_examined_rows"]));
    }

    /// <summary>
    /// 验证后台任务查询的三列排序可沿复合范围索引流式过滤，并在凑够 LIMIT 后停止读取。
    /// </summary>
    [Fact]
    public void OrderedResidualRange_MultipleColumns_StopsAfterQualifyingLimit()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE backfill_tasks (
                Id STRING NOT NULL,
                Status STRING NOT NULL,
                NextAttemptAt DATETIME NOT NULL,
                CreatedAt DATETIME NOT NULL,
                AttemptCount INT NOT NULL,
                PRIMARY KEY (Id)
            )
            """);
        SqlExecutor.Execute(
            db,
            "CREATE INDEX ix_backfill_ready ON backfill_tasks (Status, NextAttemptAt, CreatedAt, Id)");
        DateTimeOffset epoch = DateTimeOffset.UnixEpoch;
        string[] tiedIds = ["z", "aa", "b", .. Enumerable.Range(0, 17).Select(index => $"task-tie-{index:D2}")];
        db.Tables.Open("backfill_tasks").InsertMany(
            Enumerable.Range(0, 200)
                .Select(index =>
                {
                    bool exhausted = index < 5;
                    bool tied = index is >= 5 and <= 24;
                    string id = exhausted
                        ? $"drop-{index}"
                        : tied ? tiedIds[index - 5] : $"task-{index:D3}";
                    long nextAttemptMilliseconds = tied ? 5 : index < 5 ? index : index - 19;
                    long createdMilliseconds = tied ? 0 : nextAttemptMilliseconds;
                    return (IReadOnlyList<object?>)new object?[]
                    {
                        id,
                        "pending",
                        epoch.AddMilliseconds(nextAttemptMilliseconds),
                        epoch.AddMilliseconds(createdMilliseconds),
                        exhausted ? 3L : 0L,
                    };
                })
                .ToArray());

        const string query = """
            SELECT Id FROM backfill_tasks
            WHERE Status = 'pending' AND NextAttemptAt <= 1000 AND AttemptCount < 3
            ORDER BY NextAttemptAt ASC, CreatedAt ASC, Id ASC
            LIMIT 10
            """;
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, query));
        var explain = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "EXPLAIN ANALYZE " + query));
        var values = explain.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.Equal(
            ["aa", "b", "task-tie-00", "task-tie-01", "task-tie-02", "task-tie-03", "task-tie-04", "task-tie-05", "task-tie-06", "task-tie-07"],
            result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal("ix_backfill_ready", values["actual_index_name"]);
        Assert.Equal(10L, Convert.ToInt64(values["actual_rows"]));
        // LIMIT 只能在完整读取首个同值组后停止；26 = 5 个淘汰行 + 20 个并列行 + 1 个组边界前视。
        Assert.Equal(26L, Convert.ToInt64(values["actual_candidate_rows"]));
        Assert.Equal(26L, Convert.ToInt64(values["actual_examined_rows"]));
    }

    /// <summary>
    /// 验证跨零范围及显式或隐式 Unicode 主键保持 SQL 字符串排序和 OFFSET 语义。
    /// </summary>
    [Theory]
    [InlineData("CaptureTime, Id", "ASC")]
    [InlineData("CaptureTime, Id", "DESC")]
    [InlineData("CaptureTime", "DESC")]
    public void CompositeRange_SignedDirectionAndImplicitId_PreservesSqlTieOrder(string indexColumns, string direction)
    {
        using var db = CreateDatabase(indexColumns,
            "('z', 'north', -1, 'keep'),('aa', 'north', -1, 'keep'),"
            + "('b', 'north', 0, 'keep'),('cccc', 'north', 0, 'keep'),"
            + "('\uE000', 'north', 0, 'keep'),('\U00010000', 'north', 0, 'keep'),('high', 'north', 1, 'keep')");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            $"SELECT Id FROM ordered_captures WHERE CaptureTime >= -1 ORDER BY CaptureTime {direction}, Id {direction} LIMIT 5 OFFSET 1"));

        // UTF-8 字节顺序与 UTF-16 Ordinal 顺序对补充平面字符不同，必须读取完整组后应用 SQL 比较器。
        string[] expected = direction == "DESC" ? ["\uE000", "\U00010000", "cccc", "b", "z"] : ["z", "b", "cccc", "\U00010000", "\uE000"];
        Assert.Equal(expected,
            result.Rows.Select(static row => (string)row[0]!).ToArray());
    }

    /// <summary>验证降序 DATETIME 时间组跨页续读，Id 长度前缀差异不能截断 SQL 字符串排序。</summary>
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(100)]
    public void CompositeRange_DescendingDateTimeTieGroup_ContinuesStrictlyAfterPage(int pageByteLimit)
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE ordered_events (Id STRING NOT NULL, OccurredAt DATETIME NOT NULL, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_ordered_events ON ordered_events (OccurredAt, Id)");
        SqlExecutor.Execute(db, "INSERT INTO ordered_events (Id, OccurredAt) VALUES ('z', 1000), ('aa', 1000), ('b', 1000), ('older', 0)");
        var continuations = new List<bool>();
        db.Tables.Open("ordered_events").RangeScanContinuationTestHook = continuations.Add;
        db.Tables.Open("ordered_events").RangeScanPageByteLimitTestOverride = pageByteLimit;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT Id FROM ordered_events WHERE OccurredAt >= 0 ORDER BY OccurredAt DESC, Id DESC LIMIT 1 OFFSET 1"));

        Assert.Equal("b", Assert.Single(result.Rows)[0]);
        Assert.Equal(pageByteLimit == int.MaxValue ? new[] { false, true } : new[] { false, true, true }, continuations);
    }

    /// <summary>验证磁盘上大时间组的降序分页保持稳定结果，同时候选和 payload 读取显著少于全分区排序。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(12000)]
    public void CompositeRange_DescendingPersistedLargeTieGroup_ReadsLessThanFullPartition(int otherPartitionRows)
    {
        using var db = CreateDatabase("Lane, CaptureTime, Id", "('seed', 'north', 1, 'keep')");
        var store = db.Tables.Open("ordered_captures");
        string[] tiedIds = Enumerable.Range(0, 600).Select(static index => index.ToString()).ToArray();
        store.InsertMany(Enumerable.Range(0, 4000).Select(static index => (IReadOnlyList<object?>)new object?[]
            { $"old-{index:D4}", "north", 1L, "keep" }).Concat(
            tiedIds.Select(static id => (IReadOnlyList<object?>)new object?[] { id, "north", 1000L, "keep" })).Concat(
            Enumerable.Range(0, otherPartitionRows).Select(static index => (IReadOnlyList<object?>)new object?[]
                { $"other-{index:D5}", "south", 1000L, "keep" })).ToArray());
        store.RefreshStatistics();
        store.CreateSnapshot();
        const string query = "SELECT Id FROM ordered_captures WHERE Lane = 'north' ORDER BY CaptureTime DESC, Id DESC LIMIT 5 OFFSET 2";
        if (otherPartitionRows == 0)
        {
            // 单一宽分区的基础成本确实选择表扫描；完整有序 LIMIT 仍必须恢复有界索引执行。
            TableSchema schema = db.Tables.Catalog.TryGet("ordered_captures")!;
            var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(query));
            Assert.Null(TableSqlExecutor.ChooseBestIndexAccessPlan(store, schema, statement.Where));
        }
        var optimizedMetrics = new SqlExecutionMetrics();
        SelectExecutionResult optimized;
        using (SqlExecutionTelemetry.Enter(optimizedMetrics))
            optimized = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, query));
        SqlExecutionMetricsSnapshot optimizedSnapshot = optimizedMetrics.Complete();

        // Marker 在本数据集中恒定；插入该非索引排序列产生等价的全分区排序基线。
        var baselineMetrics = new SqlExecutionMetrics();
        SelectExecutionResult baseline;
        using (SqlExecutionTelemetry.Enter(baselineMetrics))
            baseline = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT Id FROM ordered_captures WHERE Lane = 'north' ORDER BY CaptureTime DESC, Marker DESC, Id DESC LIMIT 5 OFFSET 2"));
        SqlExecutionMetricsSnapshot baselineSnapshot = baselineMetrics.Complete();
        _output.WriteLine($"DESC persisted reads: optimized={optimizedSnapshot.PhysicalReads}, full={baselineSnapshot.PhysicalReads}; candidates={optimizedSnapshot.CandidateRows}/{baselineSnapshot.CandidateRows}");
        string[] expected = tiedIds.OrderByDescending(static id => id, StringComparer.Ordinal).Skip(2).Take(5).ToArray();
        Assert.Equal(expected, optimized.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal(expected, baseline.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal(600, optimizedSnapshot.CandidateRows);
        Assert.Equal(4601 + otherPartitionRows, baselineSnapshot.CandidateRows);
        Assert.True(optimizedSnapshot.PhysicalReadSnapshotComplete && baselineSnapshot.PhysicalReadSnapshotComplete);
        Assert.True(optimizedSnapshot.PhysicalReads > 0);
        Assert.True(optimizedSnapshot.PhysicalReads * 2 < baselineSnapshot.PhysicalReads,
            $"optimized={optimizedSnapshot.PhysicalReads}, full={baselineSnapshot.PhysicalReads}");
        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN ANALYZE " + query));
        var values = explain.Rows.ToDictionary(static row => (string)row[0]!, static row => row[1], StringComparer.Ordinal);
        Assert.Equal("idx_ordered_captures", values["actual_index_name"]);
        Assert.Equal(600L, Convert.ToInt64(values["actual_candidate_rows"]));
        Assert.Equal(5L, Convert.ToInt64(values["actual_rows"]));
    }

    /// <summary>验证 DESC 多列排序先过滤完整时间组，再按有效结果执行 OFFSET/LIMIT，并跳过后续历史分区。</summary>
    [Fact]
    public void OrderedResidualRange_DescendingMultipleColumns_FiltersBeforePagination()
    {
        using var db = CreateDatabase("CaptureTime, Id", "('old', 'north', 1, 'keep')");
        var store = db.Tables.Open("ordered_captures");
        store.InsertMany(Enumerable.Range(0, 600).Select(static index => (IReadOnlyList<object?>)new object?[]
            { index.ToString(), "north", 1000L, index % 2 == 0 ? "keep" : "drop" }).Concat(
            Enumerable.Range(0, 1000).Select(static index => (IReadOnlyList<object?>)new object?[]
                { $"old-{index:D4}", "north", 1L, "keep" })).ToArray());
        var metrics = new SqlExecutionMetrics();
        SelectExecutionResult result;
        using (SqlExecutionTelemetry.Enter(metrics))
            result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT Id FROM ordered_captures WHERE CaptureTime >= 0 AND Marker = 'keep' ORDER BY CaptureTime DESC, Id DESC LIMIT 3 OFFSET 1"));

        Assert.Equal(Enumerable.Range(0, 600).Where(static index => index % 2 == 0).Select(static index => index.ToString())
            .OrderByDescending(static id => id, StringComparer.Ordinal).Skip(1).Take(3).ToArray(),
            result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal(601, metrics.Complete().CandidateRows);
    }

    /// <summary>验证边界组读取中途取消会抛出异常并释放快照，后续查询仍能正常续页。</summary>
    [Fact]
    public void CompositeRange_DescendingTieGroup_CancelsBetweenPages()
    {
        using var db = CreateDatabase("CaptureTime, Id", string.Join(",",
            Enumerable.Range(0, 40).Select(static index => $"('{index}', 'north', 10, 'keep')")));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var store = db.Tables.Open("ordered_captures");
        store.RangeScanContinuationTestHook = continued => { if (continued) cancellation.Cancel(); };
        const string query = "SELECT Id FROM ordered_captures WHERE CaptureTime >= 0 ORDER BY CaptureTime DESC, Id DESC LIMIT 1";
        Assert.ThrowsAny<OperationCanceledException>(() => SqlExecutor.Execute(db, null, query, null, null,
            SqlExecutionOptions.Default with { CancellationToken = cancellation.Token }));
        store.RangeScanContinuationTestHook = null;
        Assert.Equal("9", Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, query)).Rows)[0]);
    }

    /// <summary>验证 NULL 和不同长度字符串后缀使用统一 SQL 比较，而混合方向仍保留完整排序回退。</summary>
    [Fact]
    public void CompositeRange_DescendingNullableSuffixAndMixedDirection_PreservesSqlComparison()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE nullable_events (Id STRING NOT NULL, OccurredAt INT NOT NULL, Label STRING NULL, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_nullable_events ON nullable_events (OccurredAt, Label, Id)");
        SqlExecutor.Execute(db, "INSERT INTO nullable_events (Id, OccurredAt, Label) VALUES ('null', 10, NULL), ('short', 10, 'z'), ('long', 10, 'aa'), ('old', 0, 'z')");
        var descending = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT Id FROM nullable_events WHERE OccurredAt >= 0 ORDER BY OccurredAt DESC, Label DESC, Id DESC LIMIT 3"));
        Assert.Equal(["short", "long", "null"], descending.Rows.Select(static row => (string)row[0]!).ToArray());
        var mixed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT Id FROM nullable_events WHERE OccurredAt >= 0 ORDER BY OccurredAt DESC, Label ASC, Id ASC LIMIT 3"));
        Assert.Equal(["null", "long", "short"], mixed.Rows.Select(static row => (string)row[0]!).ToArray());
    }

    /// <summary>验证第一页全为缺失主行的旧索引项时仍严格按索引键续页，不能误判为已经读完。</summary>
    [Fact]
    public void CompositeRange_DescendingDanglingIndexPage_ContinuesToValidTieGroup()
    {
        using var db = CreateDatabase("CaptureTime, Id", "('seed', 'north', 0, 'keep')");
        TableSchema schema = db.Tables.Catalog.TryGet("ordered_captures")!;
        using var keyspace = KvKeyspace.Open("dangling", Path.Combine(_root, "dangling"), new KvOptions());
        using var store = new TableStore(schema, keyspace);
        store.InsertMany(new IReadOnlyList<object?>[] {
            new object?[] { "zz-0", "north", 10L, "keep" }, new object?[] { "zz-1", "north", 10L, "keep" },
            new object?[] { "a", "north", 10L, "keep" }, new object?[] { "b", "north", 10L, "keep" },
            new object?[] { "old", "north", 0L, "keep" } });
        // 仅删主行，模拟仍存在的旧索引项；两个悬空项占满最初的一页。
        keyspace.Delete(TableIndexCodec.EncodePrimaryRowKey(TableKeyCodec.EncodePrimaryKeyValues(schema, ["zz-0"])));
        keyspace.Delete(TableIndexCodec.EncodePrimaryRowKey(TableKeyCodec.EncodePrimaryKeyValues(schema, ["zz-1"])));
        var continuations = new List<bool>();
        store.RangeScanContinuationTestHook = continuations.Add;
        var range = new TableIndexRange(schema.TryGetColumn("CaptureTime")!, Lower: null, Upper: null);
        IReadOnlyList<TableRow> rows = store.GetByIndexRangeThroughValueGroup(schema.Indexes[0], [], range, 1, descending: true);
        Assert.Equal(["b", "a"], rows.Select(static row => (string)row.Values[0]!).ToArray());
        Assert.Equal([false, true], continuations);
    }

    /// <summary>创建带指定普通二级索引和初始抓拍行的测试数据库。</summary>
    private Tsdb CreateDatabase(string indexColumns, string values)
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE ordered_captures (
                Id STRING NOT NULL,
                Lane STRING NOT NULL,
                CaptureTime INT NOT NULL,
                Marker STRING NOT NULL,
                PRIMARY KEY (Id)
            )
            """);
        SqlExecutor.Execute(
            db,
            $"CREATE INDEX idx_ordered_captures ON ordered_captures ({indexColumns})");
        SqlExecutor.Execute(
            db,
            $"INSERT INTO ordered_captures (Id, Lane, CaptureTime, Marker) VALUES {values}");
        return db;
    }
}
