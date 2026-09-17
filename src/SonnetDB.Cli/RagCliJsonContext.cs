using System.Text.Json.Serialization;
using SonnetDB.SemanticContent;

namespace SonnetDB.Cli;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RagCliBundle
{
    public int SchemaVersion { get; init; } = 1;
    public required EmbeddingProfile Profile { get; init; }
    public RagIngestionSnapshot? Snapshot { get; init; }
    public required List<RagCliVector> Vectors { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RagCliVector(string ChunkId, string TextSha256, float[] Values);

internal sealed record RagCliReport(
    string Status, string Stream, long? Revision, string? GenerationId,
    int AddedContents, int UpdatedContents, int DeletedContents,
    int EmbeddedChunks, int ReusedChunks, int ValidatedChunks, bool TargetNotRead);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 64)]
[JsonSerializable(typeof(RagCliBundle))]
[JsonSerializable(typeof(RagCliReport))]
internal partial class RagCliJsonContext : JsonSerializerContext;
