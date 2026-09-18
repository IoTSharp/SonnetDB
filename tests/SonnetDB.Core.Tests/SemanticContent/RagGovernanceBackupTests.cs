using System.Text;
using SonnetDB.Backup;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Generations;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class RagGovernanceBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-rag-backup-" + Guid.NewGuid().ToString("N"));
    private static readonly EmbeddingProfile Profile = new("backup-v1", "fixture", "fixture", "1", 3,
        supportedModalities: [SemanticContentModality.Text]);
    private static readonly RagIngestionWriterOptions Options = new()
    {
        MaxEmbeddingAttempts = 1,
        MaxDuration = TimeSpan.FromSeconds(15),
    };

    [Fact]
    public async Task Restore_ActiveSnapshotAndSourceObject_CanQueryAndUpgradeModelWithLeaseSafeCleanup()
    {
        var service = new BackupService();
        using (Tsdb database = Open("source"))
        {
            var objects = new SndbObjectStore(database);
            objects.CreateBucket("manuals");
            using var input = new MemoryStream(Encoding.UTF8.GetBytes("alpha manual"));
            var source = await objects.PutObjectAsync("manuals", "a.txt", input, "text/plain");
            var snapshot = Snapshot("alpha manual");
            snapshot = new([snapshot.Manifests[0] with
            {
                ObjectRef = new("manuals", "a.txt", source.VersionId, source.ETag),
            }]);
            await new RagIngestionWriter(database, "docs", Profile, Options).WriteAsync(snapshot, Embed);
            service.Create(database, new() { DestinationDirectory = Path.Combine(_root, "backup") });
        }

        Assert.True(service.Verify(Path.Combine(_root, "backup")).IsValid);
        service.Restore(new() { BackupDirectory = Path.Combine(_root, "backup"), TargetDirectory = Path.Combine(_root, "restored") });
        using Tsdb restored = Open("restored");
        Assert.NotNull(new SndbObjectStore(restored).HeadObject("manuals", "a.txt"));
        Assert.Single(await new RagGenerationSearch(restored, "docs", Profile).SearchAsync("alpha", new float[] { 1, 0, 0 }));
        var manager = new RagIngestionManager(restored, "docs", Options);
        using (DatabaseGenerationQueryLease old = restored.Generations.AcquireActive("docs"))
        {
            EmbeddingProfile next = Profile with { Id = "backup-v2", Model = "next-fixture", Revision = "2" };
            var result = await manager.RebuildAsync(next, 1, Embed);
            Assert.Equal(2, result.Generation.Revision);
            Assert.Equal(1, result.EmbeddedChunks);
            Assert.Single(await new RagGenerationSearch(restored, "docs", next).SearchAsync("alpha", new float[] { 1, 0, 0 }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new RagGenerationSearch(restored, "docs", Profile)
                .SearchAsync("alpha", new float[] { 1, 0, 0 }).AsTask());
            Assert.Equal([1L], (await manager.CleanupRetiredAsync(2, 10)).DeferredRevisions);
        }
        Assert.Equal([1L], (await manager.CleanupRetiredAsync(2, 10)).RemovedRevisions);
        Assert.Null((await manager.GetStatusAsync()).Pending);
        Assert.NotNull(new SndbObjectStore(restored).HeadObject("manuals", "a.txt"));
    }

    [Fact]
    public async Task Restore_BackupContainsInterruptedRebuild_ResumesFrozenTaskWithoutPublishingPartialGeneration()
    {
        var service = new BackupService();
        EmbeddingProfile next = Profile with { Id = "backup-v2", Revision = "2" };
        string pendingId;
        using (Tsdb database = Open("source"))
        {
            await new RagIngestionWriter(database, "docs", Profile, Options).WriteAsync(Snapshot("alpha", "bravo"), Embed);
            var manager = new RagIngestionManager(database, "docs", Options);
            int calls = 0;
            await Assert.ThrowsAsync<IOException>(() => manager.RebuildAsync(next, 1,
                (chunk, token) => ++calls == 2 ? throw new IOException("fixture interrupted") : Embed(chunk, token)).AsTask());
            var status = await manager.GetStatusAsync();
            pendingId = status.Pending!.GenerationId;
            Assert.Equal(Profile.Id, status.Active!.Profile.Id);
            service.Create(database, new() { DestinationDirectory = Path.Combine(_root, "backup") });
        }

        service.Restore(new() { BackupDirectory = Path.Combine(_root, "backup"), TargetDirectory = Path.Combine(_root, "restored") });
        using Tsdb restored = Open("restored");
        var recovered = new RagIngestionManager(restored, "docs", Options);
        var before = await recovered.GetStatusAsync();
        Assert.Equal(1, before.Active!.Revision);
        Assert.Equal(pendingId, before.Pending!.GenerationId);
        int resumedCalls = 0;
        var result = await recovered.ResumeAsync(pendingId, 1, next, (chunk, token) =>
        {
            resumedCalls++;
            return Embed(chunk, token);
        });
        Assert.Equal(1, resumedCalls);
        Assert.Equal(1, result!.ReusedChunks);
        Assert.Equal(2, result.Generation.Revision);
        Assert.Null((await recovered.GetStatusAsync()).Pending);
        Assert.Equal(2, (await new RagGenerationSearch(restored, "docs", next).SearchAsync("alpha", new float[] { 1, 0, 0 })).Count);
    }

    [Fact]
    public async Task Rebuild_MissingDerivedCollection_ReconstructsFromPersistedSnapshot()
    {
        using Tsdb database = Open("source");
        await new RagIngestionWriter(database, "docs", Profile, Options).WriteAsync(Snapshot("alpha"), Embed);
        using (DatabaseGenerationQueryLease active = database.Generations.AcquireActive("docs"))
        {
            // 模拟已丢失的派生集合；权威快照仍完整，不靠旧向量修复新版本。
            database.Documents.Drop(active.GetRequiredResource(RagIngestionWriter.ChunksResourceRole,
                DatabaseGenerationResourceKind.DocumentCollection).Name);
        }
        var result = await new RagIngestionManager(database, "docs", Options).RebuildAsync(Profile, 1, Embed);
        Assert.Equal(2, result.Generation.Revision);
        Assert.Single(await new RagGenerationSearch(database, "docs", Profile).SearchAsync("alpha", new float[] { 1, 0, 0 }));
    }

    private Tsdb Open(string name) => Tsdb.Open(new()
    {
        RootDirectory = Path.Combine(_root, name),
        BackgroundFlush = new() { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    private static RagIngestionSnapshot Snapshot(params string[] texts)
        => new(texts.Select((text, index) =>
        {
            string id = "manual-" + index;
            var chunked = RagTextChunker.Chunk(id, text);
            return new SemanticContentManifest(id, new("manuals", id + ".txt", eTag: chunked.ContentHash), chunked.ContentHash,
                "text/plain", SemanticContentModality.Text, Encoding.UTF8.GetByteCount(text), "fixture", Profile.Id)
            { Chunks = chunked.Chunks };
        }).ToArray());

    private static ValueTask<float[]> Embed(SemanticContentChunk chunk, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new float[] { 1, 0, 0 });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
