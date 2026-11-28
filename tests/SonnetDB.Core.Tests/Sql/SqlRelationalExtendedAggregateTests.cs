using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证关系表扩展统计聚合的数值、分组、错误和执行资源合同。</summary>
public sealed class SqlRelationalExtendedAggregateTests : IDisposable
{
    private const string DirectoryPrefix = "sndb-relational-extended-aggregate-";
    private const int DigestReservationBytes = 128 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), DirectoryPrefix + Guid.NewGuid().ToString("N"));

    /// <summary>八个函数通过普通关系 SELECT 返回可空 Float64，样本方差采用 N-1。</summary>
    [Fact]
    public void Select_StatisticsAcceptIntegerFloatAndDecimal()
    {
        using Tsdb db = OpenReadings();

        var result = Select(db, """
            SELECT MeDiAn(reading + 1) AS middle, STdDeV(floating) AS deviation,
                   VARIANCE(floating) AS variation, PERCENTILE(amount, 50.0) AS fee_middle,
                   P50(amount) AS fee_p50, P90(reading) AS q90, P95(reading) AS q95, P99(reading) AS q99,
                   PERCENTILE(reading, 90) AS explicit90, PERCENTILE(reading, 95) AS explicit95,
                   PERCENTILE(reading, 99) AS explicit99, PERCENTILE(amount, 100) AS fee_max
            FROM readings WHERE category = 'a'
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(7d, Assert.IsType<double>(row[0]), precision: 10);
        Assert.Equal(Math.Sqrt(10d), Assert.IsType<double>(row[1]), precision: 10);
        Assert.Equal(10d, Assert.IsType<double>(row[2]), precision: 10);
        Assert.Equal(0.375d, Assert.IsType<double>(row[3]), precision: 10);
        Assert.Equal(row[3], row[4]);
        double p90 = Assert.IsType<double>(row[5]);
        double p95 = Assert.IsType<double>(row[6]);
        double p99 = Assert.IsType<double>(row[7]);
        Assert.InRange(p90, 8d, 10d);
        Assert.InRange(p95, p90, 10d);
        Assert.InRange(p99, p95, 10d);
        Assert.Equal(row[5], row[8]);
        Assert.Equal(row[6], row[9]);
        Assert.Equal(row[7], row[10]);
        Assert.Equal(0.625d, Assert.IsType<double>(row[11]));
        AssertStatisticsMetadata(result, expectedColumns: 12);
    }

    /// <summary>不同固定分位点匹配各自的百分数，较高分位点返回较高的估算值。</summary>
    [Fact]
    public void Select_FixedPercentileAliases_UseTheirOwnPercentages()
    {
        using Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE distribution (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO distribution (id) VALUES
            (1), (2), (3), (4), (5), (6), (7), (8), (9), (10),
            (11), (12), (13), (14), (15), (16), (17), (18), (19), (20)
            """);

        var result = Select(db, """
            SELECT median(id), p50(id), p90(id), p95(id), p99(id),
                   percentile(id, 50), percentile(id, 90), percentile(id, 95), percentile(id, 99)
            FROM distribution
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(row[0], row[1]);
        Assert.Equal(row.Skip(1).Take(4), row.Skip(5));
        Assert.InRange(Assert.IsType<double>(row[1]), 9d, 11d);
        Assert.InRange(Assert.IsType<double>(row[2]), 17.5d, 18.5d);
        Assert.InRange(Assert.IsType<double>(row[3]), 18.5d, 19.5d);
        Assert.InRange(Assert.IsType<double>(row[4]), 19.5d, 20d);
    }

    /// <summary>绑定数值参数可复用语句，分位点改变时按本次绑定值计算。</summary>
    [Fact]
    public void Select_BoundPercentileRank_UsesCurrentNumericParameter()
    {
        using Tsdb db = OpenReadings();
        const string sql = "SELECT percentile(amount, @q) FROM readings WHERE category = 'a'";
        var parameters = new SqlParameters().AddNamed("q", 50m);

        var median = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters));
        Assert.Equal(0.375d, Assert.IsType<double>(Assert.Single(Assert.Single(median.Rows))));

        parameters.AddNamed("q", 100L);
        var maximum = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters));
        Assert.Equal(0.625d, Assert.IsType<double>(Assert.Single(Assert.Single(maximum.Rows))));
    }

    /// <summary>分位点参数绑定后仍需满足类型、非空和范围要求。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("50")]
    [InlineData(0L)]
    [InlineData(101L)]
    public void Select_BoundInvalidPercentileRank_Rejects(object? rank)
    {
        using Tsdb db = OpenReadings();
        var parameters = new SqlParameters().AddNamed("q", rank);

        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, null, "SELECT percentile(reading, @q) FROM readings", parameters));
    }

    /// <summary>Int64 和 DECIMAL 按 binary64 转换，超过 53 位整数精度时允许舍入。</summary>
    [Fact]
    public void Select_LargeExactInputs_UseBinary64Precision()
    {
        using Tsdb db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE exact_readings (id INT, reading INT, amount DECIMAL(20,0), PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO exact_readings (id, reading, amount) VALUES
            (1, 9007199254740992, CAST('9007199254740992' AS DECIMAL)),
            (2, 9007199254740993, CAST('9007199254740993' AS DECIMAL))
            """);

        var result = Select(db, """
            SELECT max(reading), max(amount), percentile(reading, 100), p50(amount),
                   variance(reading), variance(amount)
            FROM exact_readings
            """);

        Assert.Equal(new object?[]
        {
            9007199254740993L, 9007199254740993m, 9007199254740992d, 9007199254740992d, 0d, 0d,
        }, Assert.Single(result.Rows));
    }

    /// <summary>有限的异号极值可计算分位数，无法表示的方差报告 SQL 错误。</summary>
    [Fact]
    public void Select_ExtremeFloatValues_ChecksResultFiniteness()
    {
        using Tsdb db = OpenReadings();
        db.Functions.RegisterScalar("extreme_sample", arguments =>
            Assert.IsType<long>(arguments[0]) == 1 ? -1.7e308 : 1.7e308, 1, 1);
        db.Functions.RegisterScalar("nonfinite_sample", _ => double.PositiveInfinity, 0, 0);

        var result = Select(db, """
            SELECT median(extreme_sample(id)), percentile(extreme_sample(id), 100)
            FROM readings WHERE id <= 2
            """);
        Assert.Equal(new object?[] { 0d, 1.7e308 }, Assert.Single(result.Rows));
        InvalidOperationException overflow = Assert.Throws<InvalidOperationException>(() => Select(db, """
            SELECT variance(extreme_sample(id)) FROM readings WHERE id <= 2
            """));
        Assert.Contains("有限 Float64", overflow.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Select(db, """
            SELECT stddev(extreme_sample(id)) FROM readings WHERE id <= 2
            """));
        InvalidOperationException nonfinite = Assert.Throws<InvalidOperationException>(() => Select(db, """
            SELECT p50(nonfinite_sample()) FROM readings WHERE id = 1
            """));
        Assert.Contains("有限 Float64", nonfinite.Message, StringComparison.Ordinal);
    }

    /// <summary>缩放的样本矩保留可表示的结果，允许标准差在对应方差溢出时仍可计算。</summary>
    [Fact]
    public void Select_ScaledMoments_PreserveRepresentableStatistics()
    {
        using Tsdb db = OpenReadings();
        db.Functions.RegisterScalar("scaled_sample", arguments =>
            (Assert.IsType<long>(arguments[0]) - 2L) * 1e154, 1, 1);
        db.Functions.RegisterScalar("wide_sample", arguments =>
            Assert.IsType<long>(arguments[0]) == 1 ? -1e308 : 1e308, 1, 1);

        var result = Select(db, """
            SELECT variance(scaled_sample(id)), stddev(scaled_sample(id)) FROM readings WHERE id <= 3
            """);
        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(1d, Assert.IsType<double>(row[0]) / 1e308, precision: 12);
        Assert.Equal(1d, Assert.IsType<double>(row[1]) / 1e154, precision: 12);

        var finiteDeviation = Select(db, "SELECT stddev(wide_sample(id)) FROM readings WHERE id <= 2");
        Assert.Equal(Math.Sqrt(2d), Assert.IsType<double>(Assert.Single(Assert.Single(finiteDeviation.Rows))) / 1e308,
            precision: 12);
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT variance(wide_sample(id)) FROM readings WHERE id <= 2"));
    }

    /// <summary>共同偏移量很大时，样本矩保留两个相邻 binary64 值之间的差值。</summary>
    [Fact]
    public void Select_LargeOffset_PreservesAdjacentFloatValues()
    {
        using Tsdb db = OpenReadings();
        db.Functions.RegisterScalar("adjacent_sample", arguments =>
            1e16 + 2d * Assert.IsType<long>(arguments[0]), 1, 1);
        var result = Select(db, """
            SELECT variance(adjacent_sample(id)), stddev(adjacent_sample(id))
            FROM readings WHERE id <= 2
            """);

        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(2d, Assert.IsType<double>(row[0]));
        Assert.Equal(Math.Sqrt(2d), Assert.IsType<double>(row[1]), precision: 12);
    }

    /// <summary>未分组的空输入和全 NULL 输入各产生一行 NULL，分组的空输入不产生行。</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public void Select_EmptyOrAllNull_PreservesNullableStatistics(string category)
    {
        using Tsdb db = OpenReadings();

        var result = Select(db, $"""
            SELECT median(reading), stddev(reading), variance(reading), percentile(reading, 50),
                   p50(reading), p90(reading), p95(reading), p99(reading)
            FROM readings WHERE category = '{category}'
            """);
        Assert.All(Assert.Single(result.Rows), value => Assert.Null(value));
        AssertStatisticsMetadata(result, expectedColumns: 8);

        var emptyGroups = Select(db, """
            SELECT median(reading) FROM readings WHERE category = 'missing' GROUP BY category
            """);
        Assert.Empty(emptyGroups.Rows);
        AssertStatisticsMetadata(emptyGroups, expectedColumns: 1);
    }

    /// <summary>单个有效值的分位数等于样本值，样本方差及标准差保持 NULL。</summary>
    [Fact]
    public void Select_Singleton_SeparatesQuantileFromSampleVariance()
    {
        using Tsdb db = OpenReadings();
        var result = Select(db, """
            SELECT median(amount), stddev(amount), variance(reading), percentile(amount, 0.1),
                   p50(amount), p90(amount), p95(amount), p99(amount)
            FROM readings WHERE category = 'singleton'
            """);

        Assert.Equal(new object?[] { -0.5d, null, null, -0.5d, -0.5d, -0.5d, -0.5d, -0.5d },
            Assert.Single(result.Rows));
        AssertStatisticsMetadata(result, expectedColumns: 8);
    }

    /// <summary>HAVING 和 ORDER BY 可以使用未出现在投影里的聚合，分组仍按各自样本计算。</summary>
    [Fact]
    public void GroupBy_HavingAndAggregateOrder_UsesEachGroupsInput()
    {
        using Tsdb db = OpenReadings();

        var sortedByAggregate = Select(db, """
            SELECT category, median(reading) AS middle, count(reading) AS samples
            FROM readings
            GROUP BY category
            HAVING variance(reading) >= 4
            ORDER BY percentile(reading, 100) DESC, middle ASC
            """);
        Assert.Collection(sortedByAggregate.Rows,
            row => Assert.Equal(new object?[] { "b", 20d, 3L }, row),
            row => Assert.Equal(new object?[] { "a", 6d, 5L }, row));
        Assert.Equal(new[] { "category", "middle", "samples" }, sortedByAggregate.Columns);

        var sortedByAlias = Select(db, """
            SELECT category, p50(reading) AS middle
            FROM readings
            GROUP BY category
            HAVING stddev(reading) IS NOT NULL
            ORDER BY middle ASC
            """);
        Assert.Collection(sortedByAlias.Rows,
            row => Assert.Equal(new object?[] { "a", 6d }, row),
            row => Assert.Equal(new object?[] { "b", 20d }, row));

        var hiddenSort = Select(db, """
            SELECT category FROM readings WHERE category IN ('a', 'b')
            GROUP BY category ORDER BY p95(reading) DESC
            """);
        Assert.Collection(hiddenSort.Rows,
            row => Assert.Equal("b", Assert.Single(row)),
            row => Assert.Equal("a", Assert.Single(row)));

        var allNullGroup = Select(db, """
            SELECT median(reading) FROM readings GROUP BY category
            HAVING count(reading) = 0 AND p50(reading) IS NULL
            """);
        Assert.Null(Assert.Single(Assert.Single(allNullGroup.Rows)));
    }

    /// <summary>关系统计函数对无效参数形状给出 SQL 错误。</summary>
    [Theory]
    [InlineData("median()")]
    [InlineData("stddev(reading, 10)")]
    [InlineData("p50(*)")]
    [InlineData("variance(DISTINCT reading)")]
    [InlineData("percentile(reading)")]
    [InlineData("percentile(reading, 50, 80)")]
    [InlineData("percentile(reading, reading)")]
    [InlineData("percentile(reading, NULL)")]
    [InlineData("percentile(reading, '50')")]
    [InlineData("percentile(reading, 0)")]
    [InlineData("percentile(reading, -1)")]
    [InlineData("percentile(reading, 101)")]
    [InlineData("p95(reading) OVER (ORDER BY id)")]
    [InlineData("p90(SUM(reading))")]
    public void Select_InvalidAggregateShape_Rejects(string expression)
    {
        using Tsdb db = OpenReadings();
        Assert.Throws<InvalidOperationException>(() => Select(db, $"SELECT {expression} FROM readings"));
    }

    /// <summary>WHERE、分组键和表连接条件不能在逐行求值时调用统计聚合。</summary>
    [Theory]
    [InlineData("SELECT id FROM readings WHERE median(reading) > 1")]
    [InlineData("SELECT id FROM readings WHERE id < 0 AND p95(reading) > 0")]
    [InlineData("SELECT count(*) FROM readings GROUP BY p50(reading)")]
    [InlineData("SELECT l.id FROM readings l JOIN readings r ON median(l.reading) = r.reading")]
    public void Select_StatisticsInRowContext_Rejects(string sql)
    {
        using Tsdb db = OpenReadings();
        Assert.Throws<InvalidOperationException>(() => Select(db, sql));
    }

    /// <summary>关系表扩展聚合返回各自声明的结果类型。</summary>
    [Theory]
    [InlineData("spread(reading)", typeof(double))]
    [InlineData("mode(reading)", typeof(long))]
    [InlineData("histogram(reading, 5)", typeof(string))]
    [InlineData("distinct_count(reading)", typeof(long))]
    [InlineData("tdigest_agg(reading)", typeof(string))]
    public void Select_ExtendedAggregate_ReturnsDeclaredType(string expression, Type expectedType)
    {
        using Tsdb db = OpenReadings();
        var result = Select(db, $"SELECT {expression} FROM readings");
        Assert.IsType(expectedType, Assert.Single(Assert.Single(result.Rows)));
    }

    /// <summary>布尔值及文本列即使可转换为数值也会报错。</summary>
    [Theory]
    [InlineData("median(enabled)", "")]
    [InlineData("variance(text_value)", "")]
    [InlineData("median(enabled)", "WHERE id < 0")]
    [InlineData("variance(text_value)", "LIMIT 0")]
    public void Select_NonNumericValueType_Rejects(string expression, string suffix)
    {
        using Tsdb db = OpenReadings();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Select(db, $"SELECT {expression} FROM readings {suffix}"));
        Assert.Contains("需要数值参数", error.Message, StringComparison.Ordinal);
    }

    /// <summary>在聚合表达式求值期间取消会释放资源，后续查询可重新执行。</summary>
    [Fact]
    public void Select_CancelledDuringAggregation_ReleasesBudgetAndAllowsRetry()
    {
        using Tsdb db = OpenReadings();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int evaluated = 0;
        db.Functions.RegisterScalar("cancel_on_third_sample", arguments =>
        {
            if (++evaluated == 3)
                cancellation.Cancel();
            return arguments[0];
        }, 1, 1);
        const string sql = "SELECT median(cancel_on_third_sample(reading)) FROM readings WHERE category = 'a'";

        RoutineExecutionException error = Assert.Throws<RoutineExecutionException>(() =>
            Select(db, sql, new SqlExecutionOptions { CancellationToken = cancellation.Token }));

        Assert.Equal("routine_cancelled", error.Code);
        Assert.Equal(3, evaluated);
        AssertExecutionResourcesReleased(db);
        Assert.Equal(6d, Assert.IsType<double>(Assert.Single(Assert.Single(Select(db, sql).Rows))));
        AssertExecutionResourcesReleased(db);
    }

    /// <summary>阻塞算子预算拒绝无法容纳累加器的查询并释放资源。</summary>
    [Fact]
    public void Select_InsufficientOperatorBudget_ReleasesBudgetAndAllowsRetry()
    {
        using Tsdb db = OpenReadings();
        const string sql = "SELECT p50(amount) FROM readings WHERE category = 'a'";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Select(db, sql,
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = DigestReservationBytes - 1 }));

        Assert.Contains("统计聚合状态超出 SQL 查询内存预算", error.Message, StringComparison.Ordinal);
        AssertExecutionResourcesReleased(db);
        Assert.Equal(0.375d, Assert.IsType<double>(Assert.Single(Assert.Single(Select(db, sql).Rows))));
        AssertExecutionResourcesReleased(db);
    }

    /// <summary>分组行写入临时文件后仍可回读并计算统计值，执行结束时清除工作区。</summary>
    [Fact]
    public void GroupBy_SpilledRows_PreservesStatisticsAndCleansWorkspace()
    {
        using Tsdb db = OpenReadings();
        var metrics = new SqlExecutionMetrics();
        var result = Select(db, """
            SELECT category, p50(reading) AS middle
            FROM readings GROUP BY category
            HAVING count(reading) >= 2
            ORDER BY p99(reading) DESC
            """, new SqlExecutionOptions
        {
            BlockingOperatorMemoryLimitBytes = DigestReservationBytes + 256,
            DeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            Metrics = metrics,
        });

        Assert.Collection(result.Rows,
            row => Assert.Equal(new object?[] { "b", 20d }, row),
            row => Assert.Equal(new object?[] { "a", 6d }, row));
        Assert.True(metrics.Complete().SpillCount > 0);
        AssertExecutionResourcesReleased(db);
    }

    /// <summary>清除本测试实例创建的数据库目录。</summary>
    public void Dispose()
    {
        string target = Path.GetFullPath(_root);
        string tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(target), tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith(DirectoryPrefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("测试数据库清理路径不在本测试的临时目录范围内。");
        }

        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
    }

    private Tsdb OpenReadings()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        try
        {
            SqlExecutor.Execute(db, """
                CREATE TABLE readings (
                    id INT, category STRING, reading INT, floating FLOAT, amount DECIMAL(22,6),
                    enabled BOOL, text_value STRING, PRIMARY KEY (id))
                """);
            SqlExecutor.Execute(db, """
                INSERT INTO readings (id, category, reading, floating, amount, enabled, text_value) VALUES
                (1, 'a', 2, 2.5, CAST('0.125' AS DECIMAL), TRUE, '2'),
                (2, 'a', 4, 4.5, CAST('0.25' AS DECIMAL), FALSE, '4'),
                (3, 'a', 6, 6.5, CAST('0.375' AS DECIMAL), NULL, NULL),
                (4, 'a', 8, 8.5, CAST('0.5' AS DECIMAL), NULL, NULL),
                (5, 'a', 10, 10.5, CAST('0.625' AS DECIMAL), NULL, NULL),
                (6, 'a', NULL, NULL, NULL, NULL, NULL),
                (7, 'b', 18, NULL, NULL, NULL, NULL),
                (8, 'b', 20, NULL, NULL, NULL, NULL),
                (9, 'b', 22, NULL, NULL, NULL, NULL),
                (10, 'empty', NULL, NULL, NULL, NULL, NULL),
                (11, 'singleton', -2, NULL, CAST('-0.5' AS DECIMAL), NULL, NULL),
                (12, 'singleton', NULL, NULL, NULL, NULL, NULL)
                """);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null)
        => options is null
            ? Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql))
            : Assert.IsType<SelectExecutionResult>(SqlExecutor.ExecuteStatement(
                db, null, SqlParser.Parse(sql), null, null, options));

    private static void AssertStatisticsMetadata(SelectExecutionResult result, int expectedColumns)
    {
        Assert.NotNull(result.ColumnInfo);
        Assert.Equal(expectedColumns, result.ColumnInfo.Count);
        Assert.All(result.ColumnInfo, column =>
        {
            Assert.Equal(TableColumnType.Float64, column.DataType);
            Assert.True(column.IsNullable);
        });
    }

    private void AssertExecutionResourcesReleased(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
        string spillRoot = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        if (Directory.Exists(spillRoot))
            Assert.Empty(Directory.EnumerateDirectories(spillRoot));
    }
}
