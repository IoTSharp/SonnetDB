using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentFullTextFilteredSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-document-fulltext-filtered-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void SearchFullTextFiltered_WithAllowedIds_ReturnsStableFilteredRanking()
    {
        using Tsdb database = Open();
        (DocumentCollectionStore collection, DocumentFullTextIndex index) = CreateCollection(database);
        InsertFixture(collection);
        IReadOnlyList<DocumentFullTextSearchHit> unfiltered = collection.SearchFullText(
            index,
            "$.body",
            "shared token",
            10);
        var allowed = new HashSet<string>(["a", "c"], StringComparer.Ordinal);

        DocumentFullTextFilteredSearchResult filtered = collection.SearchFullTextFiltered(
            index,
            "$.body",
            "shared token",
            10,
            allowed,
            maxPostingVisits: 100);

        Assert.False(filtered.PostingBudgetExceeded);
        Assert.Equal(2, filtered.FilterCandidateCount);
        Assert.InRange(filtered.PostingVisits, 1, 100);
        Assert.Equal(
            unfiltered.Where(hit => allowed.Contains(hit.DocumentId)),
            filtered.Hits);
    }

    [Fact]
    public void SearchFullTextFiltered_WithSmallPostingBudget_FailsWithoutPartialHits()
    {
        using Tsdb database = Open();
        (DocumentCollectionStore collection, DocumentFullTextIndex index) = CreateCollection(database);
        InsertFixture(collection);

        DocumentFullTextFilteredSearchResult result = collection.SearchFullTextFiltered(
            index,
            "$.body",
            "shared token",
            10,
            new HashSet<string>(["a", "b", "c"], StringComparer.Ordinal),
            maxPostingVisits: 1);

        Assert.True(result.PostingBudgetExceeded);
        Assert.Empty(result.Hits);
        Assert.Equal(3, result.FilterCandidateCount);
        Assert.Equal(2, result.PostingVisits);
    }

    [Fact]
    public void SearchFullTextFiltered_WithCancellation_ThrowsBeforeReturningResults()
    {
        using Tsdb database = Open();
        (DocumentCollectionStore collection, DocumentFullTextIndex index) = CreateCollection(database);
        InsertFixture(collection);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => collection.SearchFullTextFiltered(
            index,
            "$.body",
            "shared token",
            10,
            new HashSet<string>(["a"], StringComparer.Ordinal),
            maxPostingVisits: 100,
            cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private Tsdb Open()
    {
        Directory.CreateDirectory(_root);
        return Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    }

    private static (DocumentCollectionStore Collection, DocumentFullTextIndex Index) CreateCollection(
        Tsdb database)
    {
        DocumentCollectionSchema schema = DocumentCollectionSchema.Create(
            "documents",
            fullTextIndexes:
            [
                new DocumentFullTextIndexDefinition(
                    "by_body",
                    ["$.body"],
                    "unicode"),
            ]);
        database.Documents.Create(schema);
        return (
            database.Documents.Open(schema.Name),
            schema.TryGetFullTextIndex("by_body")!);
    }

    private static void InsertFixture(DocumentCollectionStore collection)
    {
        DocumentWriteResult result = collection.InsertMany(
        [
            new DocumentWriteRequest("a", "{\"body\":\"shared token token\"}"),
            new DocumentWriteRequest("b", "{\"body\":\"shared token\"}"),
            new DocumentWriteRequest("c", "{\"body\":\"shared token extra\"}"),
            new DocumentWriteRequest("d", "{\"body\":\"unrelated\"}"),
        ]);
        Assert.True(result.Committed);
        Assert.False(result.HasErrors);
    }
}
