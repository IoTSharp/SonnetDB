using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentFindOneAndUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-document-find-one-and-update-" + Guid.NewGuid().ToString("N"));

    public DocumentFindOneAndUpdateTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FindOneAndUpdate_ReturnBeforeAndAfter_UsesAtomicWritePath()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = db.Documents.Open("docs");
        store.Insert("dev-1", """{"count":2}""");
        long beforeVersion = store.LastVersion;

        var before = store.FindOneAndUpdate(
            IdFilter("dev-1"),
            new DocumentUpdate(Mul: Values(("$.count", "3"))),
            DocumentReturnDocument.Before);

        Assert.Equal(1, before.WriteResult.Modified);
        Assert.Equal(2, ReadInt(before.Document, "count"));
        Assert.Equal(beforeVersion + 1, store.LastVersion);

        var after = store.FindOneAndUpdate(
            IdFilter("dev-1"),
            new DocumentUpdate(Inc: Values(("$.count", "1"))),
            DocumentReturnDocument.After);

        Assert.Equal(7, ReadInt(after.Document, "count"));
        Assert.Equal(store.Get("dev-1")!.Version, after.Document!.Version);
    }

    [Fact]
    public void FindOneAndUpdate_Upsert_ReturnsNullBeforeAndCreatedDocumentAfter()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = db.Documents.Open("docs");

        var before = store.FindOneAndUpdate(
            IdFilter("new-before"),
            new DocumentUpdate(Set: Values(("$.status", "\"created\""))),
            DocumentReturnDocument.Before,
            upsert: true);
        Assert.Equal(1, before.WriteResult.Inserted);
        Assert.Null(before.Document);

        var after = store.FindOneAndUpdate(
            IdFilter("new-after"),
            new DocumentUpdate(Set: Values(("$.status", "\"created\""))),
            DocumentReturnDocument.After,
            upsert: true);
        Assert.Equal(1, after.WriteResult.Inserted);
        Assert.Equal("new-after", after.Document!.Id);
        Assert.Contains("\"status\":\"created\"", after.Document.Json, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static DocumentFilter IdFilter(string id)
        => new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, id);

    private static IReadOnlyDictionary<string, JsonElement> Values(params (string Path, string Json)[] values)
        => values.ToDictionary(
            static value => value.Path,
            static value =>
            {
                using var document = JsonDocument.Parse(value.Json);
                return document.RootElement.Clone();
            },
            StringComparer.Ordinal);

    private static int ReadInt(DocumentRow? row, string property)
    {
        Assert.NotNull(row);
        using var document = JsonDocument.Parse(row!.Json);
        return document.RootElement.GetProperty(property).GetInt32();
    }
}
