using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Graph;

/// <summary>
/// Graph Beta 本地正确性场景：覆盖原生 adjacency、SQL/PGQ 关系映射和共享 SQL JOIN。
/// 外部 PostgreSQL/Neo4j 语义对拍由 runner 明确记录为未运行。
/// </summary>
public sealed class GraphSqlPgqBetaScenario : IScenario
{
    /// <inheritdoc />
    public string Name => "graph_sql_pgq_beta";

    /// <inheritdoc />
    public Capability Required => Capability.Graph
        | Capability.GraphSqlPgq
        | Capability.GraphNativeTraversal
        | Capability.GraphCrossModelSql;

    /// <inheritdoc />
    public async Task<ScenarioResult> RunAsync(IDataPlane plane, ScenarioContext ctx)
    {
        if ((plane.Capabilities & Required) != Required)
        {
            return new ScenarioResult
            {
                Pass = true,
                GapReason = $"backend '{plane.BackendName}' lacks required capabilities: {Required & ~plane.Capabilities}",
            };
        }

        IRelationalOps sql = plane.Relational;
        CancellationToken ct = ctx.Cancellation;
        await sql.ExecuteAsync("CREATE GRAPH native_topology", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("INSERT INTO GRAPH native_topology VERTEX (id, labels) VALUES (1, 1), (2, 1)", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("INSERT INTO GRAPH native_topology EDGE (id, source_id, target_id, label_id) VALUES (10, 1, 2, 2)", ct).ConfigureAwait(false);
        RelationalSqlResult native = await sql.QueryAsync("""
            SELECT target_id FROM GRAPH_TABLE (
                native_topology
                MATCH (a IS 1)-[e IS 2]->(b IS 1)
                WHERE a.id = 1
                COLUMNS (b.id AS target_id)
            )
            """, ct).ConfigureAwait(false);

        await sql.ExecuteAsync("CREATE TABLE graph_person (id INT NOT NULL, name STRING, PRIMARY KEY (id))", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("CREATE TABLE graph_follows (id INT NOT NULL, source_id INT NOT NULL, target_id INT NOT NULL, PRIMARY KEY (id), FOREIGN KEY (source_id) REFERENCES graph_person (id), FOREIGN KEY (target_id) REFERENCES graph_person (id))", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("CREATE INDEX ix_graph_follows_source ON graph_follows (source_id)", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("CREATE TABLE graph_status (id INT NOT NULL, status STRING, PRIMARY KEY (id))", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("INSERT INTO graph_person (id, name) VALUES (1, 'Ada'), (2, 'Lin')", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("INSERT INTO graph_follows (id, source_id, target_id) VALUES (10, 1, 2)", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("INSERT INTO graph_status (id, status) VALUES (2, 'active')", ct).ConfigureAwait(false);
        await sql.ExecuteAsync("""
            CREATE PROPERTY GRAPH mapped_social
            VERTEX TABLES (graph_person KEY (id) LABEL person PROPERTIES (id, name))
            EDGE TABLES (
                graph_follows KEY (id)
                    SOURCE KEY (source_id) REFERENCES graph_person (id)
                    DESTINATION KEY (target_id) REFERENCES graph_person (id)
                    LABEL follows PROPERTIES (id)
            )
            """, ct).ConfigureAwait(false);

        const string composedSql = """
            SELECT g.target_id AS target_id, s.status AS status
            FROM (
                SELECT target_id FROM GRAPH_TABLE (
                    mapped_social
                    MATCH (a IS person)-[e IS follows]->(b IS person)
                    WHERE a.id = 1
                    COLUMNS (b.id AS target_id)
                )
            ) AS g
            JOIN graph_status AS s ON g.target_id = s.id
            """;
        RelationalSqlResult composed = await sql.QueryAsync(composedSql, ct).ConfigureAwait(false);
        RelationalSqlResult explain = await sql.QueryAsync("EXPLAIN " + composedSql, ct).ConfigureAwait(false);
        string? accessPath = explain.Rows
            .FirstOrDefault(static row => row.Values.Count > 1 && Equals(row.Values[0], "access_path"))
            ?.Values[1]?.ToString();
        bool pass = native.Rows.Count == 1
            && Convert.ToInt64(native.Rows[0].Values[0]) == 2L
            && composed.Rows.Count == 1
            && Convert.ToInt64(composed.Rows[0].Values[0]) == 2L
            && string.Equals(Convert.ToString(composed.Rows[0].Values[1]), "active", StringComparison.Ordinal)
            && accessPath?.Contains("graph_table", StringComparison.Ordinal) == true;

        var result = new ScenarioResult
        {
            Pass = pass,
            SqlResult = composed,
        };
        result.Metrics["native_rows"] = native.Rows.Count;
        result.Metrics["composed_rows"] = composed.Rows.Count;
        result.Metrics["access_path"] = accessPath;
        result.Metrics["external_postgresql"] = "not_run";
        result.Metrics["external_neo4j"] = "not_run";
        return result;
    }
}
