using SonnetDB.Engine;
using SonnetDB.Data;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlCteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-cte-sql-" + Guid.NewGuid().ToString("N"));

    public SqlCteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 测试清理失败不应掩盖断言结果。
        }
    }

    [Fact]
    public void Parse_WithMultipleCtes_CapturesDefinitionsInOrder()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "WITH base AS (SELECT id FROM devices), filtered AS (SELECT id FROM base WHERE id > 1) SELECT id FROM filtered"));

        Assert.Equal(["base", "filtered"], statement.CommonTableExpressions.Select(cte => cte.Name));
        Assert.Equal("devices", statement.CommonTableExpressions[0].Query.Measurement);
        Assert.Equal("base", statement.CommonTableExpressions[1].Query.Measurement);
    }

    [Fact]
    public void Parse_CteColumnList_PreservesDeclaredOrder()
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "WITH renamed (device_id, area) AS (SELECT id, region FROM devices) SELECT area FROM renamed"));

        Assert.Equal(["device_id", "area"], statement.CommonTableExpressions[0].ColumnNames);
    }

    [Fact]
    public void Execute_SingleCteFromSource_ReturnsFilteredRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH filtered AS (
                SELECT id, region FROM devices WHERE region = 'west'
            )
            SELECT id FROM filtered ORDER BY id
            """));

        Assert.Equal(["id"], result.Columns);
        Assert.Equal([1L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_MultipleCtesAndInSubquery_UsesEarlierCte()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH west AS (
                SELECT id FROM devices WHERE region = 'west'
            ), selected AS (
                SELECT id FROM west WHERE id > 1
            )
            SELECT id FROM devices WHERE id IN (SELECT id FROM selected) ORDER BY id
            """));

        Assert.Equal([3L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_CteInCorrelatedExistsSubquery_ReturnsMatchingRows()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH west AS (
                SELECT id FROM devices WHERE region = 'west'
            )
            SELECT d.id FROM devices AS d
            WHERE EXISTS (SELECT 1 FROM west AS w WHERE w.id = d.id)
            ORDER BY d.id
            """));

        Assert.Equal([1L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[0])).ToArray());
    }

    [Fact]
    public void Execute_CteColumnList_RenamesResultsBeforeOuterOrderAndPagination()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH filtered (device_id, area) AS (
                SELECT id AS original_id, region FROM devices ORDER BY original_id DESC
            )
            SELECT device_id, area FROM filtered ORDER BY device_id DESC LIMIT 2 OFFSET 1
            """));

        Assert.Equal(["device_id", "area"], result.Columns);
        Assert.Equal([2L, 1L], result.Rows.Select(row => Assert.IsType<long>(row[0])));
    }

    [Fact]
    public void Execute_CteColumnListWithJoinAndAggregate_UsesRenamedColumns()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var joined = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH source (device_id, area) AS (SELECT id, region FROM devices),
                 west (device_id) AS (SELECT device_id FROM source WHERE area = 'west')
            SELECT d.id FROM devices AS d JOIN west AS w ON d.id = w.device_id ORDER BY d.id
            """));
        Assert.Equal([1L, 3L], joined.Rows.Select(row => Assert.IsType<long>(row[0])));

        var grouped = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH source (area) AS (SELECT region FROM devices)
            SELECT area, COUNT(*) AS total FROM source GROUP BY area ORDER BY area
            """));
        Assert.Equal(["area", "total"], grouped.Columns);
        Assert.Equal(["east", "west"], grouped.Rows.Select(row => Assert.IsType<string>(row[0])));
        Assert.Equal([1L, 2L], grouped.Rows.Select(row => Assert.IsType<long>(row[1])));
    }

    [Fact]
    public void Execute_CteColumnListInParameterizedInAndExists_PreservesBindings()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null, """
            WITH selected (device_id) AS (SELECT id FROM devices WHERE region = @region)
            SELECT d.id FROM devices AS d
            WHERE d.id IN (SELECT device_id FROM selected)
              AND EXISTS (SELECT 1 FROM selected AS s WHERE s.device_id = d.id)
            ORDER BY d.id
            """, new SqlParameters().AddNamed("region", "west")));

        Assert.Equal([1L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[0])));
    }

    [Fact]
    public void Execute_CteColumnListWithEmptyResult_RejectsWidthMismatch()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            WITH filtered (device_id, area) AS (SELECT id FROM devices WHERE id = 999)
            SELECT device_id FROM filtered
            """));

        Assert.Contains("CTE 输出列数不一致", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_CteColumnListWithDuplicateNames_RejectsAmbiguousColumns()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            WITH filtered (device_id, DEVICE_ID) AS (SELECT id, region FROM devices)
            SELECT device_id FROM filtered
            """));

        Assert.Contains("CTE 输出列名不能重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_CteColumnListWithMeasurementAndDocumentSources_UsesSourceResults()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE MEASUREMENT samples (host TAG, value FIELD FLOAT)");
        SqlExecutor.Execute(db, "INSERT INTO samples (time, host, value) VALUES (1000, 'pump', 1.5)");
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, "INSERT INTO device_docs (id, document) VALUES ('doc-1', '{\"kind\":\"pump\"}')");

        var measurements = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH sample_hosts (device) AS (SELECT host FROM samples)
            SELECT device FROM sample_hosts
            """));
        Assert.Equal("pump", Assert.Single(measurements.Rows)[0]);

        var documents = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH source_docs (document_id) AS (SELECT id FROM device_docs)
            SELECT document_id FROM source_docs
            """));
        Assert.Equal("doc-1", Assert.Single(documents.Rows)[0]);
    }

    [Fact]
    public void Execute_CteColumnListAfterUnion_RenamesAfterInternalOrdering()
    {
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        CreateDevices(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            WITH selected (device_id) AS (
                SELECT id FROM devices WHERE id = 1
                UNION ALL
                SELECT id FROM devices WHERE id = 3
                ORDER BY id DESC
            )
            SELECT device_id FROM selected ORDER BY device_id
            """));

        Assert.Equal([1L, 3L], result.Rows.Select(row => Assert.IsType<long>(row[0])));
    }

    [Fact]
    public void EmbeddedAdo_CteColumnList_PreservesColumnNameAndType()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE devices (id INT, region STRING, PRIMARY KEY (id))";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO devices (id, region) VALUES (1, 'west')";
        command.ExecuteNonQuery();
        command.CommandText = "WITH selected (device_id) AS (SELECT id FROM devices) SELECT device_id FROM selected";

        using var reader = command.ExecuteReader();
        Assert.Equal("device_id", reader.GetName(0));
        Assert.Equal(typeof(long), reader.GetFieldType(0));
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
    }

    private static void CreateDevices(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, region STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, region) VALUES (1, 'west'), (2, 'east'), (3, 'west')");
    }
}
