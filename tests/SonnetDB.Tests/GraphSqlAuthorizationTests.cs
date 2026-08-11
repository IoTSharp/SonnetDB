using SonnetDB.Endpoints;
using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Tests;

public sealed class GraphSqlAuthorizationTests
{
    [Theory]
    [InlineData("SHOW GRAPHS")]
    [InlineData("DESCRIBE GRAPH topology")]
    [InlineData("SHOW PROPERTY GRAPHS")]
    [InlineData("DESCRIBE PROPERTY GRAPH social")]
    [InlineData("EXPLAIN DESCRIBE PROPERTY GRAPH social")]
    [InlineData("SELECT id FROM graph_nodes('topology')")]
    [InlineData("SELECT id FROM GRAPH_TABLE (topology MATCH (a IS 1)-[e IS 2]->(b IS 1) COLUMNS (a.id AS id))")]
    public void RequiresWritePermission_GraphReads_ReturnsFalse(string sql)
        => Assert.False(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));

    [Theory]
    [InlineData("CREATE GRAPH topology")]
    [InlineData("DROP GRAPH topology")]
    [InlineData("CREATE PROPERTY GRAPH social VERTEX TABLES (person KEY (id) LABEL person PROPERTIES (id))")]
    [InlineData("DROP PROPERTY GRAPH social")]
    [InlineData("INSERT INTO GRAPH topology VERTEX (id, labels) VALUES (1, 1)")]
    public void RequiresWritePermission_GraphMutations_ReturnsTrue(string sql)
        => Assert.True(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));
}
