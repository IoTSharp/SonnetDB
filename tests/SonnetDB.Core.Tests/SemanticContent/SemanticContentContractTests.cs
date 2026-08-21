using System.Text.Json;
using SonnetDB.SemanticContent;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Core.Tests.SemanticContent;

/// <summary>
/// Semantic Content 合同、校验和 source-generated JSON 的单元测试。
/// </summary>
public sealed class SemanticContentContractTests
{
    [Fact]
    public void Validate_WithObjectReferenceChunksSegmentsAndProfile_Passes()
    {
        var profile = CreateImageProfile();
        var now = DateTimeOffset.Parse("2026-08-03T00:00:00Z");
        var manifest = CreateManifest(now) with
        {
            IndexState = new SemanticIndexStateInfo(
                SemanticIndexState.Ready,
                attempt: 1,
                updatedUtc: now),
            Embeddings =
            [
                new SemanticEmbeddingBinding("image", profile.Id, "embedding.image")
                {
                    IndexState = new SemanticIndexStateInfo(
                        SemanticIndexState.Ready,
                        attempt: 1,
                        updatedUtc: now),
                },
            ],
        };

        var result = SemanticContentValidator.Validate(
            manifest,
            new Dictionary<string, EmbeddingProfile>(StringComparer.Ordinal)
            {
                [profile.Id] = profile,
            });

        Assert.True(result.IsValid);
        Assert.Empty(result.Failures);
        Assert.Equal("v1", manifest.ObjectRef!.Version);
        Assert.Equal("chunk-0", manifest.Chunks[0].StableId);
        Assert.Equal("segment-0", manifest.Segments[0].SegmentId);
    }

