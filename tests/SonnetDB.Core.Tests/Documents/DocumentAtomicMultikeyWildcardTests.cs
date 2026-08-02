using SonnetDB.Documents;
using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentAtomicMultikeyWildcardTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sndb-document-m32-index-" + Guid.NewGuid().ToString("N"));

    public DocumentAtomicMultikeyWildcardTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void InsertMany_CommitsDocumentsIndexesAndFeedInOneKvVersion()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create(
            "docs",
            indexes: [new DocumentPathIndexDefinition("idx_site", "$.site")]));
        var store = db.Documents.Open("docs");
        long before = store.LastVersion;

        var result = store.InsertMany(
        [
            new DocumentWriteRequest("a", """{"site":"north"}"""),
            new DocumentWriteRequest("b", """{"site":"south"}"""),
            new DocumentWriteRequest("c", """{"site":"north"}"""),
        ]);

        Assert.False(result.HasErrors);
        Assert.Equal(before + 1, store.LastVersion);
        Assert.All(store.Scan(), row => Assert.Equal(store.LastVersion, row.Version));
        var feed = store.ReadChangeFeed(0, 10);
        Assert.Equal(3, feed.Changes.Count);
        Assert.All(feed.Changes, change => Assert.Equal(store.LastVersion, change.DocumentVersion));
        Assert.True(store.VerifyIndexConsistency().IsConsistent);
    }

    [Fact]
    public void Multikey_DeduplicatesArrayValuesAndKeepsScalarTypesDistinct()
    {
        using var db = Open();
        var store = Create(
            db,
            new DocumentPathIndexDefinition("idx_value", "$.value"),
            new DocumentPathIndexDefinition("idx_name", "$.items.name"));
        store.Upsert("array", """{"value":["a","a","b"],"items":[{"name":"pump"},{"name":"fan"}]}""");
        store.Upsert("string", """{"value":"1"}""");
        store.Upsert("number", """{"value":1}""");
        store.Upsert("boolean", """{"value":true}""");
        store.Upsert("true-string", """{"value":"true"}""");

        Assert.Equal(
            ["array"],
            store.GetByIndex(store.Schema.TryGetIndex("idx_value")!, "a")
                .Select(static row => row.Id)
                .ToArray());
        Assert.Equal(["array"], QueryIds(store, "$.value", "a"));
        Assert.Equal(["string"], QueryIds(store, "$.value", "1"));
        Assert.Equal(["number"], QueryIds(store, "$.value", 1L));
        Assert.Equal(["boolean"], QueryIds(store, "$.value", true));
        Assert.Equal(["true-string"], QueryIds(store, "$.value", "true"));
        Assert.Equal(["array"], QueryIds(store, "$.items.name", "fan"));

        var report = store.VerifyIndexConsistency();
        Assert.True(report.IsConsistent);
        Assert.Equal(6, report.Indexes.Single(index => index.IndexName == "idx_value").ActualEntries);
    }

    [Fact]
    public void CompoundMultikey_RejectsParallelArraysAndUniqueDeduplicatesWithinDocument()
    {
        using var db = Open();
        var parallel = Create(
            db,
            new DocumentPathIndexDefinition("idx_parallel", ["$.left", "$.right"]));
        var error = Assert.Throws<InvalidOperationException>(() =>
            parallel.Upsert("bad", """{"left":[1,2],"right":[3,4]}"""));
        Assert.Contains("parallel arrays", error.Message, StringComparison.Ordinal);

        db.Documents.Create(DocumentCollectionSchema.Create(
            "unique_docs",
            indexes: [new DocumentPathIndexDefinition("idx_unique_tags", "$.tags", IsUnique: true)]));
        var unique = db.Documents.Open("unique_docs");
        unique.Upsert("first", """{"tags":["a","a"]}""");
        Assert.Throws<InvalidOperationException>(() => unique.Upsert("second", """{"tags":["a"]}"""));
    }

    [Fact]
    public void Wildcard_RoundTripsAndPlannerPrefersDedicatedPathIndex()
    {
        using (var db = Open())
        {
            var store = Create(
                db,
                new DocumentPathIndexDefinition(
                    "a_wildcard",
                    "$.metadata",
                    Kind: DocumentIndexKind.Wildcard),
                new DocumentPathIndexDefinition("z_site", "$.metadata.site"));
            store.Upsert("a", """{"metadata":{"site":"north","sensors":[{"kind":"temperature"},{"kind":"pressure"}]}}""");
            store.Upsert("b", """{"metadata":{"site":"south","sensors":[{"kind":"flow"}]}}""");
            store.Upsert("c", """{"metadata":{"site":"east"}}""");

            var dedicated = DocumentQueryPlanner.Execute(store, store.Schema, new DocumentQuery(
                Filter: new DocumentFieldFilter(
                    DocumentFieldRef.JsonPath("$.metadata.site"),
                    DocumentFilterOperator.Equal,
                    "north")));
            Assert.Equal("document_index", dedicated.AccessPath);
            Assert.Equal("z_site", dedicated.IndexName);

            var wildcard = DocumentQueryPlanner.Execute(store, store.Schema, new DocumentQuery(
                Filter: new DocumentFieldFilter(
                    DocumentFieldRef.JsonPath("$.metadata.sensors.kind"),
                    DocumentFilterOperator.Equal,
                    "pressure")));
            Assert.Equal("document_wildcard_index", wildcard.AccessPath);
            Assert.Equal(["a"], wildcard.Items.Select(static item => item.Id).ToArray());

            var explicitArrayIndex = DocumentQueryPlanner.Execute(store, store.Schema, new DocumentQuery(
                Filter: new DocumentFieldFilter(
                    DocumentFieldRef.JsonPath("$.metadata.sensors[0].kind"),
                    DocumentFilterOperator.Equal,
                    "temperature")));
            Assert.Equal("document_scan", explicitArrayIndex.AccessPath);
            Assert.Equal(["a"], explicitArrayIndex.Items.Select(static item => item.Id).ToArray());
            var explicitArrayIndexPlan = DocumentQueryPlanner.Explain(store, store.Schema, new DocumentQuery(
                Filter: new DocumentFieldFilter(
                    DocumentFieldRef.JsonPath("$.metadata.sensors[0].kind"),
                    DocumentFilterOperator.Equal,
                    "temperature")));
            Assert.Equal("wildcard_index_predicate_not_supported", explicitArrayIndexPlan.GapReason);
        }

        using var reopened = Open();
        var reopenedStore = reopened.Documents.Open("docs");
        Assert.Equal(DocumentIndexKind.Wildcard, reopenedStore.Schema.TryGetIndex("a_wildcard")!.Kind);
        Assert.Equal(
            ["b"],
            QueryIds(reopenedStore, "$.metadata.sensors.kind", "flow"));
    }

    [Fact]
    public void DerivedIndexRepairMarker_RebuildsStaleFullTextContentOnReopen()
    {
        using (var db = Open())
        {
            db.Documents.Create(DocumentCollectionSchema.Create("search"));
            db.Documents.CreateFullTextIndex(
                "search",
                new DocumentFullTextIndexDefinition("ft_body", ["$.body"]));
            var store = db.Documents.Open("search");
            store.Upsert("a", """{"body":"oldtoken"}""");
            store.AfterPrimaryBatchCommitTestHook = static () =>
                throw new InvalidOperationException("simulated process stop");

            Assert.Throws<InvalidOperationException>(() =>
                store.Replace("a", """{"body":"newtoken"}"""));
            Assert.Equal("""{"body":"newtoken"}""", store.Get("a")!.Json);
        }

        using var reopened = Open();
        var repaired = reopened.Documents.Open("search");
        var index = Assert.Single(repaired.Schema.FullTextIndexes);
        Assert.Equal(["a"], repaired.SearchFullText(index, "$.body", "newtoken", 10).Select(static hit => hit.DocumentId).ToArray());
        Assert.Empty(repaired.SearchFullText(index, "$.body", "oldtoken", 10));
    }

    [Fact]
    public void ConsistencyCheck_DetectsWrongOwnerAndReopenRepairsIt()
    {
        using (var db = Open())
        {
            var store = Create(db, new DocumentPathIndexDefinition("idx_site", "$.site"));
            store.Upsert("a", """{"site":"north"}""");
            Assert.True(store.CorruptFirstIndexEntryValueForTest("wrong-owner"));
            var corrupted = Assert.Single(store.VerifyIndexConsistency().Indexes);
            Assert.Equal(1, corrupted.MissingEntries);
            Assert.Equal(1, corrupted.OrphanEntries);
        }

        using var reopened = Open();
        Assert.True(reopened.Documents.Open("docs").VerifyIndexConsistency().IsConsistent);
    }

    private DocumentCollectionStore Create(Tsdb db, params DocumentPathIndexDefinition[] indexes)
    {
        db.Documents.Create(DocumentCollectionSchema.Create("docs", indexes));
        return db.Documents.Open("docs");
    }

    private static string[] QueryIds(DocumentCollectionStore store, string path, object? value)
        => DocumentQueryPlanner.Execute(store, store.Schema, new DocumentQuery(
                Filter: new DocumentFieldFilter(
                    DocumentFieldRef.JsonPath(path),
                    DocumentFilterOperator.Equal,
                    value)))
            .Items
            .Select(static item => item.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
