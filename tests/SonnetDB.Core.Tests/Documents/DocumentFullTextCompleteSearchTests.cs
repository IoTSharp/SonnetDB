using System.Text.Json;

using SonnetDB.Data.Documents;
using SonnetDB.Data.FullText;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.FullText;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentFullTextCompleteSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-document-fulltext-complete-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SearchFullTextComplete_FiltersBeforeRankingAndReturnsWindowOutsideTopK()
    {
        using Tsdb database = Open();
        (DocumentCollectionStore collection, DocumentFullTextIndex index) = CreateCollection(database);
        Assert.True(collection.InsertMany(
        [
            new DocumentWriteRequest("a", "{\"body\":\"shared\",\"site\":\"north\"}"),
            new DocumentWriteRequest("b", "{\"body\":\"shared shared\",\"site\":\"south\"}"),
            new DocumentWriteRequest("c", "{\"body\":\"shared\",\"site\":\"north\"}"),
        ]).Committed);

        DocumentFullTextCompleteSearchResult result = collection.SearchFullTextComplete(
            index,
            "$.body",
            "shared",
            new DocumentFieldFilter(DocumentFieldRef.JsonPath("$.site"), DocumentFilterOperator.Equal, "north"));

        Assert.Equal(["a", "c"], result.Hits.Select(static hit => hit.DocumentId).ToArray());
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void SearchFullTextComplete_RejectsIncompleteScanWithoutPartialHits()
    {
        using Tsdb database = Open();
        (DocumentCollectionStore collection, DocumentFullTextIndex index) = CreateCollection(database);
        Assert.True(collection.InsertMany(
        [
            new DocumentWriteRequest("a", "{\"body\":\"shared\"}"),
            new DocumentWriteRequest("b", "{\"body\":\"shared\"}"),
            new DocumentWriteRequest("c", "{\"body\":\"shared\"}"),
        ]).Committed);

        Assert.Throws<FullTextSearchBudgetExceededException>(() => collection.SearchFullTextComplete(
            index, "$.body", "shared", maxDocuments: 2));
    }

    [Fact]
    public async Task TypedEmbeddedSearch_FiltersBeyondTenThousandAndPaginatesFacets()
    {
        {
            using Tsdb database = Open();
            (DocumentCollectionStore collection, _) = CreateCollection(database);
            var writes = Enumerable.Range(0, 10_001)
                .Select(static number => new DocumentWriteRequest(
                    number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    $"{{\"body\":\"shared\",\"site\":\"{(number is 9_000 or 10_000 ? "target" : "other")}\"}}"))
                .ToArray();
            Assert.True(collection.InsertMany(writes).Committed);
        }

        using var client = new SndbFullTextClient($"Data Source={_root}");
        using JsonDocument expected = JsonDocument.Parse("\"target\"");
        var result = await client.SearchAsync(
            "documents",
            "by_body",
            "$.body",
            "shared",
            new SndbFullTextSearchOptions(
                PageSize: 1,
                Filter: new SndbDocumentFilter("$.site", "eq", expected.RootElement.Clone()),
                Facets: [new SndbFullTextFacetRequest("$.site", 4)]));

        Assert.True(result.HasMore);
        Assert.Equal(2, result.TotalHits);
        Assert.Equal("10000", Assert.Single(result.Hits).DocumentId);
        var bucket = Assert.Single(Assert.Single(result.Facets).Buckets);
        Assert.Equal("target", bucket.Value);
        Assert.Equal(2, bucket.Count);

        var next = await client.SearchAsync(
            "documents", "by_body", "$.body", "shared",
            new SndbFullTextSearchOptions(
                PageSize: 1,
                Filter: new SndbDocumentFilter("$.site", "eq", expected.RootElement.Clone()),
                ContinuationToken: result.NextContinuationToken));
        Assert.False(next.HasMore);
        Assert.Equal("9000", Assert.Single(next.Hits).DocumentId);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open()
    {
        Directory.CreateDirectory(_root);
        return Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    }

    private static (DocumentCollectionStore Collection, DocumentFullTextIndex Index) CreateCollection(Tsdb database)
    {
        DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
            "documents",
            fullTextIndexes: [new DocumentFullTextIndexDefinition("by_body", ["$.body"], "unicode")]);
        database.Documents.Create(schema);
        return (database.Documents.Open(schema.Name), schema.TryGetFullTextIndex("by_body")!);
    }
}
