using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlExecutorDocumentTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorDocumentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-document-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    [Fact]
    public void ParseCreateDocumentCollection_ReturnsAst()
    {
        var stmt = Assert.IsType<CreateDocumentCollectionStatement>(SqlParser.Parse(
            "CREATE DOCUMENT COLLECTION IF NOT EXISTS device_docs"));

        Assert.Equal("device_docs", stmt.Name);
        Assert.True(stmt.IfNotExists);
    }

    [Fact]
    public void DocumentCollection_CreateShowDescribe_PersistsAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
            SqlExecutor.Execute(db, "CREATE JSON INDEX idx_device_type ON device_docs ('$.type')");
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var show = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SHOW DOCUMENT COLLECTIONS"));
            Assert.Equal(new[] { "name" }, show.Columns);
            Assert.Equal("device_docs", show.Rows.Single()[0]);

            var describe = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "DESCRIBE DOCUMENT COLLECTION device_docs"));
            Assert.Equal(new[] { "collection_name", "document_count", "index_count", "indexes", "created_utc" }, describe.Columns);
            Assert.Equal("device_docs", describe.Rows.Single()[0]);
            Assert.Equal(1L, describe.Rows.Single()[2]);
            Assert.Equal("idx_device_type:$.type", describe.Rows.Single()[3]);

            var indexes = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SHOW JSON INDEXES ON device_docs"));
            Assert.Equal(new[] { "index_name", "path", "created_utc" }, indexes.Columns);
            Assert.Equal("idx_device_type", indexes.Rows.Single()[0]);
            Assert.Equal("$.type", indexes.Rows.Single()[1]);
        }
    }

    [Fact]
    public void DocumentCollection_InsertSelectUpdateDelete_WorksEndToEnd()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");

        var inserted = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES
              ('dev-1', '{"type":"pump","site":"north","metrics":{"temp":21.5},"tags":["a","b"]}'),
              ('dev-2', '{"type":"fan","site":"south","metrics":{"temp":18}}')
            """));
        Assert.Equal(2, inserted.RowsInserted);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id,
                   json_value(document, '$.type') AS type,
                   json_value(document, '$.metrics.temp') AS temp,
                   json_value(document, '$.tags[1]') AS tag
            FROM device_docs
            WHERE json_value(document, '$.site') = 'north'
            """));

        Assert.Equal(new[] { "id", "type", "temp", "tag" }, selected.Columns);
        Assert.Equal(new object?[] { "dev-1", "pump", 21.5, "b" }, selected.Rows.Single());

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, """
            UPDATE device_docs
            SET document = '{"type":"pump","site":"north","metrics":{"temp":22}}'
            WHERE id = 'dev-1'
            """));
        Assert.Equal(1, updated.RowsAffected);

        var afterUpdate = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT json_value(document, '$.metrics.temp') AS temp FROM device_docs WHERE id = 'dev-1'"));
        Assert.Equal(22.0, Assert.IsType<double>(afterUpdate.Rows.Single()[0]));

        var deleted = Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db,
            "DELETE FROM device_docs WHERE json_value(document, '$.site') = 'south'"));
        Assert.Equal(1, deleted.TombstonesAdded);

        var all = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT id FROM device_docs ORDER BY id"));
        Assert.Equal(["dev-1"], all.Rows.Select(static r => (string)r[0]!).ToArray());
    }

    [Fact]
    public void DocumentCollection_SelectSupportsQualifiedDocumentPseudoColumn()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{ "type" : "pump", "site" : "north" }')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT d.id, d.document, json_value(d.document, '$.type') AS type
            FROM device_docs AS d
            WHERE json_value(d.document, '$.site') = 'north'
            """));

        Assert.Equal(new[] { "id", "document", "type" }, result.Columns);
        Assert.Equal("dev-1", result.Rows.Single()[0]);
        Assert.Equal("{\"type\":\"pump\",\"site\":\"north\"}", result.Rows.Single()[1]);
        Assert.Equal("pump", result.Rows.Single()[2]);
    }

    [Fact]
    public void DocumentCollection_JsonPathIndex_IsUsedByExplainAndQuery()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"type":"pump","site":"north"}'),
                   ('dev-2', '{"type":"fan","site":"south"}'),
                   ('dev-3', '{"type":"pump","site":"east"}')
            """);
        SqlExecutor.Execute(db, "CREATE JSON INDEX idx_device_type ON device_docs ('$.type')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, json_value(document, '$.site') AS site
            FROM device_docs
            WHERE json_value(document, '$.type') = 'pump'
            ORDER BY id
            """));
        Assert.Equal(["dev-1", "dev-3"], result.Rows.Select(static r => (string)r[0]!).ToArray());

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id FROM device_docs WHERE json_value(document, '$.type') = 'pump'
            """));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("json_path_index", values["access_path"]);
        Assert.Equal("idx_device_type", values["index_name"]);
    }

    [Fact]
    public void TableJsonColumn_JsonValue_UsesSamePathEvaluator()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, metadata JSON, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO devices (id, metadata)
            VALUES (1, '{"site":"north","metrics":{"temp":21.5}}'),
                   (2, '{"site":"south","metrics":{"temp":18}}')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, json_value(metadata, '$.metrics.temp') AS temp
            FROM devices
            WHERE json_value(metadata, '$.site') = 'north'
            """));

        Assert.Equal(new[] { "id", "temp" }, result.Columns);
        Assert.Equal(new object?[] { 1L, 21.5 }, result.Rows.Single());
    }
}
