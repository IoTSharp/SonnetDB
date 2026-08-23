using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class GraphSqlContractV1Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-sql-v1-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parse_GraphMutationAndAnalyze_ReturnTypedAstWithoutStealingGraphTableName()
    {
        var upsert = Assert.IsType<InsertGraphStatement>(SqlParser.Parse(
            "UPSERT INTO GRAPH topology VERTEX (id, element_version, labels, property_7) VALUES (?, ?, 1, @name)"));
        Assert.Equal(GraphValuesMutationMode.Upsert, upsert.Mode);
        Assert.Equal(GraphMutationKind.Vertex, upsert.Kind);

        var update = Assert.IsType<UpdateGraphStatement>(SqlParser.Parse(
            "UPDATE GRAPH topology VERTEX SET property_7 = @name WHERE id = ? AND element_version = ?"));
        Assert.Equal("topology", update.GraphName);
        Assert.Equal("property_7", Assert.Single(update.Assignments).ColumnName);

        var delete = Assert.IsType<DeleteGraphStatement>(SqlParser.Parse(
            "DELETE FROM GRAPH topology EDGE WHERE id = 10 AND element_version = 2"));
        Assert.Equal(GraphMutationKind.Edge, delete.Kind);
        Assert.Equal("topology", Assert.IsType<AnalyzeGraphStatement>(
            SqlParser.Parse("ANALYZE GRAPH topology")).GraphName);

        Assert.IsType<UpdateStatement>(SqlParser.Parse("UPDATE graph SET value = 1 WHERE id = 1"));
        Assert.IsType<DeleteStatement>(SqlParser.Parse("DELETE FROM graph WHERE id = 1"));
    }

    [Fact]
    public void Execute_GraphSqlV1PropertiesUpsertUpdateDelete_MatchTypedTransactionSemantics()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");

        var insert = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            "fixture",
            """
                INSERT INTO GRAPH topology VERTEX
                    (id, labels, property_7, property_8, property_10, unique_property_ids)
                VALUES (@id, '1,2', @name, true, DEFAULT, 7), (2, 1, 'target', NULL, DEFAULT, NULL)
                """,
            new SqlParameters().AddNamed("id", 1L).AddNamed("name", "pump")));
        Assert.Equal(2, insert.RowsAffected);
        _ = SqlExecutor.Execute(
            db,
            "INSERT INTO GRAPH topology EDGE (id, source_id, target_id, label_id, property_9) VALUES (10, 1, 2, 3, 1.5)");

        var upsert = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            """
                UPSERT INTO GRAPH topology VERTEX
                    (id, element_version, labels, property_7, property_8, unique_property_ids)
                VALUES (1, 1, 2, 'pump-v2', 42, 7)
                """));
        Assert.Equal("upsert_graph", upsert.Operation);

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            "fixture",
            """
                UPDATE GRAPH topology VERTEX
                SET labels = '2,4', property_7 = @name, property_8 = NULL
                WHERE id = @id AND element_version = @version
                """,
            new SqlParameters()
                .AddNamed("name", "pump-v3")
                .AddNamed("id", 1L)
                .AddNamed("version", 2L)));
        Assert.Equal(1, updated.RowsAffected);

        GraphStore store = db.Graphs.Open("topology");
        using (GraphReadSession read = store.BeginRead())
        {
            GraphVertex vertex = Assert.IsType<GraphVertex>(read.GetVertex(new GraphElementId(1)));
            Assert.Equal(3, vertex.ElementVersion);
            Assert.Equal([new LabelId(2), new LabelId(4)], vertex.Labels);
            GraphProperty property = Assert.Single(vertex.Properties);
            Assert.Equal(7, property.PropertyId);
            Assert.Equal("pump-v3", property.Value.AsString());
            Assert.Equal([7], read.GetOwnedUniquePropertyIds(vertex));
        }

        GraphVertexDeleteRestrictedException restricted = Assert.Throws<GraphVertexDeleteRestrictedException>(() =>
            SqlExecutor.Execute(
                db,
                "DELETE FROM GRAPH topology VERTEX WHERE id = 1 AND element_version = 3"));
        Assert.Equal(new GraphElementId(1), restricted.VertexId);

        _ = SqlExecutor.Execute(
            db,
            "DELETE FROM GRAPH topology EDGE WHERE id = 10 AND element_version = 1");
        _ = SqlExecutor.Execute(
            db,
            "DELETE FROM GRAPH topology VERTEX WHERE id = 1 AND element_version = 3");
        using GraphReadSession finalRead = store.BeginRead();
        Assert.Null(finalRead.GetVertex(new GraphElementId(1)));
        Assert.Null(finalRead.GetEdge(new GraphElementId(10)));
    }

    [Fact]
    public void Execute_GraphBatchUpsertVersionConflict_PublishesNoPartialRows()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");
        _ = SqlExecutor.Execute(
            db,
            "INSERT INTO GRAPH topology VERTEX (id, labels, property_7) VALUES (1, 1, 'one'), (2, 1, 'two')");

        Assert.Throws<GraphConcurrencyException>(() => SqlExecutor.Execute(
            db,
            """
                UPSERT INTO GRAPH topology VERTEX
                    (id, element_version, labels, property_7)
                VALUES (1, 1, 1, 'changed'), (2, 9, 1, 'invalid')
                """));

        using GraphReadSession read = db.Graphs.Open("topology").BeginRead();
        GraphVertex first = Assert.IsType<GraphVertex>(read.GetVertex(new GraphElementId(1)));
        GraphVertex second = Assert.IsType<GraphVertex>(read.GetVertex(new GraphElementId(2)));
        Assert.Equal(1, first.ElementVersion);
        Assert.Equal("one", Assert.Single(first.Properties).Value.AsString());
        Assert.Equal(1, second.ElementVersion);
        Assert.Equal("two", Assert.Single(second.Properties).Value.AsString());

        Assert.Throws<GraphConcurrencyException>(() => SqlExecutor.Execute(
            db,
            "UPDATE GRAPH topology VERTEX SET property_7 = 'stale' WHERE id = 1 AND element_version = 8"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "UPDATE GRAPH topology VERTEX SET property_7 = 'unsafe' WHERE id = 1"));
    }

    [Fact]
    public void ExplainAnalyze_PropertyStatistics_SelectHighSelectivityRightAnchorAndActualIndex()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        GraphStore store = db.Graphs.Create("topology");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1_000),
            0,
            [new LabelId(3)],
            [new GraphProperty(7, GraphPropertyValue.FromString("needle"))]);
        for (int id = 1; id <= 100; id++)
        {
            transaction.UpsertVertex(
                new GraphElementId(id),
                0,
                [new LabelId(1)],
                [new GraphProperty(7, GraphPropertyValue.FromString("common"))]);
            transaction.UpsertEdge(
                new GraphElementId(10_000 + id),
                0,
                new GraphElementId(id),
                new GraphElementId(1_000),
                new LabelId(2),
                []);
        }
        transaction.Commit();

        var analyzed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "ANALYZE GRAPH topology"));
        Assert.Equal(101L, Assert.Single(analyzed.Rows)[2]);

        const string Query = """
            SELECT source_id, target_id
            FROM GRAPH_TABLE (
                topology
                MATCH (a IS 1)-[e IS 2]->(b IS 3)
                WHERE b.property_7 = 'needle'
                COLUMNS (a.id AS source_id, b.id AS target_id)
            )
            """;
        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + Query));
        Dictionary<string, object?> plan = ToPlan(explain);
        Assert.Equal("right", plan["anchor_side"]);
        Assert.Equal("b", plan["anchor_variable"]);
        Assert.Equal("incoming", plan["execution_direction"]);
        Assert.Equal("native_property_index_seek", plan["anchor_access_path"]);
        Assert.Equal(7, plan["anchor_property_id"]);
        Assert.Equal("refreshed", plan["statistics_freshness"]);
        Assert.Equal("property_value_statistics_refreshed", plan["estimate_source"]);
        Assert.Equal(1L, plan["estimated_anchor_rows"]);
        Assert.Contains("property_7", Assert.IsType<string>(plan["anchor_index"]), StringComparison.Ordinal);
        Assert.Equal("anchor_then_expand_then_residual_filter", plan["anchor_expand_order"]);
        Assert.Null(plan["fallback_reason"]);

        var actual = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN ANALYZE " + Query));
        Dictionary<string, object?> actualPlan = ToPlan(actual);
        Assert.Equal("native_property_index_seek", actualPlan["actual_anchor_access_path"]);
        Assert.Equal(plan["anchor_index"], actualPlan["actual_anchor_index"]);
        Assert.Equal(1L, actualPlan["actual_anchor_rows"]);
        Assert.Equal(100L, actualPlan["actual_rows"]);

        var fallback = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT source_id FROM GRAPH_TABLE (
                topology
                MATCH (a IS 1)-[e IS 2]->(b IS 3)
                WHERE b.property_7 > 'a'
                COLUMNS (a.id AS source_id)
            )
            """));
        Dictionary<string, object?> fallbackPlan = ToPlan(fallback);
        Assert.Equal("native_label_index", fallbackPlan["anchor_access_path"]);
        Assert.Equal("property_predicate_not_exact_or_unsupported", fallbackPlan["fallback_reason"]);
    }

    [Fact]
    public void DescribeGraph_ReportsVersionedAutomaticIndexContract()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");

        var described = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "DESCRIBE GRAPH topology"));
        Assert.Equal(GraphSqlContract.CurrentName, Assert.Single(described.Rows)[4]);
        Assert.Equal(GraphSqlContract.LabelIndexPolicy, described.Rows[0][5]);
        Assert.Equal(GraphSqlContract.PropertyIndexPolicy, described.Rows[0][6]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static Dictionary<string, object?> ToPlan(SelectExecutionResult result)
        => result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);
}
