using Microsoft.Extensions.VectorData;
using SonnetDB.Data;
using SonnetDB.Data.VectorData;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SonnetDBVectorDataTests : IDisposable
{
    private readonly string _root;

    public SonnetDBVectorDataTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-vectordata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task CollectionLifecycle_UsesDocumentCollections()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, KnowledgeRecord>("knowledge");

        Assert.False(await collection.CollectionExistsAsync());
        await collection.EnsureCollectionExistsAsync();

        Assert.True(await collection.CollectionExistsAsync());
        Assert.True(await store.CollectionExistsAsync("knowledge"));
        Assert.Contains("knowledge", await store.ListCollectionNamesAsync().ToArrayAsync());

        await collection.EnsureCollectionDeletedAsync();

        Assert.False(await collection.CollectionExistsAsync());
    }

    [Fact]
    public async Task UpsertGetSearchAndFilter_RoundTripsDocumentRecords()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var store = new SonnetDBVectorStore(connection);
        var collection = store.GetCollection<string, KnowledgeRecord>("knowledge");
        await collection.EnsureCollectionExistsAsync();

        await collection.UpsertAsync([
            new KnowledgeRecord { Id = "kb-1", Title = "Pump alarm", Site = "north", Embedding = [1, 0, 0] },
            new KnowledgeRecord { Id = "kb-2", Title = "Pump pressure", Site = "south", Embedding = [0.7f, 0.7f, 0] },
            new KnowledgeRecord { Id = "kb-3", Title = "Fan maintenance", Site = "north", Embedding = [0, 1, 0] },
        ]);

        var fetched = await collection.GetAsync("kb-1", new RecordRetrievalOptions { IncludeVectors = true });

        Assert.NotNull(fetched);
        Assert.Equal("Pump alarm", fetched.Title);
        Assert.Equal("north", fetched.Site);
        Assert.Equal([1, 0, 0], fetched.Embedding);

        var filtered = await collection
            .GetAsync(record => record.Site == "north", top: 10)
            .ToArrayAsync();

        Assert.Equal(["kb-1", "kb-3"], filtered.Select(static record => record.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.All(filtered, static record => Assert.Empty(record.Embedding));

        var search = await collection
            .SearchAsync(
                new ReadOnlyMemory<float>([1, 0, 0]),
                top: 3,
                new VectorSearchOptions<KnowledgeRecord>
                {
                    IncludeVectors = true,
                    Filter = record => record.Site == "north",
                })
            .ToArrayAsync();

        Assert.Equal(["kb-1", "kb-3"], search.Select(static result => result.Record.Id).ToArray());
        Assert.InRange(Math.Abs(search[0].Score ?? double.NaN), 0.0, 0.000001);
        Assert.Equal([1, 0, 0], search[0].Record.Embedding);
    }

    private sealed class KnowledgeRecord
    {
        [VectorStoreKey]
        public string Id { get; set; } = string.Empty;

        [VectorStoreData]
        public string Title { get; set; } = string.Empty;

        [VectorStoreData]
        public string Site { get; set; } = string.Empty;

        [VectorStoreVector(3, DistanceFunction = DistanceFunction.CosineDistance)]
        public float[] Embedding { get; set; } = [];
    }
}
