using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证关系表复合升序范围查询的候选上限下推及安全回退。
/// </summary>
public sealed class SqlOrderedRangeLimitTests : IDisposable
{
    private readonly string _root;

    /// <summary>
    /// 为每个测试创建独立数据库目录。
    /// </summary>
    public SqlOrderedRangeLimitTests()
    {
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
    /// 验证降序复合排序不能使用升序范围候选截断。
    /// </summary>
    [Fact]
    public void CompositeRange_DescendingOrder_DoesNotPushCandidateLimit()
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
        Assert.Equal([int.MaxValue], observedLimits);
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
    /// 创建带指定普通二级索引和初始抓拍行的测试数据库。
    /// </summary>
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
