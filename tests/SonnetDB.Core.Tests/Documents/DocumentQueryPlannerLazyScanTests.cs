using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

public sealed class DocumentQueryPlannerLazyScanTests : IDisposable
{
    private readonly string _root;

    public DocumentQueryPlannerLazyScanTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-doc-lazy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static DocumentCollectionStore CreateIndexedCollection(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document)
            VALUES ('dev-1', '{"site":"north","kind":"pump"}'),
                   ('dev-2', '{"site":"north","kind":"fan"}'),
                   ('dev-3', '{"site":"south","kind":"pump"}')
            """);
        SqlExecutor.Execute(db, "CREATE INDEX idx_docs_site ON device_docs ('$.site')");
        return db.Documents.Open("device_docs");
    }

    private static DocumentFilter SiteEquals(string value)
        => new DocumentFieldFilter(DocumentFieldRef.JsonPath("$.site"), DocumentFilterOperator.Equal, value);

    [Fact]
    public void Execute_IndexPathSelected_DoesNotMaterializeFullScan()
    {
        using var db = Open();
        var store = CreateIndexedCollection(db);

        long baseline = store.FullScanCount;
        var result = DocumentQueryPlanner.Execute(
            store,
            store.Schema,
            new DocumentQuery(Filter: SiteEquals("north")));

        Assert.Equal("document_index", result.AccessPath);
        Assert.Equal("idx_docs_site", result.IndexName);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(baseline, store.FullScanCount);
    }

    [Fact]
    public void Execute_IdPathSelected_DoesNotMaterializeFullScan()
    {
        using var db = Open();
        var store = CreateIndexedCollection(db);

        long baseline = store.FullScanCount;
        var result = DocumentQueryPlanner.Execute(
            store,
            store.Schema,
            new DocumentQuery(Filter: new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, "dev-2")));

        Assert.Equal("document_id", result.AccessPath);
        Assert.Single(result.Items);
        Assert.Equal(baseline, store.FullScanCount);
    }

    [Fact]
    public void Explain_IndexPathSelected_ReportsScanEstimateWithoutMaterializing()
    {
        using var db = Open();
        var store = CreateIndexedCollection(db);

        long baseline = store.FullScanCount;
        var plan = DocumentQueryPlanner.Explain(
            store,
            store.Schema,
            new DocumentQuery(Filter: SiteEquals("north")));

        Assert.Equal("document_index", plan.AccessPath);
        Assert.Equal(2, plan.EstimatedCandidateRows);
        var scanCandidate = Assert.Single(plan.Candidates, static c => c.AccessPath == "document_scan");
        Assert.False(scanCandidate.Selected);
        Assert.Equal(3, scanCandidate.EstimatedCandidateRows);
        Assert.Equal(baseline, store.FullScanCount);
    }

    [Fact]
    public void Execute_NoUsableIndex_FallsBackToScanWithSameResults()
    {
        using var db = Open();
        var store = CreateIndexedCollection(db);

        long baseline = store.FullScanCount;
        var result = DocumentQueryPlanner.Execute(
            store,
            store.Schema,
            new DocumentQuery(Filter: new DocumentFieldFilter(
                DocumentFieldRef.JsonPath("$.kind"), DocumentFilterOperator.Equal, "pump")));

        Assert.Equal("document_scan", result.AccessPath);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(new[] { "dev-1", "dev-3" }, result.Items.Select(static item => item.Id).OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(baseline + 1, store.FullScanCount);
    }

    [Fact]
    public void CountApis_MatchMaterializedCounts()
    {
        using var db = Open();
        var store = CreateIndexedCollection(db);
        var index = store.Schema.Indexes.Single(static i => i.Name == "idx_docs_site");

        Assert.Equal(store.Scan().Count, store.Count());
        Assert.Equal(store.GetByIndex(index, ["north"]).Count, store.CountByIndex(index, ["north"]));
        Assert.Equal(store.GetByIndex(index, ["south"]).Count, store.CountByIndex(index, ["south"]));
        Assert.Equal(store.GetByIndex(index, ["missing"]).Count, store.CountByIndex(index, ["missing"]));
        Assert.Equal(store.GetByIndexPrefix(index, ["north"]).Count, store.CountByIndexPrefix(index, ["north"]));
    }

    [Fact]
    public void CountApis_TrackDeletes()
    {
        using var db = Open();
        var store = CreateIndexedCollection(db);
        var index = store.Schema.Indexes.Single(static i => i.Name == "idx_docs_site");

        Assert.True(store.Delete("dev-1"));

        Assert.Equal(2, store.Count());
        Assert.Equal(1, store.CountByIndex(index, ["north"]));
        Assert.Equal(store.GetByIndex(index, ["north"]).Count, store.CountByIndex(index, ["north"]));
    }

    [Fact]
    public void CompositeIndexPrefix_CountMatchesAndStaysLazy()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION tenant_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO tenant_docs (id, document)
            VALUES ('t1-a', '{"tenant":"t1","site":"north"}'),
                   ('t1-b', '{"tenant":"t1","site":"south"}'),
                   ('t2-a', '{"tenant":"t2","site":"north"}')
            """);
        SqlExecutor.Execute(db, "CREATE INDEX idx_tenant_site ON tenant_docs ('$.tenant', '$.site')");
        var store = db.Documents.Open("tenant_docs");
        var index = store.Schema.Indexes.Single(static i => i.Name == "idx_tenant_site");

        Assert.Equal(2, store.CountByIndexPrefix(index, ["t1"]));

        long baseline = store.FullScanCount;
        var result = DocumentQueryPlanner.Execute(
            store,
            store.Schema,
            new DocumentQuery(Filter: new DocumentFieldFilter(
                DocumentFieldRef.JsonPath("$.tenant"), DocumentFilterOperator.Equal, "t1")));

        Assert.Equal("document_index_prefix", result.AccessPath);
        Assert.Equal(2, result.MatchedCount);
        Assert.Equal(baseline, store.FullScanCount);
    }
}
