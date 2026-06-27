using SonnetDB.Data;
using SonnetDB.Data.Documents;
using System.Text.Json;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class SndbDocumentClientTests : IDisposable
{
    private readonly string _root;

    public SndbDocumentClientTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-document-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task DocumentClient_EmbeddedCrudAndDistinct_RoundTrips()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        Assert.Equal("created", await client.CreateCollectionAsync("devices"));
        Assert.Equal("exists", await client.CreateCollectionAsync("devices"));

        var inserted = await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","kind":"pump","metrics":{"temp":21.5}}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south","kind":"fan","metrics":{"temp":18}}"""),
        ]);
        Assert.Equal(2, inserted.Inserted);

        var one = await client.FindOneAsync("devices", "dev-1");
        Assert.NotNull(one);
        Assert.Equal("""{"site":"north","kind":"pump","metrics":{"temp":21.5}}""", one!.Json);
        Assert.True(one.Version > 0);

        var scan = await client.FindAsync("devices", new SndbDocumentFindOptions(Limit: 10));
        Assert.Equal(["dev-1", "dev-2"], scan.Select(static x => x.Id).ToArray());
        Assert.Equal(2, await client.CountAsync("devices"));

        var distinct = await client.DistinctAsync("devices", "$.site");
        Assert.Equal(["north", "south"], distinct.Values.Cast<string>().Order(StringComparer.Ordinal).ToArray());

        var updated = await client.UpdateOneAsync("devices", "dev-1", """{"site":"north","kind":"pump","metrics":{"temp":22}}""");
        Assert.Equal(1, updated.Matched);
        Assert.Equal(1, updated.Modified);
        Assert.Contains("\"temp\":22", (await client.FindOneAsync("devices", "dev-1"))!.Json);

        var deleted = await client.DeleteManyAsync("devices", ["dev-1", "missing"]);
        Assert.Equal(1, deleted.Deleted);
        Assert.Equal(["dev-2"], (await client.FindAsync("devices")).Select(static x => x.Id).ToArray());

        Assert.True(await client.DropCollectionAsync("devices"));
    }

    [Fact]
    public async Task DocumentClient_FindOptions_FilterProjectionSort_ReturnsProjectedDocuments()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","kind":"pump","score":7,"tags":["hot","critical"],"metrics":{"temp":22},"nullable":null}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"south","kind":"fan","score":3,"tags":["cold"],"metrics":{"temp":18}}"""),
            new KeyValuePair<string, string>("dev-3", """{"site":"north","kind":"pump","score":9,"tags":["hot"],"metrics":{"temp":24}}"""),
        ]);

        using var site = JsonDocument.Parse("\"north\"");
        using var minScore = JsonDocument.Parse("5");
        using var tag = JsonDocument.Parse("\"hot\"");
        var docs = await client.FindAsync("devices", new SndbDocumentFindOptions(
            Filter: new SndbDocumentFilter(And: [
                new SndbDocumentFilter("$.site", "eq", site.RootElement.Clone()),
                new SndbDocumentFilter("$.score", "gte", minScore.RootElement.Clone()),
                new SndbDocumentFilter("$.tags", "contains", tag.RootElement.Clone()),
            ]),
            Projection: [
                new SndbDocumentProjection("_id", "_id"),
                new SndbDocumentProjection("temp", "$.metrics.temp"),
            ],
            Sort: [new SndbDocumentSort("$.score", Descending: true)],
            Limit: 10));

        Assert.Equal(["dev-3", "dev-1"], docs.Select(static d => d.Id).ToArray());
        Assert.Equal("""{"_id":"dev-3","temp":24}""", docs[0].Json);
        Assert.Equal("""{"_id":"dev-1","temp":22}""", docs[1].Json);
    }

    [Fact]
    public async Task DocumentClient_FindPageAsync_WithCursor_ReturnsBatches()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":1}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"north","score":2}"""),
            new KeyValuePair<string, string>("dev-3", """{"site":"north","score":3}"""),
        ]);

        var first = await client.FindPageAsync("devices", new SndbDocumentFindOptions(Limit: 2));
        Assert.Equal(["dev-1", "dev-2"], first.Documents.Select(static doc => doc.Id).ToArray());
        Assert.True(first.HasMore);
        Assert.NotNull(first.ContinuationToken);
        Assert.NotNull(first.CursorExpiresAtUtc);

        var second = await client.FindPageAsync("devices", new SndbDocumentFindOptions(
            Limit: 2,
            ContinuationToken: first.ContinuationToken));
        Assert.Equal(["dev-3"], second.Documents.Select(static doc => doc.Id).ToArray());
        Assert.False(second.HasMore);
        Assert.Null(second.ContinuationToken);
    }

    [Fact]
    public async Task DocumentClient_FindPageAsync_WithChangedSnapshot_RejectsCursor()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("dev-1", """{"site":"north","score":1}"""),
            new KeyValuePair<string, string>("dev-2", """{"site":"north","score":2}"""),
        ]);

        var first = await client.FindPageAsync("devices", new SndbDocumentFindOptions(Limit: 1));
        Assert.True(first.HasMore);

        await client.InsertOneAsync("devices", "dev-3", """{"site":"north","score":3}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.FindPageAsync("devices", new SndbDocumentFindOptions(
                Limit: 1,
                ContinuationToken: first.ContinuationToken)));
        Assert.Contains("snapshot is stale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DocumentClient_FindOptions_ExistsDistinguishesNullFromMissing()
    {
        using var client = new SndbDocumentClient(new SndbConnectionStringBuilder
        {
            DataSource = _root,
        }.ConnectionString);

        await client.CreateCollectionAsync("devices");
        await client.InsertManyAsync("devices", [
            new KeyValuePair<string, string>("null-value", """{"nullable":null}"""),
            new KeyValuePair<string, string>("missing", """{"other":1}"""),
        ]);

        var exists = await client.FindAsync("devices", new SndbDocumentFindOptions(
            Filter: new SndbDocumentFilter("$.nullable", "exists")));
        Assert.Equal(["null-value"], exists.Select(static d => d.Id).ToArray());

        using var nullValue = JsonDocument.Parse("null");
        var equalsNull = await client.FindAsync("devices", new SndbDocumentFindOptions(
            Filter: new SndbDocumentFilter("$.nullable", "eq", nullValue.RootElement.Clone())));
        Assert.Equal(["null-value"], equalsNull.Select(static d => d.Id).ToArray());
    }
}
