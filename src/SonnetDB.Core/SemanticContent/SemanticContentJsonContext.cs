using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>
/// Semantic Content 的 AOT JSON 元数据。
/// Server 若在 HTTP 或持久化边界使用这些类型，应复用该 context 的类型信息，避免反射序列化。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SemanticObjectReference))]
[JsonSerializable(typeof(SemanticContentChunk))]
[JsonSerializable(typeof(SemanticContentSegment))]
[JsonSerializable(typeof(SemanticEmbeddingBinding))]
[JsonSerializable(typeof(EmbeddingProfile))]
[JsonSerializable(typeof(SemanticDataEgressPolicy))]
[JsonSerializable(typeof(SemanticIndexStateInfo))]
[JsonSerializable(typeof(SemanticContentManifest))]
[JsonSerializable(typeof(SemanticContentValidationResult))]
[JsonSerializable(typeof(SemanticContentValidationFailure))]
[JsonSerializable(typeof(RagTextSnapshot))]
[JsonSerializable(typeof(RagIngestionSnapshot))]
[JsonSerializable(typeof(RagIngestionAction))]
[JsonSerializable(typeof(RagIngestionPlan))]
[JsonSerializable(typeof(IReadOnlyList<SemanticContentChunk>))]
[JsonSerializable(typeof(IReadOnlyList<SemanticContentSegment>))]
[JsonSerializable(typeof(IReadOnlyList<SemanticEmbeddingBinding>))]
[JsonSerializable(typeof(IReadOnlyList<SemanticContentModality>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, EmbeddingProfile>))]
[JsonSerializable(typeof(IReadOnlyList<SemanticContentManifest>))]
[JsonSerializable(typeof(IReadOnlyList<RagIngestionAction>))]
internal sealed partial class SemanticContentJsonContext : JsonSerializerContext;
