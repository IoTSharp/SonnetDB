using SonnetDB.Documents;
using SonnetDB.Engine;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

/// <summary>
/// 文档二级索引 / 全文索引相对主数据的一致性校验（M28 P4 #229，索引 I10）。
/// <para>
/// 验证 insert/update/delete/批量/TTL 后二级索引与主数据保持一致，且崩溃 / torn write 造成的欠包含
/// 由 open 时 <c>RebuildIndexesLocked</c> 从主数据全量重建自愈。
/// </para>
/// </summary>
public sealed class DocumentIndexConsistencyTests : IDisposable
{
    private readonly string _root;

    public DocumentIndexConsistencyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-doc-consistency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static DocumentCollectionStore CreateCollection(Tsdb db, params DocumentPathIndexDefinition[] indexes)
    {
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        foreach (var index in indexes)
            db.Documents.CreateIndex("docs", index);
        return db.Documents.Open("docs");
    }

    [Fact]
    public void Insert_Update_Delete_KeepIndexConsistent()
    {
        using var db = Open();
        var store = CreateCollection(db, new DocumentPathIndexDefinition("idx_site", "$.site"));

        store.Insert("dev-1", """{"site":"north","kind":"pump"}""");
        store.Insert("dev-2", """{"site":"south","kind":"fan"}""");
        AssertConsistent(store, expectedDocuments: 2);

        store.Replace("dev-1", """{"site":"east","kind":"pump"}""");
        AssertConsistent(store, expectedDocuments: 2);

        Assert.True(store.Delete("dev-2"));
        AssertConsistent(store, expectedDocuments: 1);

        var report = store.VerifyIndexConsistency();
        var idx = Assert.Single(report.Indexes);
        Assert.Equal("idx_site", idx.IndexName);
        Assert.Equal(1, idx.ExpectedEntries);
        Assert.Equal(1, idx.ActualEntries);
        Assert.Equal(0, idx.MissingEntries);
        Assert.Equal(0, idx.OrphanEntries);
    }

    [Fact]
    public void BatchInsertAndDelete_KeepIndexConsistent()
    {
        using var db = Open();
        var store = CreateCollection(db, new DocumentPathIndexDefinition("idx_kind", "$.kind"));

        store.InsertMany(
        [
            new DocumentWriteRequest("a", """{"kind":"x"}"""),
            new DocumentWriteRequest("b", """{"kind":"y"}"""),
            new DocumentWriteRequest("c", """{"kind":"x"}"""),
        ]);
        AssertConsistent(store, expectedDocuments: 3);

        store.DeleteMany(["a", "c"]);
        AssertConsistent(store, expectedDocuments: 1);
    }

    [Fact]
    public void UniqueSparsePartialCompound_KeepIndexConsistent()
    {
        using var db = Open();
        var store = CreateCollection(
            db,
            new DocumentPathIndexDefinition("idx_unique", "$.serial", IsUnique: true),
            new DocumentPathIndexDefinition("idx_sparse", "$.optional", IsSparse: true),
            new DocumentPathIndexDefinition(
                "idx_partial",
                "$.status",
                PartialFilter: new DocumentIndexPartialFilter("$.active", DocumentIndexPartialFilterOperator.Equal, "true")),
            new DocumentPathIndexDefinition("idx_compound", ["$.site", "$.kind"]));

        store.Insert("d1", """{"serial":"s1","site":"north","kind":"pump","status":"ok","active":true}""");
        store.Insert("d2", """{"serial":"s2","site":"south","kind":"fan","optional":"present","active":false}""");
        store.Insert("d3", """{"serial":"s3","site":"north","kind":"fan","status":"warn","active":true,"optional":"yes"}""");

        var report = store.VerifyIndexConsistency();
        Assert.True(report.IsConsistent);
        Assert.All(report.Indexes, entry => Assert.True(entry.IsConsistent, $"index {entry.IndexName} inconsistent"));

        // sparse 索引只应索引带 $.optional 的两条；partial 只索引 active=true 的两条。
        Assert.Equal(2, report.Indexes.Single(e => e.IndexName == "idx_sparse").ActualEntries);
        Assert.Equal(2, report.Indexes.Single(e => e.IndexName == "idx_partial").ActualEntries);
        Assert.Equal(3, report.Indexes.Single(e => e.IndexName == "idx_compound").ActualEntries);
    }

