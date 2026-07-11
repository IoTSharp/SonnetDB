using SonnetDB.Documents;
using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentChangeFeedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-document-change-feed-" + Guid.NewGuid().ToString("N"));

    public DocumentChangeFeedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ChangeFeed_ReopenCollection_PreservesSequenceAndPayloads()
    {
        using (var db = Open())
        {
            db.Documents.Create(DocumentCollectionSchema.Create("docs"));
            var store = db.Documents.Open("docs");
            store.Insert("a", """{"value":1}""");
            store.Replace("a", """{"value":2}""");
            Assert.True(store.Delete("a"));
            Assert.Equal(3, store.LatestChangeSequence);
        }

        using (var reopened = Open())
        {
            var store = reopened.Documents.Open("docs");
            var page = store.ReadChangeFeed(0, 10);
            Assert.Equal(3, page.LatestSequence);
            Assert.Equal(["insert", "update", "delete"], page.Changes.Select(static item => item.Operation).ToArray());
            Assert.Equal("""{"value":1}""", page.Changes[0].AfterJson);
            Assert.Equal("""{"value":1}""", page.Changes[1].BeforeJson);
            Assert.Equal("""{"value":2}""", page.Changes[1].AfterJson);
            Assert.Equal("""{"value":2}""", page.Changes[2].BeforeJson);
            Assert.Null(page.Changes[2].AfterJson);
        }
    }

    [Fact]
    public void PreviewUpdate_DoesNotWriteOrAdvanceChangeSequence()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = db.Documents.Open("docs");
        store.Insert("a", """{"value":1}""");
        long sequence = store.LatestChangeSequence;

        using var increment = System.Text.Json.JsonDocument.Parse("2");
        var preview = store.PreviewUpdate(
            new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, "a"),
            new DocumentUpdate(Inc: new Dictionary<string, System.Text.Json.JsonElement>
            {
                ["$.value"] = increment.RootElement.Clone(),
            }));

        Assert.Equal("""{"value":3}""", Assert.Single(preview).AfterJson);
        Assert.Equal("""{"value":1}""", store.Get("a")!.Json);
        Assert.Equal(sequence, store.LatestChangeSequence);
    }

    [Fact]
    public void ChangeFeed_FilteredPage_AdvancesResumePositionWithoutFalseHasMore()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = db.Documents.Open("docs");
        store.Insert("a", """{"value":1}""");
        store.Replace("a", """{"value":2}""");

        var page = store.ReadChangeFeed(
            0,
            10,
            new HashSet<string>(StringComparer.Ordinal) { "delete" });

        Assert.Empty(page.Changes);
        Assert.Equal(store.LatestChangeSequence, page.ResumeSequence);
        Assert.False(page.HasMore);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
}
