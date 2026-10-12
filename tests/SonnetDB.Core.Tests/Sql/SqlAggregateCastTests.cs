using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #254：关系聚合结果上的 CAST、标量组合与精确类型。</summary>
public sealed class SqlAggregateCastTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-aggregate-cast-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Select_CastAggregateProjection_EvaluatesAggregateBeforeCast()
    {
        using var db = OpenValues();
        var result = Select(db, "SELECT CAST(SUM(value) AS INT), CAST(AVG(value) AS FLOAT), "
            + "CAST(MIN(value) AS DECIMAL), CAST(MAX(value) AS STRING), CAST(COUNT(value) AS INT), "
            + "CAST(COUNT(*) AS FLOAT) FROM cast_values");
        Assert.Equal(new object?[] { 15L, 5d, 3m, "7", 3L, 4d }, Assert.Single(result.Rows));
        AssertMetadata(result, [TableColumnType.Int64, TableColumnType.Float64, TableColumnType.Decimal,
            TableColumnType.String, TableColumnType.Int64, TableColumnType.Float64], [true, true, true, true, false, false]);
    }

    [Theory]
    [InlineData("WHERE id < 0")]
    [InlineData("WHERE tenant = 3")]
    public void Select_EmptyAndAllNullAggregate_KeepsCastMetadataAndNulls(string filter)
    {
        using var db = OpenValues();
        var result = Select(db, "SELECT CAST(SUM(value) AS INT), CAST(AVG(value) AS FLOAT), "
            + "CAST(MIN(value) AS DECIMAL), CAST(MAX(value) AS STRING), CAST(COUNT(value) AS INT) FROM cast_values " + filter);
        Assert.Equal(new object?[] { null, null, null, null, 0L }, Assert.Single(result.Rows));
        AssertMetadata(result, [TableColumnType.Int64, TableColumnType.Float64, TableColumnType.Decimal,
            TableColumnType.String, TableColumnType.Int64], [true, true, true, true, false]);
    }

    [Fact]
    public void GroupBy_CastInHavingAndScalarCombinations_UsesGroupResults()
    {
        using var db = OpenValues();
        var result = Select(db, "SELECT tenant, CAST(COALESCE(SUM(value), 0) AS INT), "
            + "COALESCE(CAST(AVG(value) AS FLOAT), CAST(0 AS FLOAT)), "
            + "concat('n=', CAST(COUNT(*) AS STRING)) FROM cast_values GROUP BY tenant "
            + "HAVING CAST(SUM(value) AS INT) > 7 ORDER BY tenant");
        Assert.Equal(new object?[] { 1L, 8L, 4d, "n=2" }, Assert.Single(result.Rows));
        var nullGroup = Select(db, "SELECT tenant, CAST(SUM(value) AS INT) FROM cast_values GROUP BY tenant "
            + "HAVING CAST(SUM(value) AS INT) IS NULL");
        Assert.Equal(new object?[] { 3L, null }, Assert.Single(nullGroup.Rows));
        var booleanHaving = Select(db, "SELECT tenant, CAST(SUM(value) AS INT) FROM cast_values GROUP BY tenant "
            + "HAVING CAST(SUM(value) > 7 AS BOOL)");
        Assert.Equal(new object?[] { 1L, 8L }, Assert.Single(booleanHaving.Rows));
        var emptyGroups = Select(db, "SELECT tenant, CAST(SUM(value) AS DECIMAL) FROM cast_values WHERE id < 0 GROUP BY tenant");
        Assert.Empty(emptyGroups.Rows);
        Assert.Equal(TableColumnType.Decimal, emptyGroups.ColumnInfo![1].DataType);
        Assert.True(emptyGroups.ColumnInfo[1].IsNullable);
    }

    [Fact]
    public void GroupBy_OuterScalarAroundCast_IsCalledOncePerGroup()
    {
        using var db = OpenValues();
        var observed = new List<object?>();
        db.Functions.RegisterScalar("remember_total", args => { observed.Add(args[0]); return args[0]; }, 1, 1);
        var result = Select(db, "SELECT tenant, CAST(remember_total(CAST(SUM(value) AS INT)) AS INT) "
            + "FROM cast_values GROUP BY tenant ORDER BY tenant");
        Assert.Equal(3, observed.Count);
        Assert.Contains(8L, observed);
        Assert.Contains(7L, observed);
        Assert.Contains(null, observed);
        Assert.Equal(new object?[] { 1L, 8L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 7L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, null }, result.Rows[2]);
    }

    [Fact]
    public void Select_CastAndCaseComposition_PreservesExistingNullAndTruncationRules()
    {
        using var db = OpenValues();
        var result = Select(db, "SELECT CAST(AVG(value) AS INT), "
            + "CAST(CASE WHEN CAST(COUNT(*) AS INT) > 0 THEN SUM(value) ELSE 0 END AS FLOAT), "
            + "CAST(COUNT(DISTINCT value) AS STRING) FROM cast_values WHERE tenant = 1 AND id = 1");
        Assert.Equal(new object?[] { 3L, 3d, "1" }, Assert.Single(result.Rows));
        SqlExecutor.Execute(db, "INSERT INTO cast_values (id, tenant, value) VALUES (5, 1, 4)");
        Assert.Equal(3L, Assert.Single(Select(db, "SELECT CAST(AVG(value) AS INT) FROM cast_values WHERE id IN (1, 5)").Rows)[0]);
        Assert.Equal(0L, Assert.Single(Select(db, "SELECT CAST(COALESCE(SUM(value), 0) AS INT) FROM cast_values WHERE id < 0").Rows)[0]);
    }

    [Fact]
    public void Select_DecimalAggregateAndScalarComposition_PreservesExactValues()
    {
        using var db = OpenDecimalValues();
        var result = Select(db, "SELECT CAST(SUM(amount) AS DECIMAL), CAST(AVG(amount) AS NUMERIC), "
            + "CAST(MIN(amount) AS DECIMAL), CAST(MAX(amount) AS INT), "
            + "CAST(SUM(amount) + CAST('0.1' AS DECIMAL) AS DECIMAL), "
            + "CAST(COALESCE(SUM(amount), CAST('0' AS DECIMAL)) AS DECIMAL) FROM cast_amounts");
        Assert.Equal(new object?[] { 9007199254740994m, 4503599627370497m, 0.875m, 9007199254740993L,
            9007199254740994.1m, 9007199254740994m }, Assert.Single(result.Rows));
        var having = Select(db, "SELECT CAST(SUM(amount) AS DECIMAL) FROM cast_amounts "
            + "HAVING CAST(SUM(amount) AS DECIMAL) = CAST('9007199254740994' AS DECIMAL) "
            + "AND CAST(MAX(amount) AS DECIMAL) = CAST('9007199254740993.125' AS DECIMAL)");
        Assert.Equal(9007199254740994m, Assert.Single(having.Rows)[0]);
    }

    [Fact]
    public void Select_DecimalBoundsAndOverflow_RejectsAtAggregateOrCastStage()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE decimal_bounds (id INT, amount DECIMAL(29,0), PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO decimal_bounds (id, amount) VALUES (1, '79228162514264337593543950335'), (2, '1')");
        var bounds = Select(db, "SELECT CAST(MAX(amount) AS DECIMAL), CAST(MIN(amount) AS DECIMAL) FROM decimal_bounds");
        Assert.Equal(new object?[] { decimal.MaxValue, 1m }, Assert.Single(bounds.Rows));
        var aggregateOverflow = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT CAST(SUM(amount) AS DECIMAL) FROM decimal_bounds"));
        Assert.Contains("DECIMAL 聚合发生溢出", aggregateOverflow.Message, StringComparison.Ordinal);
        Assert.IsType<OverflowException>(aggregateOverflow.InnerException);
        var castOverflow = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT CAST(MAX(amount) AS INT) FROM decimal_bounds"));
        Assert.IsType<OverflowException>(castOverflow.InnerException);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Fact]
    public void Select_OuterCastDoesNotRescueInt64SumOverflow_RequiresCastingInput()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE cast_bigints (id INT, value INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO cast_bigints (id, value) VALUES (1, 9223372036854775807), (2, 1)");
        var overflow = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT CAST(SUM(value) AS DECIMAL) FROM cast_bigints"));
        Assert.Contains("SUM(INT) 的 Int64 累加发生溢出", overflow.Message, StringComparison.Ordinal);
        Assert.Equal(9223372036854775808m, Assert.Single(Select(db,
            "SELECT CAST(SUM(CAST(value AS DECIMAL)) AS DECIMAL) FROM cast_bigints").Rows)[0]);
        var castOverflow = Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT CAST(SUM(CAST(value AS DECIMAL)) AS INT) FROM cast_bigints"));
        Assert.IsType<OverflowException>(castOverflow.InnerException);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Fact]
    public void EmbeddedAdo_CastAggregateNullMetadata_MatchesDeclaredTypes()
    {
        using (var db = OpenValues()) { }
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SUM(value) AS INT), CAST(AVG(value) AS FLOAT), "
            + "CAST(MIN(value) AS DECIMAL), CAST(MAX(value) AS STRING), CAST(COUNT(value) AS INT) FROM cast_values WHERE id < 0";
        using var reader = command.ExecuteReader();
        Assert.Equal(new[] { typeof(long), typeof(double), typeof(decimal), typeof(string), typeof(long) },
            Enumerable.Range(0, 5).Select(reader.GetFieldType).ToArray());
        Assert.Equal(new bool?[] { true, true, true, true, false }, reader.GetColumnSchema().Select(column => column.AllowDBNull).ToArray());
        Assert.True(reader.Read());
        for (int ordinal = 0; ordinal < 4; ordinal++) Assert.True(reader.IsDBNull(ordinal));
        Assert.Equal(0L, reader.GetInt64(4));
        Assert.False(reader.Read());
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private Tsdb OpenValues()
    {
        var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE cast_values (id INT, tenant INT, value INT NULL, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO cast_values (id, tenant, value) VALUES (1, 1, 3), (2, 1, 5), (3, 2, 7), (4, 3, NULL)");
        return db;
    }

    private Tsdb OpenDecimalValues()
    {
        var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE cast_amounts (id INT, amount DECIMAL(28,6), PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO cast_amounts (id, amount) VALUES (1, '9007199254740993.125'), (2, '0.875')");
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private static void AssertMetadata(SelectExecutionResult result, TableColumnType[] types, bool?[] nullable)
    {
        var info = Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(result.ColumnInfo);
        Assert.Equal(types, info.Select(column => column.DataType!.Value).ToArray());
        Assert.Equal(nullable, info.Select(column => column.IsNullable).ToArray());
    }
}
