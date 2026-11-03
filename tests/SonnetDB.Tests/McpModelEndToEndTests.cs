using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SonnetDB.Auth;
using SonnetDB.Graphs;
using SonnetDB.Hosting;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class McpEndToEndTests
{
    private static async Task<JsonElement> ModelCallAsync(McpClient client, string name, params (string Key, object? Value)[] args)
    {
        var result = await client.CallToolAsync(name, args.ToDictionary(item => item.Key, item => item.Value));
        Assert.False(result.IsError.GetValueOrDefault(), string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text)));
        var structured = result.StructuredContent!.Value;
        Assert.Equal("1.0", structured.GetProperty("contractVersion").GetString());
        return structured;
    }

    private static void AssertModelError(CallToolResult result, string code)
    {
        Assert.True(result.IsError.GetValueOrDefault());
        Assert.False(result.StructuredContent.HasValue);
        using var error = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[1]).Text);
        Assert.Equal(code, error.RootElement.GetProperty("code").GetString());
        Assert.False(error.RootElement.GetProperty("retryable").GetBoolean());
    }

    [Fact]
    public async Task KvTools_WriteReadScanDelete_PreserveBinaryVersionAndTtl()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        var bytes = new byte[] { 0, 255, 1, 10 };
        var expires = DateTimeOffset.UtcNow.AddHours(1);
        var written = await ModelCallAsync(admin, "put_kv", ("keyspace", "settings"), ("key", "device:1"),
            ("valueBase64", Convert.ToBase64String(bytes)), ("expiresAtUtc", expires));
        var value = await ModelCallAsync(reader, "get_kv", ("keyspace", "settings"), ("key", "device:1"));
        Assert.True(value.GetProperty("found").GetBoolean());
        Assert.Equal(bytes, Convert.FromBase64String(value.GetProperty("valueBase64").GetString()!));
        Assert.Equal(written.GetProperty("version").GetInt64(), value.GetProperty("version").GetInt64());
        Assert.Equal(expires.ToUnixTimeSeconds(), value.GetProperty("expiresAtUtc").GetDateTimeOffset().ToUnixTimeSeconds());
        var names = await ModelCallAsync(reader, "list_keyspaces");
        Assert.Contains(names.GetProperty("names").EnumerateArray(), item => item.GetString() == "settings");
        var scan = await ModelCallAsync(reader, "scan_kv", ("keyspace", "settings"), ("prefix", "device:"), ("maxRows", 1));
        Assert.Single(scan.GetProperty("entries").EnumerateArray());
        await ModelCallAsync(admin, "delete_kv", ("keyspace", "settings"), ("key", "device:1"));
        var missing = await ModelCallAsync(reader, "get_kv", ("keyspace", "settings"), ("key", "device:1"));
        Assert.False(missing.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task KvTools_MissingReadAndOversizedWrite_DoNotCreateKeyspace()
    {
        await using var reader = await CreateMcpClientAsync();
        await using var admin = await CreateMcpClientAsync(_adminToken);
        var absent = await reader.CallToolAsync("get_kv", new Dictionary<string, object?> { ["keyspace"] = "missing", ["key"] = "x" });
        AssertModelError(absent, "operation_failed");
        var oversized = await admin.CallToolAsync("put_kv", new Dictionary<string, object?>
        {
            ["keyspace"] = "too_large",
            ["key"] = "x",
            ["valueBase64"] = Convert.ToBase64String(new byte[65537]),
        });
        AssertModelError(oversized, "invalid_argument");
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet(_dbName, out var db));
        Assert.DoesNotContain("missing", db.Keyspaces.List());
        Assert.DoesNotContain("too_large", db.Keyspaces.List());
    }

    [Fact]
    public async Task ObjectTools_WriteRangePageAndDelete_KeepVersionAndCursorContracts()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        await ModelCallAsync(admin, "create_bucket", ("bucket", "samples"));
        var bytes = Encoding.UTF8.GetBytes("hello object");
        var written = await ModelCallAsync(admin, "put_object", ("bucket", "samples"), ("key", "a"),
            ("valueBase64", Convert.ToBase64String(bytes)));
        await ModelCallAsync(admin, "put_object", ("bucket", "samples"), ("key", "b"), ("valueBase64", ""));
        var range = await ModelCallAsync(reader, "get_object", ("bucket", "samples"), ("key", "a"), ("offset", 1), ("maxBytes", 3));
        Assert.Equal(bytes[1..4], Convert.FromBase64String(range.GetProperty("valueBase64").GetString()!));
        Assert.True(range.GetProperty("truncated").GetBoolean());
        Assert.Equal(written.GetProperty("versionId").GetString(), range.GetProperty("versionId").GetString());
        var page = await ModelCallAsync(reader, "list_objects", ("bucket", "samples"), ("maxRows", 1));
        Assert.True(page.GetProperty("truncated").GetBoolean());
        var next = await ModelCallAsync(reader, "list_objects", ("bucket", "samples"), ("maxRows", 1),
            ("continuationToken", page.GetProperty("nextCursor").GetString()));
        Assert.NotEqual(page.GetProperty("objects")[0].GetProperty("key").GetString(), next.GetProperty("objects")[0].GetProperty("key").GetString());
        await ModelCallAsync(admin, "delete_object", ("bucket", "samples"), ("key", "a"));
        var missing = await ModelCallAsync(reader, "get_object", ("bucket", "samples"), ("key", "a"));
        Assert.False(missing.GetProperty("found").GetBoolean());
        var buckets = await ModelCallAsync(reader, "list_buckets");
        Assert.Contains(buckets.GetProperty("names").EnumerateArray(), item => item.GetString() == "samples");
    }

    [Fact]
    public async Task MqTools_ScopeAndPeek_DoNotLeakOtherDatabasesOrAdvanceGroups()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        var mq = _app!.Services.GetRequiredService<SonnetMqStore>();
        mq.Publish(_hiddenDbName + ".secret", [1]);
        mq.Publish("legacy_unscoped", [1]);
        await ModelCallAsync(admin, "publish_mq", ("topic", "events"), ("payloadBase64", "AAE="));
        await ModelCallAsync(admin, "publish_mq", ("topic", "events"), ("payloadBase64", "AgM="));
        var names = await ModelCallAsync(reader, "list_mq_topics");
        Assert.Equal("instance", names.GetProperty("storageScope").GetString());
        Assert.Equal(new[] { "events" }, names.GetProperty("names").EnumerateArray().Select(item => item.GetString()));
        var messages = await ModelCallAsync(reader, "read_mq", ("topic", "events"), ("maxRows", 1));
        Assert.True(messages.GetProperty("truncated").GetBoolean());
        Assert.Equal("AAE=", messages.GetProperty("messages")[0].GetProperty("payloadBase64").GetString());
        var stats = await ModelCallAsync(reader, "get_mq_stats", ("topic", "events"));
        Assert.Empty(stats.GetProperty("consumerOffsets").EnumerateObject());
        var later = await ModelCallAsync(reader, "read_mq", ("topic", "events"), ("offset", messages.GetProperty("nextOffset").GetInt64()));
        Assert.Equal("AgM=", later.GetProperty("messages")[0].GetProperty("payloadBase64").GetString());
        await ModelCallAsync(admin, "ack_mq", ("topic", "events"), ("consumerGroup", "workers"), ("offset", 0));
        Assert.Equal(1, mq.GetStats(_dbName + ".events").ConsumerOffsets["workers"]);
        var hidden = await ModelCallAsync(reader, "read_mq", ("topic", _hiddenDbName + ".secret"));
        Assert.Empty(hidden.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task DocumentSearchTools_WriteIndexSearchAndDelete_UseRealData()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE DOCUMENT COLLECTION notes"));
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE FULLTEXT INDEX notes_text ON notes ('$.body') USING unicode"));
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE VECTOR INDEX notes_vector ON notes ('$.embedding') WITH (dimensions=3, metric='l2')"));
        var schema = await ModelCallAsync(reader, "describe_collection", ("collection", "notes"));
        Assert.Equal(2, schema.GetProperty("indexes").GetArrayLength());
        Assert.Contains(schema.GetProperty("indexes").EnumerateArray(), item =>
            item.GetProperty("kind").GetString() == "vector" && item.GetProperty("dimensions").GetInt32() == 3);
        await ModelCallAsync(admin, "put_document", ("collection", "notes"), ("id", "a"),
            ("json", """{"body":"pump alarm","embedding":[0,0,0]}"""));
        await ModelCallAsync(admin, "put_document", ("collection", "notes"), ("id", "b"),
            ("json", """{"body":"motor running","embedding":[1,1,1]}"""));
        var doc = await ModelCallAsync(reader, "get_document", ("collection", "notes"), ("id", "a"));
        using var json = JsonDocument.Parse(doc.GetProperty("documents")[0].GetProperty("json").GetString()!);
        Assert.Equal("pump alarm", json.RootElement.GetProperty("body").GetString());
        var scan = await ModelCallAsync(reader, "scan_documents", ("collection", "notes"), ("maxRows", 1));
        Assert.True(scan.GetProperty("truncated").GetBoolean());
        var next = await ModelCallAsync(reader, "scan_documents", ("collection", "notes"), ("afterId", scan.GetProperty("nextCursor").GetString()));
        Assert.Equal("b", next.GetProperty("documents")[0].GetProperty("id").GetString());
        var text = await ModelCallAsync(reader, "search_fulltext", ("collection", "notes"), ("index", "notes_text"), ("path", "$.body"), ("query", "pump"));
        Assert.Equal("a", text.GetProperty("rows")[0][0].GetProperty("stringValue").GetString());
        var vector = await ModelCallAsync(reader, "search_vector", ("collection", "notes"), ("path", "$.embedding"), ("vector", new float[] { 0, 0, 0 }), ("metric", "l2"), ("k", 1));
        Assert.Equal("a", vector.GetProperty("rows")[0][0].GetProperty("stringValue").GetString());
        await ModelCallAsync(admin, "delete_document", ("collection", "notes"), ("id", "a"));
        var empty = await ModelCallAsync(reader, "get_document", ("collection", "notes"), ("id", "a"));
        Assert.Empty(empty.GetProperty("documents").EnumerateArray());
    }

    [Fact]
    public async Task SqlTools_WriteDataAndRejectControlPlaneBatchAndReturning()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE TABLE Devices (Id INT, Name STRING, PRIMARY KEY (Id))"));
        await ModelCallAsync(admin, "execute_sql", ("sql", "INSERT INTO Devices (Id, Name) VALUES (1, 'pump')"));
        await ModelCallAsync(admin, "execute_sql", ("sql", "INSERT INTO cpu (time, host, usage, temp) VALUES (4000, 'h2', 0.1, 20)"));
        var tables = await ModelCallAsync(reader, "list_tables");
        Assert.Contains(tables.GetProperty("names").EnumerateArray(), item => item.GetString() == "Devices");
        var result = await ModelCallAsync(reader, "query_sql", ("sql", "SELECT Name FROM Devices"));
        Assert.Equal("pump", result.GetProperty("rows")[0][0].GetProperty("stringValue").GetString());
        foreach (var sql in new[] { "CREATE USER blocked WITH PASSWORD 'p'", "INSERT INTO Devices (Id, Name) VALUES (2, 'bad') RETURNING Id", "DELETE FROM Devices; CREATE USER blocked WITH PASSWORD 'p'" })
        {
            var rejected = await admin.CallToolAsync("execute_sql", new Dictionary<string, object?> { ["sql"] = sql });
            Assert.True(rejected.IsError.GetValueOrDefault());
        }
        var unchanged = await ModelCallAsync(reader, "query_sql", ("sql", "SELECT Id FROM Devices"));
        Assert.Single(unchanged.GetProperty("rows").EnumerateArray());
    }

    [Fact]
    public async Task GraphTools_VersionedWriteExpandAndDelete_RespectNativeGraphContract()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE GRAPH topology"));
        await ModelCallAsync(admin, "upsert_graph_vertex", ("graph", "topology"),
            ("request", new GraphUpsertVertexRequest { Id = 1, Labels = [10], RequestId = Guid.NewGuid() }));
        await ModelCallAsync(admin, "upsert_graph_vertex", ("graph", "topology"),
            ("request", new GraphUpsertVertexRequest { Id = 2, Labels = [10], RequestId = Guid.NewGuid() }));
        await ModelCallAsync(admin, "upsert_graph_edge", ("graph", "topology"),
            ("request", new GraphUpsertEdgeRequest { Id = 3, SourceId = 1, TargetId = 2, LabelId = 20, RequestId = Guid.NewGuid() }));
        var vertex = await ModelCallAsync(reader, "get_graph_vertex", ("graph", "topology"), ("vertexId", 1));
        Assert.True(vertex.GetProperty("found").GetBoolean());
        var edges = await ModelCallAsync(reader, "expand_graph", ("graph", "topology"), ("vertexId", 1), ("maxRows", 1));
        Assert.Equal(2, edges.GetProperty("edges")[0].GetProperty("neighborId").GetInt64());
        await ModelCallAsync(admin, "delete_graph_edge", ("graph", "topology"), ("id", 3),
            ("expectedElementVersion", edges.GetProperty("edges")[0].GetProperty("edge").GetProperty("elementVersion").GetInt64()), ("requestId", Guid.NewGuid()));
        await ModelCallAsync(admin, "delete_graph_vertex", ("graph", "topology"), ("id", 1),
            ("expectedElementVersion", vertex.GetProperty("vertex").GetProperty("elementVersion").GetInt64()), ("requestId", Guid.NewGuid()));
        var missing = await ModelCallAsync(reader, "get_graph_vertex", ("graph", "topology"), ("vertexId", 1));
        Assert.False(missing.GetProperty("found").GetBoolean());
    }

    [Theory]
    [InlineData("put_kv")]
    [InlineData("delete_kv")]
    [InlineData("create_bucket")]
    [InlineData("delete_bucket")]
    [InlineData("put_object")]
    [InlineData("delete_object")]
    [InlineData("put_document")]
    [InlineData("delete_document")]
    [InlineData("publish_mq")]
    [InlineData("ack_mq")]
    [InlineData("upsert_graph_vertex")]
    [InlineData("delete_graph_vertex")]
    [InlineData("upsert_graph_edge")]
    [InlineData("delete_graph_edge")]
    [InlineData("execute_sql")]
    public async Task ModelWriteTools_ReadOnlyCredential_IsRejectedWithoutMutation(string tool)
    {
        await using var reader = await CreateMcpClientAsync();
        var args = new Dictionary<string, object?>
        {
            ["keyspace"] = "blocked",
            ["key"] = "x",
            ["valueBase64"] = "AA==",
            ["bucket"] = "blocked",
            ["collection"] = "blocked",
            ["id"] = tool.Contains("graph", StringComparison.Ordinal) ? 1L : "x",
            ["json"] = "{}",
            ["topic"] = "blocked",
            ["payloadBase64"] = "AA==",
            ["consumerGroup"] = "g",
            ["offset"] = 0,
            ["graph"] = "blocked",
            ["vertexId"] = 1,
            ["expectedElementVersion"] = 0,
            ["requestId"] = Guid.NewGuid(),
            ["sql"] = "CREATE TABLE blocked (Id INT, PRIMARY KEY (Id))",
            ["request"] = tool == "upsert_graph_vertex"
                ? (object)new GraphUpsertVertexRequest { Id = 1, RequestId = Guid.NewGuid() }
                : new GraphUpsertEdgeRequest { Id = 1, SourceId = 2, TargetId = 3, LabelId = 1, RequestId = Guid.NewGuid() },
        };
        var result = await reader.CallToolAsync(tool, args);
        AssertModelError(result, "forbidden");
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet(_dbName, out var db));
        Assert.DoesNotContain("blocked", db.Keyspaces.List());
        Assert.Null(db.Tables.Catalog.TryGet("blocked"));
        Assert.DoesNotContain(_app.Services.GetRequiredService<SonnetMqStore>().ListTopicStats(), stats => stats.Topic == _dbName + ".blocked");
    }

    [Fact]
    public async Task ModelTools_ReservedKeyspaceAndNegativeLimits_AreRejected()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        var invalid = await admin.CallToolAsync("list_keyspaces", new Dictionary<string, object?> { ["maxRows"] = 0 });
        AssertModelError(invalid, "invalid_argument");
        var traversal = await admin.CallToolAsync("put_kv", new Dictionary<string, object?>
        {
            ["keyspace"] = "../other",
            ["key"] = "x",
            ["valueBase64"] = "AA==",
        });
        AssertModelError(traversal, "invalid_argument");
    }

    [Fact]
    public async Task ModelTools_DynamicWriteGrantDowngrade_IsAppliedOnNextCall()
    {
        using var control = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(control, "CREATE USER model_writer WITH PASSWORD 'p'");
        await ExecuteSqlAsync(control, $"GRANT WRITE ON DATABASE {_dbName} TO model_writer");
        var token = await LoginAsync("model_writer", "p");
        await using var writer = await CreateMcpClientAsync(token);
        await ModelCallAsync(writer, "put_kv", ("keyspace", "grant_probe"), ("key", "state"), ("valueBase64", "AQ=="));
        var grants = _app!.Services.GetRequiredService<GrantsStore>();
        Assert.True(grants.Revoke("model_writer", _dbName));
        grants.Grant("model_writer", _dbName, DatabasePermission.Read);
        var rejected = await writer.CallToolAsync("put_kv", new Dictionary<string, object?>
        {
            ["keyspace"] = "grant_probe",
            ["key"] = "state",
            ["valueBase64"] = "Ag==",
        });
        AssertModelError(rejected, "forbidden");
        var value = await ModelCallAsync(writer, "get_kv", ("keyspace", "grant_probe"), ("key", "state"));
        Assert.Equal("AQ==", value.GetProperty("valueBase64").GetString());
    }
}
