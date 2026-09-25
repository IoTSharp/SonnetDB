using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Data.Embedded;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #172：关系表 ANSI 窗口函数的分区、排序与聚合边界。</summary>
public sealed class SqlRelationalWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "sndb-rel-window-" + Guid.NewGuid().ToString("N"));

    public SqlRelationalWindowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Parse_PartitionAndCompositeOrder_PreservesWindowSpecification()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT row_number() OVER (PARTITION BY tenant_id, category ORDER BY created_at DESC, id ASC)
            FROM devices
            """));
        var function = Assert.IsType<FunctionCallExpression>(Assert.Single(statement.Projections).Expression);
        Assert.Equal(2, function.Over!.PartitionBy.Count);
        Assert.Equal(2, function.Over.OrderBy.Count);
        Assert.Equal(SortDirection.Descending, function.Over.OrderBy[0].Direction);
        Assert.Equal(SortDirection.Ascending, function.Over.OrderBy[1].Direction);
    }

    [Fact]
    public void Execute_MultiplePartitionsAndCompositeOrder_ProducesOrdinalsAndFullPartitionAggregates()
    {
        using var db = OpenDatabase(withRows: true);
        var result = Select(db, """
            SELECT id,
                   row_number() OVER (PARTITION BY tenant_id ORDER BY created_at, id) AS rn,
                   count(*) OVER (PARTITION BY tenant_id) AS total,
                   count(value) OVER (PARTITION BY tenant_id) AS non_null,
                   sum(value) OVER (PARTITION BY tenant_id) AS sum_value,
                   avg(value) OVER (PARTITION BY tenant_id) AS avg_value,
                   min(value) OVER (PARTITION BY tenant_id) AS min_value,
                   max(value) OVER (PARTITION BY tenant_id) AS max_value
            FROM devices ORDER BY id
            """);

        Assert.Equal(5, result.Rows.Count);
        Assert.Equal(new object?[] { 1L, 1L, 3L, 2L, 30L, 15d, 10L, 20L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 2L, 3L, 2L, 30L, 15d, 10L, 20L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, 3L, 3L, 2L, 30L, 15d, 10L, 20L }, result.Rows[2]);
        Assert.Equal(new object?[] { 4L, 1L, 2L, 2L, 10L, 5d, 5L, 5L }, result.Rows[3]);
        Assert.Equal(new object?[] { 5L, 2L, 2L, 2L, 10L, 5d, 5L, 5L }, result.Rows[4]);
    }

    [Fact]
    public void Execute_OrderedAggregate_UsesPeerAwareCumulativeFrame()
    {
        using var db = OpenDatabase(withRows: true);
        var result = Select(db, """
            SELECT id,
                   count(*) OVER (PARTITION BY tenant_id ORDER BY created_at) AS count_so_far,
                   sum(value) OVER (PARTITION BY tenant_id ORDER BY created_at) AS sum_so_far
            FROM devices ORDER BY id
            """);

        Assert.Equal(new object?[] { 1L, 2L, 10L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 2L, 10L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, 3L, 30L }, result.Rows[2]);
        Assert.Equal(new object?[] { 4L, 2L, 10L }, result.Rows[3]);
        Assert.Equal(new object?[] { 5L, 2L, 10L }, result.Rows[4]);
    }

    [Fact]
    public void Execute_CompositePartitionAndPagination_ProcessesFullFilteredRelation()
    {
        using var db = OpenDatabase(withRows: true);
        var result = Select(db, """
            SELECT id,
                   row_number() OVER (PARTITION BY tenant_id, value ORDER BY id DESC) AS rn,
                   count(*) OVER (PARTITION BY tenant_id, value) AS peers
            FROM devices ORDER BY id LIMIT 2 OFFSET 3
            """);

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new object?[] { 4L, 2L, 2L }, result.Rows[0]);
        Assert.Equal(new object?[] { 5L, 1L, 2L }, result.Rows[1]);
    }

    [Fact]
    public void Execute_OrderByWindowAliasAndLimit_SortsAfterWindowEvaluation()
    {
        using var db = OpenDatabase(withRows: true);
        var result = Select(db, """
            SELECT id, row_number() OVER (ORDER BY id) AS rn
            FROM devices ORDER BY rn DESC LIMIT 2
            """);

        Assert.Equal(new object?[] { 5L, 5L }, result.Rows[0]);
        Assert.Equal(new object?[] { 4L, 4L }, result.Rows[1]);
    }

    [Fact]
    public void Execute_FilteredSingleRowAndNullOnlyPartition_PreserveSqlNullSemantics()
    {
        using var db = OpenDatabase(withRows: true);
        var result = Select(db, """
            SELECT id, count(value) OVER (PARTITION BY tenant_id) AS present,
                   sum(value) OVER (PARTITION BY tenant_id) AS total
            FROM devices WHERE id = 2
            """);

        Assert.Equal(new object?[] { 2L, 0L, null }, Assert.Single(result.Rows));
    }

    [Fact]
    public void Execute_NullSortKeyAndDescendingOrder_UsesStablePeerTieBreak()
    {
        using var db = OpenDatabase(withRows: true);
        SqlExecutor.Execute(db, "INSERT INTO devices (id, tenant_id, created_at, value) VALUES (6, 1, NULL, NULL)");
        var result = Select(db, """
            SELECT id, row_number() OVER (PARTITION BY tenant_id ORDER BY created_at DESC, id ASC) AS rn
            FROM devices ORDER BY id
            """);

        Assert.Equal([2L, 3L, 1L, 4L],
            result.Rows.Where(row => (long)row[0]! is 1 or 2 or 3 or 6)
                .Select(row => (long)row[1]!).ToArray());
    }

    [Fact]
    public void Execute_EmptyTable_ProducesNoRowsAndWindowMetadata()
    {
        using var db = OpenDatabase(withRows: false);
        var result = Select(db, """
            SELECT id, row_number() OVER (ORDER BY id) AS rn,
                   count(*) OVER () AS total, sum(value) OVER () AS total_value
            FROM devices
            """);

        Assert.Empty(result.Rows);
        Assert.Equal(4, result.Columns.Count);
        Assert.Equal(SonnetDB.Tables.TableColumnType.Int64, Assert.IsType<SelectColumnInfo[]>(result.ColumnInfo)[1].DataType);
    }

    [Fact]
    public void Execute_RowNumberWithoutOrderAndMixedAggregate_FailClosed()
    {
        using var db = OpenDatabase(withRows: true);
        var unordered = Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT row_number() OVER (PARTITION BY tenant_id) FROM devices"));
        Assert.Contains("ORDER BY", unordered.Message, StringComparison.Ordinal);

        var mixed = Assert.Throws<NotSupportedException>(() =>
            Select(db, "SELECT count(*), row_number() OVER (ORDER BY id) FROM devices"));
        Assert.Contains("普通聚合", mixed.Message, StringComparison.Ordinal);

        var grouped = Assert.Throws<NotSupportedException>(() =>
            Select(db, "SELECT tenant_id, count(*) OVER () FROM devices GROUP BY tenant_id"));
        Assert.Contains("GROUP BY", grouped.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_InvalidWindowExpressionAndUnknownKey_FailClosed()
    {
        using var db = OpenDatabase(withRows: false);
        Assert.Throws<NotSupportedException>(() =>
            Select(db, "SELECT row_number() OVER (ORDER BY id) + 1 FROM devices"));
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT count(*) OVER (PARTITION BY missing) FROM devices"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "SELECT count(*) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM devices"));
    }

    [Fact]
    public void EmbeddedAdo_WindowColumns_PreserveValuesAndInt64Metadata()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE devices (id INT, tenant_id INT, PRIMARY KEY (id))";
            setup.ExecuteNonQuery();
            setup.CommandText = "INSERT INTO devices (id, tenant_id) VALUES (1, 7), (2, 7)";
            setup.ExecuteNonQuery();
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, row_number() OVER (PARTITION BY tenant_id ORDER BY id) AS rn,
                   count(*) OVER () AS total FROM devices ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        Assert.Equal(typeof(long), reader.GetFieldType(1));
        Assert.Equal(typeof(long), reader.GetFieldType(2));
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(2L, reader.GetInt64(2));
        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.False(reader.Read());
    }

    private Tsdb OpenDatabase(bool withRows)
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE devices (id INT, tenant_id INT, created_at INT, value INT, PRIMARY KEY (id))
            """);
        if (withRows)
            SqlExecutor.Execute(db, """
                INSERT INTO devices (id, tenant_id, created_at, value) VALUES
                (1, 1, 100, 10), (2, 1, 100, NULL), (3, 1, 101, 20),
                (4, 2, 100, 5), (5, 2, 100, 5)
                """);
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
