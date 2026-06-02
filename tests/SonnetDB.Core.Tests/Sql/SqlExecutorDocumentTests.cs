using SonnetDB.Engine;
using SonnetDB.Documents;
using SonnetDB.FullText;
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
            Assert.Equal(
                new[] { "collection_name", "document_count", "index_count", "indexes", "fulltext_index_count", "fulltext_indexes", "created_utc" },
                describe.Columns);
            Assert.Equal("device_docs", describe.Rows.Single()[0]);
            Assert.Equal(1L, describe.Rows.Single()[2]);
            Assert.Equal("idx_device_type:$.type", describe.Rows.Single()[3]);
            Assert.Equal(0L, describe.Rows.Single()[4]);
            Assert.Equal(string.Empty, describe.Rows.Single()[5]);

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
    public void DocumentCollection_FullTextIndex_SearchesScoresAndPersistsAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
            SqlExecutor.Execute(db, """
                INSERT INTO logs (id, document)
                VALUES ('log-1', '{"message":"Pump alarm in north station","level":"warn"}'),
                       ('log-2', '{"message":"Fan alarm cleared","level":"info"}'),
                       ('log-3', '{"message":"Pump pressure normal","level":"info"}')
                """);
            SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode");
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var indexes = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SHOW FULLTEXT INDEXES ON logs"));
            Assert.Equal(new[] { "index_name", "fields", "tokenizer", "document_count", "created_utc" }, indexes.Columns);
            Assert.Equal("ft_logs_message", indexes.Rows.Single()[0]);
            Assert.Equal("$.message", indexes.Rows.Single()[1]);
            Assert.Equal("unicode", indexes.Rows.Single()[2]);
            Assert.Equal(3L, indexes.Rows.Single()[3]);

            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, """
                SELECT id, bm25_score() AS score
                FROM logs
                WHERE match(ft_logs_message, '$.message', 'pump alarm', 5)
                ORDER BY score DESC
                """));

            Assert.Equal(new[] { "id", "score" }, result.Columns);
            Assert.Equal(["log-1"], result.Rows.Select(static row => (string)row[0]!).ToArray());
            Assert.True(Convert.ToDouble(result.Rows.Single()[1]) > 0);

            var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, """
                EXPLAIN SELECT id FROM logs WHERE match(ft_logs_message, '$.message', 'pump alarm', 5)
                """));
            var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
            Assert.Equal("select_document_collection", values["statement_type"]);
            Assert.Equal("fulltext_index", values["access_path"]);
            Assert.Equal("ft_logs_message", values["index_name"]);
            Assert.Equal(1L, Convert.ToInt64(values["estimated_scanned_rows"]));
        }
    }

    [Fact]
    public void DocumentCollection_FullTextIndex_TracksUpdateAndDelete()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
        SqlExecutor.Execute(db, """
            INSERT INTO logs (id, document)
            VALUES ('log-1', '{"message":"Pump alarm in north station"}'),
                   ('log-2', '{"message":"Pump alarm in east station"}')
            """);
        SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode");

        SqlExecutor.Execute(db, """
            UPDATE logs
            SET document = '{"message":"Fan normal in north station"}'
            WHERE id = 'log-1'
            """);
        SqlExecutor.Execute(db, "DELETE FROM logs WHERE id = 'log-2'");

        var pump = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM logs WHERE match(ft_logs_message, '$.message', 'pump alarm', 10)
            """));
        Assert.Empty(pump.Rows);

        var fan = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM logs WHERE match(ft_logs_message, '$.message', 'fan normal', 10)
            """));
        Assert.Equal(["log-1"], fan.Rows.Select(static row => (string)row[0]!).ToArray());

        var indexes = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SHOW FULLTEXT INDEXES ON logs"));
        Assert.Equal(1L, indexes.Rows.Single()[3]);
    }

    [Fact]
    public void DocumentCollection_FullTextIndex_CanSearchAcrossAllFields()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
        SqlExecutor.Execute(db, """
            INSERT INTO logs (id, document)
            VALUES ('log-1', '{"title":"Pump incident","message":"Station north alarm"}'),
                   ('log-2', '{"title":"Fan incident","message":"Station south alarm"}')
            """);
        SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_logs ON logs ('$.title', '$.message') USING unicode");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id FROM logs WHERE match(ft_logs, *, 'pump', 10)
            """));

        Assert.Equal(["log-1"], result.Rows.Select(static row => (string)row[0]!).ToArray());
    }

    [Fact]
    public void DocumentCollection_FullTextIndex_RebuildsWhenDerivedDirectoryIsMissing()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
            SqlExecutor.Execute(db, """
                INSERT INTO logs (id, document)
                VALUES ('log-1', '{"message":"Pump alarm in north station"}')
                """);
            SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode");
        }

        string fullTextRoot = Path.Combine(_root, "documents", "fulltext");
        Assert.True(Directory.Exists(fullTextRoot));
        Directory.Delete(fullTextRoot, recursive: true);

        using (var reopened = Tsdb.Open(Options()))
        {
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened, """
                SELECT id FROM logs WHERE match(ft_logs_message, '$.message', 'pump alarm', 10)
                """));

            Assert.Equal(["log-1"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        }
    }

    [Fact]
    public void DocumentCollection_FullTextIndex_RebuildDropsStaleDerivedDocuments()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
            SqlExecutor.Execute(db, """
                INSERT INTO logs (id, document)
                VALUES ('log-1', '{"message":"Pump alarm in north station"}')
                """);
            SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode");
        }

        string fullTextIndexDirectory = Path.Combine(
            _root,
            "documents",
            "fulltext",
            EncodeName("logs"),
            EncodeName("ft_logs_message"));
        var derivedIndex = DocumentFullTextIndexStore.Open(
            fullTextIndexDirectory,
            new DocumentFullTextIndex("ft_logs_message", ["$.message"], "unicode", DateTime.UtcNow.Ticks));
        derivedIndex.Upsert(new DocumentRow("stale", """{"message":"Ghost alarm"}""", Version: 0));

        using var reopened = Tsdb.Open(Options());
        var before = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "SHOW FULLTEXT INDEXES ON logs"));
        Assert.Equal(2L, before.Rows.Single()[3]);

        int documentCount = reopened.Documents.RebuildFullTextIndex("logs", "ft_logs_message");
        Assert.Equal(1, documentCount);

        var after = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "SHOW FULLTEXT INDEXES ON logs"));
        Assert.Equal(1L, after.Rows.Single()[3]);
    }

    [Fact]
    public void DocumentCollection_HybridSearch_FusesFullTextAndVectorScores()
    {
        using var db = Tsdb.Open(Options());
        CreateHybridSearchFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, bm25_score() AS text_score, vector_distance() AS distance, hybrid_score() AS score
            FROM hybrid_search(
                source => logs,
                text_index => ft_logs_message,
                text_field => '$.message',
                text => 'pump alarm',
                vector_field => '$.embedding',
                vector => [1, 0, 0],
                k => 3,
                text_weight => 0.6,
                vector_weight => 0.4)
            ORDER BY score DESC
            """));

        Assert.Equal(new[] { "id", "text_score", "distance", "score" }, result.Columns);
        Assert.Equal(["log-1", "log-2", "log-3"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.True(Convert.ToDouble(result.Rows[0][3]) > Convert.ToDouble(result.Rows[1][3]));
        Assert.True(Convert.ToDouble(result.Rows[1][3]) > Convert.ToDouble(result.Rows[2][3]));
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0][2]), 6);
    }

    [Fact]
    public void DocumentCollection_HybridSearch_AppliesJsonPathFilters()
    {
        using var db = Tsdb.Open(Options());
        CreateHybridSearchFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, site, hybrid_score() AS score
            FROM hybrid_search(source => logs, text => 'pump alarm', vector => [1, 0, 0], k => 10)
            WHERE site = 'south'
            ORDER BY score DESC
            """));

        Assert.Equal(new[] { "id", "site", "score" }, result.Columns);
        Assert.Equal(["log-2", "log-4"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.All(result.Rows, row => Assert.Equal("south", row[1]));
    }

    [Fact]
    public void DocumentCollection_HybridSearch_ExplainShowsHybridAccessPath()
    {
        using var db = Tsdb.Open(Options());
        CreateHybridSearchFixture(db);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM hybrid_search(source => logs, text => 'pump alarm', vector => [1, 0, 0], k => 2)
            """));

        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("hybrid_search", values["statement_type"]);
        Assert.Equal("hybrid_search", values["access_path"]);
        Assert.Equal("ft_logs_message", values["index_name"]);
        Assert.True(Convert.ToInt64(values["estimated_scanned_rows"]) >= 2L);
    }

    private static void CreateHybridSearchFixture(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
        SqlExecutor.Execute(db, """
            INSERT INTO logs (id, document)
            VALUES ('log-1', '{"message":"Pump alarm overheating","site":"north","embedding":[1,0,0]}'),
                   ('log-2', '{"message":"Pump alarm pressure","site":"south","embedding":[0.7,0.7,0]}'),
                   ('log-3', '{"message":"Pump maintenance normal","site":"north","embedding":[0.95,0.05,0]}'),
                   ('log-4', '{"message":"Fan alarm cleared","site":"south","embedding":[0,1,0]}')
        """);
        SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode");
    }

    private static string EncodeName(string name)
        => Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)).ToLowerInvariant();

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

    [Fact]
    public void JsonEach_ReadsJsonArrayFile_AsVirtualTable()
    {
        string path = Path.Combine(_root, "devices.json");
        File.WriteAllText(path, """
            [
              {"id":"dev-1","site":"north","temp":21.5},
              {"id":"dev-2","site":"south","temp":18}
            ]
            """);

        using var db = Tsdb.Open(Options());
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, $"""
            SELECT id, json_value(document, '$.temp') AS temp
            FROM json_each('{EscapeSql(path)}')
            WHERE json_value(document, '$.site') = 'north'
            """));

        Assert.Equal(new[] { "id", "temp" }, result.Columns);
        Assert.Equal(new object?[] { "dev-1", 21.5 }, result.Rows.Single());

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, $"""
            EXPLAIN SELECT id FROM json_each('{EscapeSql(path)}')
            """));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("json_file_virtual_table", values["statement_type"]);
        Assert.Equal("json_file_virtual_table", values["access_path"]);
        Assert.Equal(2L, Convert.ToInt64(values["estimated_scanned_rows"]));
    }

    [Fact]
    public void ImportJson_IntoDocumentCollection_UsesIdPathAndNormalizesDocuments()
    {
        string path = Path.Combine(_root, "logs.ndjson");
        File.WriteAllText(path, """
            {"device":{"id":"dev-1"},"site":"north"}
            {"device":{"id":"dev-2"},"site":"south"}
            """);

        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");

        var imported = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, $"""
            IMPORT JSON '{EscapeSql(path)}' INTO device_docs FORMAT LINES ID PATH '$.device.id'
            """));
        Assert.Equal(2, imported.RowsInserted);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, json_value(document, '$.site') AS site
            FROM device_docs
            ORDER BY id
            """));

        Assert.Equal(["dev-1", "dev-2"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal(["north", "south"], result.Rows.Select(static row => (string)row[1]!).ToArray());
    }

    [Fact]
    public void ImportJson_IntoTable_MapsObjectPropertiesToColumns()
    {
        string path = Path.Combine(_root, "table-devices.json");
        File.WriteAllText(path, """
            [
              {"id":1,"name":"pump","metadata":{"site":"north"}},
              {"id":2,"name":"fan","metadata":{"site":"south"}}
            ]
            """);

        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, name STRING, metadata JSON, PRIMARY KEY (id))");

        var imported = Assert.IsType<InsertExecutionResult>(SqlExecutor.Execute(db, $"""
            IMPORT JSON '{EscapeSql(path)}' INTO devices FORMAT ARRAY
            """));
        Assert.Equal(2, imported.RowsInserted);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, json_value(metadata, '$.site') AS site
            FROM devices
            ORDER BY id
            """));

        Assert.Equal(new object?[] { 1L, "north" }, result.Rows[0]);
        Assert.Equal(new object?[] { 2L, "south" }, result.Rows[1]);
    }

    private static string EscapeSql(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);
}
