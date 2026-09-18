using System.Text;
using System.Text.Json;
using SonnetDB.Data;
using SonnetDB.Data.VectorData;
using SonnetDB.Documents;
using SonnetDB.Query;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SonnetDBVectorLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-vector-lifecycle-" + Guid.NewGuid().ToString("N"));
    private static readonly EmbeddingProfile Profile = new("fixture-v1", "fixture", "fixture", "1", 3,
        supportedModalities: [SemanticContentModality.Text]);

    [Fact]
    public async Task PreflightAsync_CatalogMismatchAndNonFinite_ReturnsStableIssues()
    {
        await using var connection = Open();
        using var vectors = new SonnetDBVectorStore(connection);
        CreateIndex(connection);
        var result = await vectors.PreflightAsync("docs", "vector", new float[] { float.NaN, 0 }, KnnMetric.L2);
        Assert.False(result.IsIndexCompatible);
        Assert.Equal(["vector_dimension_mismatch", "vector_metric_mismatch", "vector_non_finite"], result.Issues.Select(x => x.Code));
        Assert.Null(result.IsProfileCompatible);
        Assert.Equal("profile_unbound", result.ProfileStatus);
        Assert.Equal(3, result.Dimensions);
        Assert.Equal(KnnMetric.Cosine, result.Metric);
    }

    [Fact]
    public async Task PreflightAsync_ValidVectorDoesNotInventProfileBinding()
    {
        await using var connection = Open();
        using var vectors = new SonnetDBVectorStore(connection);
        CreateIndex(connection);
        var result = await vectors.PreflightAsync("docs", "vector", new float[] { 1, 0, 0 });
        Assert.True(result.IsIndexCompatible);
        Assert.Null(result.IsProfileCompatible);
        Assert.Null(result.GenerationId);
        Assert.Null(result.ProfileId);
        Assert.Equal("profile_unbound", result.ProfileStatus);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task PreflightRagAsync_PublishedProfile_UsesPersistentGeneration()
    {
        string generation;
        await using (var first = Open())
        {
            generation = (await PublishAsync(first)).Generation.GenerationId;
        }
        await using var reopened = Open();
        using var vectors = new SonnetDBVectorStore(reopened);
        var result = await vectors.PreflightRagAsync("manuals", Profile, new float[] { 1, 0, 0 });
        Assert.True(result.IsIndexCompatible);
        Assert.True(result.IsProfileCompatible);
        Assert.Equal("verified", result.ProfileStatus);
        Assert.Equal(generation, result.GenerationId);
        Assert.Equal(Profile.Id, result.ProfileId);
        Assert.Equal(RagIngestionWriter.VectorIndexName, result.Index);
        Assert.NotNull(reopened.UnderlyingTsdb!.Documents.Catalog.TryGet(result.Collection));
        string json = JsonSerializer.Serialize(result, SndbVectorLifecycleJsonContext.Default.SndbVectorPreflightResult);
        var decoded = JsonSerializer.Deserialize(json, SndbVectorLifecycleJsonContext.Default.SndbVectorPreflightResult)!;
        Assert.Equal(result.GenerationId, decoded.GenerationId);
        Assert.DoesNotContain("private alpha", json);
        Assert.DoesNotContain("embedding", json);
    }

    [Theory]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("revision")]
    [InlineData("modalities")]
    [InlineData("egress")]
    [InlineData("normalization")]
    [InlineData("null_modalities")]
    [InlineData("null_egress")]
    public async Task PreflightRagAsync_SameIdDifferentContract_RejectsProfile(string field)
    {
        await using var connection = Open();
        await PublishAsync(connection);
        using var vectors = new SonnetDBVectorStore(connection);
        EmbeddingProfile changed = field switch
        {
            "provider" => Profile with { Provider = "other" },
            "model" => Profile with { Model = "other" },
            "revision" => Profile with { Revision = "2" },
            "modalities" => Profile with { SupportedModalities = [SemanticContentModality.Document] },
            "egress" => Profile with { DataEgressPolicy = new(SemanticDataEgressMode.ConfiguredProvider, "other") },
            "normalization" => Profile with { Normalization = EmbeddingNormalization.L2 },
            "null_modalities" => Profile with { SupportedModalities = null! },
            "null_egress" => Profile with { DataEgressPolicy = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        var result = await vectors.PreflightRagAsync("manuals", changed, new float[] { 1, 0, 0 });
        Assert.True(result.IsIndexCompatible);
        Assert.False(result.IsProfileCompatible);
        Assert.Equal("mismatch", result.ProfileStatus);
        Assert.Equal("vector_profile_mismatch", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task GetIndexHealthAsync_Reopen_DoesNotOpenOrRebuildDerivedIndex()
    {
        await using (var first = Open())
        {
            CreateIndex(first);
            first.UnderlyingTsdb!.Documents.Open("docs").Insert("a", "{\"v\":[1,0,0]}");
            using var vectors = new SonnetDBVectorStore(first);
            var loaded = await vectors.GetIndexHealthAsync("docs", "vector");
            Assert.Equal("loaded", loaded.State);
            Assert.Equal(1, loaded.GraphVectorCount);
        }
        await using var reopened = Open();
        using var coldVectors = new SonnetDBVectorStore(reopened);
        var cold = await coldVectors.GetIndexHealthAsync("docs", "vector");
        Assert.Equal("not_loaded", cold.State);
        Assert.Null(cold.GraphVectorCount);
        Assert.Equal("$.v", cold.Definition.Path);
        // 重复只读不会把尚未打开的集合变成 loaded。
        var repeated = await coldVectors.GetIndexHealthAsync("docs", "vector");
        Assert.Equal(cold, repeated);
        string json = JsonSerializer.Serialize(cold, SndbVectorLifecycleJsonContext.Default.DocumentVectorIndexHealth);
        Assert.Equal(cold, JsonSerializer.Deserialize(json, SndbVectorLifecycleJsonContext.Default.DocumentVectorIndexHealth));
    }

    [Fact]
    public async Task PreflightRagAsync_NormalizationAndCosineZero_MatchesSearchConstraints()
    {
        await using var connection = Open();
        EmbeddingProfile normalized = Profile with { Normalization = EmbeddingNormalization.L2 };
        await PublishAsync(connection, normalized);
        using var vectors = new SonnetDBVectorStore(connection);
        var zero = await vectors.PreflightRagAsync("manuals", normalized, new float[] { 0, 0, 0 });
        Assert.False(zero.IsIndexCompatible);
        Assert.True(zero.IsProfileCompatible);
        Assert.Contains(zero.Issues, issue => issue.Code == "vector_zero_norm");
        var scaled = await vectors.PreflightRagAsync("manuals", normalized, new float[] { 2, 0, 0 });
        Assert.False(scaled.IsIndexCompatible);
        Assert.Equal("vector_normalization_mismatch", Assert.Single(scaled.Issues).Code);
        var unit = await vectors.PreflightRagAsync("manuals", normalized, new float[] { 1, 0, 0 });
        Assert.True(unit.IsIndexCompatible);
        Assert.Empty(unit.Issues);
    }

    [Theory]
    [InlineData(2, KnnMetric.Cosine)]
    [InlineData(3, KnnMetric.L2)]
    public async Task PreflightRagAsync_CatalogDriftsFromPersistentProfile_RejectsContract(int dimensions, KnnMetric metric)
    {
        await using var connection = Open();
        var published = await PublishAsync(connection);
        string collection = Assert.Single(published.Generation.Resources,
            resource => resource.Role == RagIngestionWriter.ChunksResourceRole).Name;
        var documents = connection.UnderlyingTsdb!.Documents;
        Assert.True(documents.DropVectorIndex(collection, RagIngestionWriter.VectorIndexName));
        documents.CreateVectorIndex(collection, new(RagIngestionWriter.VectorIndexName, "$.embedding", dimensions, metric));
        using var vectors = new SonnetDBVectorStore(connection);
        var result = await vectors.PreflightRagAsync("manuals", Profile, new float[] { 1, 0, 0 });
        Assert.False(result.IsIndexCompatible);
        Assert.False(result.IsProfileCompatible);
        Assert.Contains(result.Issues, issue => issue.Code == "vector_profile_mismatch");
    }

    [Fact]
    public async Task EnsureMatchingGeneration_StatusFromRetiredGeneration_RejectsMixedSnapshot()
    {
        await using var connection = Open();
        await PublishAsync(connection);
        var manager = new RagIngestionManager(connection.UnderlyingTsdb!, "manuals");
        var oldStatus = (await manager.GetStatusAsync()).Active!;
        await manager.RebuildAsync(Profile with { Id = "fixture-v2", Revision = "2" }, oldStatus.Revision,
            static (_, token) => { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new float[] { 1, 0, 0 }); });
        using var current = connection.UnderlyingTsdb!.Generations.AcquireActive("manuals");
        Assert.Throws<InvalidOperationException>(() => SonnetDBVectorLifecycleExtensions.EnsureMatchingGeneration(oldStatus, current));
        using var vectors = new SonnetDBVectorStore(connection);
        var result = await vectors.PreflightRagAsync("manuals", Profile, new float[] { 1, 0, 0 });
        Assert.Equal(current.Generation.GenerationId, result.GenerationId);
        Assert.False(result.IsProfileCompatible);
    }

    [Fact]
    public async Task Lifecycle_MissingIndexAndClosedHealth_FailWithoutCreatingIndex()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var vectors = new SonnetDBVectorStore(connection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => vectors.GetIndexHealthAsync("docs", "missing"));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.False(Directory.Exists(_root));
        connection.Open();
        connection.UnderlyingTsdb!.Documents.Create(DocumentCollectionSchema.Create("docs"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => vectors.PreflightAsync("docs", "missing", new float[] { 1, 0, 0 }));
        Assert.Empty(connection.UnderlyingTsdb.Documents.Catalog.TryGet("docs")!.VectorIndexes);
    }

    [Fact]
    public async Task Lifecycle_PreCancelled_DoesNotOpenConnection()
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        using var vectors = new SonnetDBVectorStore(connection);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vectors.PreflightAsync("docs", "vector", new float[] { 1, 0, 0 }, cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vectors.GetIndexHealthAsync("docs", "vector", cancelled.Token));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Lifecycle_RemoteConnection_ExplicitlyRejectsUnavailableTransport()
    {
        await using var connection = new SndbConnection("Data Source=sonnetdb+http://127.0.0.1:1/test");
        using var vectors = new SonnetDBVectorStore(connection);
        await Assert.ThrowsAsync<NotSupportedException>(() => vectors.PreflightAsync("docs", "vector", new float[] { 1, 0, 0 }));
        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private SndbConnection Open()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }

    private static void CreateIndex(SndbConnection connection)
    {
        var database = connection.UnderlyingTsdb!;
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        database.Documents.CreateVectorIndex("docs", new("vector", "$.v", 3));
    }

    private static async Task<RagIngestionWriteResult> PublishAsync(SndbConnection connection, EmbeddingProfile? profile = null)
    {
        const string text = "private alpha";
        var chunks = RagTextChunker.Chunk("a", text);
        var manifest = new SemanticContentManifest("a", new("manuals", "a", chunks.ContentHash), chunks.ContentHash,
            "text/plain", SemanticContentModality.Text, Encoding.UTF8.GetByteCount(text))
        { Text = text, Chunks = chunks.Chunks };
        var writer = new RagIngestionWriter(connection.UnderlyingTsdb!, "manuals", profile ?? Profile,
            new() { MaxEmbeddingAttempts = 1, MaxDuration = TimeSpan.FromSeconds(10) });
        return await writer.WriteAsync(new([manifest]), static (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new float[] { 1, 0, 0 });
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
