using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Graphs;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class PropertyGraphPhase2SqlTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-property-graph-sql-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ParseCreatePropertyGraph_WithVertexAndEdgeMappings_ReturnsTypedAst()
    {
        var statement = Assert.IsType<CreatePropertyGraphStatement>(SqlParser.Parse(CreateSocialGraphSql));

        Assert.Equal("social", statement.Name);
        Assert.True(statement.IfNotExists);
        PropertyGraphVertexTableClause vertex = Assert.Single(statement.VertexTables);
        Assert.Equal("person", vertex.TableName);
        Assert.Equal(["id"], vertex.KeyColumns);
        Assert.Equal(["id", "name"], vertex.PropertyColumns);
        PropertyGraphEdgeTableClause edge = Assert.Single(statement.EdgeTables);
        Assert.Equal("knows", edge.TableName);
        Assert.Equal(["source_id"], edge.SourceColumns);
        Assert.Equal(["target_id"], edge.DestinationColumns);

        Assert.IsType<ShowPropertyGraphsStatement>(SqlParser.Parse("SHOW PROPERTY GRAPHS"));
        Assert.Equal("social", Assert.IsType<DescribePropertyGraphStatement>(
            SqlParser.Parse("DESCRIBE PROPERTY GRAPH social")).Name);
        Assert.True(Assert.IsType<DropPropertyGraphStatement>(
            SqlParser.Parse("DROP PROPERTY GRAPH IF EXISTS social")).IfExists);
    }

    [Fact]
    public void ExecutePropertyGraph_DdlAccessorAndExplain_UseRelationalRowsAndIndexes()
    {
        Directory.CreateDirectory(_root);
        using (var db = Open())
        {
            CreateSocialTables(db);
            InsertSocialRows(db);
            var created = Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(db, CreateSocialGraphSql));
            Assert.Equal(1, created.RowsAffected);
            Assert.Equal(0, Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(db, CreateSocialGraphSql)).RowsAffected);

            Assert.Equal(0, db.Graphs.Catalog.Count);
            Assert.Equal(1, db.Graphs.PropertyGraphs.Count);
            RelationalGraphAccessor accessor = db.Graphs.OpenPropertyGraph("social");
            RelationalGraphReadResult vertex = accessor.SeekVertex("person", [1L]);
            Assert.Equal("relation_primary_key_seek", Assert.Single(vertex.AccessPlans).AccessPath);
            Assert.Equal("Ada", Assert.Single(vertex.Rows).Values[1]);

            RelationalGraphReadResult outgoing = accessor.ExpandEdges(
                "knows",
                GraphDirection.Outgoing,
                [1L]);
            Assert.Equal("relation_index_seek", Assert.Single(outgoing.AccessPlans).AccessPath);
            Assert.Equal("ix_knows_source", outgoing.AccessPlans[0].IndexName);
            Assert.Equal([10L, 11L], outgoing.Rows.Select(static row => (long)row.Values[0]!).ToArray());

            RelationalGraphReadResult incoming = accessor.ExpandEdges(
                "knows",
                GraphDirection.Incoming,
                [2L],
                new RelationalGraphAccessOptions { MaxScanDuration = TimeSpan.FromSeconds(5) });
            Assert.Equal("relation_scan_fallback", Assert.Single(incoming.AccessPlans).AccessPath);
            Assert.Equal(3, incoming.ExaminedRows);
            Assert.Equal(10L, Assert.Single(incoming.Rows).Values[0]);
            Assert.Throws<GraphTraversalLimitExceededException>(() => accessor.ExpandEdges(
                "knows",
                GraphDirection.Incoming,
                [2L],
                new RelationalGraphAccessOptions
                {
                    MaxScanRows = 2,
                    MaxScanDuration = TimeSpan.FromSeconds(5),
                }));

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => accessor.ExpandEdges(
                "knows",
                GraphDirection.Outgoing,
                [1L],
                cancellationToken: cancelled.Token));

            var described = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(db, "DESCRIBE PROPERTY GRAPH social"));
            IReadOnlyList<object?> edgeDescription = Assert.Single(
                described.Rows,
                static row => Equals(row[0], "edge"));
            Assert.Equal("relation_index_seek", edgeDescription[7]);
            Assert.Equal("ix_knows_source", edgeDescription[8]);
            Assert.Equal("relation_scan_fallback", edgeDescription[9]);

            var explain = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(db, "EXPLAIN DESCRIBE PROPERTY GRAPH social"));
            var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
            Assert.Equal("relational_mapping", plan["graph_kind"]);
            Assert.Equal(false, plan["copies_relational_rows"]);
            Assert.Equal("relation_index_seek", plan["edge.knows.outgoing.access_path"]);
            Assert.Equal("relation_scan_fallback", plan["edge.knows.incoming.access_path"]);

            var shown = Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(db, "SHOW PROPERTY GRAPHS"));
            Assert.Equal("social", Assert.Single(shown.Rows)[0]);

            var dependency = Assert.Throws<InvalidOperationException>(() =>
                SqlExecutor.Execute(db, "DROP TABLE knows"));
            Assert.Contains("property graph 'social'", dependency.Message, StringComparison.Ordinal);
        }

        using (var reopened = Open())
        {
            Assert.NotNull(reopened.Graphs.PropertyGraphs.TryGet("social"));
            RelationalGraphReadResult outgoing = reopened.Graphs.OpenPropertyGraph("social")
                .ExpandEdges("knows", GraphDirection.Outgoing, [1L]);
            Assert.Equal(2, outgoing.Rows.Count);

            Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(reopened, "DROP PROPERTY GRAPH social")).RowsAffected);
            Assert.Equal(0, Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(reopened, "DROP PROPERTY GRAPH IF EXISTS social")).RowsAffected);
            Assert.NotNull(SqlExecutor.Execute(reopened, "DROP TABLE knows"));
        }
    }

    [Fact]
    public void ExecutePropertyGraph_InvalidKeyOrReference_RejectsDefinition()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);

        var invalidKey = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            CREATE PROPERTY GRAPH invalid_key
            VERTEX TABLES (person KEY (name) LABEL person PROPERTIES (name))
            """));
        Assert.Contains("PRIMARY KEY 或完整 UNIQUE INDEX", invalidKey.Message, StringComparison.Ordinal);

        var invalidReference = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            CREATE PROPERTY GRAPH invalid_reference
            VERTEX TABLES (person KEY (id) LABEL person PROPERTIES (name))
            EDGE TABLES (
                knows KEY (id)
                    SOURCE KEY (source_id) REFERENCES person (name)
                    DESTINATION KEY (target_id) REFERENCES person (id)
                    LABEL knows PROPERTIES (since)
            )
            """));
        Assert.Contains("REFERENCES 必须匹配", invalidReference.Message, StringComparison.Ordinal);
        Assert.Equal(0, db.Graphs.PropertyGraphs.Count);
    }

    [Fact]
    public void ExecuteGraphTable_RelationalFixedPattern_UsesMappedAccessorAndSqlShape()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);
        InsertSocialRows(db);
        _ = SqlExecutor.Execute(db, CreateSocialGraphSql);

        const string sql = """
            SELECT source_id, target_id, edge_id, since
            FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = @anchor
                COLUMNS (
                    a.id AS source_id,
                    b.id AS target_id,
                    e.id AS edge_id,
                    e.since AS since
                )
            )
            WHERE since >= @minimum
            ORDER BY edge_id DESC
            LIMIT 1
            """;
        var parameters = new SqlParameters()
            .AddNamed("anchor", 1L)
            .AddNamed("minimum", 2020L);
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "fixture",
            sql,
            parameters));
        Assert.Equal(["source_id", "target_id", "edge_id", "since"], result.Columns);
        Assert.Equal(new object?[] { 1L, 3L, 11L, 2021L }, Assert.Single(result.Rows));

        var incoming = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT source_id, edge_id
            FROM GRAPH_TABLE (
                social
                MATCH (b IS person)<-[e IS knows]-(a IS person)
                WHERE b.id = 1
                COLUMNS (a.id AS source_id, e.id AS edge_id)
            )
            """));
        Assert.Equal(new object?[] { 3L, 12L }, Assert.Single(incoming.Rows));

        var undirected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT edge_id
            FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]-(b IS person)
                WHERE a.id = 1
                COLUMNS (e.id AS edge_id)
            )
            ORDER BY edge_id
            """));
        Assert.Equal([10L, 11L, 12L], undirected.Rows.Select(static row => (long)row[0]!).ToArray());

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT *
            FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = 1
                COLUMNS (a.id AS source_id, b.id AS target_id, e.id AS edge_id)
            )
            """));
        var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal("relational_mapping", plan["graph_kind"]);
        Assert.Equal("relation_primary_key_seek", plan["anchor.person.access_path"]);
        Assert.Equal("relation_index_seek", plan["edge.knows.outgoing.access_path"]);
        Assert.Equal("ix_knows_source", plan["edge.knows.outgoing.index"]);
    }

    [Fact]
    public void GraphTable_ViewsTrackGraphDependencyAndExpandPersistedSource()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);
        InsertSocialRows(db);
        _ = SqlExecutor.Execute(db, CreateSocialGraphSql);

        var missing = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, """
            CREATE VIEW missing_edges AS
            SELECT edge_id FROM GRAPH_TABLE (
                missing_graph
                MATCH (a IS person)-[e IS knows]->(b IS person)
                COLUMNS (e.id AS edge_id)
            )
            """));
        Assert.Contains("missing_graph", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("__graph_table__", missing.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(db, """
            CREATE VIEW parameterized_edges AS
            SELECT edge_id FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = @anchor
                COLUMNS (e.id AS edge_id)
            )
            """));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "CREATE VIEW social AS SELECT id FROM person"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "CREATE MATERIALIZED VIEW social AS SELECT id FROM person"));

        var view = Assert.IsType<ViewDefinition>(SqlExecutor.Execute(db, """
            CREATE VIEW social_edges AS
            SELECT edge_id FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = 1
                COLUMNS (e.id AS edge_id)
            )
            """));
        Assert.Equal(["social"], view.Dependencies);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT edge_id FROM social_edges ORDER BY edge_id"));
        Assert.Equal([10L, 11L], selected.Rows.Select(static row => (long)row[0]!).ToArray());

        var materialized = Assert.IsType<MaterializedViewDefinition>(SqlExecutor.Execute(db, """
            CREATE MATERIALIZED VIEW cached_social_edges AS
            SELECT edge_id FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = 1
                COLUMNS (e.id AS edge_id)
            )
            """));
        Assert.Equal(["social"], materialized.Dependencies);
        _ = SqlExecutor.Execute(db, "REFRESH MATERIALIZED VIEW cached_social_edges");

        var dependency = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "DROP PROPERTY GRAPH social"));
        Assert.Contains("social_edges", dependency.Message, StringComparison.Ordinal);

        _ = SqlExecutor.Execute(db, "DROP VIEW social_edges");
        _ = SqlExecutor.Execute(db, "DROP MATERIALIZED VIEW cached_social_edges");
        Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(db, "DROP PROPERTY GRAPH social")).RowsAffected);
    }

    [Fact]
    public void GraphTable_ProcedureBindsParametersPersistsAndBlocksGraphDrop()
    {
        Directory.CreateDirectory(_root);
        using (var db = Open())
        {
            CreateSocialTables(db);
            InsertSocialRows(db);
            _ = SqlExecutor.Execute(db, CreateSocialGraphSql);
            var procedure = Assert.IsType<ProcedureDefinition>(SqlExecutor.Execute(db, """
                CREATE PROCEDURE outgoing_edges (IN anchor INT)
                LANGUAGE SQL AS BEGIN
                    SELECT edge_id FROM GRAPH_TABLE (
                        social
                        MATCH (a IS person)-[e IS knows]->(b IS person)
                        WHERE a.id = @anchor
                        COLUMNS (e.id AS edge_id)
                    ) ORDER BY edge_id;
                END
                """));
            Assert.Equal(["social"], procedure.ObjectDependencies);
        }

        using var reopened = Open();
        var result = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(reopened, "CALL outgoing_edges(1)"));
        Assert.Equal([10L, 11L], result.Rows.Select(static row => (long)row[0]!).ToArray());
        Assert.Throws<RoutineExecutionException>(() =>
            SqlExecutor.Execute(reopened, "DROP PROPERTY GRAPH social"));

        _ = SqlExecutor.Execute(reopened, "DROP PROCEDURE outgoing_edges");
        Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(
            SqlExecutor.Execute(reopened, "DROP PROPERTY GRAPH social")).RowsAffected);
    }

    [Fact]
    public void GraphTable_SharedLabelsAcrossMappings_UnionBranchesAndDeduplicateUndirectedSelfLoops()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        _ = SqlExecutor.Execute(db, "CREATE TABLE people (id INT NOT NULL, PRIMARY KEY (id))");
        _ = SqlExecutor.Execute(db, "CREATE TABLE companies (id INT NOT NULL, PRIMARY KEY (id))");
        _ = SqlExecutor.Execute(db, """
            CREATE TABLE person_relations (
                id INT NOT NULL,
                source_id INT NOT NULL,
                target_id INT NOT NULL,
                PRIMARY KEY (id),
                FOREIGN KEY (source_id) REFERENCES people (id),
                FOREIGN KEY (target_id) REFERENCES people (id)
            )
            """);
        _ = SqlExecutor.Execute(db, """
            CREATE TABLE company_relations (
                id INT NOT NULL,
                source_id INT NOT NULL,
                target_id INT NOT NULL,
                PRIMARY KEY (id),
                FOREIGN KEY (source_id) REFERENCES companies (id),
                FOREIGN KEY (target_id) REFERENCES companies (id)
            )
            """);
        _ = SqlExecutor.Execute(db, "CREATE INDEX ix_person_relations_source ON person_relations (source_id)");
        _ = SqlExecutor.Execute(db, "CREATE INDEX ix_person_relations_target ON person_relations (target_id)");
        _ = SqlExecutor.Execute(db, "CREATE INDEX ix_company_relations_source ON company_relations (source_id)");
        _ = SqlExecutor.Execute(db, "CREATE INDEX ix_company_relations_target ON company_relations (target_id)");
        _ = SqlExecutor.Execute(db, "INSERT INTO people (id) VALUES (1), (2)");
        _ = SqlExecutor.Execute(db, "INSERT INTO companies (id) VALUES (10)");
        _ = SqlExecutor.Execute(db, """
            INSERT INTO person_relations (id, source_id, target_id)
            VALUES (100, 1, 2), (101, 1, 1)
            """);
        _ = SqlExecutor.Execute(db, """
            INSERT INTO company_relations (id, source_id, target_id)
            VALUES (200, 10, 10)
            """);
        _ = SqlExecutor.Execute(db, """
            CREATE PROPERTY GRAPH entities
            VERTEX TABLES (
                people KEY (id) LABEL entity PROPERTIES (id),
                companies KEY (id) LABEL entity PROPERTIES (id)
            )
            EDGE TABLES (
                person_relations KEY (id)
                    SOURCE KEY (source_id) REFERENCES people (id)
                    DESTINATION KEY (target_id) REFERENCES people (id)
                    LABEL relation PROPERTIES (id),
                company_relations KEY (id)
                    SOURCE KEY (source_id) REFERENCES companies (id)
                    DESTINATION KEY (target_id) REFERENCES companies (id)
                    LABEL relation PROPERTIES (id)
            )
            """);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT edge_id, left_id, right_id
            FROM GRAPH_TABLE (
                entities
                MATCH (a IS entity)-[e IS relation]-(b IS entity)
                COLUMNS (e.id AS edge_id, a.id AS left_id, b.id AS right_id)
            )
            ORDER BY edge_id, left_id
            """));
        Assert.Equal(
            [
                new object?[] { 100L, 1L, 2L },
                new object?[] { 100L, 2L, 1L },
                new object?[] { 101L, 1L, 1L },
                new object?[] { 200L, 10L, 10L },
            ],
            result.Rows);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT *
            FROM GRAPH_TABLE (
                entities
                MATCH (a IS entity)-[e IS relation]-(b IS entity)
                COLUMNS (e.id AS edge_id)
            )
            """));
        var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal(2, plan["mapping_branch_count"]);
        Assert.Equal("relation_index_seek", plan["edge.person_relations.outgoing.access_path"]);
        Assert.Equal("relation_index_seek", plan["edge.company_relations.incoming.access_path"]);
    }

    [Fact]
    public void GraphTable_RelationalVariableTrailSimpleAndShortestPath_UseMappedAccessor()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);
        InsertSocialRows(db);
        _ = SqlExecutor.Execute(db, CreateSocialGraphSql);

        var trail = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT end_id, hops, vertex_ids, edge_ids
            FROM GRAPH_TABLE (
                social
                MATCH p = TRAIL (a IS person)-[e IS knows]->{2,2}(b IS person)
                WHERE a.id = 1
                COLUMNS (
                    b.id AS end_id,
                    p.length AS hops,
                    p.vertex_ids AS vertex_ids,
                    p.edge_ids AS edge_ids
                )
            )
            """));
        IReadOnlyList<object?> trailRow = Assert.Single(trail.Rows);
        Assert.Equal(1L, trailRow[0]);
        Assert.Equal(2L, trailRow[1]);
        Assert.Contains("person:", Assert.IsType<string>(trailRow[2]), StringComparison.Ordinal);
        Assert.Contains("knows:", Assert.IsType<string>(trailRow[3]), StringComparison.Ordinal);

        var simple = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT end_id FROM GRAPH_TABLE (
                social
                MATCH p = SIMPLE (a IS person)-[e IS knows]->{2,2}(b IS person)
                WHERE a.id = 1 AND b.id = 1
                COLUMNS (b.id AS end_id)
            )
            """));
        Assert.Empty(simple.Rows);

        var shortest = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT end_id, hops FROM GRAPH_TABLE (
                social
                MATCH p = ANY SHORTEST SIMPLE (a IS person)-[e IS knows]->{1,3}(b IS person)
                WHERE a.id = 3 AND b.id = 2
                COLUMNS (b.id AS end_id, p.length AS hops)
            )
            """));
        Assert.Equal(new object?[] { 2L, 2L }, Assert.Single(shortest.Rows));

        _ = SqlExecutor.Execute(db, """
            INSERT INTO knows (id, source_id, target_id, since)
            VALUES (13, 3, 2, 2023)
            """);
        var boundedShortest = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT end_id, hops FROM GRAPH_TABLE (
                social
                MATCH p = ANY SHORTEST SIMPLE (a IS person)-[e IS knows]->{2,3}(b IS person)
                WHERE a.id = 1 AND b.id = 2
                COLUMNS (b.id AS end_id, p.length AS hops)
            )
            """));
        Assert.Equal(new object?[] { 2L, 2L }, Assert.Single(boundedShortest.Rows));

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT * FROM GRAPH_TABLE (
                social
                MATCH p = ANY SHORTEST SIMPLE (a IS person)-[e IS knows]->{1,3}(b IS person)
                WHERE a.id = 3
                COLUMNS (p.length AS hops)
            )
            """));
        var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal("GraphShortestPath", plan["logical_plan"]);
        Assert.Equal("relational_mapping", plan["graph_kind"]);
        Assert.Equal("relation_index_seek", plan["edge.knows.outgoing.access_path"]);
    }

    [Fact]
    public void GraphTableCostPlanner_RightKeyUsesLowerCostFallbackAndAnalyzeReportsIt()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);
        InsertSocialRows(db);
        _ = SqlExecutor.Execute(db, CreateSocialGraphSql);

        var analyzed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN ANALYZE SELECT source_id, edge_id FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE b.id = 2
                COLUMNS (a.id AS source_id, e.id AS edge_id)
            )
            """));
        var plan = analyzed.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);

        Assert.Equal("right", plan["anchor_side"]);
        Assert.Equal("b", plan["anchor_variable"]);
        Assert.Equal("incoming", plan["execution_direction"]);
        Assert.Equal("relation_scan_fallback", plan["edge.knows.incoming.access_path"]);
        Assert.Equal(1L, plan["actual_rows"]);
        Assert.Equal(1L, plan["actual_anchor_rows"]);
        Assert.Equal(3L, plan["actual_fallback_rows"]);
        Assert.True((long)plan["actual_expansions"]! > 0);
        Assert.True((double)plan["actual_fallback_ms"]! >= 0);
        Assert.Equal("relation_accessor_current", plan["read_consistency"]);
        Assert.Equal("relation_accessor_current", plan["actual_read_consistency"]);
        Assert.Null(plan["actual_snapshot_sequence"]);
    }

    [Fact]
    public void GraphLifecycle_InsideLightTransaction_IsRejectedWithoutPublishingChanges()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);
        _ = SqlExecutor.Execute(db, CreateSocialGraphSql);
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");
        var transaction = Assert.IsType<SqlTransactionContext>(
            SqlExecutor.ExecuteStatement(db, SqlParser.Parse("BEGIN")));

        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            statement: SqlParser.Parse("CREATE GRAPH other"),
            controlPlane: null,
            transaction: transaction));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            statement: SqlParser.Parse(CreateSocialGraphSql),
            controlPlane: null,
            transaction: transaction));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            statement: SqlParser.Parse("DROP GRAPH topology"),
            controlPlane: null,
            transaction: transaction));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            statement: SqlParser.Parse("DROP PROPERTY GRAPH social"),
            controlPlane: null,
            transaction: transaction));
        Assert.Throws<NotSupportedException>(() => SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            statement: SqlParser.Parse(
                "INSERT INTO GRAPH topology VERTEX (id, labels) VALUES (1, 1)"),
            controlPlane: null,
            transaction: transaction));

        _ = SqlExecutor.ExecuteStatement(
            db,
            databaseName: null,
            statement: SqlParser.Parse("ROLLBACK"),
            controlPlane: null,
            transaction: transaction);
        Assert.Null(db.Graphs.Catalog.TryGet("other"));
        Assert.NotNull(db.Graphs.Catalog.TryGet("topology"));
        Assert.NotNull(db.Graphs.PropertyGraphs.TryGet("social"));
        Assert.Empty(Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "SELECT id FROM graph_nodes('topology')")).Rows);
    }

    [Fact]
    public void GraphTable_DerivedRowsJoinTableDocumentAndHybridCandidates_UsesSharedSqlPipeline()
    {
        Directory.CreateDirectory(_root);
        using var db = Open();
        CreateSocialTables(db);
        InsertSocialRows(db);
        _ = SqlExecutor.Execute(db, CreateSocialGraphSql);
        _ = SqlExecutor.Execute(db, "CREATE TABLE person_status (id INT NOT NULL, status STRING, PRIMARY KEY (id))");
        _ = SqlExecutor.Execute(db, "INSERT INTO person_status (id, status) VALUES (2, 'active'), (3, 'inactive')");
        _ = SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION profiles");
        _ = SqlExecutor.Execute(db, """
            INSERT INTO profiles (id, document)
            VALUES ('profile-2', '{"person_id":2,"title":"Pump alarm expert","embedding":[1,0,0]}'),
                   ('profile-3', '{"person_id":3,"title":"Routine maintenance","embedding":[0,1,0]}')
            """);
        _ = SqlExecutor.Execute(db, "CREATE FULLTEXT INDEX ft_profiles_title ON profiles ('$.title') USING unicode");

        const string sql = """
            SELECT g.source_id AS source_id,
                   g.target_id AS target_id,
                   s.status AS status,
                   d.title AS title,
                   h.score AS score
            FROM (
                SELECT source_id, target_id
                FROM GRAPH_TABLE (
                    social
                    MATCH (a IS person)-[e IS knows]->(b IS person)
                    WHERE a.id = 1
                    COLUMNS (a.id AS source_id, b.id AS target_id)
                )
            ) AS g
            JOIN person_status AS s ON g.target_id = s.id
            JOIN (
                SELECT json_value(document, '$.person_id') AS person_id,
                       json_value(document, '$.title') AS title
                FROM profiles
            ) AS d ON g.target_id = d.person_id
            JOIN (
                SELECT person_id, hybrid_score() AS score
                FROM hybrid_search(
                    source => profiles,
                    text_index => ft_profiles_title,
                    text_field => '$.title',
                    text => 'pump alarm',
                    vector_field => '$.embedding',
                    vector => [1, 0, 0],
                    k => 2)
            ) AS h ON g.target_id = h.person_id
            WHERE s.status = 'active'
            """;

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

        Assert.Equal(["source_id", "target_id", "status", "title", "score"], result.Columns);
        IReadOnlyList<object?> row = Assert.Single(result.Rows);
        Assert.Equal(new object?[] { 1L, 2L, "active", "Pump alarm expert" }, row.Take(4).ToArray());
        Assert.True(Convert.ToDouble(row[4]) > 0d);

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + sql));
        var plan = explain.Rows.ToDictionary(static item => (string)item[0]!, static item => item[1]);
        Assert.Equal("cross_model_select", plan["statement_type"]);
        Assert.Contains("graph_table", (string)plan["access_path"]!, StringComparison.Ordinal);
        Assert.Contains("table_scan", (string)plan["access_path"]!, StringComparison.Ordinal);
        Assert.Contains("document_scan", (string)plan["access_path"]!, StringComparison.Ordinal);
        Assert.Contains("hybrid_search", (string)plan["access_path"]!, StringComparison.Ordinal);
        Assert.Contains("fulltext", (string)plan["candidate_contract"]!, StringComparison.Ordinal);
        Assert.Contains("vector", (string)plan["candidate_contract"]!, StringComparison.Ordinal);
        Assert.NotNull(plan["fallback_reason"]);
    }

    [Fact]
    public void RoutineRowBinder_GraphTablePredicate_BindsTriggerRowReference()
    {
        var select = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT edge_id FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = NEW.id
                COLUMNS (e.id AS edge_id)
            )
            """));
        TableSchema schema = TableSchema.Create(
            "events",
            [("id", TableColumnType.Int64, false)],
            ["id"]);
        var context = new RoutineRowContext(schema, OldValues: null, NewValues: [42L]);

        var bound = Assert.IsType<ExistsExpression>(RoutineRowBinder.BindExpression(
            new ExistsExpression(select),
            context));
        GraphTableSource source = bound.Select.GraphTable!;
        var predicate = Assert.IsType<BinaryExpression>(source.Predicate);
        var literal = Assert.IsType<LiteralExpression>(predicate.Right);
        Assert.Equal(SqlLiteralKind.Integer, literal.Kind);
        Assert.Equal(42L, literal.IntegerValue);
    }

    [Fact]
    public void OpenPropertyGraph_WithCorruptedCatalog_RejectsRecovery()
    {
        Directory.CreateDirectory(_root);
        string catalogPath;
        using (var db = Open())
        {
            CreateSocialTables(db);
            _ = SqlExecutor.Execute(db, CreateSocialGraphSql);
            catalogPath = db.Graphs.PropertyGraphCatalogPath;
        }

        byte[] bytes = File.ReadAllBytes(catalogPath);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(catalogPath, bytes);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => Open());
        Assert.Contains("PropertyGraphCatalog", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateSocialTables(Tsdb db)
    {
        _ = SqlExecutor.Execute(db, "CREATE TABLE person (id INT NOT NULL, name STRING, PRIMARY KEY (id))");
        _ = SqlExecutor.Execute(db, """
            CREATE TABLE knows (
                id INT NOT NULL,
                source_id INT NOT NULL,
                target_id INT NOT NULL,
                since INT,
                PRIMARY KEY (id),
                FOREIGN KEY (source_id) REFERENCES person (id),
                FOREIGN KEY (target_id) REFERENCES person (id)
            )
            """);
        _ = SqlExecutor.Execute(db, "CREATE INDEX ix_knows_source ON knows (source_id)");
    }

    private static void InsertSocialRows(Tsdb db)
    {
        _ = SqlExecutor.Execute(db, "INSERT INTO person (id, name) VALUES (1, 'Ada'), (2, 'Lin'), (3, 'Sam')");
        _ = SqlExecutor.Execute(db, """
            INSERT INTO knows (id, source_id, target_id, since)
            VALUES (10, 1, 2, 2020), (11, 1, 3, 2021), (12, 3, 1, 2022)
            """);
    }

    private const string CreateSocialGraphSql = """
        CREATE PROPERTY GRAPH IF NOT EXISTS social
        VERTEX TABLES (
            person KEY (id) LABEL person PROPERTIES (id, name)
        )
        EDGE TABLES (
            knows KEY (id)
                SOURCE KEY (source_id) REFERENCES person (id)
                DESTINATION KEY (target_id) REFERENCES person (id)
                LABEL knows PROPERTIES (id, since)
        )
        """;
}
