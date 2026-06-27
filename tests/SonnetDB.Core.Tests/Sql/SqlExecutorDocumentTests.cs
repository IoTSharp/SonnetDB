using SonnetDB.Documents;
using SonnetDB.Engine;
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
                new[] { "collection_name", "document_count", "index_count", "indexes", "fulltext_index_count", "fulltext_indexes", "validator_enabled", "validation_action", "validator_rules", "created_utc" },
                describe.Columns);
            Assert.Equal("device_docs", describe.Rows.Single()[0]);
            Assert.Equal(1L, describe.Rows.Single()[2]);
            Assert.Equal("idx_device_type:$.type", describe.Rows.Single()[3]);
            Assert.Equal(0L, describe.Rows.Single()[4]);
            Assert.Equal(string.Empty, describe.Rows.Single()[5]);
            Assert.Equal(false, describe.Rows.Single()[6]);

            var indexes = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SHOW JSON INDEXES ON device_docs"));
            Assert.Equal(
                new[] { "index_name", "paths", "is_unique", "is_sparse", "is_partial", "partial_filter", "is_ttl", "ttl_seconds", "created_utc" },
                indexes.Columns);
            Assert.Equal("idx_device_type", indexes.Rows.Single()[0]);
            Assert.Equal("$.type", indexes.Rows.Single()[1]);
        }
    }

    [Fact]
    public void DocumentCollection_ValidatorSql_ErrorRejectsAndPersists()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
            var set = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(db, """
                ALTER DOCUMENT COLLECTION device_docs
                SET VALIDATOR '{"rules":[{"path":"$.site","required":true,"type":"string","enum":["north","south"]},{"path":"$.score","type":"number","minimum":0,"maximum":100},{"path":"$.code","type":"string","pattern":"^[A-Z]{2}-[0-9]+$"}]}'
                VALIDATION ACTION ERROR
                """));
            Assert.Equal(1, set.RowsAffected);

            SqlExecutor.Execute(db, """
                INSERT INTO device_docs (id, document)
                VALUES ('ok', '{"site":"north","score":99,"code":"AB-1"}')
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
                INSERT INTO device_docs (id, document)
                VALUES ('bad', '{"site":"west","score":101,"code":"oops"}')
                """));
            Assert.Contains("$.site", ex.Message, StringComparison.Ordinal);
            Assert.Contains("$.score", ex.Message, StringComparison.Ordinal);
            Assert.Contains("$.code", ex.Message, StringComparison.Ordinal);
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var describe = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "DESCRIBE DOCUMENT COLLECTION device_docs"));
            Assert.Equal(true, describe.Rows.Single()[6]);
            Assert.Equal("error", describe.Rows.Single()[7]);
            Assert.Contains("$.site", (string)describe.Rows.Single()[8]!);

            var ex = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(reopened, """
                UPDATE device_docs
                SET document = '{"score":5,"code":"AB-2"}'
                WHERE id = 'ok'
                """));
            Assert.Contains("$.site", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DocumentCollection_ValidatorSql_WarnAllowsAndReportsWarning()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            ALTER DOCUMENT COLLECTION device_docs
            SET VALIDATOR '{"rules":[{"path":"$.site","required":true,"type":"string"}]}'
            VALIDATION ACTION WARN
            """);

        var store = db.Documents.Open("device_docs");
        var result = store.Insert("bad", """{"score":1}""");

        Assert.False(result.HasErrors);
        Assert.True(result.HasWarnings);
        Assert.Equal(DocumentWriteErrorCodes.ValidationFailed, Assert.Single(result.Errors).Code);
        Assert.Equal(DocumentWriteErrorSeverity.Warning, Assert.Single(result.Errors).Severity);
        Assert.NotNull(store.Get("bad"));

        var drop = Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "ALTER DOCUMENT COLLECTION device_docs DROP VALIDATOR"));
        Assert.Equal(1, drop.RowsAffected);
        Assert.Null(db.Documents.Catalog.TryGet("device_docs")!.Validator);
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
    public void DocumentCollection_InsertDuplicateId_DoesNotOverwriteExistingDocument()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"north"}')
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"south"}')
            """));

        Assert.Contains("dev-1", ex.Message, StringComparison.Ordinal);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT document FROM device_docs WHERE id = 'dev-1'"));
        Assert.Equal("""{"site":"north"}""", selected.Rows.Single()[0]);
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
        Assert.Equal("document_index", values["access_path"]);
        Assert.Equal("idx_device_type", values["index_name"]);
    }

    [Fact]
    public void DocumentCollection_AdvancedIndexes_SupportCompositeUniqueSparsePartialAndTtl()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
            SqlExecutor.Execute(db, """
                INSERT INTO device_docs (id, document)
                VALUES ('dev-1', '{"tenant":"t1","site":"north","serial":"s1","active":true,"expiresAt":"2000-01-01T00:00:00Z"}'),
                       ('dev-2', '{"tenant":"t1","site":"south","active":false,"expiresAt":"2999-01-01T00:00:00Z"}'),
                       ('dev-3', '{"tenant":"t2","site":"north","serial":"s3","active":true,"expiresAt":"2999-01-01T00:00:00Z"}')
                """);

            SqlExecutor.Execute(db, "CREATE TTL INDEX idx_docs_expires ON device_docs ('$.expiresAt') WITH ttl_seconds = 1");
            SqlExecutor.Execute(db, "CREATE INDEX idx_docs_tenant_site ON device_docs ('$.tenant', '$.site')");
            SqlExecutor.Execute(db, "CREATE UNIQUE SPARSE INDEX ux_docs_serial ON device_docs ('$.serial')");
            SqlExecutor.Execute(db, "CREATE SPARSE INDEX idx_docs_active_site ON device_docs ('$.site') WHERE json_value(document, '$.active') = true");

            var duplicate = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
                INSERT INTO device_docs (id, document)
                VALUES ('dev-dup', '{"tenant":"t3","site":"west","serial":"s3","expiresAt":"2999-01-01T00:00:00Z"}')
                """));
            Assert.Contains("唯一索引 'ux_docs_serial' 冲突", duplicate.Message, StringComparison.Ordinal);

            var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
                EXPLAIN SELECT id
                FROM device_docs
                WHERE json_value(document, '$.tenant') = 't1'
                  AND json_value(document, '$.site') = 'south'
                """));
            var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
            Assert.Equal("document_index", values["access_path"]);
            Assert.Equal("idx_docs_tenant_site", values["index_name"]);

            var show = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW JSON INDEXES ON device_docs"));
            var rows = show.Rows.ToDictionary(static r => (string)r[0]!, StringComparer.Ordinal);
            Assert.Equal("$.tenant,$.site", rows["idx_docs_tenant_site"][1]);
            Assert.True((bool)rows["ux_docs_serial"][2]!);
            Assert.True((bool)rows["ux_docs_serial"][3]!);
            Assert.True((bool)rows["idx_docs_active_site"][4]!);
            Assert.Equal("$.active = true", rows["idx_docs_active_site"][5]);
            Assert.True((bool)rows["idx_docs_expires"][6]!);
            Assert.Equal(1L, rows["idx_docs_expires"][7]);

            var alive = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
                "SELECT id FROM device_docs ORDER BY id"));
            Assert.Equal(["dev-2", "dev-3"], alive.Rows.Select(static row => (string)row[0]!).ToArray());
        }

        using (var reopened = Tsdb.Open(Options()))
        {
            var indexes = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(reopened, "SHOW JSON INDEXES ON device_docs"));
            Assert.Equal(4, indexes.Rows.Count);
            Assert.Contains(indexes.Rows, static row => string.Equals((string)row[0]!, "idx_docs_tenant_site", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void DocumentCollection_PartialIndex_IsUsedOnlyWhenFilterImpliesPredicate()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"north","active":true}'),
                   ('dev-2', '{"site":"north","active":false}'),
                   ('dev-3', '{"site":"south","active":true}')
            """);
        SqlExecutor.Execute(db, """
            CREATE SPARSE INDEX idx_docs_active_site ON device_docs ('$.site')
            WHERE json_value(document, '$.active') = true
            """);

        var unconstrained = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = 'north'
            ORDER BY id
            """));
        Assert.Equal(["dev-1", "dev-2"], unconstrained.Rows.Select(static row => (string)row[0]!).ToArray());

        var unconstrainedExplain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = 'north'
            """));
        var unconstrainedValues = unconstrainedExplain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_scan", unconstrainedValues["access_path"]);

        var constrained = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = 'north'
              AND json_value(document, '$.active') = true
            """));
        Assert.Equal(["dev-1"], constrained.Rows.Select(static row => (string)row[0]!).ToArray());

        var constrainedExplain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = 'north'
              AND json_value(document, '$.active') = true
            """));
        var constrainedValues = constrainedExplain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_index", constrainedValues["access_path"]);
        Assert.Equal("idx_docs_active_site", constrainedValues["index_name"]);
    }

    [Fact]
    public void DocumentCollection_ExplainDocumentPlanner_ShowsCostPushdownSortAndPrefixIndex()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"tenant":"t1","site":"north","score":7}'),
                   ('dev-2', '{"tenant":"t1","site":"south","score":3}'),
                   ('dev-3', '{"tenant":"t2","site":"north","score":9}')
            """);
        SqlExecutor.Execute(db, "CREATE INDEX idx_docs_tenant_site ON device_docs ('$.tenant', '$.site')");

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id, json_value(document, '$.tenant') AS tenant
            FROM device_docs
            WHERE json_value(document, '$.tenant') = 't1'
            ORDER BY id
            """));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);

        Assert.Equal("document_index_prefix", values["access_path"]);
        Assert.Equal("idx_docs_tenant_site", values["index_name"]);
        Assert.Equal(2L, Convert.ToInt64(values["estimated_candidate_rows"]));
        Assert.Equal(2L, Convert.ToInt64(values["estimated_output_rows"]));
        Assert.True((bool)values["filter_pushdown"]!);
        Assert.Equal("$.tenant", values["filter_pushdown_fields"]);
        Assert.Equal(string.Empty, values["residual_filter_fields"]);
        Assert.False((bool)values["sort_uses_index"]!);
        Assert.True((bool)values["projection_covered_by_index"]!);
        Assert.Contains("*document_index_prefix:idx_docs_tenant_site", (string)values["candidate_plans"]!);
        Assert.Equal("sort_requires_in_memory_order_by", values["gap_reason"]);
    }

    [Fact]
    public void DocumentCollection_ExplainDocumentPlanner_ReportsIndexIntersectionGap()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"north","kind":"pump"}'),
                   ('dev-2', '{"site":"north","kind":"fan"}'),
                   ('dev-3', '{"site":"south","kind":"pump"}')
            """);
        SqlExecutor.Execute(db, "CREATE INDEX idx_docs_site ON device_docs ('$.site')");
        SqlExecutor.Execute(db, "CREATE INDEX idx_docs_kind ON device_docs ('$.kind')");

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = 'north'
              AND json_value(document, '$.kind') = 'pump'
            """));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);

        Assert.Equal("document_index", values["access_path"]);
        Assert.Equal("index_intersection_not_supported", values["gap_reason"]);
        Assert.Contains("document_index:idx_docs_site", (string)values["candidate_plans"]!);
        Assert.Contains("document_index:idx_docs_kind", (string)values["candidate_plans"]!);
    }

    [Fact]
    public void DocumentCollection_DocumentIndex_NullLookupMatchesJsonNull()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-null', '{"site":null}'),
                   ('dev-missing', '{"type":"pump"}'),
                   ('dev-north', '{"site":"north"}')
            """);
        SqlExecutor.Execute(db, "CREATE INDEX idx_docs_site ON device_docs ('$.site')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = NULL
            ORDER BY id
            """));
        Assert.Equal(["dev-null"], result.Rows.Select(static row => (string)row[0]!).ToArray());

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM device_docs
            WHERE json_value(document, '$.site') = NULL
            """));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_index", values["access_path"]);
        Assert.Equal("idx_docs_site", values["index_name"]);

        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION sparse_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO sparse_docs (id, document)
            VALUES ('dev-null', '{"site":null}'),
                   ('dev-missing', '{"type":"pump"}'),
                   ('dev-north', '{"site":"north"}')
            """);
        SqlExecutor.Execute(db, "CREATE SPARSE INDEX idx_sparse_site ON sparse_docs ('$.site')");

        var sparse = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id
            FROM sparse_docs
            WHERE json_value(document, '$.site') = NULL
            ORDER BY id
            """));
        Assert.Equal(["dev-null"], sparse.Rows.Select(static row => (string)row[0]!).ToArray());

        var sparseExplain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM sparse_docs
            WHERE json_value(document, '$.site') = NULL
            """));
        var sparseValues = sparseExplain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_scan", sparseValues["access_path"]);
    }

    [Fact]
    public void DocumentCollection_CreateUniqueIndex_WithExistingDuplicates_RejectsAndKeepsSchema()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"serial":"s1"}'),
                   ('dev-2', '{"serial":"s1"}')
            """);

        var duplicate = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "CREATE UNIQUE INDEX ux_docs_serial ON device_docs ('$.serial')"));
        Assert.Contains("ux_docs_serial", duplicate.Message, StringComparison.Ordinal);

        var indexes = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SHOW JSON INDEXES ON device_docs"));
        Assert.Empty(indexes.Rows);
    }

    [Fact]
    public void DocumentCollection_SelectUsesSharedFindPlanner_ForFilterSortPagination()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"north","score":7,"metrics":{"temp":22}}'),
                   ('dev-2', '{"site":"south","score":3,"metrics":{"temp":18}}'),
                   ('dev-3', '{"site":"north","score":9,"metrics":{"temp":24}}'),
                   ('dev-4', '{"site":"east","score":9,"metrics":{"temp":20}}')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, json_value(document, '$.score') AS score
            FROM device_docs
            WHERE json_value(document, '$.score') >= 7
              AND NOT json_value(document, '$.site') = 'east'
            ORDER BY score DESC
            LIMIT 1 OFFSET 1
            """));

        Assert.Equal(new[] { "id", "score" }, result.Columns);
        Assert.Equal(new object?[] { "dev-1", 7.0 }, result.Rows.Single());
    }

    [Fact]
    public void DocumentCollection_SelectGroupByAggregate_ReturnsGroups()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"north","kind":"pump","score":7}'),
                   ('dev-2', '{"site":"south","kind":"fan","score":3}'),
                   ('dev-3', '{"site":"north","kind":"pump","score":9}')
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT json_value(document, '$.site') AS site,
                   count(*) AS rows,
                   sum(json_value(document, '$.score')) AS total,
                   avg(json_value(document, '$.score')) AS avg_score,
                   first(json_value(document, '$.kind')) AS first_kind
            FROM device_docs
            GROUP BY json_value(document, '$.site')
            HAVING sum(json_value(document, '$.score')) >= 10
            ORDER BY total DESC
            """));

        Assert.Equal(["site", "rows", "total", "avg_score", "first_kind"], result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal(new object?[] { "north", 2L, 16.0, 8.0, "pump" }, result.Rows[0]);
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

    [Fact]
    public void DocumentCollection_VectorSearch_RanksAndFiltersJsonDocuments()
    {
        using var db = Tsdb.Open(Options());
        CreateHybridSearchFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT id, site, vector_distance() AS distance, vector_score() AS score
            FROM vector_search(source => logs, vector_field => '$.embedding', vector => [1, 0, 0], k => 4)
            WHERE site = 'north'
            ORDER BY distance
            """));

        Assert.Equal(new[] { "id", "site", "distance", "score" }, result.Columns);
        Assert.Equal(["log-1", "log-3"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.All(result.Rows, static row => Assert.Equal("north", row[1]));
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0][2]), 6);
        Assert.True(Convert.ToDouble(result.Rows[0][3]) > Convert.ToDouble(result.Rows[1][3]));
    }

    [Fact]
    public void DocumentCollection_VectorSearch_ExplainShowsDocumentVectorScan()
    {
        using var db = Tsdb.Open(Options());
        CreateHybridSearchFixture(db);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT id
            FROM vector_search(source => logs, vector_field => '$.embedding', vector => [1, 0, 0], k => 2)
            """));

        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("vector_search", values["statement_type"]);
        Assert.Equal("document_vector_scan", values["access_path"]);
        Assert.Equal("$.embedding", values["index_name"]);
        Assert.Equal(4L, Convert.ToInt64(values["estimated_scanned_rows"]));
    }

    [Fact]
    public void HybridSearch_MeasurementKnn_AssociatesDocumentKnowledgeRows()
    {
        using var db = Tsdb.Open(Options());
        CreateMeasurementKnowledgeFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT measurement.device_id AS device,
                   document_id,
                   json_value(document, '$.title') AS title,
                   measurement_distance() AS m_distance,
                   bm25_score() AS text_score,
                   hybrid_score() AS score
            FROM hybrid_search(
                source => incidents,
                documents => knowledge,
                vector_field => embedding,
                vector => [1, 0, 0],
                k => 5,
                measurement_join_tag => device_id,
                document_join_path => '$.device_id',
                document_join_index => idx_knowledge_device,
                text_index => ft_knowledge_body,
                text_field => '$.body',
                text => 'pump alarm overheating',
                measurement_weight => 0.7,
                text_weight => 0.3)
            WHERE time >= 1000 AND category = 'fault'
            ORDER BY score DESC
            """));

        Assert.Equal(new[] { "device", "document_id", "title", "m_distance", "text_score", "score" }, result.Columns);
        Assert.Equal(["pump-1", "pump-2"], result.Rows.Select(static row => (string)row[0]!).ToArray());
        Assert.Equal(["kb-pump-1", "kb-pump-2"], result.Rows.Select(static row => (string)row[1]!).ToArray());
        Assert.Equal("Pump overheating guide", result.Rows[0][2]);
        Assert.Equal(0.0, Convert.ToDouble(result.Rows[0][3]), 6);
        Assert.True(Convert.ToDouble(result.Rows[0][4]) > 0);
        Assert.True(Convert.ToDouble(result.Rows[0][5]) > Convert.ToDouble(result.Rows[1][5]));
    }

    [Fact]
    public void HybridSearch_MeasurementKnn_ExplainShowsCrossModelAccessPath()
    {
        using var db = Tsdb.Open(Options());
        CreateMeasurementKnowledgeFixture(db);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT document_id
            FROM hybrid_search(
                source => incidents,
                documents => knowledge,
                vector_field => embedding,
                vector => [1, 0, 0],
                measurement_join_tag => device_id,
                document_join_path => '$.device_id',
                text => 'pump alarm')
            """));

        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("hybrid_search", values["statement_type"]);
        Assert.Equal("hybrid_search_measurement_knn_documents", values["access_path"]);
        Assert.Contains("embedding", (string)values["index_name"]!);
        Assert.Contains("ft_knowledge_body", (string)values["index_name"]!);
    }

    [Fact]
    public void HybridSearch_MeasurementKnnJoinDimensionTable_PushesRelationFilter()
    {
        using var db = Tsdb.Open(Options());
        CreateMeasurementKnowledgeFixture(db);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT measurement.device_id AS device,
                   d.site AS site,
                   document_id,
                   hybrid_score() AS score
            FROM hybrid_search(
                source => incidents,
                documents => knowledge,
                vector_field => embedding,
                vector => [1, 0, 0],
                k => 5,
                measurement_join_tag => device_id,
                document_join_path => '$.device_id',
                document_join_index => idx_knowledge_device,
                text_index => ft_knowledge_body,
                text_field => '$.body',
                text => 'pump alarm')
            JOIN devices d ON measurement.device_id = d.id
            WHERE d.tenant = 'tenant-1' AND measurement.time >= 1000 AND category = 'fault'
            ORDER BY score DESC
            """));

        Assert.Equal(new[] { "device", "site", "document_id", "score" }, result.Columns);
        Assert.Single(result.Rows);
        Assert.Equal("pump-1", result.Rows[0][0]);
        Assert.Equal("north", result.Rows[0][1]);
        Assert.Equal("kb-pump-1", result.Rows[0][2]);
        Assert.True(Convert.ToDouble(result.Rows[0][3]) > 0);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT document_id
            FROM hybrid_search(
                source => incidents,
                documents => knowledge,
                vector_field => embedding,
                vector => [1, 0, 0],
                measurement_join_tag => device_id,
                document_join_path => '$.device_id',
                text => 'pump alarm')
            JOIN devices d ON measurement.device_id = d.id
            WHERE d.tenant = 'tenant-1'
            """));

        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Contains("relation_filter:secondary_index", (string)values["access_path"]!);
        Assert.Contains("idx_devices_tenant", (string)values["index_name"]!);
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

    private static void CreateMeasurementKnowledgeFixture(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE MEASUREMENT incidents (device_id TAG, embedding FIELD VECTOR(3), severity FIELD FLOAT)");
        SqlExecutor.Execute(db, """
            INSERT INTO incidents (device_id, embedding, severity, time)
            VALUES ('pump-1', [1, 0, 0], 9.0, 1000),
                   ('pump-2', [0.8, 0.2, 0], 7.5, 2000),
                   ('fan-1', [0, 1, 0], 3.0, 3000)
            """);

        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION knowledge");
        SqlExecutor.Execute(db, """
            INSERT INTO knowledge (id, document)
            VALUES ('kb-pump-1', '{"device_id":"pump-1","title":"Pump overheating guide","body":"pump alarm overheating recovery","category":"fault","embedding":[1,0,0]}'),
                   ('kb-pump-2', '{"device_id":"pump-2","title":"Pump pressure guide","body":"pump alarm pressure inspection","category":"fault","embedding":[0.8,0.2,0]}'),
                   ('kb-fan-1', '{"device_id":"fan-1","title":"Fan maintenance note","body":"fan maintenance normal","category":"normal","embedding":[0,1,0]}')
            """);
        SqlExecutor.Execute(db, "CREATE JSON INDEX idx_knowledge_device ON knowledge ('$.device_id')");
        SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_knowledge_body ON knowledge ('$.body') USING unicode");

        SqlExecutor.Execute(db, "CREATE TABLE devices (id STRING, tenant STRING, site STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE INDEX idx_devices_tenant ON devices (tenant)");
        SqlExecutor.Execute(db, """
            INSERT INTO devices (id, tenant, site)
            VALUES ('pump-1', 'tenant-1', 'north'),
                   ('pump-2', 'tenant-2', 'south'),
                   ('fan-1', 'tenant-1', 'east')
            """);
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
