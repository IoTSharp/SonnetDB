using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RagIngestionWriterCheckpoint))]
[JsonSerializable(typeof(RagIngestionChunkDocument))]
[JsonSerializable(typeof(EmbeddingProfile))]
internal sealed partial class RagIngestionWriterJsonContext : JsonSerializerContext;
