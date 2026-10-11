using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Query.Functions;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证基础字符串聚合、关联查询和共享预算清理。</summary>
public sealed class SqlStringAggregateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-string-agg-" + Guid.NewGuid().ToString("N"));

    public SqlStringAggregateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("string_agg")]
    [InlineData("group_concat")]
    public void GroupBy_NullsDuplicatesAndUnicode_PreserveStringValues(string function)
    {
        using var db = OpenLabels();
        Assert.Equal(FunctionKind.Aggregate, FunctionRegistry.GetFunctionKind(function.ToUpperInvariant()));
        var result = Select(db, $"SELECT tenant, {function}(name, '|') AS names FROM labels GROUP BY tenant ORDER BY tenant");
        Assert.Equal(3, result.Rows.Count);
        Assert.Equal(new[] { "blue", "red", "red" }, Pieces(result.Rows[0][1], "|"));
        Assert.Null(result.Rows[1][1]);
        Assert.Equal(new[] { "", "😀汉字" }, Pieces(result.Rows[2][1], "|"));
        AssertStringMetadata(result, 1);
    }

    [Theory]
    [InlineData("string_agg")]
    [InlineData("group_concat")]
    public void Select_EmptyInputAndCorrelatedAggregate_KeepNullableStringMetadata(string function)
    {
        using var db = OpenLabels();
        var empty = Select(db, $"SELECT {function}(name, ',') FROM labels WHERE id < 0");
        Assert.Null(Assert.Single(empty.Rows)[0]);
        AssertStringMetadata(empty, 0);
        var emptyGroups = Select(db, $"SELECT tenant, {function}(name, ',') FROM labels WHERE id < 0 GROUP BY tenant");
        Assert.Empty(emptyGroups.Rows);
        AssertStringMetadata(emptyGroups, 1);
        SqlExecutor.Execute(db, "CREATE TABLE tenants (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO tenants (id) VALUES (1), (2), (3), (4)");
        string correlated = $"SELECT t.id, (SELECT {function}(l.name, ',') FROM labels l WHERE l.tenant = t.id) AS names FROM tenants t";
        var result = Select(db, correlated + " ORDER BY t.id");
        Assert.Equal(new[] { "blue", "red", "red" }, Pieces(result.Rows[0][1], ","));
        Assert.Null(result.Rows[1][1]);
        Assert.Equal(new[] { "", "😀汉字" }, Pieces(result.Rows[2][1], ","));
        Assert.Null(result.Rows[3][1]);
        AssertStringMetadata(result, 1);
        var emptyOuter = Select(db, correlated + " WHERE t.id < 0");
        Assert.Empty(emptyOuter.Rows);
        AssertStringMetadata(emptyOuter, 1);
    }

    [Theory]
    [InlineData("string_agg", null)]
    [InlineData("group_concat", "")]
    [InlineData("string_agg", "::")]
    public void Select_ParameterDelimiterAndJoin_UseBoundStringOrNull(string function, string? delimiter)
    {
        using var db = OpenLabels();
        var joined = Select(db, $"SELECT a.tenant, {function}(a.name, ?) FROM labels a JOIN labels b ON a.id = b.id GROUP BY a.tenant ORDER BY a.tenant",
            parameters: new SqlParameters().AddPositional(delimiter));
        Assert.Null(joined.Rows[1][1]);
        Assert.Equal(new[] { "blue", "red", "red" }, Pieces(joined.Rows[0][1], delimiter ?? "", concatenated: string.IsNullOrEmpty(delimiter)));
        AssertStringMetadata(joined, 1);
        Assert.Equal("x", Select(db, $"SELECT {function}('x', NULL)").Rows.Single()[0]);
        Assert.Equal("", Select(db, $"SELECT {function}('', ',')").Rows.Single()[0]);
    }

    [Fact]
    public void Select_ConcatAndNumericAggregates_PreserveExistingContracts()
    {
        using var db = OpenLabels();
        var result = Select(db, "SELECT tenant, concat('total=', sum(id)), "
            + "count(*), sum(id), avg(id), min(id), max(id) FROM labels GROUP BY tenant ORDER BY tenant");
        Assert.Equal(new object?[] { 1L, "total=10", 4L, 10L, 2.5d, 1L, 4L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, "total=5", 1L, 5L, 5d, 5L, 5L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, "total=13", 2L, 13L, 6.5d, 6L, 7L }, result.Rows[2]);
    }

    [Fact]
    public void Select_InputExpression_IsEvaluatedOncePerRowWithoutIntegralPrescan()
    {
        using var db = OpenLabels();
        int calls = 0;
        db.Functions.RegisterScalar("render_name", args => { calls++; return args[0]; }, 1, 1);
        var result = Select(db, "SELECT tenant, string_agg(render_name(name), ',') FROM labels GROUP BY tenant ORDER BY tenant");
        Assert.Equal(7, calls);
        Assert.Equal(new[] { "blue", "red", "red" }, Pieces(result.Rows[0][1], ","));
        Assert.Null(result.Rows[1][1]);
    }

    [Theory]
    [InlineData("string_agg()")]
    [InlineData("string_agg(name)")]
    [InlineData("group_concat(name, ',', 'x')")]
    [InlineData("string_agg(*)")]
    [InlineData("string_agg(name, 1)")]
    [InlineData("group_concat(name, tenant)")]
    public void Select_InvalidCall_RejectsBeforeEmptyGroupScanning(string expression)
    {
        using var db = OpenLabels();
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT tenant, " + expression + " FROM labels WHERE id < 0 GROUP BY tenant"));
    }

    [Fact]
    public void Select_ExpressionDelimiter_DoesNotEvaluateCallback()
    {
        using var db = OpenLabels();
        int calls = 0;
        db.Functions.RegisterScalar("choose_separator", _ => { calls++; return ","; }, 0, 0);
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT string_agg(name, choose_separator()) FROM labels"));
        Assert.Equal(0, calls);
        Assert.Contains("STRING", Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT string_agg(id, ',') FROM labels")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("string_agg(DISTINCT name, ',')")]
    [InlineData("group_concat(name, ',') OVER ()")]
    public void Select_OptionalAggregateFeatures_AreExplicitlyUnsupported(string expression)
    {
        using var db = OpenLabels();
        Assert.Throws<NotSupportedException>(() => Select(db, "SELECT " + expression + " FROM labels WHERE id < 0"));
        Assert.Throws<SqlParseException>(() => Select(db, "SELECT string_agg(name, ',' ORDER BY id) FROM labels"));
    }

    [Fact]
    public void Measurement_StringAggregate_IsExplicitlyUnsupported()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE MEASUREMENT text_values (device TAG, name FIELD STRING)");
        var error = Assert.Throws<NotSupportedException>(() => Select(db, "SELECT string_agg(name, ',') FROM text_values"));
        Assert.Contains("measurement", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_SameNameScalarOverride_PreservesUnknownMetadata()
    {
        using var db = Open();
        db.Functions.RegisterScalar("string_agg", _ => 42L, 2, 2);
        var result = Select(db, "SELECT string_agg('x', ',')");
        Assert.Equal(42L, result.Rows.Single()[0]);
        Assert.Null(Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(result.ColumnInfo).Single().DataType);
    }

    [Fact]
    public void Select_LargeDelimiter_RejectsBeforeFinalStringAllocationAndCleansBudget()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE empty_names (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO empty_names (id, name) VALUES (1, ''), (2, '')");
        var error = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT string_agg(name, ?) FROM empty_names",
            new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 4096 },
            new SqlParameters().AddPositional(new string('x', 10_000))));
        Assert.Contains("字符串聚合", error.Message, StringComparison.Ordinal);
        AssertClean(db);
    }

    [Fact]
    public void Aggregate_DifferentResultsInSharedRoot_RetainBudgetUntilRootEnds()
    {
        using var db = Open();
        var call = Assert.IsType<FunctionCallExpression>(Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT string_agg(name, ',') FROM labels")).Projections.Single().Expression);
        IReadOnlyList<object?[]> rows = [new object?[] { new string('x', 120) }];
        using (SqlQueryResources.EnterRoot(db, new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 1000 }))
        {
            Assert.Equal(120, Assert.IsType<string>(StringAggregateFunction.Evaluate(call, rows, row => row[0], 1000)).Length);
            Assert.Equal(120, Assert.IsType<string>(StringAggregateFunction.Evaluate(call, rows, row => row[0], 1000)).Length);
            Assert.Throws<InvalidOperationException>(() => StringAggregateFunction.Evaluate(call, rows, row => row[0], 1000));
            Assert.Equal(528L, db.SqlMemoryBudget.ReservedBytes);
        }
        AssertClean(db);
    }

    [Fact]
    public void Select_AllNullAndEmptyAggregate_DoNotRetainStringCapacity()
    {
        using var db = OpenLabels();
        var metrics = new SqlExecutionMetrics();
        Assert.Null(Select(db, "SELECT string_agg(name, ',') FROM labels WHERE tenant = 2",
            new SqlExecutionOptions { Metrics = metrics }).Rows.Single()[0]);
        AssertClean(db);
        Assert.Null(Select(db, "SELECT string_agg(name, ',') FROM labels WHERE id < 0").Rows.Single()[0]);
        AssertClean(db);
        var call = Assert.IsType<FunctionCallExpression>(Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT string_agg(name, ',') FROM labels")).Projections.Single().Expression);
        using (SqlQueryResources.EnterRoot(db, SqlExecutionOptions.Default))
        {
            Assert.Null(StringAggregateFunction.Evaluate(call, [new object?[] { null }], row => row[0], 4096));
            Assert.Null(StringAggregateFunction.Evaluate(call, [], row => row[0], 4096));
            Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Select_BudgetFailureOrCancellationAfterSpill_ReleasesAllOwnedResources(bool cancel, bool global)
    {
        using var db = Open(new SqlMemoryOptions { QueryLimitBytes = global ? 65_536 : 4096, GlobalLimitBytes = cancel ? 8192 : 4096 });
        SqlExecutor.Execute(db, "CREATE TABLE many_names (id INT, name STRING, PRIMARY KEY (id))");
        string text = new('x', cancel ? 100 : 200);
        SqlExecutor.Execute(db, "INSERT INTO many_names (id, name) VALUES " + string.Join(",",
            Enumerable.Range(1, 32).Select(id => $"({id}, '{text}')")));
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        db.Functions.RegisterScalar("render_name", args =>
        {
            if (++calls == 3 && cancel) cancellation.Cancel();
            return args[0];
        }, 1, 1);
        var metrics = new SqlExecutionMetrics();
        var options = new SqlExecutionOptions { CancellationToken = cancellation.Token, Metrics = metrics };
        if (cancel)
        {
            var error = Assert.Throws<RoutineExecutionException>(() => Select(db,
                "SELECT string_agg(render_name(name), ',') FROM many_names", options));
            Assert.Equal("routine_cancelled", error.Code);
            Assert.Equal(3, calls);
        }
        else
            Assert.Contains("字符串聚合", Assert.Throws<InvalidOperationException>(() => Select(db,
                "SELECT string_agg(name, ',') FROM many_names", options)).Message, StringComparison.Ordinal);
        Assert.True(metrics.Complete().SpillCount > 0);
        AssertClean(db);
    }

    [Fact]
    public void Select_SpillAndMaterializationBudget_PreserveOrRejectCompleteResult()
    {
        using var db = Open(new SqlMemoryOptions { QueryLimitBytes = 4096, GlobalLimitBytes = 8192 });
        SqlExecutor.Execute(db, "CREATE TABLE repeated_names (id INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO repeated_names (id, name) VALUES " + string.Join(",",
            Enumerable.Range(1, 32).Select(id => $"({id}, 'x')")));
        var metrics = new SqlExecutionMetrics();
        var result = Select(db, "SELECT group_concat(name, ',') FROM repeated_names", new SqlExecutionOptions { Metrics = metrics });
        Assert.Equal(32, Assert.IsType<string>(result.Rows.Single()[0]).Split(',').Length);
        Assert.True(metrics.Complete().SpillCount > 0);
        AssertClean(db);
        Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT string_agg(name, ',') FROM repeated_names",
            new SqlExecutionOptions { MaxMaterializedBytes = 100 }));
        AssertClean(db);
    }

    private Tsdb Open(SqlMemoryOptions? memory = null)
        => Tsdb.Open(new TsdbOptions { RootDirectory = _root, SqlMemory = memory ?? SqlMemoryOptions.Default });

    private Tsdb OpenLabels()
    {
        var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE labels (id INT, tenant INT, name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO labels (id, tenant, name) VALUES "
            + "(1, 1, 'red'), (2, 1, 'blue'), (3, 1, 'red'), (4, 1, NULL), (5, 2, NULL), (6, 3, ''), (7, 3, '😀汉字')");
        return db;
    }

    private void AssertClean(Tsdb db)
    {
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
        string spillRoot = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        if (Directory.Exists(spillRoot)) Assert.Empty(Directory.EnumerateDirectories(spillRoot));
    }

    private static string[] Pieces(object? value, string delimiter, bool concatenated = false)
    {
        string text = Assert.IsType<string>(value);
        if (concatenated)
        {
            Assert.Contains(text, new[] { "redbluered", "redredblue", "blueredred" });
            return ["blue", "red", "red"];
        }
        return text.Split(delimiter, StringSplitOptions.None).Order(StringComparer.Ordinal).ToArray();
    }

    private static void AssertStringMetadata(SelectExecutionResult result, int ordinal)
    {
        var column = Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(result.ColumnInfo)[ordinal];
        Assert.Equal(SonnetDB.Tables.TableColumnType.String, column.DataType);
        Assert.True(column.IsNullable);
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlExecutionOptions? options = null, SqlParameters? parameters = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, sql, parameters, null, options ?? SqlExecutionOptions.Default));
}
