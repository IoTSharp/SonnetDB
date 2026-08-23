using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class GqlPhase3Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-gql-phase3-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Parse_PathQuery_ProducesSameTypedGraphSourceAsSqlPgq()
    {
        const string gql = """
            EXPLAIN ANALYZE USE GRAPH social
            MATCH p = ANY SHORTEST SIMPLE (a IS person)-[e IS knows]->{1,3}(b IS person)
            WHERE a.id = @anchor AND b.id = @target
            RETURN DISTINCT a.id AS source_id, b.id AS target_id, p.length AS hops
            ORDER BY hops
            LIMIT 1
            """;
        const string sql = """
            EXPLAIN ANALYZE SELECT DISTINCT source_id, target_id, hops
            FROM GRAPH_TABLE (
                social
                MATCH p = ANY SHORTEST SIMPLE (a IS person)-[e IS knows]->{1,3}(b IS person)
                WHERE a.id = @anchor AND b.id = @target
                COLUMNS (a.id AS source_id, b.id AS target_id, p.length AS hops)
            )
            ORDER BY hops
            LIMIT 1
            """;

        var gqlExplain = Assert.IsType<ExplainStatement>(GqlParser.Parse(gql));
        var sqlExplain = Assert.IsType<ExplainStatement>(SqlParser.Parse(sql));
        Assert.True(gqlExplain.Analyze);
        Assert.Equal(sqlExplain.Analyze, gqlExplain.Analyze);
        var gqlSelect = Assert.IsType<SelectStatement>(gqlExplain.Statement);
        var sqlSelect = Assert.IsType<SelectStatement>(sqlExplain.Statement);

        AssertSelectShapeEqual(sqlSelect, gqlSelect);
        AssertGraphSourceEqual(sqlSelect.GraphTable!, gqlSelect.GraphTable!);
    }

    [Fact]
    public void ExecuteGql_RelationalGraphParameters_MatchesSqlPlanAndResult()
    {
        Directory.CreateDirectory(_root);
        using Tsdb db = Open();
        CreateSocialPropertyGraph(db);
        var parameters = new SqlParameters()
            .AddNamed("anchor", 1L)
            .AddNamed("minimum", 2020L);
        const string gql = """
            USE GRAPH social
            MATCH (a IS person)-[e IS knows]->(b IS person)
            WHERE a.id = @anchor AND e.since >= @minimum
            RETURN a.id AS source_id, b.id AS target_id, e.id AS edge_id, e.since AS since
            ORDER BY edge_id DESC
            LIMIT 1
            """;
        const string sql = """
            SELECT source_id, target_id, edge_id, since
            FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS knows]->(b IS person)
                WHERE a.id = @anchor AND e.since >= @minimum
                COLUMNS (
                    a.id AS source_id,
                    b.id AS target_id,
                    e.id AS edge_id,
                    e.since AS since
                )
            )
            ORDER BY edge_id DESC
            LIMIT 1
            """;

        SelectExecutionResult gqlResult = SqlExecutor.ExecuteGql(db, gql, parameters);
        var sqlResult = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "fixture", sql, parameters));
        AssertResultsEqual(sqlResult, gqlResult);
        Assert.Equal(new object?[] { 1L, 3L, 11L, 2021L }, Assert.Single(gqlResult.Rows));

        SelectExecutionResult gqlPlan = SqlExecutor.ExecuteGql(db, "EXPLAIN " + gql, parameters);
        var sqlPlan = Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, "fixture", "EXPLAIN " + sql, parameters));
        AssertResultsEqual(sqlPlan, gqlPlan);
        var plan = gqlPlan.Rows.ToDictionary(static row => (string)row[0]!, static row => row[1]);
        Assert.Equal("relational_mapping", plan["graph_kind"]);
        Assert.Equal("relation_index_seek", plan["edge.knows.outgoing.access_path"]);
    }

    [Fact]
    public void ExecuteGql_NativeShortestPath_MatchesSqlPlanAndResult()
    {
        Directory.CreateDirectory(_root);
        using Tsdb db = Open();
        CreateNativeGraph(db);
        const string gql = """
            USE GRAPH topology
            MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{1,3}(b IS 1)
            WHERE a.id = 1
            RETURN p.end_id AS end_id, p.length AS hops
            ORDER BY end_id
            """;
        const string sql = """
            SELECT end_id, hops
            FROM GRAPH_TABLE (
                topology
                MATCH p = ANY SHORTEST SIMPLE (a IS 1)-[e IS 2]->{1,3}(b IS 1)
                WHERE a.id = 1
                COLUMNS (p.end_id AS end_id, p.length AS hops)
            )
            ORDER BY end_id
            """;

        SelectExecutionResult gqlResult = SqlExecutor.ExecuteGql(db, gql);
        var sqlResult = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
        AssertResultsEqual(sqlResult, gqlResult);
        Assert.Equal(
            new long[] { 2, 3, 4 },
            gqlResult.Rows.Select(static row => (long)row[0]!).ToArray());

        SelectExecutionResult gqlPlan = SqlExecutor.ExecuteGql(db, "EXPLAIN " + gql);
        var sqlPlan = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + sql));
        AssertResultsEqual(sqlPlan, gqlPlan);
        var plan = gqlPlan.Rows.ToDictionary(static row => (string)row[0]!, static row => row[1]);
        Assert.Equal("GraphShortestPath", plan["logical_plan"]);
        Assert.Equal("native_adjacency", plan["edge_access_path"]);
    }

    [Fact]
    public void Parse_UnsupportedOrUnsafeSyntax_RejectsOutsidePublishedSubset()
    {
        Assert.Throws<SqlParseException>(() => GqlParser.Parse("CREATE GRAPH hidden"));
        Assert.Throws<SqlParseException>(() => GqlParser.Parse("""
            USE GRAPH social
            MATCH (a IS person)-[e IS knows]->(b IS person)
            RETURN *
            """));
        Assert.Throws<SqlParseException>(() => GqlParser.Parse("""
            USE GRAPH social
            MATCH (a:person)-[e:knows]->(b:person)
            RETURN a.id
            """));
        Assert.Throws<SqlParseException>(() => GqlParser.Parse("""
            USE GRAPH social
            MATCH (a IS person)-[e IS knows]->(b IS person)
            RETURN a.id; DROP GRAPH social
            """));
        Assert.Throws<SqlParseException>(() => GqlParser.Parse("""
            USE GRAPH social
            MATCH (a IS person)-[e IS knows]->(b IS person)
            DELETE e
            """));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateSocialPropertyGraph(Tsdb db)
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
        _ = SqlExecutor.Execute(db, "INSERT INTO person (id, name) VALUES (1, 'Ada'), (2, 'Lin'), (3, 'Sam')");
        _ = SqlExecutor.Execute(db, """
            INSERT INTO knows (id, source_id, target_id, since)
            VALUES (10, 1, 2, 2020), (11, 1, 3, 2021), (12, 3, 1, 2022)
            """);
        _ = SqlExecutor.Execute(db, """
            CREATE PROPERTY GRAPH social
            VERTEX TABLES (
                person KEY (id) LABEL person PROPERTIES (id, name)
            )
            EDGE TABLES (
                knows KEY (id)
                    SOURCE KEY (source_id) REFERENCES person (id)
                    DESTINATION KEY (target_id) REFERENCES person (id)
                    LABEL knows PROPERTIES (id, since)
            )
            """);
    }

    private static void CreateNativeGraph(Tsdb db)
    {
        _ = SqlExecutor.Execute(db, "CREATE GRAPH topology");
        _ = SqlExecutor.Execute(db, """
            INSERT INTO GRAPH topology VERTEX (id, labels)
            VALUES (1, 1), (2, 1), (3, 1), (4, 1)
            """);
        _ = SqlExecutor.Execute(db, """
            INSERT INTO GRAPH topology EDGE (id, source_id, target_id, label_id)
            VALUES (10, 1, 2, 2), (11, 2, 3, 2), (12, 3, 4, 2), (13, 1, 4, 2)
            """);
    }

    private static void AssertSelectShapeEqual(SelectStatement expected, SelectStatement actual)
    {
        Assert.Equal(expected.Measurement, actual.Measurement);
        Assert.Equal(expected.Distinct, actual.Distinct);
        Assert.Equal(expected.Pagination, actual.Pagination);
        Assert.Equal(expected.Projections, actual.Projections);
        Assert.Equal(expected.OrderByList, actual.OrderByList);
    }

    private static void AssertGraphSourceEqual(GraphTableSource expected, GraphTableSource actual)
    {
        Assert.Equal(expected.GraphName, actual.GraphName);
        Assert.Equal(expected.LeftVertex, actual.LeftVertex);
        Assert.Equal(expected.Edge, actual.Edge);
        Assert.Equal(expected.RightVertex, actual.RightVertex);
        Assert.Equal(expected.Direction, actual.Direction);
        Assert.Equal(expected.Predicate, actual.Predicate);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Columns, actual.Columns);
    }

    private static void AssertResultsEqual(SelectExecutionResult expected, SelectExecutionResult actual)
    {
        Assert.Equal(expected.Columns, actual.Columns);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
            Assert.Equal(expected.Rows[i], actual.Rows[i]);
    }
}
