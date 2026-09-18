using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Generations;
using SonnetDB.Kv;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class RagIngestionManagementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rag-management-" + Guid.NewGuid().ToString("N"));
    private static readonly EmbeddingProfile Profile = new("fixture-v1", "fixture", "fixture", "1", 3,
        supportedModalities: [SemanticContentModality.Text]);
    private static readonly RagIngestionWriterOptions Options = new()
    { MaxEmbeddingAttempts = 1, MaxDuration = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task GetStatusAsync_FreshAndPublished_ReturnsMetadataWithoutCreatingStorageOrLeakingContent()
    {
        using Tsdb database = Open();
        var manager = Manager(database);
        IReadOnlyList<string> before = database.Keyspaces.List();
        RagIngestionStatus empty = await manager.GetStatusAsync();
        Assert.Null(empty.Active);
        Assert.Null(empty.Pending);
        Assert.Equal(before, database.Keyspaces.List());
        await Writer(database).WriteAsync(Snapshot(("a", "private alpha"), ("b", "private bravo")), Embed);
        RagIngestionStatus status = await manager.GetStatusAsync();
        Assert.Equal(1, status.Active!.Revision);
        Assert.Equal(Profile.Id, status.Active.Profile.Id);
        Assert.Equal(2, status.Active.ChunkCount);
        Assert.Null(status.Pending);
        string json = JsonSerializer.Serialize(status, RagIngestionManagementJsonContext.Default.RagIngestionStatus);
        Assert.DoesNotContain("private", json);
        Assert.DoesNotContain("embedding\":", json);
        Assert.Equal(status.Active.GenerationId, JsonSerializer.Deserialize(json,
            RagIngestionManagementJsonContext.Default.RagIngestionStatus)!.Active!.GenerationId);
    }

    [Fact]
    public async Task RebuildAsync_UnchangedSnapshot_ReembedsAndKeepsLeasedOldGeneration()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        using DatabaseGenerationQueryLease old = database.Generations.AcquireActive("manuals");
        int calls = 0;
        var manager = Manager(database);
        var rebuilt = await manager.RebuildAsync(Profile, 1, (chunk, token) => { calls++; return Embed(chunk, token); });
        Assert.Equal(1, calls);
        Assert.Equal(1, rebuilt.EmbeddedChunks);
        Assert.Equal(0, rebuilt.ReusedChunks);
        Assert.Equal(2, rebuilt.Generation.Revision);
        Assert.Equal([1L], (await manager.CleanupRetiredAsync(2, 1)).DeferredRevisions);
        Assert.NotNull(database.Generations.TryGet("manuals", 1));
    }

    [Fact]
    public async Task RebuildAsync_NewProfileFailure_ReopenResumesOnlyCompletedStagingChunks()
    {
        EmbeddingProfile next = Profile with { Id = "fixture-v2", Model = "next", Revision = "2" };
        string pendingId;
        using (Tsdb database = Open())
        {
            await Writer(database).WriteAsync(Snapshot(("a", "alpha"), ("b", "bravo")), Embed);
            int calls = 0;
            await Assert.ThrowsAsync<IOException>(() => Manager(database).RebuildAsync(next, 1,
                (chunk, token) => ++calls == 2 ? throw new IOException("fixture") : Embed(chunk, token)).AsTask());
            var state = await Manager(database).GetStatusAsync();
            Assert.Equal(Profile.Id, state.Active!.Profile.Id);
            Assert.Equal(1, state.Active.Revision);
            Assert.Equal(next.Id, state.Pending!.Profile.Id);
            pendingId = state.Pending.GenerationId;
        }
        using (Tsdb database = Open())
        {
            var manager = Manager(database);
            int calls = 0;
            var result = await manager.ResumeAsync(pendingId, 1, next, (chunk, token) => { calls++; return Embed(chunk, token); });
            Assert.Equal(1, calls);
            Assert.Equal(1, result!.ReusedChunks);
            Assert.Equal(2, result.Generation.Revision);
            var state = await manager.GetStatusAsync();
            Assert.Equal(next.Id, state.Active!.Profile.Id);
            Assert.Null(state.Pending);
            Assert.Equal(2, (await new RagGenerationSearch(database, "manuals", next)
                .SearchAsync("alpha", new float[] { 1, 0, 0 })).Count);
        }
    }

    [Fact]
    public async Task RebuildAsync_StaleRevisionOrChangedSameProfile_RejectsBeforeProvider()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = Manager(database);
        var conflict = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.RebuildAsync(Profile, 0, NeverEmbed).AsTask());
        Assert.Equal(DatabaseGenerationErrorCodes.RevisionConflict, conflict.Code);
        var mismatch = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.RebuildAsync(Profile with { Model = "different" }, 1, NeverEmbed).AsTask());
        Assert.Equal(RagIngestionManagementErrorCodes.ProfileMismatch, mismatch.Code);
        Assert.Null((await manager.GetStatusAsync()).Pending);
    }

    [Fact]
    public async Task RebuildAsync_ChangedProfileIdFromRetainedHistory_RejectsBeforeProvider()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = Manager(database);
        await manager.RebuildAsync(Profile with { Id = "fixture-v2", Model = "next" }, 1, Embed);
        var mismatch = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.RebuildAsync(
            Profile with { Revision = "changed" }, 2, NeverEmbed).AsTask());
        Assert.Equal(RagIngestionManagementErrorCodes.ProfileMismatch, mismatch.Code);
        Assert.Null((await manager.GetStatusAsync()).Pending);
        var rollback = await manager.RebuildAsync(Profile, 2, Embed);
        Assert.Equal(3, rollback.Generation.Revision);
    }

    [Fact]
    public async Task CleanupRetiredAsync_EmptyOrNonRagStream_NeverDeletesBusinessResources()
    {
        using Tsdb database = Open();
        var manager = Manager(database);
        var empty = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.CleanupRetiredAsync(0, 1).AsTask());
        Assert.Equal(DatabaseGenerationErrorCodes.NoActiveGeneration, empty.Code);
        database.Keyspaces.Open("business-v1").Put("important", [1]);
        database.Keyspaces.Open("business-v2").Put("important", [2]);
        database.Generations.Publish(new()
        {
            Stream = "manuals",
            GenerationId = "business-v1",
            ExpectedRevision = 0,
            Resources = [new("business", DatabaseGenerationResourceKind.KvKeyspace, "business-v1")]
        });
        database.Generations.Publish(new()
        {
            Stream = "manuals",
            GenerationId = "business-v2",
            ExpectedRevision = 1,
            Resources = [new("business", DatabaseGenerationResourceKind.KvKeyspace, "business-v2")]
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.CleanupRetiredAsync(2, 1).AsTask());
        Assert.NotNull(database.Generations.TryGet("manuals", 1));
        Assert.Equal(new byte[] { 1 }, database.Keyspaces.Open("business-v1").Get("important"));
    }

    [Fact]
    public async Task CleanupRetiredAsync_CorruptActiveSnapshot_PreservesRetiredGeneration()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = Manager(database);
        var active = await manager.RebuildAsync(Profile, 1, Embed);
        string snapshot = active.Generation.Resources.Single(resource => resource.Role == RagIngestionWriter.SnapshotResourceRole).Name;
        database.Keyspaces.Open(snapshot).Delete("snapshot");
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.CleanupRetiredAsync(2, 1).AsTask());
        Assert.NotNull(database.Generations.TryGet("manuals", 1));
    }

    [Fact]
    public async Task ResumeAndDiscardAsync_IdentityAndRevisionPreconditions_PreserveUnrelatedPending()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = Manager(database);
        await Assert.ThrowsAsync<IOException>(() => manager.RebuildAsync(Profile, 1, (_, _) => throw new IOException("fixture")).AsTask());
        var state = await manager.GetStatusAsync();
        string pendingId = state.Pending!.GenerationId;
        var identity = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.ResumeAsync("different", 1, Profile, NeverEmbed).AsTask());
        Assert.Equal(RagIngestionManagementErrorCodes.PendingConflict, identity.Code);
        var profile = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.ResumeAsync(pendingId, 1,
            Profile with { Id = "other" }, NeverEmbed).AsTask());
        Assert.Equal(RagIngestionManagementErrorCodes.ProfileMismatch, profile.Code);
        await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.DiscardPendingAsync("different", 1).AsTask());
        await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.DiscardPendingAsync(pendingId, 0).AsTask());
        Assert.Equal(pendingId, (await manager.GetStatusAsync()).Pending!.GenerationId);
        Assert.True(await manager.DiscardPendingAsync(pendingId, 1));
        Assert.Null((await manager.GetStatusAsync()).Pending);
        Assert.Single(database.Documents.Catalog.Snapshot());
        Assert.NotNull(database.Generations.TryGet("manuals", 1));
    }

    [Fact]
    public async Task CleanupRetiredAsync_BoundedAndConditional_InspectsOnlyRequestedBatch()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = Manager(database);
        await manager.RebuildAsync(Profile, 1, Embed);
        await manager.RebuildAsync(Profile, 2, Embed);
        await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.CleanupRetiredAsync(2, 1).AsTask());
        var retained = await manager.CleanupRetiredAsync(3, 1, DateTimeOffset.UnixEpoch);
        Assert.Equal([1L], retained.RetentionDeferredRevisions);
        Assert.Empty(retained.RemovedRevisions);
        var cleaned = await manager.CleanupRetiredAsync(3, 1);
        Assert.Equal([1L], cleaned.RemovedRevisions);
        Assert.Null(database.Generations.TryGet("manuals", 1));
        Assert.NotNull(database.Generations.TryGet("manuals", 2));
        Assert.NotNull(database.Generations.TryGet("manuals", 3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.CleanupRetiredAsync(3, 1,
            cancellationToken: new CancellationToken(true)).AsTask());
        Assert.NotNull(database.Generations.TryGet("manuals", 2));
    }

    [Fact]
    public async Task DiscardPendingAsync_PublishedLeftoverOrDamagedPublishedResource_NeverDropsPublishedData()
    {
        using Tsdb database = Open();
        var result = await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = Manager(database);
        var missing = await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.DiscardPendingAsync(result.Generation.GenerationId, 1).AsTask());
        Assert.Equal(RagIngestionManagementErrorCodes.PendingUnavailable, missing.Code);
        using var lease = database.Generations.AcquireActive("manuals");
        string chunks = lease.GetRequiredResource(RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name;
        string snapshot = lease.GetRequiredResource(RagIngestionWriter.SnapshotResourceRole, DatabaseGenerationResourceKind.KvKeyspace).Name;
        database.Documents.Drop(chunks);
        await Assert.ThrowsAsync<DatabaseGenerationException>(() => manager.DiscardPendingAsync(result.Generation.GenerationId, 1).AsTask());
        Assert.NotNull(database.Generations.TryGet("manuals", 1));
        Assert.NotNull(database.Keyspaces.Open(snapshot).Get("snapshot"));
    }

    [Fact]
    public async Task RebuildAsync_CancelledProvider_LeavesOldActiveAndInspectablePending()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = new RagIngestionManager(database, "manuals", Options with { MaxDuration = TimeSpan.FromMilliseconds(100) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.RebuildAsync(Profile, 1, async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return [1f, 0f, 0f];
        }).AsTask());
        var state = await Manager(database).GetStatusAsync();
        Assert.Equal(1, state.Active!.Revision);
        Assert.NotNull(state.Pending);
    }

    [Fact]
    public async Task GetStatusAsync_OverBudgetCheckpoint_FailsWithoutReturningPartialMetadata()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha")), Embed);
        var manager = new RagIngestionManager(database, "manuals", Options with { MaxCheckpointBytes = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.GetStatusAsync().AsTask());
        var textBudget = new RagIngestionManager(database, "manuals", Options with { MaxTextCharacters = 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => textBudget.GetStatusAsync().AsTask());
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false, CleanupEnabled = false },
    });
    private static RagIngestionManager Manager(Tsdb db) => new(db, "manuals", Options);
    private static RagIngestionWriter Writer(Tsdb db) => new(db, "manuals", Profile, Options);
    private static RagIngestionSnapshot Snapshot(params (string Id, string Text)[] contents)
        => new(contents.Select(item =>
        {
            RagTextSnapshot chunked = RagTextChunker.Chunk(item.Id, item.Text);
            return new SemanticContentManifest(item.Id, new("manuals", item.Id, chunked.ContentHash), chunked.ContentHash,
                "text/plain", SemanticContentModality.Text, Encoding.UTF8.GetByteCount(item.Text))
            { Text = item.Text, Chunks = chunked.Chunks };
        }).ToArray());
    private static ValueTask<float[]> Embed(SemanticContentChunk _, CancellationToken token)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new[] { 1f, 0f, 0f }); }
    private static ValueTask<float[]> NeverEmbed(SemanticContentChunk _, CancellationToken __)
        => throw new InvalidOperationException("must not call provider");
    public void Dispose()
    { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
