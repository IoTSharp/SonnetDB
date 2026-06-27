using SonnetDB.Data;
using SonnetDB.Data.Documents;
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
}
