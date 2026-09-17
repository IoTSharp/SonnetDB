using System.Text.Json.Serialization;
using SonnetDB.SemanticContent;

namespace SonnetDB.Cli;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RagCliBundle
{
    public int SchemaVersion { get; init; } = 1;
    public required EmbeddingProfile Profile { get; init; }
    public RagIngestionSnapshot? Snapshot { get; init; }
    public List<RagCliVector> Vectors { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RagCliVector(string ChunkId, string TextSha256, float[] Values);

internal sealed record RagCliReport(
    string Status, string Stream, long? Revision, string? GenerationId,
    int AddedContents, int UpdatedContents, int DeletedContents,
    int EmbeddedChunks, int ReusedChunks, int ValidatedChunks, bool TargetNotRead);

internal sealed record RagOnlineEmbeddingRequest(string Model, string Input,
    [property: JsonPropertyName("encoding_format")] string EncodingFormat = "float");
internal sealed record RagOnlineEmbeddingResponse(string? Model, List<RagOnlineEmbeddingData>? Data);
internal sealed record RagOnlineEmbeddingData(int? Index, float[]? Embedding);
internal sealed record RagOnlineAudit(DateTimeOffset Time, string RequestId, string ProfileId,
    string Endpoint, string TextSha256, string Outcome);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 64)]
[JsonSerializable(typeof(RagCliBundle))]
[JsonSerializable(typeof(RagCliReport))]
[JsonSerializable(typeof(RagOnlineEmbeddingRequest))]
[JsonSerializable(typeof(RagOnlineEmbeddingResponse))]
[JsonSerializable(typeof(RagOnlineAudit))]
internal partial class RagCliJsonContext : JsonSerializerContext;
