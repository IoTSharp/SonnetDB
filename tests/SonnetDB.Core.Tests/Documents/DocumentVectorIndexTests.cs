using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

/// <summary>
/// 文档集合持久向量（HNSW ANN）索引（M28 P4 #227，索引 I12）：schema/codec、DDL、store 生命周期、
/// 崩溃重建、<c>vector_search</c> 走索引与暴力扫等价、EXPLAIN、回落。
/// </summary>
public sealed class DocumentVectorIndexTests : IDisposable
{
    private readonly string _root;

    public DocumentVectorIndexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-doc-vector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    // ---- schema + codec ----

    [Fact]
    public void SchemaCodec_V5_RoundTripsVectorIndex()
    {
        string path = Path.Combine(_root, DocumentCollectionSchemaCodec.FileName);
        var schema = DocumentCollectionSchema.Create(
            "docs",
            indexes: [new DocumentPathIndexDefinition("idx_site", "$.site")],
            vectorIndexes:
            [
                new DocumentVectorIndexDefinition("vec_embed", "$.embedding", 3, KnnMetric.L2, M: 8, EfConstruction: 128, EfSearch: 48),
            ]);

        DocumentCollectionSchemaCodec.Save(path, [schema]);
        var loaded = Assert.Single(DocumentCollectionSchemaCodec.Load(path));

        var vec = Assert.Single(loaded.VectorIndexes);
        Assert.Equal("vec_embed", vec.Name);
        Assert.Equal("$.embedding", vec.Path);
        Assert.Equal(3, vec.Dimensions);
        Assert.Equal(KnnMetric.L2, vec.Metric);
        Assert.Equal(8, vec.M);
        Assert.Equal(128, vec.EfConstruction);
        Assert.Equal(48, vec.EfSearch);
        Assert.Equal("idx_site", Assert.Single(loaded.Indexes).Name);
    }

