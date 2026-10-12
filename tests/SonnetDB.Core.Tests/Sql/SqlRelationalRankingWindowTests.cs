using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #222：关系表排名、分桶、参数绑定及整数投影合同。</summary>
public sealed class SqlRelationalRankingWindowTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
        "sndb-ranking-window-" + Guid.NewGuid().ToString("N")));

    /// <summary>删除本用例独占的数据目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>问题中的最小查询保留并列排名与分区重置。</summary>
    [Fact]
    public void Execute_Issue222Example_ReturnsRanksAndTiles()
    {
        using var db = OpenDatabase();
        var result = Select(db, """
            SELECT id,
                   rank() OVER (PARTITION BY tenant ORDER BY score) AS rank_value,
                   dense_rank() OVER (PARTITION BY tenant ORDER BY score) AS dense_rank_value,
                   ntile(2) OVER (PARTITION BY tenant ORDER BY score, id) AS tile_value
            FROM scores ORDER BY id
            """);

        Assert.Equal(new object?[] { 1L, 1L, 1L, 1L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 1L, 1L, 1L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, 3L, 2L, 2L }, result.Rows[2]);
        Assert.Equal(new object?[] { 4L, 1L, 1L, 1L }, result.Rows[3]);
    }

    /// <summary>NULL 和复合分区、升降序键沿用已有比较合同。</summary>
    [Fact]
    public void Execute_CompositeKeysAndNullPeers_PreserveAscendingAndDescendingRanks()
    {
        using var db = OpenDatabase(withRows: false);
        SqlExecutor.Execute(db, """
            INSERT INTO scores (id, tenant, category, score, tie) VALUES
            (1, 1, NULL, NULL, 2), (2, 1, NULL, NULL, 2),
            (3, 1, NULL, 10, 2), (4, 1, NULL, 10, 1),
            (5, 1, 2, 10, 1), (6, 2, NULL, 5, 1)
            """);
        var result = Select(db, """
            SELECT id,
                   rank() OVER (PARTITION BY tenant, category ORDER BY score ASC, tie DESC) AS ar,
                   dense_rank() OVER (PARTITION BY tenant, category ORDER BY score ASC, tie DESC) AS ad,
                   rank() OVER (PARTITION BY tenant, category ORDER BY score DESC, tie ASC) AS dr,
                   dense_rank() OVER (PARTITION BY tenant, category ORDER BY score DESC, tie ASC) AS dd,
                   ntile(3) OVER (PARTITION BY tenant, category ORDER BY score ASC, tie DESC, id) AS tile
            FROM scores ORDER BY id
            """);

        Assert.Equal(new object?[] { 1L, 1L, 1L, 3L, 3L, 1L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 1L, 1L, 3L, 3L, 1L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, 3L, 2L, 2L, 2L, 2L }, result.Rows[2]);
        Assert.Equal(new object?[] { 4L, 4L, 3L, 1L, 1L, 3L }, result.Rows[3]);
        Assert.Equal(new object?[] { 5L, 1L, 1L, 1L, 1L, 1L }, result.Rows[4]);
        Assert.Equal(new object?[] { 6L, 1L, 1L, 1L, 1L, 1L }, result.Rows[5]);
    }

    /// <summary>显式唯一排序键会拆分并列，输入稳定次序本身不会拆分并列。</summary>
    [Fact]
    public void Execute_UniqueOrderKey_SeparatesPeersOnlyWhenExplicit()
    {
        using var db = OpenDatabase();
        var result = Select(db, """
            SELECT id,
                   rank() OVER (PARTITION BY tenant ORDER BY score) AS peers,
                   rank() OVER (PARTITION BY tenant ORDER BY score, id) AS unique_rank,
                   dense_rank() OVER (PARTITION BY tenant ORDER BY score, id) AS unique_dense
            FROM scores ORDER BY id
            """);

        Assert.Equal(new object?[] { 1L, 1L, 1L, 1L }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, 1L, 2L, 2L }, result.Rows[1]);
        Assert.Equal(new object?[] { 3L, 3L, 3L, 3L }, result.Rows[2]);
    }

    /// <summary>非整除分区把余数分给前面的桶，大桶数不会留下编号间隙。</summary>
    /// <param name="buckets">桶数。</param>
    /// <param name="expected">六行对应的桶编号。</param>
    [Theory]
    [InlineData(1L, new long[] { 1, 1, 1, 1, 1, 1 })]
    [InlineData(2L, new long[] { 1, 1, 1, 2, 2, 2 })]
    [InlineData(3L, new long[] { 1, 1, 2, 2, 3, 3 })]
    [InlineData(4L, new long[] { 1, 1, 2, 2, 3, 4 })]
    [InlineData(6L, new long[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(10L, new long[] { 1, 2, 3, 4, 5, 6 })]
    [InlineData(long.MaxValue, new long[] { 1, 2, 3, 4, 5, 6 })]
    public void Execute_NtileBucketBoundaries_DistributesLargerBucketsFirst(long buckets, long[] expected)
    {
        using var db = OpenDatabase(withRows: false);
        SqlExecutor.Execute(db, """
            INSERT INTO scores (id, tenant, score) VALUES
            (1, 1, 10), (2, 1, 10), (3, 1, 10),
            (4, 1, 10), (5, 1, 10), (6, 1, 10)
            """);
        var result = Select(db,
            "SELECT ntile(@buckets) OVER (ORDER BY score, id) AS tile FROM scores ORDER BY id",
            new SqlParameters().AddNamed("buckets", buckets));

        Assert.Equal(expected, result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    /// <summary>正 Int64 最大字面量按实际行数形成非空桶。</summary>
    [Fact]
    public void Execute_NtileInt64MaximumLiteral_UsesOneBucketPerRow()
    {
        using var db = OpenDatabase();
        var result = Select(db,
            "SELECT ntile(9223372036854775807) OVER (ORDER BY id) FROM scores ORDER BY id");

        Assert.Equal([1L, 2L, 3L, 4L],
            result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    /// <summary>重复解析相同 SQL 时各次绑定使用本次命名和位置参数。</summary>
    [Fact]
    public void Execute_ChangedNamedAndPositionalParameters_UseCurrentBoundValues()
    {
        using var db = OpenDatabase();
        const string sql = "SELECT ntile(@buckets) OVER (ORDER BY id) FROM scores ORDER BY id";
        var two = Select(db, sql, new SqlParameters().AddNamed("buckets", 2));
        var three = Select(db, sql, new SqlParameters().AddNamed("buckets", 3L));
        var positional = Select(db,
            "SELECT ntile(?) OVER (ORDER BY id) FROM scores ORDER BY id",
            new SqlParameters().AddPositional(3));

        Assert.Equal([1L, 1L, 2L, 2L], two.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
        Assert.Equal([1L, 1L, 2L, 3L], three.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
        Assert.Equal([1L, 1L, 2L, 3L], positional.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    /// <summary>空表也先拒绝非法排名参数。</summary>
    /// <param name="expression">非法窗口函数调用。</param>
    [Theory]
    [InlineData("rank(1)")]
    [InlineData("rank(*)")]
    [InlineData("rank(DISTINCT 1)")]
    [InlineData("dense_rank(1)")]
    [InlineData("dense_rank(*)")]
    [InlineData("dense_rank(DISTINCT 1)")]
    [InlineData("ntile()")]
    [InlineData("ntile(*)")]
    [InlineData("ntile(DISTINCT 2)")]
    [InlineData("ntile(2, 3)")]
    [InlineData("ntile(0)")]
    [InlineData("ntile(-1)")]
    [InlineData("ntile(NULL)")]
    [InlineData("ntile(1.5)")]
    [InlineData("ntile(2.0)")]
    [InlineData("ntile('2')")]
    [InlineData("ntile(TRUE)")]
    [InlineData("ntile(id)")]
    [InlineData("ntile(1 + 1)")]
    public void Execute_InvalidRankingArgumentsOnEmptyTable_FailClosed(string expression)
    {
        using var db = OpenDatabase(withRows: false);
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, $"SELECT {expression} OVER (ORDER BY id) FROM scores"));
    }

    /// <summary>参数类型必须保留整数语义，不把浮点、字符串或 NULL 转换为桶数。</summary>
    /// <param name="value">无效桶数参数。</param>
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(2.0)]
    [InlineData("2")]
    [InlineData(true)]
    [InlineData(null)]
    public void Execute_InvalidBoundBucketParameter_FailsBeforeEmptyPartition(object? value)
    {
        using var db = OpenDatabase(withRows: false);
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT ntile(@buckets) OVER (ORDER BY id) FROM scores",
            new SqlParameters().AddNamed("buckets", value)));
    }

    /// <summary>数据库 NULL 参数和缺失参数分别明确拒绝。</summary>
    [Fact]
    public void Execute_DbNullAndMissingBucketParameter_FailClosed()
    {
        using var db = OpenDatabase(withRows: false);
        const string sql = "SELECT ntile(@buckets) OVER (ORDER BY id) FROM scores";
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, sql, new SqlParameters().AddNamed("buckets", DBNull.Value)));
        var missing = Assert.Throws<InvalidOperationException>(() => Select(db, sql, new SqlParameters()));
        Assert.Contains("未提供参数", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>无排序时每分区为一个 peer，分桶保留当前输入次序且不提供跨执行顺序保证。</summary>
    [Fact]
    public void Execute_RankingWithoutWindowOrder_UsesWholePartitionPeers()
    {
        using var db = OpenDatabase();
        var result = Select(db, """
            SELECT tenant,
                   rank() OVER (PARTITION BY tenant) AS rank_value,
                   dense_rank() OVER (PARTITION BY tenant) AS dense_value,
                   ntile(2) OVER (PARTITION BY tenant) AS tile_value
            FROM scores ORDER BY id
            """);

        Assert.All(result.Rows, row =>
        {
            Assert.Equal(1L, Assert.IsType<long>(row[1]));
            Assert.Equal(1L, Assert.IsType<long>(row[2]));
        });
        Assert.Equal([1L, 1L, 2L], result.Rows.Where(row => (long)row[0]! == 1)
            .Select(row => Assert.IsType<long>(row[3])).Order().ToArray());
        Assert.Equal(1L, Assert.IsType<long>(Assert.Single(result.Rows, row => (long)row[0]! == 2)[3]));
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT row_number() OVER (PARTITION BY tenant) FROM scores"));
    }

    /// <summary>空表或筛选为空仍保留三列非空 Int64 元数据。</summary>
    /// <param name="withRows">是否先插入被过滤掉的数据。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_EmptyRankingResult_PreservesInt64Metadata(bool withRows)
    {
        using var db = OpenDatabase(withRows);
        var result = Select(db, """
            SELECT RaNk() OVER (ORDER BY score) AS r,
                   DENSE_RANK() OVER (ORDER BY score) AS d,
                   NTILE(2) OVER (ORDER BY id) AS t
            FROM scores WHERE id = 404
            """);

        Assert.Empty(result.Rows);
        Assert.Equal(["r", "d", "t"], result.Columns);
        Assert.All(Assert.IsType<SelectColumnInfo[]>(result.ColumnInfo), column =>
        {
            Assert.Equal(TableColumnType.Int64, column.DataType);
            Assert.False(column.IsNullable);
        });
    }

    /// <summary>窗口在筛选之后、别名排序和分页之前计算。</summary>
    [Fact]
    public void Execute_FilterThenRankAliasPagination_UsesEntireFilteredPartition()
    {
        using var db = OpenDatabase();
        var result = Select(db, """
            SELECT id, rank() OVER (ORDER BY score) AS r,
                   dense_rank() OVER (ORDER BY score) AS d,
                   ntile(2) OVER (ORDER BY score, id) AS t
            FROM scores WHERE tenant = 1 ORDER BY r DESC, id DESC LIMIT 2 OFFSET 1
            """);

        Assert.Equal(new object?[] { 2L, 1L, 1L, 1L }, result.Rows[0]);
        Assert.Equal(new object?[] { 1L, 1L, 1L, 1L }, result.Rows[1]);
    }

    /// <summary>新排名沿用直接投影与可解析窗口键的范围。</summary>
    [Fact]
    public void Execute_UnsupportedWindowShapes_KeepExistingBoundaries()
    {
        using var db = OpenDatabase(withRows: false);
        Assert.Throws<NotSupportedException>(() =>
            Select(db, "SELECT rank() OVER (ORDER BY id) + 1 FROM scores"));
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT dense_rank() OVER (PARTITION BY missing ORDER BY id) FROM scores"));
        Assert.Throws<InvalidOperationException>(() =>
            Select(db, "SELECT ntile(2) OVER (ORDER BY missing) FROM scores"));
    }

    /// <summary>嵌入式 ADO 在读取前暴露 Int64 类型，并可按整数投影读取。</summary>
    [Fact]
    public void EmbeddedAdo_RankingParametersAndEmptySchema_PreserveInt64AndInt32Reads()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE scores (id INT, score INT, PRIMARY KEY (id))";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO scores (id, score) VALUES (1, 10), (2, 10), (3, 20)";
        Assert.Equal(3, command.ExecuteNonQuery());
        command.CommandText = """
            SELECT rank() OVER (ORDER BY score) AS r,
                   dense_rank() OVER (ORDER BY score) AS d,
                   ntile(@buckets) OVER (ORDER BY score, id) AS t
            FROM scores WHERE id >= @minimum ORDER BY id
            """;
        command.Parameters.AddWithValue("@buckets", 2);
        command.Parameters.AddWithValue("@minimum", 1);
        using (var reader = command.ExecuteReader())
        {
            AssertRankingSchema(reader);
            long[][] expectedRows = [[1L, 1L, 1L], [1L, 1L, 1L], [3L, 2L, 2L]];
            foreach (long[] expected in expectedRows)
            {
                Assert.True(reader.Read());
                for (int column = 0; column < expected.Length; column++)
                {
                    Assert.Equal(expected[column], reader.GetInt64(column));
                    Assert.Equal((int)expected[column], reader.GetInt32(column));
                }
            }
            Assert.False(reader.Read());
        }

        command.Parameters["@minimum"].Value = 404;
        using var empty = command.ExecuteReader();
        AssertRankingSchema(empty);
        Assert.False(empty.Read());
    }

    private static void AssertRankingSchema(DbDataReader reader)
    {
        var schema = Assert.IsType<DataTable>(reader.GetSchemaTable());
        for (int column = 0; column < 3; column++)
        {
            Assert.Equal(typeof(long), reader.GetFieldType(column));
            Assert.Equal(typeof(long), schema.Rows[column][SchemaTableColumn.DataType]);
            Assert.Equal((int)DbType.Int64, schema.Rows[column][SchemaTableColumn.ProviderType]);
            Assert.False((bool)schema.Rows[column][SchemaTableColumn.AllowDBNull]);
        }
    }

    private Tsdb OpenDatabase(bool withRows = true)
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE scores (id INT, tenant INT, category INT, score INT, tie INT, PRIMARY KEY (id))
            """);
        if (withRows)
            SqlExecutor.Execute(db, """
                INSERT INTO scores (id, tenant, category, score, tie) VALUES
                (1, 1, 1, 10, 1), (2, 1, 1, 10, 1),
                (3, 1, 1, 20, 1), (4, 2, 1, 5, 1)
                """);
        return db;
    }

    private static SelectExecutionResult Select(Tsdb db, string sql, SqlParameters? parameters = null)
        => Assert.IsType<SelectExecutionResult>(parameters is null
            ? SqlExecutor.Execute(db, sql)
            : SqlExecutor.Execute(db, databaseName: null, sql, parameters, controlPlane: null));
}
