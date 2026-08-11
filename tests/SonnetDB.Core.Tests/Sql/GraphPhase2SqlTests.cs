using SonnetDB.Engine;
using SonnetDB.Graphs;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class GraphPhase2SqlTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-phase2-sql-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parse_GraphDdlAndTableFunctions_ReturnsTypedAst()
    {
        var create = Assert.IsType<CreateGraphStatement>(
            SqlParser.Parse("CREATE GRAPH IF NOT EXISTS topology"));
        Assert.Equal("topology", create.Name);
        Assert.True(create.IfNotExists);

        var drop = Assert.IsType<DropGraphStatement>(
            SqlParser.Parse("DROP GRAPH IF EXISTS topology"));
        Assert.True(drop.IfExists);
        Assert.IsType<ShowGraphsStatement>(SqlParser.Parse("SHOW GRAPHS"));
        Assert.Equal("topology", Assert.IsType<DescribeGraphStatement>(
            SqlParser.Parse("DESCRIBE GRAPH topology")).Name);

        var insert = Assert.IsType<InsertGraphStatement>(
            SqlParser.Parse("INSERT INTO GRAPH topology VERTEX (id, labels) VALUES (?, '1,3')"));
        Assert.Equal(GraphMutationKind.Vertex, insert.Kind);
        Assert.Equal(2, insert.Columns.Count);

        var select = Assert.IsType<SelectStatement>(
            SqlParser.Parse("SELECT id FROM graph_nodes(@graph, @label) WHERE id >= @minimum LIMIT @take"));
        Assert.Equal("__graph__", select.Measurement);
        Assert.Equal("graph_nodes", select.TableValuedFunction!.Name);
    }

    [Fact]
    public void ParseInsert_TableNamedGraph_ReturnsRelationalInsert()
    {
        var insert = Assert.IsType<InsertStatement>(
            SqlParser.Parse("INSERT INTO graph (id) VALUES (1)"));

        Assert.Equal("graph", insert.Measurement);
    }

    [Fact]
    public void ParseGraphTable_VariableAndShortestPath_ReturnsTypedPathPattern()
    {
        var variable = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT start_id, end_id, hops
            FROM GRAPH_TABLE (
                topology
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{2,4}(b IS 1)
                WHERE a.id = @anchor
                COLUMNS (a.id AS start_id, b.id AS end_id, p.length AS hops)
            )
            """));
        GraphPathPattern path = variable.GraphTable!.Path!;
        Assert.Equal("p", path.Variable);
        Assert.Equal(2, path.MinDepth);
        Assert.Equal(4, path.MaxDepth);
        Assert.Equal(GraphPathUniqueness.Edge, path.Uniqueness);
        Assert.False(path.IsAnyShortest);

        var shortest = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT p.vertex_ids
            FROM GRAPH_TABLE (
                topology
                MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{1,6}(b IS 1)
                COLUMNS (p.vertex_ids AS vertex_ids)
            )
            """));
        Assert.True(shortest.GraphTable!.Path!.IsAnyShortest);
        Assert.Equal(GraphPathUniqueness.Vertex, shortest.GraphTable.Path.Uniqueness);

        Assert.Throws<SqlParseException>(() => SqlParser.Parse("""
            SELECT * FROM GRAPH_TABLE (
                topology
                MATCH (a IS 1)-[e IS 2]->{1,65}(b IS 1)
                COLUMNS (a.id AS id)
            )
            """));

        var analyzed = Assert.IsType<ExplainStatement>(SqlParser.Parse("""
            EXPLAIN ANALYZE SELECT end_id FROM GRAPH_TABLE (
                topology
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{1,2}(b IS 1)
                COLUMNS (b.id AS end_id)
            )
            """));
        Assert.True(analyzed.Analyze);
    }

    [Fact]
    public void ExecuteGraphTable_InvalidTypedPathAst_IsRejectedBeforeGraphAccess()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT end_id FROM GRAPH_TABLE (
                topology
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{1,4}(b IS 1)
                COLUMNS (b.id AS end_id)
            )
            """));
        GraphTableSource source = statement.GraphTable!;
        GraphPathPattern path = source.Path!;

        Assert.Throws<ArgumentOutOfRangeException>(() => SqlExecutor.ExecuteStatement(
            db,
            statement with { GraphTable = source with { Path = path with { MaxDepth = 65 } } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlExecutor.ExecuteStatement(
            db,
            statement with
            {
                GraphTable = source with
                {
                    Path = path with { Uniqueness = (GraphPathUniqueness)byte.MaxValue },
                },
            }));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteStatement(
            db,
            statement with { GraphTable = source with { Path = path with { Variable = "a" } } }));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteStatement(
            db,
            statement with { GraphTable = source with { RightVertex = source.LeftVertex } }));
    }

    [Fact]
    public void GraphLogicalPlan_NativeApiAndPlanExecutor_ReturnSameRows()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        GraphStore store = db.Graphs.Create("topology");
        Seed(store);
        using GraphReadSession read = store.BeginRead();

        long[] native = ReadAll(read.SeekVerticesByLabel(new LabelId(1)))
            .Select(static vertex => vertex.Id.Value)
            .ToArray();
        long[] planned = ReadAll(GraphPlanExecutor.Execute(
                read,
                new GraphNodeScanPlan(new LabelId(1))))
            .Select(static vertex => vertex.Id.Value)
            .ToArray();
        long[] allEdges = ReadAll(GraphPlanExecutor.Execute(read, new GraphEdgeScanPlan()))
            .Select(static edge => edge.Id.Value)
            .ToArray();

        Assert.Equal(native, planned);
        Assert.Equal([10L, 11L], allEdges);
    }

    [Fact]
    public void Execute_GraphDdlParameterizedReadAndExplain_UseNativeGraphPlan()
    {
        Directory.CreateDirectory(_root);
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var created = Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(db, "CREATE GRAPH IF NOT EXISTS topology"));
            Assert.Equal(1, created.RowsAffected);
            Assert.Equal(0, Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(db, "CREATE GRAPH IF NOT EXISTS topology")).RowsAffected);
            Seed(db.Graphs.Open("topology"));
            Assert.Equal(2, Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
                db,
                "INSERT INTO GRAPH topology VERTEX (id, labels) VALUES (4, 1), (5, '1,3')")).RowsAffected);
            Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
                db,
                "INSERT INTO GRAPH topology EDGE (id, source_id, target_id, label_id) VALUES (12, 4, 5, 2)")).RowsAffected);

            var parameters = new SqlParameters()
                .AddNamed("graph", "topology")
                .AddNamed("label", 1)
                .AddNamed("minimum", 2)
                .AddNamed("take", 1);
            var nodes = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db,
                databaseName: "fixture",
                sql: "SELECT id, labels, property_count FROM graph_nodes(@graph, @label) WHERE id >= @minimum LIMIT @take",
                parameters,
                controlPlane: null));
            Assert.Equal(["id", "labels", "property_count"], nodes.Columns);
            Assert.Equal(2L, Assert.Single(nodes.Rows)[0]);

            var edges = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db,
                "SELECT id, source_id, target_id, label_id FROM graph_edges('topology', 2) WHERE source_id = 1"));
            Assert.Equal(2, edges.Rows.Count);

            var ordered = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db,
                "SELECT id FROM graph_nodes('topology') ORDER BY id DESC LIMIT 2"));
            Assert.Equal([5L, 4L], ordered.Rows.Select(static row => (long)row[0]!).ToArray());

            var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db,
                "EXPLAIN SELECT * FROM graph_edges('topology', 2)"));
            var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
            Assert.Equal("GraphEdgeScan", plan["logical_plan"]);
            Assert.Equal("native_adjacency_or_index", plan["access_path"]);

            var shown = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW GRAPHS"));
            Assert.Equal("topology", Assert.Single(shown.Rows)[0]);
            Assert.Equal("topology", Assert.Single(Assert.IsType<SelectExecutionResult>(
                SqlExecutor.Execute(db, "DESCRIBE GRAPH topology")).Rows)[0]);
        }

        using (var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            var nodes = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                reopened,
                "SELECT id FROM graph_nodes('topology') WHERE id = 3"));
            Assert.Equal(3L, Assert.Single(nodes.Rows)[0]);
            Assert.Equal(1, Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(reopened, "DROP GRAPH topology")).RowsAffected);
            Assert.Equal(0, Assert.IsType<RowsAffectedExecutionResult>(
                SqlExecutor.Execute(reopened, "DROP GRAPH IF EXISTS topology")).RowsAffected);
        }
    }

    [Fact]
    public void Execute_GraphParameterizedInsert_BindsVertexAndEdgeValues()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");

        var vertexParameters = new SqlParameters()
            .AddNamed("first_id", 1L)
            .AddNamed("first_labels", "1,3")
            .AddNamed("second_id", 2L)
            .AddNamed("second_labels", 1);
        var vertices = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: "fixture",
            sql: "INSERT INTO GRAPH topology VERTEX (id, labels) VALUES (@first_id, @first_labels), (@second_id, @second_labels)",
            vertexParameters,
            controlPlane: null));
        Assert.Equal(2, vertices.RowsAffected);

        var edges = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: "fixture",
            sql: "INSERT INTO GRAPH topology EDGE (id, source_id, target_id, label_id) VALUES (?, ?, ?, ?)",
            new SqlParameters().AddPositional(10L).AddPositional(1L).AddPositional(2L).AddPositional(4),
            controlPlane: null));
        Assert.Equal(1, edges.RowsAffected);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "SELECT id FROM graph_edges('topology', 4)"));
        Assert.Equal(10L, Assert.Single(selected.Rows)[0]);
    }

    [Fact]
    public void Execute_GraphInsertInvalidShape_ReturnsStableErrors()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");

        var duplicate = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "INSERT INTO GRAPH topology VERTEX (id, id, labels) VALUES (1, 1, 2)"));
        Assert.Contains("重复声明", duplicate.Message, StringComparison.Ordinal);

        var unsupported = Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(
            db,
            "INSERT INTO GRAPH topology VERTEX (id, labels, property) VALUES (1, 2, 'value')"));
        Assert.Contains("当前不支持列 'property'", unsupported.Message, StringComparison.Ordinal);

        var overflow = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db,
            "INSERT INTO GRAPH topology EDGE (id, source_id, target_id, label_id) VALUES (1, 1, 2, 2147483648)"));
        Assert.Contains("Int32 范围", overflow.Message, StringComparison.Ordinal);

        var valid = Assert.IsType<InsertGraphStatement>(SqlParser.Parse(
            "INSERT INTO GRAPH topology VERTEX (id, labels) VALUES (1, 2)"));
        var invalidKind = Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteStatement(
            db,
            valid with { Kind = (GraphMutationKind)byte.MaxValue }));
        Assert.Contains("mutation kind", invalidKind.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecuteGraphTable_NativeFixedPattern_UsesAdjacencyPlan()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        GraphStore store = db.Graphs.Create("topology");
        Seed(store);

        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse("""
            SELECT source_id, target_id, edge_id, target_name
            FROM GRAPH_TABLE (
                topology
                MATCH (a IS 1)-[e IS 2]->(b IS 1)
                WHERE a.id = @anchor AND b.property_7 = @name
                COLUMNS (
                    a.id AS source_id,
                    b.id AS target_id,
                    e.id AS edge_id,
                    b.property_7 AS target_name
                )
            )
            """));
        Assert.NotNull(statement.GraphTable);
        Assert.Equal(GraphDirection.Outgoing, statement.GraphTable.Direction);

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "fixture",
            """
                SELECT source_id, target_id, edge_id, target_name
                FROM GRAPH_TABLE (
                    topology
                    MATCH (a IS 1)-[e IS 2]->(b IS 1)
                    WHERE a.id = @anchor AND b.property_7 = @name
                    COLUMNS (
                        a.id AS source_id,
                        b.id AS target_id,
                        e.id AS edge_id,
                        b.property_7 AS target_name
                    )
                )
                """,
            new SqlParameters().AddNamed("anchor", 1L).AddNamed("name", "pump")));
        Assert.Equal(new object?[] { 1L, 2L, 10L, "pump" }, Assert.Single(result.Rows));

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT *
            FROM GRAPH_TABLE (
                topology
                MATCH (a IS 1)-[e IS 2]->(b IS 1)
                WHERE a.id = 1
                COLUMNS (a.id AS source_id, b.id AS target_id, e.id AS edge_id)
            )
            """));
        var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal("native", plan["graph_kind"]);
        Assert.Equal("native_vertex_id_seek", plan["anchor_access_path"]);
        Assert.Equal("native_adjacency", plan["edge_access_path"]);
    }

    [Fact]
    public void ExecuteGraphTable_NativeVariableTrailWalkAndShortestPath_ReusePathPlan()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        GraphStore store = db.Graphs.Create("paths");
        SeedPaths(store);

        var trails = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT start_id, end_id, hops, vertex_ids, edge_ids
            FROM GRAPH_TABLE (
                paths
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{2,3}(b IS 1)
                WHERE a.id = 1
                COLUMNS (
                    a.id AS start_id,
                    b.id AS end_id,
                    p.length AS hops,
                    p.vertex_ids AS vertex_ids,
                    p.edge_ids AS edge_ids
                )
            )
            ORDER BY hops, end_id
            """));
        Assert.Equal(
            [
                new object?[] { 1L, 3L, 2L, "1,2,3", "10,11" },
                new object?[] { 1L, 4L, 2L, "1,2,4", "10,13" },
                new object?[] { 1L, 1L, 3L, "1,2,3,1", "10,11,12" },
            ],
            trails.Rows);

        var walk = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT edge_ids FROM GRAPH_TABLE (
                paths
                MATCH p = WALK (a IS 1)-[e IS 2]->{4,4}(b IS 1)
                WHERE a.id = 1 AND b.id = 2
                COLUMNS (p.edge_ids AS edge_ids)
            )
            """));
        Assert.Contains(walk.Rows, static row => Equals(row[0], "10,11,12,10"));
        var trailAtFour = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT edge_ids FROM GRAPH_TABLE (
                paths
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{4,4}(b IS 1)
                WHERE a.id = 1 AND b.id = 2
                COLUMNS (p.edge_ids AS edge_ids)
            )
            """));
        Assert.Empty(trailAtFour.Rows);

        var shortest = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            "fixture",
            """
                SELECT end_id, hops, vertex_ids FROM GRAPH_TABLE (
                    paths
                    MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{1,4}(b IS 1)
                    WHERE a.id = @start AND b.id = @target
                    COLUMNS (b.id AS end_id, p.length AS hops, p.vertex_ids AS vertex_ids)
                )
                """,
            new SqlParameters().AddNamed("start", 1L).AddNamed("target", 4L)));
        Assert.Equal(new object?[] { 4L, 1L, "1,4" }, Assert.Single(shortest.Rows));

        var boundedShortest = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT end_id, hops, vertex_ids FROM GRAPH_TABLE (
                paths
                MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{2,4}(b IS 1)
                WHERE a.id = 1 AND b.id = 4
                COLUMNS (b.id AS end_id, p.length AS hops, p.vertex_ids AS vertex_ids)
            )
            """));
        Assert.Equal(new object?[] { 4L, 2L, "1,2,4" }, Assert.Single(boundedShortest.Rows));

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT * FROM GRAPH_TABLE (
                paths
                MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{1,4}(b IS 1)
                WHERE a.id = 1
                COLUMNS (p.length AS hops)
            )
            """));
        var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal("GraphShortestPath", plan["logical_plan"]);
        Assert.Equal("breadth_first", plan["path_search_mode"]);
        Assert.Equal(4, plan["path_max_depth"]);
        Assert.Equal("native_adjacency", plan["edge_access_path"]);
    }

    [Fact]
    public void GraphTableCostPlanner_RightAnchorPreservesPathDirectionAndAnalyzeReportsActuals()
    {
        Directory.CreateDirectory(_root);
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        GraphStore store = db.Graphs.Create("cost_paths");
        SeedPaths(store);

        var reversed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            SELECT start_id, end_id, vertex_ids
            FROM GRAPH_TABLE (
                cost_paths
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{1,2}(b IS 1)
                WHERE b.id = 4
                COLUMNS (a.id AS start_id, b.id AS end_id, p.vertex_ids AS vertex_ids)
            )
            """));
        Assert.NotEmpty(reversed.Rows);
        Assert.All(reversed.Rows, static row =>
        {
            string[] vertices = Assert.IsType<string>(row[2]).Split(',');
            Assert.Equal((long)row[0]!, long.Parse(vertices[0], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal((long)row[1]!, long.Parse(vertices[^1], System.Globalization.CultureInfo.InvariantCulture));
        });

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN SELECT * FROM GRAPH_TABLE (
                cost_paths
                MATCH p = TRAIL (a IS 1)-[e IS 2]->{1,2}(b IS 1)
                WHERE b.id = 4
                COLUMNS (a.id AS start_id, b.id AS end_id, p.vertex_ids AS vertex_ids)
            )
            """));
        var plan = explain.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal("graph_cost_v1", plan["planner"]);
        Assert.Equal("right", plan["anchor_side"]);
        Assert.Equal("b", plan["anchor_variable"]);
        Assert.Equal("incoming", plan["execution_direction"]);
        Assert.Equal(1L, plan["estimated_anchor_rows"]);

        var analyzed = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, """
            EXPLAIN ANALYZE SELECT end_id, hops FROM GRAPH_TABLE (
                cost_paths
                MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{1,4}(b IS 1)
                WHERE a.id = 1 AND b.id = 4
                COLUMNS (b.id AS end_id, p.length AS hops)
            )
            """));
        var actual = analyzed.Rows.ToDictionary(row => (string)row[0]!, row => row[1]);
        Assert.Equal(true, actual["analyze"]);
        Assert.Equal(1L, actual["actual_rows"]);
        Assert.Equal(1L, actual["actual_anchor_rows"]);
        Assert.True((long)actual["actual_expansions"]! > 0);
        Assert.True((long)actual["actual_generated_paths"]! > 0);
        Assert.True((int)actual["actual_peak_frontier"]! > 0);
        Assert.Equal(false, actual["bidirectional_bfs_admitted"]);
        Assert.Equal("benchmark_evidence_missing", actual["bidirectional_bfs_reason"]);
        Assert.True((double)actual["actual_elapsed_ms"]! >= 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void Seed(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(new GraphElementId(1), 0, [new LabelId(1)], []);
        transaction.UpsertVertex(new GraphElementId(2), 0, [new LabelId(1)], [
            new GraphProperty(7, GraphPropertyValue.FromString("pump")),
        ]);
        transaction.UpsertVertex(new GraphElementId(3), 0, [new LabelId(3)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(1), new GraphElementId(3), new LabelId(2), []);
        transaction.Commit();
    }

    private static void SeedPaths(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        for (int id = 1; id <= 4; id++)
            transaction.UpsertVertex(new GraphElementId(id), 0, [new LabelId(1)], []);
        transaction.UpsertEdge(new GraphElementId(10), 0, new GraphElementId(1), new GraphElementId(2), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(11), 0, new GraphElementId(2), new GraphElementId(3), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(12), 0, new GraphElementId(3), new GraphElementId(1), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(13), 0, new GraphElementId(2), new GraphElementId(4), new LabelId(2), []);
        transaction.UpsertEdge(new GraphElementId(14), 0, new GraphElementId(1), new GraphElementId(4), new LabelId(2), []);
        transaction.Commit();
    }

    private static T[] ReadAll<T>(GraphCursor<T> cursor) where T : class
    {
        using (cursor)
        {
            var rows = new List<T>();
            while (true)
            {
                IReadOnlyList<T> page = cursor.ReadNextPage();
                if (page.Count == 0)
                    return rows.ToArray();
                rows.AddRange(page);
            }
        }
    }
}