    [Fact]
    public void TtlPurge_KeepsIndexConsistent()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        db.Documents.CreateIndex("docs", new DocumentPathIndexDefinition("idx_kind", "$.kind"));
        db.Documents.CreateIndex(
            "docs",
            new DocumentPathIndexDefinition("idx_ttl", "$.createdAt", TtlPath: "$.createdAt", TtlSeconds: 1));
        var store = db.Documents.Open("docs");

        long expiredMs = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds();
        long freshMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        store.Insert("old", $$"""{"kind":"pump","createdAt":{{expiredMs}}}""");
        store.Insert("new", $$"""{"kind":"fan","createdAt":{{freshMs}}}""");

        // 任一读路径先触发 PurgeExpiredDocumentsLocked，过期文档与其索引条目一并清除。
        var report = store.VerifyIndexConsistency();
        Assert.Equal(1, report.DocumentCount);
        Assert.True(report.IsConsistent);
    }

    [Fact]
    public void CorruptIndexEntry_DetectedAsMissing_HealsOnReopen()
    {
        using (var db = Open())
        {
            var store = CreateCollection(db, new DocumentPathIndexDefinition("idx_site", "$.site"));
            store.Insert("dev-1", """{"site":"north"}""");
            store.Insert("dev-2", """{"site":"south"}""");
            AssertConsistent(store, expectedDocuments: 2);

            // 模拟 torn write：删掉一条索引条目而不动主文档。
            Assert.True(store.CorruptFirstIndexEntryForTest());

            var corrupted = store.VerifyIndexConsistency();
            Assert.False(corrupted.IsConsistent);
            var idx = Assert.Single(corrupted.Indexes);
            Assert.Equal(1, idx.MissingEntries);
            Assert.Equal(2, corrupted.DocumentCount);
        }

        // 重开集合触发 RebuildIndexesLocked，从主数据全量重建索引自愈。
        using (var reopened = Open())
        {
            var store = reopened.Documents.Open("docs");
            var report = store.VerifyIndexConsistency();
            Assert.True(report.IsConsistent);
            Assert.Equal(2, report.DocumentCount);
            Assert.Equal(0, report.Indexes.Single().MissingEntries);
        }
    }

    [Fact]
    public void FullTextIndex_ReflectsDocumentCount()
    {
        using var db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        db.Documents.CreateFullTextIndex(
            "docs",
            new DocumentFullTextIndexDefinition("ft_body", ["$.body"]));
        var store = db.Documents.Open("docs");

        store.Insert("d1", """{"body":"pump alarm overheating"}""");
        store.Insert("d2", """{"body":"fan maintenance normal"}""");

        var report = store.VerifyIndexConsistency();
        Assert.Equal(2, report.DocumentCount);
        var ft = Assert.Single(report.FullTextIndexes);
        Assert.Equal("ft_body", ft.IndexName);
        Assert.Equal(2, ft.IndexedDocumentCount);
        Assert.True(ft.IsConsistent);
    }

    private static void AssertConsistent(DocumentCollectionStore store, int expectedDocuments)
    {
        var report = store.VerifyIndexConsistency();
        Assert.Equal(expectedDocuments, report.DocumentCount);
        Assert.True(report.IsConsistent, "expected index consistency");
        Assert.All(report.Indexes, entry =>
        {
            Assert.Equal(0, entry.MissingEntries);
            Assert.Equal(0, entry.OrphanEntries);
        });
    }
}
