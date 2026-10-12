using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 验证 UUID 的逐行求值、更新返回值、事务及默认表达式边界。
/// </summary>
public sealed class SqlUuidFunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-sql-uuid-" + Guid.NewGuid().ToString("N"));

    public SqlUuidFunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ScalarFunction_ZeroArguments_ReturnsNewStandardGuidString()
    {
        Assert.True(FunctionRegistry.TryGetScalar("UUID", out var function));
        Assert.Equal(FunctionKind.Scalar, FunctionRegistry.GetFunctionKind("uuid"));
        Assert.Equal(0, function.MinArgumentCount);
        Assert.Equal(0, function.MaxArgumentCount);
        string first = AssertGuid(function.Evaluate([]));
        string second = AssertGuid(function.Evaluate([]));
        Assert.NotEqual(first, second);
        Assert.Throws<InvalidOperationException>(() => function.Evaluate([null]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Select_MultipleRowsAndCalls_ReturnsDistinctGuids(bool join)
    {
        using var database = OpenWithRows();
        string from = " FROM uuid_rows a"
            + (join ? " JOIN uuid_rows b ON a.id = b.id" : string.Empty);
        var result = Query(database, "SELECT uuid(), UUID()" + from + " ORDER BY a.id");
        Assert.Equal(4, result.Rows.Count);
        string[] values = result.Rows.SelectMany(static row => row).Select(AssertGuid).ToArray();
        Assert.Equal(8, values.Distinct(StringComparer.Ordinal).Count());
        Assert.All(GetColumnInfo(result), column =>
        {
            Assert.Equal(TableColumnType.String, column.DataType);
            Assert.False(column.IsNullable);
        });
        string[] next = Query(database, "SELECT uuid()" + from).Rows.Select(row => AssertGuid(row[0])).ToArray();
        Assert.Empty(next.Intersect(values, StringComparer.Ordinal));
    }

    [Fact]
    public void Select_NoFromAndNullBranches_UsesExistingExpressionSemantics()
    {
        using var database = OpenWithRows();
        AssertGuid(Query(database, "SELECT uuid()").Rows.Single()[0]);
        var result = Query(database, "SELECT CASE WHEN guid_value IS NULL THEN uuid() ELSE guid_value END, "
            + "coalesce(guid_value, uuid()) FROM uuid_rows ORDER BY id");
        Assert.Equal(new object?[] { "keep", "keep" }, result.Rows[1]);
        Assert.All(result.Rows.Where((_, index) => index != 1), row =>
        {
            Assert.NotEqual(AssertGuid(row[0]), AssertGuid(row[1]));
        });
    }

    [Theory]
    [InlineData("uuid(NULL)")]
    [InlineData("uuid(1)")]
    [InlineData("uuid('value')")]
    [InlineData("uuid(1, 2)")]
    public void Select_Arguments_AreRejectedEvenForEmptyResults(string expression)
    {
        using var database = OpenWithRows();
        var error = Assert.Throws<InvalidOperationException>(() =>
            Query(database, "SELECT " + expression + " FROM uuid_rows WHERE id < 0"));
        Assert.Contains("0 个参数", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Update_NullPredicateAndReturning_PreservesGeneratedValuesAfterReopen()
    {
        IReadOnlyList<IReadOnlyList<object?>> returned;
        using (var database = OpenWithRows())
        {
            var update = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(database,
                "UPDATE uuid_rows SET guid_value = uuid() WHERE guid_value IS NULL RETURNING id, guid_value"));
            var result = Assert.IsType<SelectExecutionResult>(update.Returning);
            Assert.Equal(3, result.Rows.Count);
            Assert.Equal(3, result.Rows.Select(row => AssertGuid(row[1])).Distinct(StringComparer.Ordinal).Count());
            returned = result.Rows;
            AssertReturningMatchesStored(database, returned);
            Assert.Equal("keep", Query(database, "SELECT guid_value FROM uuid_rows WHERE id = 2").Rows.Single()[0]);
        }
        using var reopened = Open();
        AssertReturningMatchesStored(reopened, returned);
    }

    [Fact]
    public void Update_TransactionCommitAndRollback_KeepGeneratedValuesStable()
    {
        using var database = OpenWithRows();
        var rolledBack = new SqlTransactionContext();
        var rolledBackUpdate = TableSqlExecutor.QueueUpdate(rolledBack, database,
            Assert.IsType<UpdateStatement>(SqlParser.Parse(
                "UPDATE uuid_rows SET guid_value = uuid() WHERE guid_value IS NULL RETURNING id, guid_value")));
        var discarded = Assert.IsType<SelectExecutionResult>(rolledBackUpdate.Returning);
        Assert.Equal(3, discarded.Rows.Select(row => AssertGuid(row[1])).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Query(database, "SELECT guid_value FROM uuid_rows WHERE guid_value IS NULL").Rows, row => Assert.Null(row[0]));
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("ROLLBACK"), null, rolledBack);
        Assert.Empty(rolledBack.SnapshotTableMutations());

        var committed = new SqlTransactionContext();
        var committedUpdate = TableSqlExecutor.QueueUpdate(committed, database,
            Assert.IsType<UpdateStatement>(SqlParser.Parse(
                "UPDATE uuid_rows SET guid_value = uuid() WHERE guid_value IS NULL RETURNING id, guid_value")));
        var returned = Assert.IsType<SelectExecutionResult>(committedUpdate.Returning);
        Assert.Equal(3, returned.Rows.Count);
        Assert.Empty(returned.Rows.Select(row => AssertGuid(row[1])).Intersect(
            discarded.Rows.Select(row => AssertGuid(row[1])), StringComparer.Ordinal));
        TableSqlExecutor.CommitTransaction(database, committed);
        AssertReturningMatchesStored(database, returned.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Select_EmptyRowsAndUdfOverride_DoesNotEvaluateDuringBinding(bool join)
    {
        using var database = OpenWithRows();
        string sql = "SELECT uuid() FROM uuid_rows a"
            + (join ? " JOIN uuid_rows b ON a.id = b.id" : string.Empty);
        var empty = Query(database, sql + " WHERE a.id < 0");
        Assert.Empty(empty.Rows);
        var column = GetColumnInfo(empty).Single();
        Assert.Equal(TableColumnType.String, column.DataType);
        Assert.False(column.IsNullable);

        int calls = 0;
        database.Functions.RegisterScalar("uuid", _ => ++calls, 0, 0);
        var overriddenEmpty = Query(database, sql + " WHERE a.id < 0");
        Assert.Empty(overriddenEmpty.Rows);
        Assert.Equal(0, calls);
        Assert.Null(GetColumnInfo(overriddenEmpty).Single().DataType);
        var overridden = Query(database, sql + " ORDER BY a.id");
        Assert.Equal(new object?[] { 1, 2, 3, 4 }, overridden.Rows.Select(static row => row[0]).ToArray());
        Assert.Equal(4, calls);
        Assert.Null(GetColumnInfo(overridden).Single().DataType);

        var shortCircuited = Query(database,
            "SELECT coalesce('keep', uuid()), CASE WHEN false THEN uuid() ELSE 'keep' END FROM uuid_rows");
        Assert.All(shortCircuited.Rows, row => Assert.Equal(new object?[] { "keep", "keep" }, row));
        Assert.Equal(4, calls);
        SqlExecutor.Execute(database, "UPDATE uuid_rows SET guid_value = uuid() WHERE id < 0");
        Assert.Equal(4, calls);
    }

    [Theory]
    [InlineData("uuid()")]
    [InlineData("lower(UUID())")]
    [InlineData("coalesce('fixed', uuid())")]
    public void Default_UuidIncludingNestedCalls_IsRejectedBeforeSchemaMutation(string expression)
    {
        using var database = OpenWithRows();
        var createError = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
            "CREATE TABLE uuid_defaults (id INT, guid_value STRING DEFAULT " + expression + ", PRIMARY KEY (id))"));
        Assert.Contains("不支持列 DEFAULT", createError.Message, StringComparison.Ordinal);
        Assert.Null(database.Tables.Catalog.TryGet("uuid_defaults"));
        var alterError = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
            "ALTER TABLE uuid_rows ADD COLUMN generated STRING DEFAULT " + expression));
        Assert.Contains("不支持列 DEFAULT", alterError.Message, StringComparison.Ordinal);
        Assert.Null(database.Tables.Catalog.TryGet("uuid_rows")!.TryGetColumn("generated"));
        Assert.Equal(4, Query(database, "SELECT id FROM uuid_rows").Rows.Count);
    }

    [Fact]
    public void Default_ExistingScalarAndParameterValues_PreserveExistingContract()
    {
        using var database = Open();
        SqlExecutor.Execute(database,
            "CREATE TABLE uuid_existing (id INT, guid_value STRING DEFAULT lower('KEEP'), PRIMARY KEY (id))");
        SqlExecutor.Execute(database, "INSERT INTO uuid_existing (id) VALUES (1), (2)");
        Assert.All(Query(database, "SELECT guid_value FROM uuid_existing").Rows, row => Assert.Equal("keep", row[0]));
        string supplied = Guid.NewGuid().ToString("D");
        SqlExecutor.Execute(database, databaseName: null,
            "UPDATE uuid_existing SET guid_value = ? WHERE id > 0", new SqlParameters().AddPositional(supplied), controlPlane: null);
        Assert.All(Query(database, "SELECT guid_value FROM uuid_existing").Rows, row => Assert.Equal(supplied, row[0]));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private Tsdb OpenWithRows()
    {
        var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE uuid_rows (id INT, guid_value STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(database,
            "INSERT INTO uuid_rows (id, guid_value) VALUES (1, NULL), (2, 'keep'), (3, NULL), (4, NULL)");
        return database;
    }

    private static string AssertGuid(object? value)
    {
        string text = Assert.IsType<string>(value);
        Assert.True(Guid.TryParseExact(text, "D", out var guid));
        Assert.NotEqual(Guid.Empty, guid);
        Assert.Equal(guid.ToString("D"), text);
        return text;
    }

    private static void AssertReturningMatchesStored(Tsdb database, IReadOnlyList<IReadOnlyList<object?>> returned)
    {
        var stored = Query(database, "SELECT id, guid_value FROM uuid_rows ORDER BY id");
        Assert.All(returned, row =>
            Assert.Equal(row[1], stored.Rows.Single(candidate => Equals(candidate[0], row[0]))[1]));
    }

    private static IReadOnlyList<SelectColumnInfo> GetColumnInfo(SelectExecutionResult result)
        => Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(result.ColumnInfo);

    private static SelectExecutionResult Query(Tsdb database, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));
}
