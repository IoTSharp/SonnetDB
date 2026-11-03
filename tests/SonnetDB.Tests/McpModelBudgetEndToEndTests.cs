using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Graphs;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class McpEndToEndTests
{
    [Fact]
    public async Task BudgetTools_Exact64KiBBinaryValues_RemainReadable()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await using var reader = await CreateMcpClientAsync();
        byte[] bytes = new byte[65536];
        Array.Fill(bytes, (byte)255);
        string encoded = Convert.ToBase64String(bytes);
        await ModelCallAsync(admin, "put_kv", ("keyspace", "exact_binary"), ("key", "a"), ("valueBase64", encoded));
        var value = await ModelCallAsync(reader, "get_kv", ("keyspace", "exact_binary"), ("key", "a"));
        Assert.Equal(encoded, value.GetProperty("valueBase64").GetString());
        await ModelCallAsync(admin, "create_bucket", ("bucket", "exact-binary"));
        await ModelCallAsync(admin, "put_object", ("bucket", "exact-binary"), ("key", "a"), ("valueBase64", encoded));
        var obj = await ModelCallAsync(reader, "get_object", ("bucket", "exact-binary"), ("key", "a"));
        Assert.Equal(encoded, obj.GetProperty("valueBase64").GetString());
        await ModelCallAsync(admin, "publish_mq", ("topic", "exact-binary"), ("payloadBase64", encoded));
        var mq = await ModelCallAsync(reader, "read_mq", ("topic", "exact-binary"));
        Assert.Equal(encoded, mq.GetProperty("messages")[0].GetProperty("payloadBase64").GetString());
    }

    [Fact]
    public async Task BudgetTools_ValidGraphProperty_RoundTripsUsingPublishedNumericEnumSchema()
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE GRAPH property_graph"));
        var request = new GraphUpsertVertexRequest
        {
            Id = 1,
            RequestId = Guid.NewGuid(),
            Properties = [new GraphPropertyDto { PropertyId = 1, Value = new GraphValueDto { Kind = GraphPropertyKind.String, String = "pump" } }],
        };
        await ModelCallAsync(admin, "upsert_graph_vertex", ("graph", "property_graph"),
            ("request", JsonSerializer.SerializeToElement(request, ServerJsonContext.Default.GraphUpsertVertexRequest)));
        var vertex = await ModelCallAsync(admin, "get_graph_vertex", ("graph", "property_graph"), ("vertexId", 1));
        Assert.Equal("pump", vertex.GetProperty("vertex").GetProperty("properties")[0].GetProperty("value").GetProperty("string").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetTools_OversizedStoredKvAndDocument_ReturnExplicitLimitBeforeMaterialization(bool cold)
    {
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet(_dbName, out var db));
        var kv = db.Keyspaces.Open("large_values");
        kv.Put("big", new byte[2 * 1024 * 1024]);
        using var control = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(control, "CREATE DOCUMENT COLLECTION large_docs");
        db.Documents.Open("large_docs").Upsert("big", "{\"body\":\"" + new string('x', 2 * 1024 * 1024) + "\"}");
        if (cold)
        {
            kv.Compact();
            db.Documents.CompactAll();
        }
        await using var reader = await CreateMcpClientAsync();
        foreach (var (tool, args) in new[]
        {
            ("get_kv", new Dictionary<string, object?> { ["keyspace"] = "large_values", ["key"] = "big" }),
            ("scan_kv", new Dictionary<string, object?> { ["keyspace"] = "large_values", ["maxRows"] = 1000 }),
            ("get_document", new Dictionary<string, object?> { ["collection"] = "large_docs", ["id"] = "big" }),
            ("scan_documents", new Dictionary<string, object?> { ["collection"] = "large_docs", ["maxRows"] = 1000 }),
        })
        {
            var result = await reader.CallToolAsync(tool, args);
            AssertModelError(result, "limit_exceeded");
        }
    }

    [Fact]
    public async Task BudgetTools_LargeKvAndDocumentPages_ReturnResumablePartialPages()
    {
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet(_dbName, out var db));
        var kv = db.Keyspaces.Open("paged_values");
        using var control = CreateHttpClient(_adminToken);
        await ExecuteSqlAsync(control, "CREATE DOCUMENT COLLECTION paged_docs");
        var documents = db.Documents.Open("paged_docs");
        byte[] value = new byte[32768];
        for (int index = 0; index < 20; index++)
        {
            kv.Put($"key{index:D2}", value);
            documents.Upsert($"id{index:D2}", "{\"body\":\"" + new string('x', 32768) + "\"}");
        }
        kv.Compact();
        db.Documents.CompactAll();
        await using var reader = await CreateMcpClientAsync();
        var first = await ModelCallAsync(reader, "scan_kv", ("keyspace", "paged_values"), ("maxRows", 1000));
        Assert.True(first.GetProperty("truncated").GetBoolean());
        Assert.InRange(first.GetProperty("entries").GetArrayLength(), 1, 2);
        var next = await ModelCallAsync(reader, "scan_kv", ("keyspace", "paged_values"), ("maxRows", 1000),
            ("afterKeyBase64", first.GetProperty("nextCursor").GetString()));
        Assert.NotEqual(first.GetProperty("entries")[0].GetProperty("key").GetString(), next.GetProperty("entries")[0].GetProperty("key").GetString());
        var docFirst = await ModelCallAsync(reader, "scan_documents", ("collection", "paged_docs"), ("maxRows", 1000));
        Assert.True(docFirst.GetProperty("truncated").GetBoolean());
        Assert.InRange(docFirst.GetProperty("documents").GetArrayLength(), 1, 2);
        var docNext = await ModelCallAsync(reader, "scan_documents", ("collection", "paged_docs"), ("maxRows", 1000),
            ("afterId", docFirst.GetProperty("nextCursor").GetString()));
        Assert.NotEqual(docFirst.GetProperty("documents")[0].GetProperty("id").GetString(), docNext.GetProperty("documents")[0].GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("large_string")]
    [InlineData("unused_large_field")]
    [InlineData("labels")]
    [InlineData("properties")]
    [InlineData("unique_ids")]
    [InlineData("escaped_total")]
    public async Task BudgetTools_GraphInput_PreflightsFieldsAndArraysBeforeWrite(string boundary)
    {
        await using var admin = await CreateMcpClientAsync(_adminToken);
        await ModelCallAsync(admin, "execute_sql", ("sql", "CREATE GRAPH budget_graph"));
        var properties = new GraphPropertyDto[]
        {
            new() { PropertyId = 1, Value = new GraphValueDto { Kind = GraphPropertyKind.String, String = new string('x', 1024 * 1024) } },
        };
        var request = new GraphUpsertVertexRequest
        {
            Id = 1,
            RequestId = Guid.NewGuid(),
            Labels = boundary == "labels" ? Enumerable.Range(1, 129).ToArray() : [],
            UniquePropertyIds = boundary == "unique_ids" ? Enumerable.Range(1, 129).ToArray() : [],
            Properties = boundary switch
            {
                "large_string" => properties,
                "unused_large_field" => [new GraphPropertyDto { PropertyId = 1, Value = new GraphValueDto { Kind = GraphPropertyKind.Int64, Int64 = 1, String = new string('x', 1024 * 1024) } }],
                "properties" => Enumerable.Range(1, 129).Select(id => new GraphPropertyDto { PropertyId = id, Value = new GraphValueDto { Kind = GraphPropertyKind.Int64, Int64 = id } }).ToArray(),
                "escaped_total" => Enumerable.Range(1, 3).Select(id => new GraphPropertyDto { PropertyId = id, Value = new GraphValueDto { Kind = GraphPropertyKind.String, String = new string('<', 4096) } }).ToArray(),
                _ => [],
            },
        };
        var requestJson = JsonSerializer.SerializeToElement(request, ServerJsonContext.Default.GraphUpsertVertexRequest);
        var rejected = await admin.CallToolAsync("upsert_graph_vertex", new Dictionary<string, object?> { ["graph"] = "budget_graph", ["request"] = requestJson });
        AssertModelError(rejected, "limit_exceeded");
        var missing = await ModelCallAsync(admin, "get_graph_vertex", ("graph", "budget_graph"), ("vertexId", 1));
        Assert.False(missing.GetProperty("found").GetBoolean());
    }
}