    [Fact]
    public void Validate_WithMissingObjectIdentityAndInvalidMime_ReportsFailures()
    {
        var manifest = CreateManifest() with
        {
            ObjectRef = new SemanticObjectReference("images", "camera/1.jpg"),
            MimeType = "image",
        };

        var result = SemanticContentValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure =>
            failure.Path == "objectRef" && failure.Rule == "identity");
        Assert.Contains(result.Failures, failure =>
            failure.Path == "mimeType" && failure.Rule == "format");
    }

    [Fact]
    public void Validate_WithDuplicateIdsAndInvalidRanges_ReportsFailures()
    {
        var manifest = CreateManifest() with
        {
            Chunks =
            [
                new SemanticContentChunk("same", 0, "first", startOffset: 10, endOffset: 10),
                new SemanticContentChunk("same", 1, "second", startOffset: 2, endOffset: 1),
            ],
            Segments =
            [
                new SemanticContentSegment("segment", 0, startMs: 100, endMs: 100),
                new SemanticContentSegment("segment", 1, startMs: -1, endMs: 10),
            ],
        };

        var result = SemanticContentValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure => failure.Rule == "unique");
        Assert.True(result.Failures.Count(failure => failure.Rule == "range") >= 3);
    }

    [Fact]
    public void Validate_WithFailedStateWithoutError_RejectsIndexState()
    {
        var manifest = CreateManifest() with
        {
            IndexState = new SemanticIndexStateInfo(SemanticIndexState.Failed),
        };

        var result = SemanticContentValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure =>
            failure.Path == "indexState.lastError" && failure.Rule == "required");
    }

    [Fact]
    public void Validate_WithUnsupportedProfileModality_RejectsBinding()
    {
        var textProfile = new EmbeddingProfile(
            "text-v1",
            "local",
            "text-model",
            "1",
            dimensions: 384,
            supportedModalities: [SemanticContentModality.Text]);
        var manifest = CreateManifest() with
        {
            Embeddings = [new SemanticEmbeddingBinding("image", textProfile.Id)],
        };

        var result = SemanticContentValidator.Validate(
            manifest,
            new Dictionary<string, EmbeddingProfile>(StringComparer.Ordinal)
            {
                [textProfile.Id] = textProfile,
            });

        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure =>
            failure.Path == "embeddings[0].profileId" && failure.Rule == "modality");
    }

    [Fact]
    public void ValidateOrThrow_WithInvalidManifest_ContainsStructuredPath()
    {
        var manifest = CreateManifest() with { Id = string.Empty };

        var exception = Assert.Throws<ArgumentException>(
            () => SemanticContentValidator.ValidateOrThrow(manifest));

        Assert.Contains("[id] required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRoundTrip_UsesCamelCaseAndStableEnumNames()
    {
        var profile = CreateImageProfile();
        var manifest = CreateManifest() with
        {
            EmbeddingProfileId = profile.Id,
            IndexState = new SemanticIndexStateInfo(
                SemanticIndexState.Running,
                attempt: 2,
                updatedUtc: DateTimeOffset.Parse("2026-08-03T00:00:00Z")),
            Embeddings = [new SemanticEmbeddingBinding("image", profile.Id)],
        };

        string json = JsonSerializer.Serialize(
            manifest,
            SemanticContentJsonContext.Default.SemanticContentManifest);

        Assert.Contains("\"objectRef\"", json, StringComparison.Ordinal);
        Assert.Contains("\"mimeType\":\"image/jpeg\"", json, StringComparison.Ordinal);
        Assert.Contains("\"modality\":\"Image\"", json, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"Running\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stableId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("chunkId", json, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize(
            json,
            SemanticContentJsonContext.Default.SemanticContentManifest);

        Assert.NotNull(roundTripped);
        Assert.Equal(manifest.Id, roundTripped!.Id);
        Assert.Equal(manifest.ObjectRef, roundTripped.ObjectRef);
        Assert.Equal(manifest.Chunks, roundTripped.Chunks);
        Assert.Equal(manifest.Segments, roundTripped.Segments);
        Assert.Equal(SemanticIndexState.Running, roundTripped.IndexState.State);
        Assert.Equal(2, roundTripped.IndexState.Attempt);
    }

    [Fact]
    public void ProfileCompatibility_RejectsChangedIdentityDimensionMetricAndNormalization()
    {
        var baseline = CreateImageProfile();

        Assert.True(baseline.IsCompatibleWith(baseline with { Provider = "same-provider-alias" }));
        Assert.False(baseline.IsCompatibleWith(baseline with { Id = "other" }));
        Assert.False(baseline.IsCompatibleWith(baseline with { Dimensions = baseline.Dimensions + 1 }));
        Assert.False(baseline.IsCompatibleWith(baseline with { Metric = KnnMetric.L2 }));
        Assert.False(baseline.IsCompatibleWith(
            baseline with { Normalization = EmbeddingNormalization.None }));
    }

    private static SemanticContentManifest CreateManifest(DateTimeOffset? now = null)
    {
        DateTimeOffset timestamp = now ?? DateTimeOffset.Parse("2026-08-03T00:00:00Z");
        return new SemanticContentManifest(
            "content-1",
            new SemanticObjectReference("images", "camera/1.jpg", versionId: "v1"),
            "sha256:abc",
            "image/jpeg",
            SemanticContentModality.Image,
            sizeBytes: 1234,
            source: "camera")
        {
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp,
            Text = "camera frame",
            Chunks =
            [
                new SemanticContentChunk(
                    "chunk-0",
                    ordinal: 0,
                    "camera frame",
                    startOffset: 0,
                    endOffset: 12,
                    contentHash: "sha256:chunk"),
            ],
            Segments =
            [
                new SemanticContentSegment(
                    "segment-0",
                    ordinal: 0,
                    startMs: 0,
                    endMs: 1000,
                    text: "camera frame")
                {
                    FrameIndex = 0,
                    ContentHash = "sha256:segment",
                },
            ],
        };
    }

    private static EmbeddingProfile CreateImageProfile()
        => new(
            "image-v1",
            "local",
            "image-model",
            "2026-01",
            dimensions: 768,
            metric: KnnMetric.Cosine,
            normalization: EmbeddingNormalization.L2,
            supportedModalities: [SemanticContentModality.Image]);
}