    [Fact]
    public void SchemaCreate_RejectsBadVectorIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentCollectionSchema.Create(
            "docs",
            vectorIndexes: [new DocumentVectorIndexDefinition("v", "$.e", 0)]));
    }

    // ---- DDL parsing ----

    [Fact]
    public void Parse_CreateVectorIndex_DefaultsAndExplicit()
    {
        var defaults = Assert.IsType<CreateDocumentVectorIndexStatement>(SqlParser.Parse(
            "CREATE VECTOR INDEX vi ON docs ('$.embedding') WITH (dimensions=384)"));
        Assert.Equal("vi", defaults.IndexName);
        Assert.Equal("docs", defaults.CollectionName);
        Assert.Equal("$.embedding", defaults.Path);
        Assert.Equal(384, defaults.Dimensions);
        Assert.Equal(KnnMetric.Cosine, defaults.Metric);
        Assert.Equal(16, defaults.M);
        Assert.Equal(200, defaults.EfConstruction);
        Assert.Equal(64, defaults.EfSearch);

        var explicitStmt = Assert.IsType<CreateDocumentVectorIndexStatement>(SqlParser.Parse(
            "CREATE VECTOR INDEX IF NOT EXISTS vi ON docs ('$.v') WITH (dimensions=3, metric='l2', m=8, ef_construction=100, ef_search=40)"));
        Assert.True(explicitStmt.IfNotExists);
        Assert.Equal(KnnMetric.L2, explicitStmt.Metric);
        Assert.Equal(8, explicitStmt.M);
        Assert.Equal(100, explicitStmt.EfConstruction);
        Assert.Equal(40, explicitStmt.EfSearch);
    }

    [Fact]
    public void Parse_CreateVectorIndex_RejectsUnknownMetricAndMissingDimensions()
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "CREATE VECTOR INDEX vi ON docs ('$.v') WITH (dimensions=3, metric='hamming')"));
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(
            "CREATE VECTOR INDEX vi ON docs ('$.v') WITH (metric='cosine')"));
    }

    [Fact]
    public void Parse_DropVectorIndex()
    {
        var drop = Assert.IsType<DropDocumentVectorIndexStatement>(SqlParser.Parse(
            "DROP VECTOR INDEX vi ON docs"));
        Assert.Equal("vi", drop.IndexName);
        Assert.Equal("docs", drop.CollectionName);
    }

    // ---- store lifecycle: KV <-> graph <-> main data ----

    [Fact]
    public void Store_UpsertDeleteRebuild_StaysConsistentWithMainData()
    {
        using var db = Open();
        var store = CreateVectorCollection(db, dim: 3);

        store.Insert("a", Doc("north", 1, 0, 0));
        store.Insert("b", Doc("south", 0, 1, 0));
        store.Insert("c", Doc("north", 0, 0, 1));

        var index = store.Schema.VectorIndexes.Single();
        Assert.Equal(3, store.GetVectorIndexedCount(index));
        AssertVectorConsistent(store);

        // update b's vector; delete c
        store.Replace("b", Doc("south", 0.9f, 0.1f, 0));
        Assert.True(store.Delete("c"));
        Assert.Equal(2, store.GetVectorIndexedCount(index));
        AssertVectorConsistent(store);

        // a document without the vector field is not indexed
        store.Insert("d", """{"site":"east"}""");
        Assert.Equal(2, store.GetVectorIndexedCount(index));
        AssertVectorConsistent(store);
    }

    [Fact]
    public void Store_SearchNearestNeighbor_ReturnsClosest()
    {
        using var db = Open();
        var store = CreateVectorCollection(db, dim: 3);
        store.Insert("a", Doc("n", 1, 0, 0));
        store.Insert("b", Doc("n", 0, 1, 0));
        store.Insert("c", Doc("n", 0, 0, 1));

        var index = store.Schema.VectorIndexes.Single();
        var hits = store.SearchVector(index, [1f, 0f, 0f], 2);
        Assert.Equal("a", hits[0].Id);
        Assert.Equal(0.0, hits[0].Distance, 5);
    }

    // ---- crash / reopen: bulk-build graph from persisted vectors ----

    [Fact]
    public void Reopen_RebuildsGraphFromPersistedVectors()
    {
        var query = new float[] { 1f, 0f, 0f };
        IReadOnlyList<(string Id, double Distance)> before;
        using (var db = Open())
        {
            var store = CreateVectorCollection(db, dim: 3);
            store.Insert("a", Doc("n", 1, 0, 0));
            store.Insert("b", Doc("n", 0, 1, 0));
            store.Insert("c", Doc("n", 0.8f, 0.2f, 0));
            before = store.SearchVector(store.Schema.VectorIndexes.Single(), query, 3);
        }

        using (var reopened = Open())
        {
            var store = reopened.Documents.Open("docs");
            var index = store.Schema.VectorIndexes.Single();
            Assert.Equal(3, store.GetVectorIndexedCount(index));
            var after = store.SearchVector(index, query, 3);
            Assert.Equal(before.Select(h => h.Id), after.Select(h => h.Id));
        }
    }

    // ---- vector_search e2e: index path vs brute-force equivalence ----

    [Fact]
    public void VectorSearch_IndexPath_RowByRowEquivalentToBruteForce()
    {
        using var db = Open();
        SeedLogs(db);

        const string sql = "SELECT id, vector_distance() AS distance FROM vector_search(source => logs, vector_field => '$.embedding', vector => [1, 0, 0], k => 3, metric => 'cosine') ORDER BY distance";
        var brute = RunVectorSearch(db, sql);

        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi_logs ON logs ('$.embedding') WITH (dimensions=3, metric='cosine')");

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "EXPLAIN " + sql));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_vector_index", values["access_path"]);
        Assert.Equal("vi_logs", values["index_name"]);

        var indexed = RunVectorSearch(db, sql);
        Assert.Equal(brute.Select(r => r.Id), indexed.Select(r => r.Id));
        for (int i = 0; i < brute.Count; i++)
            Assert.Equal(brute[i].Distance, indexed[i].Distance, 5);
    }

    [Fact]
    public void VectorSearch_DimensionMismatch_FallsBackToBruteForceScan()
    {
        using var db = Open();
        SeedLogs(db);
        // index declares dim=3; query with dim=3 matches, but a 768-d index won't match a 3-d query.
        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi_logs ON logs ('$.embedding') WITH (dimensions=768, metric='cosine')");

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "EXPLAIN SELECT id FROM vector_search(source => logs, vector_field => '$.embedding', vector => [1, 0, 0], k => 2)"));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_vector_scan", values["access_path"]);
    }

    [Fact]
    public void VectorSearch_WithWhereFilter_FallsBackToBruteForceScan()
    {
        using var db = Open();
        SeedLogs(db);
        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi_logs ON logs ('$.embedding') WITH (dimensions=3, metric='cosine')");

        // WHERE filters before Top-K; ANN would take Top-K first → not equivalent, must fall back.
        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "EXPLAIN SELECT id, site FROM vector_search(source => logs, vector_field => '$.embedding', vector => [1, 0, 0], k => 4) WHERE site = 'north'"));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_vector_scan", values["access_path"]);
    }

    // ---- DROP / REBUILD ----

    [Fact]
    public void DropVectorIndex_RemovesDeclarationAndDirectory()
    {
        using var db = Open();
        SeedLogs(db);
        SqlExecutor.Execute(db, "CREATE VECTOR INDEX vi_logs ON logs ('$.embedding') WITH (dimensions=3, metric='cosine')");
        Assert.NotNull(db.Documents.Catalog.TryGet("logs")!.TryGetVectorIndex("vi_logs"));

        SqlExecutor.Execute(db, "DROP VECTOR INDEX vi_logs ON logs");
        Assert.Null(db.Documents.Catalog.TryGet("logs")!.TryGetVectorIndex("vi_logs"));

        var explain = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "EXPLAIN SELECT id FROM vector_search(source => logs, vector_field => '$.embedding', vector => [1, 0, 0], k => 2)"));
        var values = explain.Rows.ToDictionary(static r => (string)r[0]!, static r => r[1], StringComparer.Ordinal);
        Assert.Equal("document_vector_scan", values["access_path"]);
    }

    [Fact]
    public void RebuildVectorIndex_RestoresFromMainData()
    {
        using var db = Open();
        var store = CreateVectorCollection(db, dim: 3);
        store.Insert("a", Doc("n", 1, 0, 0));
        store.Insert("b", Doc("n", 0, 1, 0));

        int rebuilt = db.Documents.RebuildVectorIndex("docs", "vec");
        Assert.Equal(2, rebuilt);
        AssertVectorConsistent(store);
    }

    // ---- helpers ----

    private static DocumentCollectionStore CreateVectorCollection(Tsdb db, int dim)
    {
        db.Documents.Create(DocumentCollectionSchema.Create("docs"));
        db.Documents.CreateVectorIndex("docs", new DocumentVectorIndexDefinition("vec", "$.embedding", dim));
        return db.Documents.Open("docs");
    }

    private static string Doc(string site, float x, float y, float z)
        => $$"""{"site":"{{site}}","embedding":[{{F(x)}},{{F(y)}},{{F(z)}}]}""";

    private static string F(float v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void SeedLogs(Tsdb db)
    {
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION logs");
        SqlExecutor.Execute(db, """
            INSERT INTO logs (id, document)
            VALUES ('log-1', '{"site":"north","embedding":[1,0,0]}'),
                   ('log-2', '{"site":"south","embedding":[0.7,0.7,0]}'),
                   ('log-3', '{"site":"north","embedding":[0.95,0.05,0]}'),
                   ('log-4', '{"site":"south","embedding":[0,1,0]}')
            """);
    }

    private static IReadOnlyList<(string Id, double Distance)> RunVectorSearch(Tsdb db, string sql)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
        return result.Rows
            .Select(row => ((string)row[0]!, Convert.ToDouble(row[1])))
            .ToArray();
    }

    private static void AssertVectorConsistent(DocumentCollectionStore store)
    {
        var report = store.VerifyIndexConsistency();
        Assert.All(report.VectorIndexes, entry =>
            Assert.True(entry.IsConsistent, $"vector index {entry.IndexName}: eligible={entry.EligibleDocuments} indexed={entry.IndexedVectors}"));
    }
}
