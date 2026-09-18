using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.SemanticContent;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class RagSqlResourceManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rag-sql-management-" + Guid.NewGuid().ToString("N"));
    private const string Reserved = "rag_0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("rag-ingestion-jobs")]
    [InlineData("RAG-INGESTION-JOBS. . ")]
    [InlineData("__RAG_MANAGEMENT_AUDIT")]
    [InlineData("RAG_0123456789ABCDEF0123456789ABCDEF. ")]
    public void ReservedNames_Aliases_AreProtected(string name)
        => Assert.True(RagReservedResourceNames.IsReserved(name));

    [Fact]
    public void Execute_ReservedDocument_RejectsReadsWritesAndRestoresTrustedSdkAccess()
    {
        using Tsdb db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create(Reserved));
        db.Documents.Open(Reserved).Upsert("a", "{\"text\":\"private\"}");
        var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, $"SELECT * FROM {Reserved}"));
        Assert.Contains("RAG", exception.Message);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, $"DELETE FROM {Reserved} WHERE id = 'a'"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, $"DROP DOCUMENT COLLECTION {Reserved}"));
        Assert.NotNull(db.Documents.Open(Reserved).Get("a"));
        db.Keyspaces.Open("rag-ingestion-jobs").Put("trusted", [1]);
        Assert.NotNull(db.Keyspaces.Open("rag-ingestion-jobs").Get("trusted"));
    }

    [Fact]
    public void Execute_ViewAndRoutineIndirection_RejectsActualReservedDocumentAccess()
    {
        using Tsdb db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create(Reserved));
        db.Documents.Open(Reserved).Upsert("a", "{\"text\":\"private\"}");
        Exception? view = Record.Exception(() =>
        {
            SqlExecutor.Execute(db, $"CREATE VIEW hidden_rag AS SELECT * FROM {Reserved}");
            SqlExecutor.Execute(db, "SELECT * FROM hidden_rag");
        });
        Assert.NotNull(view);
        Assert.Contains("RAG", view.ToString());
        Exception? routine = Record.Exception(() =>
        {
            SqlExecutor.Execute(db, $"CREATE PROCEDURE read_hidden_rag() LANGUAGE SQL AS BEGIN SELECT * FROM {Reserved}; END");
            SqlExecutor.Execute(db, "CALL read_hidden_rag()");
        });
        Assert.NotNull(routine);
        Assert.Contains("RAG", routine.ToString());
        Assert.NotNull(db.Documents.Open(Reserved).Get("a"));
    }

    [Fact]
    public void SqlScope_ActualKvAndDocumentManagersRejectAliases_ThenRestoreAfterDispose()
    {
        using Tsdb db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create(Reserved));
        using (SqlRagResourceScope.Enter())
        {
            Assert.Throws<InvalidOperationException>(() => db.Keyspaces.Open("RAG-INGESTION-JOBS."));
            Assert.Throws<InvalidOperationException>(() => db.Keyspaces.Open("__rag_management_audit"));
            Assert.Throws<InvalidOperationException>(() => db.Documents.Open(Reserved.ToUpperInvariant() + ". "));
            Assert.Throws<InvalidOperationException>(() => db.Documents.Create(DocumentCollectionSchema.Create("rag_11111111111111111111111111111111")));
        }
        Assert.NotNull(db.Documents.Open(Reserved));
        Assert.NotNull(db.Keyspaces.Open("rag-ingestion-jobs"));
    }

    [Fact]
    public void Execute_ReservedMetadataShortcuts_RejectsIfNotExistsAndExplain()
    {
        using Tsdb db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create(Reserved));
        var create = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            $"CREATE DOCUMENT COLLECTION IF NOT EXISTS {Reserved}"));
        Assert.Contains("RAG", create.Message);
        var indexes = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            $"EXPLAIN SHOW JSON INDEXES ON {Reserved}"));
        Assert.Contains("RAG", indexes.Message);
        var fullText = Assert.Throws<InvalidOperationException>(() => SqlExplainPlanner.Explain(null, db,
            SqlParser.Parse($"EXPLAIN SHOW FULLTEXT INDEXES ON {Reserved}")));
        Assert.Contains("RAG", fullText.Message);
        Assert.NotNull(db.Documents.Catalog.TryGet(Reserved));
    }

    [Fact]
    public void Execute_CollectionCatalogListing_HidesReservedMetadataOnlyInsideSqlScope()
    {
        using Tsdb db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create(Reserved));
        db.Documents.Create(DocumentCollectionSchema.Create("visible_notes"));
        using (SqlRagResourceScope.Enter())
        {
            Assert.Equal(1, db.Documents.Catalog.Count);
            Assert.Equal("visible_notes", Assert.Single(db.Documents.Catalog.Snapshot()).Name);
            Assert.Throws<InvalidOperationException>(() => db.Documents.Catalog.TryGet(Reserved));
        }
        var shown = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW DOCUMENT COLLECTIONS"));
        Assert.Equal("visible_notes", Assert.Single(shown.Rows)[0]);
        Assert.Equal(2, db.Documents.Catalog.Count);
        Assert.Equal(2, db.Documents.Catalog.Snapshot().Count);
    }

    [Fact]
    public void Execute_NormalCollectionAndDeferredInvocation_RemainUsableAndProtected()
    {
        using Tsdb db = Open();
        db.Documents.Create(DocumentCollectionSchema.Create("rag_user_notes"));
        db.Documents.Open("rag_user_notes").Upsert("a", "{\"text\":\"public\"}");
        Assert.False(RagReservedResourceNames.IsReserved("rag_user_notes"));
        var normal = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT * FROM rag_user_notes"));
        Assert.Single(normal.Rows);
        db.Documents.Create(DocumentCollectionSchema.Create(Reserved));
        Func<object?> deferred = () => SqlExecutor.ExecuteStatement(db, SqlParser.Parse($"SELECT * FROM {Reserved}"));
        Assert.Throws<InvalidOperationException>(() => deferred());
        Assert.Single(normal.Rows); // eager Document rows remain usable after the execution scope exits
    }

    [Theory]
    [InlineData("CREATE DOCUMENT COLLECTION new_notes")]
    [InlineData("CREATE JSON INDEX notes_text ON visible_notes ('$.text')")]
    [InlineData("DROP DOCUMENT COLLECTION visible_notes")]
    public async Task Execute_NormalDocumentSchemaMutation_PreservesPublishedRagCatalogAcrossReopen(string sql)
    {
        var profile = new EmbeddingProfile("catalog-v1", "fixture", "fixture", "1", 3,
            supportedModalities: [SemanticContentModality.Text]);
        string generationId;
        using (Tsdb db = Open())
        {
            db.Documents.Create(DocumentCollectionSchema.Create("visible_notes"));
            var chunked = RagTextChunker.Chunk("manual", "alpha manual");
            var manifest = new SemanticContentManifest("manual", new("manuals", "a.txt", eTag: chunked.ContentHash), chunked.ContentHash,
                "text/plain", SemanticContentModality.Text, 12, "fixture", profile.Id)
            { Chunks = chunked.Chunks };
            var published = await new RagIngestionWriter(db, "manuals", profile, new()
            {
                MaxEmbeddingAttempts = 1,
                MaxDuration = TimeSpan.FromSeconds(15),
            }).WriteAsync(new([manifest]), (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(new float[] { 1, 0, 0 });
            });
            generationId = published.Generation.GenerationId;

            SqlExecutor.Execute(db, sql);
            var shown = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SHOW DOCUMENT COLLECTIONS"));
            Assert.DoesNotContain(shown.Rows, row => RagReservedResourceNames.IsReserved((string)row[0]!));
        }

        using Tsdb reopened = Open();
        var state = await new RagIngestionManager(reopened, "manuals").GetStatusAsync();
        Assert.Equal(generationId, state.Active!.GenerationId);
        Assert.NotNull(reopened.Documents.Catalog.TryGet("rag_" + generationId));
        Assert.Single(await new RagGenerationSearch(reopened, "manuals", profile)
            .SearchAsync("alpha", new float[] { 1, 0, 0 }));
        var denied = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(reopened,
            "SELECT * FROM rag_" + generationId));
        Assert.Contains("RAG", denied.Message);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false, CleanupEnabled = false },
    });
    public void Dispose()
    { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
