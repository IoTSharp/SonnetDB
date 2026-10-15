using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 measurement 分组后的筛选、复合投影、排序和分页合同。</summary>
public sealed class SqlMeasurementGroupedPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-grouped-pipeline-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建本测试独占的数据库目录。</summary>
    public SqlMeasurementGroupedPipelineTests() => Directory.CreateDirectory(_root);

    /// <summary>回收独占目录。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>跨 series 聚合后先筛选、按别名和 TAG 排序，最后分页。</summary>
    [Fact]
    public void Select_GroupedPipeline_AppliesHavingProjectionOrderThenPagination()
    {
        using var db = OpenSeeded();
        const string sql = "SELECT Host, COUNT(*) AS Samples, SUM(Value)+1 AS \"Adjusted.Total\" "
            + "FROM Metrics WHERE time >= @start GROUP BY Host HAVING COUNT(*) > @minimum "
            + "ORDER BY \"Adjusted.Total\" DESC, Host ASC LIMIT @take OFFSET @skip";
        var parameters = new SqlParameters().AddNamed("start", 1000L).AddNamed("minimum", 1L)
            .AddNamed("take", 1L).AddNamed("skip", 1L);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters, null));

        Assert.Equal(["Host", "Samples", "Adjusted.Total"], result.Columns);
        Assert.Equal(new object?[] { "b", 2L, 11L }, Assert.Single(result.Rows));
    }

    /// <summary>HAVING 可使用未输出的聚合与分组键，并保留三值逻辑和稀疏 FIELD。</summary>
    [Theory]
    [InlineData("SUM(Value) > 9", "a,b,c")]
    [InlineData("SUM(Value) > 9 AND COUNT(*) > 1", "a,b")]
    [InlineData("SUM(Value) IS NULL", "<null>,empty")]
    [InlineData("SUM(Value) > 0 OR Host IS NULL", "<null>,a,b,c")]
    [InlineData("NOT (SUM(Value) > 0)", "")]
    [InlineData("Host IN ('a', 'b', NULL) AND COUNT(*) > 1", "a,b")]
    public void Select_HavingHiddenAggregatesAndNulls_ReturnsOnlyTrueGroups(string predicate, string expected)
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT Host FROM Metrics GROUP BY Host HAVING " + predicate + " ORDER BY Host");
        Assert.Equal(expected, string.Join(',', result.Rows.Select(static row => row[0] ?? "<null>")));
    }

    /// <summary>复用已有标量、CASE、CAST 和精确 Int64 算术，分组键可出现在复合投影中。</summary>
    [Fact]
    public void Select_CompoundProjection_ReusesScalarCastCaseAndGroupedKeys()
    {
        using var db = OpenSeeded();
        var result = Select(db,
            "SELECT lower(Host) AS label, CAST(SUM(Value) AS STRING) AS total, "
            + "CASE WHEN COUNT(Value)=0 THEN 'missing' ELSE concat(Host, ':', CAST(SUM(Value)+1 AS STRING)) END AS summary "
            + "FROM Metrics GROUP BY Host ORDER BY Host");

        Assert.Equal(new object?[] { null, null, "missing" }, result.Rows[0]);
        Assert.Equal(new object?[] { "a", "10", "a:11" }, result.Rows[1]);
        Assert.Equal(new object?[] { "b", "10", "b:11" }, result.Rows[2]);
        Assert.Equal(new object?[] { "empty", null, "missing" }, result.Rows[4]);
    }

    /// <summary>共用的聚合识别器同时保持未分组与传统时间桶 CAST 的执行能力。</summary>
    [Fact]
    public void Select_AggregateCastWithoutTagGrouping_PreservesLegacyPipeline()
    {
        using var db = OpenSeeded();
        var total = Select(db, "SELECT CAST(SUM(Value) AS STRING), CAST(COUNT(*) AS DECIMAL) FROM Metrics");
        Assert.Equal(new object?[] { "50", 8m }, Assert.Single(total.Rows));
        var buckets = Select(db, "SELECT time, CAST(SUM(Value) AS STRING) FROM Metrics GROUP BY time(1s) ORDER BY time");
        Assert.Equal(new object?[] { 1000L, "37" }, buckets.Rows[0]);
        Assert.Equal(new object?[] { 2000L, "8" }, buckets.Rows[1]);
        Assert.Equal(new object?[] { 3000L, "5" }, buckets.Rows[2]);
    }

    /// <summary>隐藏聚合与未投影 TAG 参与多键排序，NULL 升序在前、降序在后。</summary>
    [Theory]
    [InlineData("SUM(Value) DESC, Host DESC", "c,b,a,empty,<null>")]
    [InlineData("SUM(Value) ASC, Host ASC", "<null>,empty,a,b,c")]
    public void Select_HiddenOrderKeys_PreservesNullAndMixedDirectionOrdering(string order, string expected)
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT Host FROM Metrics GROUP BY Host ORDER BY " + order);
        Assert.Equal(expected, string.Join(',', result.Rows.Select(static row => row[0] ?? "<null>")));
        Assert.Single(result.Columns);
    }

    /// <summary>多 TAG 和时间桶先完成分组，HAVING 按每个复合键筛选。</summary>
    [Fact]
    public void Select_MultipleTagsAndTimeBuckets_FiltersEachCompositeGroup()
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT Host, Region, time AS bucket, SUM(Value)+1 AS total FROM Metrics "
            + "GROUP BY Host, Region, time(10s) HAVING COUNT(Value) >= 2 "
            + "ORDER BY total DESC, Host DESC, bucket ASC");
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { "b", "east", 0L, 11L }, result.Rows[0]);
        Assert.Equal(new object?[] { "a", "east", 0L, 8L }, result.Rows[1]);
    }

    /// <summary>仅按时间分组也支持 HAVING 和投影别名排序。</summary>
    [Fact]
    public void Select_TimeOnlyHavingAndAliasOrder_UsesBucketResults()
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT time AS bucket, SUM(Value)+1 AS total FROM Metrics "
            + "GROUP BY time(1s) HAVING COUNT(Value) > 1 ORDER BY total DESC, bucket ASC LIMIT 1");
        Assert.Equal(new object?[] { 1000L, 38L }, Assert.Single(result.Rows));
    }

    /// <summary>仅按时间分组的复合键投影不依赖 HAVING 或增强排序来触发。</summary>
    [Fact]
    public void Select_TimeOnlyCompoundProjectionWithoutHaving_UsesGroupedKeys()
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT time+1 AS next, CAST(SUM(Value)+1 AS DECIMAL) AS total "
            + "FROM Metrics GROUP BY time(1s)");
        Assert.Equal(new object?[] { 1001L, 38m }, result.Rows[0]);
        Assert.Equal(new object?[] { 2001L, 9m }, result.Rows[1]);
        Assert.Equal(new object?[] { 3001L, 6m }, result.Rows[2]);
    }

    /// <summary>既有时间桶复合聚合仍可使用残差 WHERE，增强路由不会扩大拒绝边界。</summary>
    [Fact]
    public void Select_TimeOnlyAggregateExpressionWithResidual_PreservesExistingSupport()
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT time, SUM(Value)+1 FROM Metrics WHERE Value>3 GROUP BY time(1s)");
        Assert.Equal(new object?[] { 1000L, 36d }, result.Rows[0]);
        Assert.Equal(new object?[] { 2000L, 6d }, result.Rows[1]);
        Assert.Equal(new object?[] { 3000L, 6d }, result.Rows[2]);
    }

    /// <summary>大整数聚合、投影和隐藏排序不经过 Double 转换。</summary>
    [Fact]
    public void Select_Int64AggregateExpression_PreservesPrecisionAndOverflow()
    {
        using var db = OpenEmpty();
        SqlExecutor.Execute(db, "INSERT INTO Metrics(time,Host,Value) VALUES "
            + "(1,'a',9007199254740993),(2,'a',2),(1,'b',9007199254740994)");
        var result = Select(db, "SELECT Host, SUM(Value)+1 FROM Metrics GROUP BY Host ORDER BY SUM(Value) DESC");
        Assert.Equal(new object?[] { "a", 9007199254740996L }, result.Rows[0]);
        SqlExecutor.Execute(db, "INSERT INTO Metrics(time,Host,Value) VALUES (1,'overflow',9223372036854775807)");
        var overflow = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT SUM(Value)+1 FROM Metrics WHERE Host='overflow' GROUP BY Host"));
        Assert.IsType<OverflowException>(overflow.InnerException);
    }

    /// <summary>别名优先于同名 TAG，双引号按创建时的拼写精确绑定。</summary>
    [Fact]
    public void Select_QuotedAliasAndAliasShadowing_PreservesIdentifierContract()
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT Host AS Original, -SUM(Value) AS Host FROM Metrics "
            + "GROUP BY Host HAVING SUM(Value)>0 ORDER BY Host ASC, Original DESC");
        Assert.Equal(["c", "b", "a"], result.Rows.Select(static row => row[0]));
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT SUM(Value) AS \"Total.Value\" FROM Metrics "
            + "GROUP BY Host ORDER BY \"total.value\""));
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT Host AS same, SUM(Value) AS same "
            + "FROM Metrics GROUP BY Host ORDER BY same"));
    }

    /// <summary>空输入仍校验聚合、未分组列、函数参数和窗口，SELECT 与 EXPLAIN 一致。</summary>
    [Theory]
    [InlineData("SELECT SUM(Value)+Marker FROM Metrics GROUP BY Host", typeof(InvalidOperationException))]
    [InlineData("SELECT Host FROM Metrics GROUP BY Host HAVING Marker > 0", typeof(InvalidOperationException))]
    [InlineData("SELECT Host, SUM(Value) FROM Metrics GROUP BY Host ORDER BY Marker", typeof(InvalidOperationException))]
    [InlineData("SELECT round(SUM(Value),1,2) FROM Metrics GROUP BY Host", typeof(InvalidOperationException))]
    [InlineData("SELECT SUM(SUM(Value)) FROM Metrics GROUP BY Host", typeof(NotSupportedException))]
    [InlineData("SELECT row_number() OVER () FROM Metrics GROUP BY Host", typeof(NotSupportedException))]
    public void SelectAndExplain_InvalidGroupedExpressions_RejectBeforeScanning(string sql, Type exceptionType)
    {
        using var db = OpenEmpty();
        Assert.Throws(exceptionType, () => Select(db, sql));
        Assert.Throws(exceptionType, () => SqlExecutor.Execute(db, "EXPLAIN " + sql));
    }

    /// <summary>空结果保留投影名称，LIMIT 0 与高 OFFSET 均不改变分组筛选。</summary>
    [Theory]
    [InlineData("HAVING COUNT(*) > 99")]
    [InlineData("HAVING COUNT(*) > 0 LIMIT 0")]
    [InlineData("HAVING COUNT(*) > 0 LIMIT 2 OFFSET 99")]
    public void Select_EmptyResults_PreservesDeclaredProjectionNames(string suffix)
    {
        using var db = OpenSeeded();
        var result = Select(db, "SELECT Host AS Name, SUM(Value)+1 AS Adjusted FROM Metrics GROUP BY Host " + suffix);
        Assert.Equal(["Name", "Adjusted"], result.Columns);
        Assert.Empty(result.Rows);
    }

    /// <summary>读取持久段和重开后分组管线结果保持一致。</summary>
    [Fact]
    public void Select_AfterFlushAndReopen_PreservesPipelineResults()
    {
        const string sql = "SELECT Host, SUM(Value)+1 AS total FROM Metrics GROUP BY Host "
            + "HAVING COUNT(Value)>1 ORDER BY total DESC, Host DESC LIMIT 1";
        using (var db = OpenSeeded())
        {
            Assert.Equal(new object?[] { "b", 11L }, Assert.Single(Select(db, sql).Rows));
            db.FlushNow();
            Assert.Equal(new object?[] { "b", 11L }, Assert.Single(Select(db, sql).Rows));
        }
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(new object?[] { "b", 11L }, Assert.Single(Select(reopened, sql).Rows));
    }

    private Tsdb OpenEmpty()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Metrics (Host TAG, Region TAG, Value FIELD INT, Marker FIELD INT)");
        return db;
    }

    private Tsdb OpenSeeded()
    {
        var db = OpenEmpty();
        SqlExecutor.Execute(db, "INSERT INTO Metrics(time,Host,Region,Value,Marker) VALUES "
            + "(1000,'a','east',2,1),(2000,'a','west',3,1),(3000,'a','east',5,1),"
            + "(1000,'b','east',5,1),(2000,'b','east',5,1),(1000,'c','east',30,1)");
        SqlExecutor.Execute(db, "INSERT INTO Metrics(time,Host,Marker) VALUES (1000,'empty',1)");
        SqlExecutor.Execute(db, "INSERT INTO Metrics(time,Marker) VALUES (1000,1)");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
