using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// 元数据查询语句单元测试：<c>SHOW MEASUREMENTS</c> / <c>SHOW TABLES</c> / <c>DESCRIBE [MEASUREMENT|TABLE] &lt;name&gt;</c>。
/// </summary>
public class SqlExecutorMetadataTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorMetadataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-meta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ShowMeasurements_OnEmptyDb_ReturnsZeroRows()
    {
        using var db = Tsdb.Open(Options());

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SHOW MEASUREMENTS"));

        Assert.Equal(new[] { "name" }, result.Columns);
        Assert.Empty(result.Rows);
    }

    [Fact]
    public void ShowMeasurements_ReturnsAllNames_OrderedAscending()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT zeta (host TAG, v FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE MEASUREMENT alpha (host TAG, v FIELD INT)");
        SqlExecutor.Execute(db, "CREATE MEASUREMENT mid (host TAG, v FIELD BOOL)");

        var result = (SelectExecutionResult)SqlExecutor.Execute(db, "SHOW MEASUREMENTS")!;

        var names = result.Rows.Select(r => (string)r[0]!).ToArray();
        Assert.Equal(new[] { "alpha", "mid", "zeta" }, names);
    }

    [Fact]
    public void ShowTables_ReturnsRelationTablesOnly()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))");

        var measurements = (SelectExecutionResult)SqlExecutor.Execute(db, "SHOW MEASUREMENTS")!;
        var tables = (SelectExecutionResult)SqlExecutor.Execute(db, "SHOW TABLES")!;

        Assert.Equal(["cpu"], measurements.Rows.Select(r => (string)r[0]!).ToArray());
        Assert.Equal(["devices"], tables.Rows.Select(r => (string)r[0]!).ToArray());
    }

    [Fact]
    public void DescribeMeasurement_ReturnsColumnSchema_InDeclaredOrder()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, region TAG, usage FIELD FLOAT, count FIELD INT, ok FIELD BOOL)");

        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "DESCRIBE MEASUREMENT cpu"));

        Assert.Equal(new[] { "column_name", "column_type", "data_type" }, result.Columns);
        Assert.Equal(5, result.Rows.Count);

        Assert.Equal(new object?[] { "host", "tag", "string" }, result.Rows[0]);
        Assert.Equal(new object?[] { "region", "tag", "string" }, result.Rows[1]);
        Assert.Equal(new object?[] { "usage", "field", "float64" }, result.Rows[2]);
        Assert.Equal(new object?[] { "count", "field", "int64" }, result.Rows[3]);
        Assert.Equal(new object?[] { "ok", "field", "boolean" }, result.Rows[4]);
    }

    [Fact]
    public void Describe_WithoutMeasurementKeyword_AlsoWorks()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT t1 (h TAG, v FIELD FLOAT)");

        var result = (SelectExecutionResult)SqlExecutor.Execute(db, "DESCRIBE t1")!;

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("h", result.Rows[0][0]);
        Assert.Equal("v", result.Rows[1][0]);
    }

    [Fact]
    public void Desc_AsAlias_ProducesSameOutput_AsDescribe()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT t (h TAG, v FIELD INT)");

        var a = (SelectExecutionResult)SqlExecutor.Execute(db, "DESCRIBE t")!;
        var b = (SelectExecutionResult)SqlExecutor.Execute(db, "DESC t")!;

        Assert.Equal(a.Columns, b.Columns);
        Assert.Equal(a.Rows.Count, b.Rows.Count);
        for (var i = 0; i < a.Rows.Count; i++)
            Assert.Equal(a.Rows[i], b.Rows[i]);
    }

    [Fact]
    public void DescribeMeasurement_OnUnknownName_Throws()
    {
        using var db = Tsdb.Open(Options());

        var ex = Assert.Throws<InvalidOperationException>(
            () => SqlExecutor.Execute(db, "DESCRIBE MEASUREMENT nope"));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void DropMeasurement_RemovesSchemaAndData_AndAllowsCleanRecreate()
    {
        using var db = Tsdb.Open(Options() with
        {
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new SonnetDB.Engine.Compaction.CompactionPolicy { Enabled = false },
        });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (1000, 'h1', 1.5)");
        db.FlushNow();

        var result = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP MEASUREMENT cpu"));

        Assert.Equal("drop_measurement", result.Operation);
        Assert.Equal(1, result.RowsAffected);
        Assert.Empty(((SelectExecutionResult)SqlExecutor.Execute(db, "SHOW MEASUREMENTS")!).Rows);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "DESCRIBE MEASUREMENT cpu"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT usage FROM cpu"));

        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "INSERT INTO cpu (time, host, usage) VALUES (2000, 'h1', 2.5)");

        var rows = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT time, usage FROM cpu"));
        Assert.Equal(new object?[] { 2000L, 2.5 }, rows.Rows.Single());
    }

    [Fact]
    public void DropMeasurement_IfExists_OnUnknownName_ReturnsZeroRows()
    {
        using var db = Tsdb.Open(Options());

        var result = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP MEASUREMENT IF EXISTS missing"));

        Assert.Equal("missing", result.Target);
        Assert.Equal(0, result.RowsAffected);
        Assert.Equal("drop_measurement", result.Operation);
    }

    [Fact]
    public void DropMeasurement_WithoutIfExists_OnUnknownName_Throws()
    {
        using var db = Tsdb.Open(Options());

        var ex = Assert.Throws<InvalidOperationException>(
            () => SqlExecutor.Execute(db, "DROP MEASUREMENT missing"));

        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public void InformationSchema_ReturnsTablesColumnsAndIndexes()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, tenant STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");

        var tables = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT table_name, table_type
            FROM information_schema.tables
            WHERE table_name = 'devices'
            """));
        Assert.Equal(new[] { "table_name", "table_type" }, tables.Columns);
        Assert.Equal(new object?[] { "devices", "BASE TABLE" }, tables.Rows.Single());

        var columns = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT column_name, data_type, is_primary_key
            FROM information_schema.columns
            WHERE table_name = 'devices'
            ORDER BY ordinal_position
            """));
        Assert.Equal(
            ["id", "name", "tenant"],
            columns.Rows.Select(static r => (string)r[0]!).ToArray());
        Assert.Equal(new object?[] { "id", "int64", true }, columns.Rows[0]);

        var indexes = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT index_name, column_name, is_unique
            FROM information_schema.indexes
            WHERE table_name = 'devices'
            """));
        Assert.Equal(new object?[] { "idx_devices_tenant", "tenant", false }, indexes.Rows.Single());
    }

    [Fact]
    public void ParseShow_Measurements_ProducesShowMeasurementsAst()
    {
        var stmt = SqlParser.Parse("SHOW MEASUREMENTS");
        Assert.IsType<ShowMeasurementsStatement>(stmt);
    }

    [Fact]
    public void ParseShow_Tables_ProducesShowTablesAst()
    {
        var stmt = SqlParser.Parse("SHOW TABLES");
        Assert.IsType<ShowTablesStatement>(stmt);
    }

    [Fact]
    public void ParseDescribe_WithKeywordMeasurement_CapturesName()
    {
        var stmt = Assert.IsType<DescribeMeasurementStatement>(
            SqlParser.Parse("DESCRIBE MEASUREMENT cpu"));
        Assert.Equal("cpu", stmt.Name);
    }

    [Fact]
    public void ParseDesc_WithoutKeywordMeasurement_CapturesName()
    {
        var stmt = Assert.IsType<DescribeMeasurementStatement>(
            SqlParser.Parse("DESC mem"));
        Assert.Equal("mem", stmt.Name);
    }

    [Fact]
    public void ParseDropMeasurement_WithIfExists_CapturesName()
    {
        var stmt = Assert.IsType<DropMeasurementStatement>(
            SqlParser.Parse("DROP MEASUREMENT IF EXISTS cpu"));

        Assert.Equal("cpu", stmt.Name);
        Assert.True(stmt.IfExists);
    }
}
