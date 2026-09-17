using System.Text;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Generations;
using SonnetDB.Kv;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class RagGenerationSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-rag-search-" + Guid.NewGuid().ToString("N"));
    private static readonly EmbeddingProfile Profile = new("fixture-v1", "fixture", "fixture", "1", 3,
        supportedModalities: [SemanticContentModality.Text]);
    private static readonly SemanticSearchFusionOptions Options = new() { TopK = 2, MaxDuration = TimeSpan.FromSeconds(10) };

    [Fact]
    public async Task SearchAsync_RealPublishedIndexes_ReopenAndReplacementRemainConsistent()
    {
        using (Tsdb database = Open())
        {
            await Writer(database).WriteAsync(Snapshot(("a", "alpha"), ("b", "bravo")), Embed);
            var result = await new RagGenerationSearch(database, "manuals", Profile).SearchAsync("bravo", new float[] { 1, 0, 0 }, Options);
            Assert.Equal("b", result[0].ContentId); // lexical + vector beats vector only
            Assert.Equal("a", result[1].ContentId);
        }
        using (Tsdb database = Open())
        {
            var reader = new RagGenerationSearch(database, "manuals", Profile);
            Assert.Equal("b", (await reader.SearchAsync("bravo", new float[] { 1, 0, 0 }, Options))[0].ContentId);
            await Writer(database).WriteAsync(Snapshot(("c", "charlie")), Embed);
            Assert.Equal("c", Assert.Single(await reader.SearchAsync("charlie", new float[] { 0, 0, 1 }, Options)).ContentId);
        }
    }

    [Fact]
    public async Task SearchAsync_AuthorizedIds_RestrictsBothRetrievalAndReranker()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha"), ("b", "bravo")), Embed);
        var reader = new RagGenerationSearch(database, "manuals", Profile);
        var initial = await reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options);
        string allowedId = initial.Single(hit => hit.ContentId == "b").Id;
        var reranker = new SemanticSearchFusionTests.Reranker((_, candidates, _) =>
        {
            Assert.Equal("bravo", Assert.Single(candidates).Text);
            return ValueTask.FromResult<IReadOnlyList<SemanticSearchRerankScore>>([new(allowedId, 1)]);
        });
        var result = await reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options, reranker,
            new HashSet<string>(StringComparer.Ordinal) { allowedId });
        Assert.Equal(allowedId, Assert.Single(result).Id);
    }

    [Fact]
    public async Task SearchAsync_GenerationChangesDuringRerank_HoldsOldLeaseUntilCompletion()
    {
        using Tsdb database = Open();
        var writer = Writer(database);
        await writer.WriteAsync(Snapshot(("a", "alpha")), Embed);
        var reranker = new SemanticSearchFusionTests.Reranker(async (_, candidates, token) =>
        {
            await writer.WriteAsync(Snapshot(("b", "bravo")), Embed, token);
            Assert.Equal([1L], database.Generations.CleanupRetired("manuals").DeferredRevisions);
            return [new(Assert.Single(candidates).Id, 1)];
        });
        var result = await new RagGenerationSearch(database, "manuals", Profile).SearchAsync("alpha", new float[] { 1, 0, 0 }, Options, reranker);
        Assert.Equal("alpha", Assert.Single(result).Text);
        Assert.Equal([1L], database.Generations.CleanupRetired("manuals").RemovedRevisions);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("revision")]
    [InlineData("id")]
    [InlineData("normalization")]
    public async Task SearchAsync_ProfileMismatch_RejectsEvenEmptyGeneration(string mismatch)
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(RagIngestionSnapshot.Empty, Embed);
        var other = mismatch switch
        {
            "model" => Profile with { Model = "other" },
            "revision" => Profile with { Revision = "2" },
            "id" => Profile with { Id = "other" },
            _ => Profile with { Normalization = EmbeddingNormalization.L2 },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RagGenerationSearch(database, "manuals", other)
            .SearchAsync("q", new float[] { 1, 0, 0 }, Options).AsTask());
    }

    [Fact]
    public async Task SearchAsync_BudgetsAndCancellation_RejectAndReleaseLease()
    {
        using Tsdb database = Open();
        await Writer(database).WriteAsync(Snapshot(("a", "alpha"), ("b", "alpha")), Embed);
        var reader = new RagGenerationSearch(database, "manuals", Profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options with { MaxScannedDocuments = 1 }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options with { MaxSnapshotBytes = 1 }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options with { MaxScannedJsonCharacters = 1 }).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options with { MaxPostingVisits = 1 }).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.SearchAsync("alpha", new float[] { 1, 0, 0 }, Options, cancellationToken: new CancellationToken(true)).AsTask());
        await Writer(database).WriteAsync(Snapshot(("c", "charlie")), Embed);
        Assert.Empty(database.Generations.CleanupRetired("manuals").DeferredRevisions);
    }

    [Fact]
    public async Task SearchAsync_InvalidVector_RejectsBeforeOpeningGeneration()
    {
        using Tsdb database = Open();
        var reader = new RagGenerationSearch(database, "manuals", Profile);
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SearchAsync("q", new float[] { float.NaN, 0, 0 }, Options).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SearchAsync("q", new float[] { 0, 0, 0 }, Options).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => reader.SearchAsync("q", new float[] { 1 }, Options).AsTask());
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false, CleanupEnabled = false },
    });
    private static RagIngestionWriter Writer(Tsdb database) => new(database, "manuals", Profile);
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
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
