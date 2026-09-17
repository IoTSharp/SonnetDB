using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.FullText;
using SonnetDB.Generations;
using SonnetDB.Kv;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class RagIngestionWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-rag-writer-" + Guid.NewGuid().ToString("N"));
    private static readonly EmbeddingProfile Profile = new(
        "test-profile-v1", "test-fixture", "fixture", "1", 3,
        supportedModalities: [SemanticContentModality.Text, SemanticContentModality.Document]);

    [Fact]
    public async Task WriteAsync_AddUpdateDelete_ReopenQueriesAndRetiredCleanupReflectCompleteSnapshot()
    {
        string firstCollection;
        using (Tsdb database = Open())
        {
            var writer = Writer(database);
            RagIngestionWriteResult first = await writer.WriteAsync(Snapshot(("a", "alpha"), ("b", "bravo")), Embed);
            Assert.Equal(2, first.AddedContents);
            Assert.Equal(2, first.EmbeddedChunks);
            using DatabaseGenerationQueryLease oldLease = database.Generations.AcquireActive("manuals");
            DocumentCollectionStore old = Collection(database, oldLease);
            firstCollection = old.Schema.Name;
            Assert.Equal(2, old.Scan().Count);
            Assert.Single(FullText(old, "alpha"));

            RagIngestionWriteResult second = await writer.WriteAsync(Snapshot(("a", "charlie")), Embed);
            Assert.Equal(1, second.UpdatedContents);
            Assert.Equal(1, second.DeletedContents);
            Assert.Equal(2, second.Generation.Revision);
            using DatabaseGenerationQueryLease active = database.Generations.AcquireActive("manuals");
            DocumentCollectionStore current = Collection(database, active);
            DocumentRow only = Assert.Single(current.Scan());
            Assert.Empty(FullText(current, "alpha"));
            Assert.Empty(FullText(current, "bravo"));
            Assert.Single(FullText(current, "charlie"));
            Assert.Equal(only.Id, Assert.Single(Vectors(current)).Id);
            Assert.Equal([1L], database.Generations.CleanupRetired("manuals").DeferredRevisions);
            Assert.Single(FullText(old, "bravo"));
        }

        using Tsdb reopened = Open();
        using DatabaseGenerationQueryLease lease = reopened.Generations.AcquireActive("manuals");
        DocumentCollectionStore final = Collection(reopened, lease);
        Assert.Single(final.Scan());
        Assert.Empty(FullText(final, "alpha"));
        Assert.Single(FullText(final, "charlie"));
        Assert.Single(Vectors(final));
        Assert.True(final.VerifyIndexConsistency().IsConsistent);
        Assert.Equal([1L], reopened.Generations.CleanupRetired("manuals").RemovedRevisions);
        Assert.Null(reopened.Documents.Catalog.TryGet(firstCollection));
        Assert.DoesNotContain(firstCollection, reopened.Keyspaces.List());
        string encodedName = Convert.ToHexString(Encoding.UTF8.GetBytes(firstCollection)).ToLowerInvariant();
        Assert.False(Directory.Exists(Path.Combine(_root, TsdbPaths.DocumentsDirName, "fulltext", encodedName)));
        Assert.False(Directory.Exists(Path.Combine(_root, TsdbPaths.DocumentsDirName, "vector", encodedName)));
    }

    [Fact]
    public async Task ResumeAsync_FailureAfterOneChunkAndReopen_ReusesDurableProgressWithoutPublishingPartialData()
    {
        using (Tsdb database = Open())
        {
            var writer = Writer(database, attempts: 1);
            await writer.WriteAsync(Snapshot(("old", "alpha")), Embed);
            int calls = 0;
            await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(
                Snapshot(("a", "bravo"), ("b", "charlie")),
                (chunk, token) => ++calls == 2 ? throw new IOException("provider offline") : Embed(chunk, token)).AsTask());
            using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
            Assert.Single(FullText(Collection(database, lease), "alpha"));
            Assert.Empty(FullText(Collection(database, lease), "bravo"));
            Assert.Single(database.Generations.List("manuals"));
        }

        using Tsdb reopened = Open();
        int resumedCalls = 0;
        RagIngestionWriteResult result = Assert.IsType<RagIngestionWriteResult>(await Writer(reopened).ResumeAsync((chunk, token) =>
        {
            resumedCalls++;
            Assert.Equal("charlie", chunk.Text);
            return Embed(chunk, token);
        }));
        Assert.Equal(1, resumedCalls);
        Assert.Equal(1, result.ReusedChunks);
        Assert.Equal(1, result.EmbeddedChunks);
        using DatabaseGenerationQueryLease active = reopened.Generations.AcquireActive("manuals");
        DocumentCollectionStore collection = Collection(reopened, active);
        Assert.Equal(2, collection.Scan().Count);
        Assert.Equal(2, Vectors(collection).Count);
        Assert.Empty(FullText(collection, "alpha"));
        Assert.Single(FullText(collection, "bravo"));
        Assert.Single(FullText(collection, "charlie"));
    }

    [Fact]
    public async Task ResumeAsync_CancellationAfterOneChunk_RetainsProgressAndDoesNotRetryCancellation()
    {
        using (Tsdb database = Open())
        {
            using var cancellation = new CancellationTokenSource();
            int calls = 0;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer(database).WriteAsync(
                Snapshot(("a", "alpha"), ("b", "bravo")), (chunk, token) =>
                {
                    if (++calls == 2)
                        cancellation.Cancel();
                    return Embed(chunk, token);
                }, cancellation.Token).AsTask());
            Assert.Equal(2, calls);
            Assert.Empty(database.Generations.List("manuals"));
        }

        using Tsdb reopened = Open();
        RagIngestionWriteResult result = Assert.IsType<RagIngestionWriteResult>(await Writer(reopened).ResumeAsync(Embed));
        Assert.Equal(1, result.ReusedChunks);
        Assert.Equal(1, result.EmbeddedChunks);
        Assert.Equal(1, result.Generation.Revision);
    }

    [Fact]
    public async Task WriteAsync_TransientProviderErrors_RetriesOnlyWithinConfiguredBudget()
    {
        using Tsdb database = Open();
        int calls = 0;
        RagIngestionWriteResult result = await Writer(database, attempts: 3).WriteAsync(Snapshot(("a", "alpha")), (chunk, token) =>
        {
            if (++calls < 3)
                throw new HttpRequestException("transient");
            return Embed(chunk, token);
        });
        Assert.Equal(3, calls);
        Assert.Equal(1, result.EmbeddedChunks);

        calls = 0;
        await Assert.ThrowsAsync<IOException>(() => Writer(database, attempts: 2).WriteAsync(Snapshot(("b", "bravo")), (_, _) =>
        {
            calls++;
            throw new IOException("offline");
        }).AsTask());
        Assert.Equal(2, calls);
        Assert.Single(database.Generations.List("manuals"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_InvalidVector_LeavesActiveVersionAndDoesNotRetry(bool nonFinite)
    {
        using Tsdb database = Open();
        var writer = Writer(database);
        await writer.WriteAsync(Snapshot(("a", "alpha")), Embed);
        int calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(Snapshot(("b", "bravo")), (_, _) =>
        {
            calls++;
            return ValueTask.FromResult(nonFinite ? new[] { float.NaN, 0f, 1f } : new[] { 1f });
        }).AsTask());
        Assert.Equal(1, calls);
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
        Assert.Single(FullText(Collection(database, lease), "alpha"));
    }

    [Fact]
    public async Task WriteAsync_UnchangedChunks_ReusesVectorsAndNoopKeepsRevision()
    {
        using Tsdb database = Open();
        var writer = Writer(database);
        RagIngestionSnapshot first = Snapshot(("a", "alpha"));
        await writer.WriteAsync(first, Embed);
        RagIngestionWriteResult noChange = await writer.WriteAsync(first, NeverEmbed);
        Assert.Equal(1, noChange.Generation.Revision);
        Assert.Equal(0, noChange.EmbeddedChunks);
        RagIngestionWriteResult update = await writer.WriteAsync(Snapshot(("a", "alpha"), ("b", "bravo")), (chunk, token) =>
        {
            Assert.Equal("bravo", chunk.Text);
            return Embed(chunk, token);
        });
        Assert.Equal(1, update.ReusedChunks);
        Assert.Equal(1, update.EmbeddedChunks);
        Assert.Equal(2, database.Generations.List("manuals").Count);
    }

    [Fact]
    public async Task ResumeAsync_FailureAfterAtomicPublish_ReturnsCommittedGenerationWithoutProviderReplay()
    {
        using (Tsdb database = Open())
        {
            database.Generations.AfterPublishTestHook = static _ => throw new IOException("lost acknowledgement");
            await Assert.ThrowsAsync<IOException>(() => Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed).AsTask());
            database.CrashSimulationCloseWal();
        }
        using Tsdb reopened = Open();
        RagIngestionWriteResult result = Assert.IsType<RagIngestionWriteResult>(await Writer(reopened).ResumeAsync(NeverEmbed));
        Assert.Equal(1, result.Generation.Revision);
        Assert.Single(reopened.Generations.List("manuals"));
        Assert.False(await Writer(reopened).DiscardPendingAsync());
    }

    [Fact]
    public async Task ResumeAsync_FailureBeforeAtomicPublishAndCrash_ReusesCompleteStagingWithoutPartialActive()
    {
        using (Tsdb database = Open())
        {
            database.Generations.BeforePublishTestHook = static _ => throw new IOException("publish interrupted");
            await Assert.ThrowsAsync<IOException>(() => Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed).AsTask());
            Assert.Empty(database.Generations.List("manuals"));
            database.CrashSimulationCloseWal();
        }
        using Tsdb reopened = Open();
        Assert.Empty(reopened.Generations.List("manuals"));
        RagIngestionWriteResult result = Assert.IsType<RagIngestionWriteResult>(await Writer(reopened).ResumeAsync(NeverEmbed));
        Assert.Equal(1, result.Generation.Revision);
        Assert.Equal(1, result.ReusedChunks);
        using DatabaseGenerationQueryLease lease = reopened.Generations.AcquireActive("manuals");
        Assert.Single(FullText(Collection(reopened, lease), "alpha"));
        Assert.Single(Vectors(Collection(reopened, lease)));
    }

    [Fact]
    public async Task ResumeAsync_PrimaryDocumentCommitBeforeDerivedFailure_RepairsBothIndexesWithoutReembedding()
    {
        using Tsdb database = Open();
        DocumentCollectionStore? staging = null;
        await Assert.ThrowsAsync<IOException>(() => Writer(database).WriteAsync(Snapshot(("a", "alpha")), (chunk, token) =>
        {
            staging = database.Documents.Open(Assert.Single(database.Documents.Catalog.Snapshot()).Name);
            staging.AfterPrimaryBatchCommitTestHook = () => throw new IOException("derived write interrupted");
            return Embed(chunk, token);
        }).AsTask());
        Assert.NotNull(staging);
        staging.AfterPrimaryBatchCommitTestHook = null;
        Assert.Empty(database.Generations.List("manuals"));
        RagIngestionWriteResult result = Assert.IsType<RagIngestionWriteResult>(await Writer(database).ResumeAsync(NeverEmbed));
        Assert.Equal(1, result.ReusedChunks);
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
        DocumentCollectionStore collection = Collection(database, lease);
        Assert.Single(FullText(collection, "alpha"));
        Assert.Single(Vectors(collection));
        Assert.True(collection.VerifyIndexConsistency().IsConsistent);
    }

    [Fact]
    public async Task DiscardPendingAsync_UnfinishedJob_RemovesOnlyStagingAndAllowsNewSnapshot()
    {
        using Tsdb database = Open();
        var writer = Writer(database, attempts: 1);
        await writer.WriteAsync(Snapshot(("a", "alpha")), Embed);
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(Snapshot(("b", "bravo")), (_, _) => throw new IOException()).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(Snapshot(("c", "charlie")), Embed).AsTask());
        Assert.Equal(2, database.Documents.Catalog.Count);
        Assert.True(await writer.DiscardPendingAsync());
        Assert.False(await writer.DiscardPendingAsync());
        Assert.Equal(1, database.Documents.Catalog.Count);
        await writer.WriteAsync(Snapshot(("c", "charlie")), Embed);
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
        Assert.Single(FullText(Collection(database, lease), "charlie"));
    }

    [Fact]
    public async Task WriteAsync_ProfileReplacement_ReembedsAndRejectsChangingExistingProfileIdentity()
    {
        using Tsdb database = Open();
        RagIngestionSnapshot snapshot = Snapshot(("a", "alpha"));
        await Writer(database).WriteAsync(snapshot, Embed);
        var incompatible = new RagIngestionWriter(database, "manuals", Profile with { Model = "different" });
        await Assert.ThrowsAsync<ArgumentException>(() => incompatible.WriteAsync(snapshot, NeverEmbed).AsTask());
        var replacement = new RagIngestionWriter(database, "manuals", Profile with { Id = "test-profile-v2", Model = "different" });
        RagIngestionWriteResult result = await replacement.WriteAsync(snapshot, Embed);
        Assert.Equal(1, result.EmbeddedChunks);
        Assert.Equal(0, result.ReusedChunks);
        Assert.Equal(2, result.Generation.Revision);
    }

    [Fact]
    public async Task ResumeAsync_ProfileMismatch_RejectsBeforeProviderOrPublication()
    {
        using Tsdb database = Open();
        await Assert.ThrowsAsync<IOException>(() => Writer(database, attempts: 1).WriteAsync(Snapshot(("a", "alpha")), (_, _) => throw new IOException()).AsTask());
        var other = new RagIngestionWriter(database, "manuals", Profile with { Id = "other" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ResumeAsync(NeverEmbed).AsTask());
        Assert.Empty(database.Generations.List("manuals"));
    }

    [Fact]
    public async Task WriteAsync_InvalidOrOverBudgetSnapshot_RejectsBeforeProviderOrStaging()
    {
        using Tsdb database = Open();
        var tiny = new RagIngestionWriter(database, "manuals", Profile, new() { MaxCheckpointBytes = 32 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => tiny.WriteAsync(Snapshot(("a", "alpha")), NeverEmbed).AsTask());
        RagIngestionSnapshot duplicate = Snapshot(("a", "alpha"), ("a", "bravo"));
        await Assert.ThrowsAsync<ArgumentException>(() => Writer(database).WriteAsync(duplicate, NeverEmbed).AsTask());
        Assert.Empty(database.Documents.Catalog.Snapshot());
        Assert.Null(await Writer(database).ResumeAsync(NeverEmbed));
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWriterWaitingForSameDatabase_CanCancelWithoutChangingPendingJob()
    {
        using Tsdb database = Open();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<RagIngestionWriteResult> first = Writer(database).WriteAsync(Snapshot(("a", "alpha")), async (chunk, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            return await Embed(chunk, token);
        }).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer(database).WriteAsync(
                Snapshot(("b", "bravo")), NeverEmbed, cancellation.Token).AsTask());
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
        Assert.Single(FullText(Collection(database, lease), "alpha"));
        Assert.Empty(FullText(Collection(database, lease), "bravo"));
    }

    [Fact]
    public async Task WriteAsync_ProviderHonorsDeadline_CancelsWithoutPublishing()
    {
        using Tsdb database = Open();
        var writer = new RagIngestionWriter(database, "manuals", Profile, new() { MaxDuration = TimeSpan.FromMilliseconds(100) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(Snapshot(("a", "alpha")), async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return new[] { 1f, 0f, 0f };
        }).AsTask());
        Assert.Empty(database.Generations.List("manuals"));
    }

    [Fact]
    public async Task WriteAsync_EmptySnapshot_DeletesAllActiveDocumentAndDerivedHits()
    {
        using Tsdb database = Open();
        var writer = Writer(database);
        await writer.WriteAsync(Snapshot(("a", "alpha")), Embed);
        RagIngestionWriteResult result = await writer.WriteAsync(RagIngestionSnapshot.Empty, NeverEmbed);
        Assert.Equal(1, result.DeletedContents);
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
        DocumentCollectionStore collection = Collection(database, lease);
        Assert.Empty(collection.Scan());
        Assert.Empty(FullText(collection, "alpha"));
        Assert.Empty(Vectors(collection));
    }

    [Fact]
    public async Task DiscardPendingAsync_PublishedJobBecomesRetired_DoesNotDeletePublishedResources()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        using DatabaseGenerationQueryLease first = database.Generations.AcquireActive("manuals");
        string oldName = Collection(database, first).Schema.Name;
        database.Documents.Create(DocumentCollectionSchema.Create("external"));
        database.Generations.Publish(new()
        {
            Stream = "manuals", GenerationId = "external", ExpectedRevision = 1,
            Resources = [new("external", DatabaseGenerationResourceKind.DocumentCollection, "external")],
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Writer(database).ResumeAsync(NeverEmbed).AsTask());
        Assert.False(await Writer(database).DiscardPendingAsync());
        Assert.NotNull(database.Documents.Catalog.TryGet(oldName));
        Assert.Single(FullText(Collection(database, first), "alpha"));
        first.Dispose();
        Assert.True(database.Documents.Drop(oldName));
        Assert.False(await Writer(database).DiscardPendingAsync());
        Assert.Contains(oldName, database.Keyspaces.List());
    }

    [Fact]
    public async Task ResumeAsync_UnknownCheckpointVersion_RejectsBeforeProviderAndKeepsStaging()
    {
        using Tsdb database = Open();
        await Assert.ThrowsAsync<IOException>(() => Writer(database, attempts: 1).WriteAsync(Snapshot(("a", "alpha")), (_, _) => throw new IOException()).AsTask());
        KvKeyspace jobs = database.Keyspaces.Open("rag-ingestion-jobs");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("manuals")));
        RagIngestionWriterCheckpoint checkpoint = JsonSerializer.Deserialize(jobs.Get(key)!, RagIngestionWriterJsonContext.Default.RagIngestionWriterCheckpoint)!;
        jobs.Put(key, JsonSerializer.SerializeToUtf8Bytes(checkpoint with { SchemaVersion = 2 }, RagIngestionWriterJsonContext.Default.RagIngestionWriterCheckpoint));
        await Assert.ThrowsAsync<InvalidDataException>(() => Writer(database).ResumeAsync(NeverEmbed).AsTask());
        Assert.Equal(1, database.Documents.Catalog.Count);
        Assert.Empty(database.Generations.List("manuals"));
    }

    [Fact]
    public async Task WriteAsync_L2ProfileWithUnnormalizedVector_RejectsBeforePublishing()
    {
        using Tsdb database = Open();
        var writer = new RagIngestionWriter(database, "manuals", Profile with { Normalization = EmbeddingNormalization.L2 });
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.WriteAsync(Snapshot(("a", "alpha")), (_, _) => ValueTask.FromResult(new[] { 2f, 0f, 0f })).AsTask());
        Assert.Empty(database.Generations.List("manuals"));
        RagIngestionWriteResult result = Assert.IsType<RagIngestionWriteResult>(await writer.ResumeAsync(Embed));
        Assert.Equal(1, result.Generation.Revision);
    }

    [Fact]
    public async Task WriteAsync_InvalidUtf16InCustomChunk_RejectsBeforeProviderOrPersistence()
    {
        using Tsdb database = Open();
        SemanticContentManifest manifest = Snapshot(("a", "alpha")).Manifests[0];
        var invalid = new RagIngestionSnapshot([manifest with
        {
            Text = "\uD800",
            Chunks = [manifest.Chunks[0] with { Text = "\uD800", StartOffset = null, EndOffset = null }],
        }]);
        await Assert.ThrowsAsync<EncoderFallbackException>(() => Writer(database).WriteAsync(invalid, NeverEmbed).AsTask());
        Assert.Empty(database.Documents.Catalog.Snapshot());
        Assert.Null(await Writer(database).ResumeAsync(NeverEmbed));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("manifests")]
    [InlineData("chunks")]
    public async Task ResumeAsync_MissingPersistedCollectionField_RejectsWithoutPublishingDeletion(string field)
    {
        using Tsdb database = Open();
        var writer = Writer(database, attempts: 1);
        await writer.WriteAsync(Snapshot(("old", "alpha")), Embed);
        await Assert.ThrowsAsync<IOException>(() => writer.WriteAsync(Snapshot(("new", "bravo")), (_, _) => throw new IOException()).AsTask());
        KvKeyspace jobs = database.Keyspaces.Open("rag-ingestion-jobs");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("manuals")));
        JsonObject root = JsonNode.Parse(jobs.Get(key)!)!.AsObject();
        JsonObject target = field switch
        {
            "snapshot" => root,
            "manifests" => root["snapshot"]!.AsObject(),
            _ => root["snapshot"]!["manifests"]![0]!.AsObject(),
        };
        Assert.True(target.Remove(field));
        jobs.Put(key, Encoding.UTF8.GetBytes(root.ToJsonString()));
        await Assert.ThrowsAsync<InvalidDataException>(() => writer.ResumeAsync(NeverEmbed).AsTask());
        Assert.Equal(2, database.Documents.Catalog.Count);
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("manuals");
        Assert.Equal(1, lease.Generation.Revision);
        Assert.Single(FullText(Collection(database, lease), "alpha"));
        Assert.Empty(FullText(Collection(database, lease), "bravo"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false, CleanupEnabled = false },
    });

    private static RagIngestionWriter Writer(Tsdb database, int attempts = 3)
        => new(database, "manuals", Profile, new() { MaxEmbeddingAttempts = attempts, RetryDelay = TimeSpan.FromMilliseconds(1), MaxDuration = TimeSpan.FromSeconds(30) });

    private static RagIngestionSnapshot Snapshot(params (string Id, string Text)[] contents)
        => new(contents.Select(item =>
        {
            RagTextSnapshot chunked = RagTextChunker.Chunk(item.Id, item.Text);
            return new SemanticContentManifest(item.Id, new("manuals", item.Id, chunked.ContentHash), chunked.ContentHash,
                "text/plain", SemanticContentModality.Text, Encoding.UTF8.GetByteCount(item.Text))
            { Text = item.Text, Chunks = chunked.Chunks };
        }).ToArray());

    private static ValueTask<float[]> Embed(SemanticContentChunk chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(chunk.Text switch
        {
            "alpha" => new[] { 1f, 0f, 0f },
            "bravo" => new[] { 0f, 1f, 0f },
            _ => new[] { 0f, 0f, 1f },
        });
    }

    private static ValueTask<float[]> NeverEmbed(SemanticContentChunk _, CancellationToken __)
        => throw new InvalidOperationException("Provider must not be called.");

    private static DocumentCollectionStore Collection(Tsdb database, DatabaseGenerationQueryLease lease)
        => database.Documents.Open(lease.GetRequiredResource(RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name);

    private static IReadOnlyList<DocumentFullTextSearchHit> FullText(DocumentCollectionStore collection, string text)
        => collection.SearchFullText(collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)!, "$.text", text, 10);

    private static IReadOnlyList<(string Id, double Distance)> Vectors(DocumentCollectionStore collection)
        => collection.SearchVector(collection.Schema.TryGetVectorIndex(RagIngestionWriter.VectorIndexName)!, [1, 0, 0], 10);
}
