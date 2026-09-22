using System.Text.Json.Serialization;

namespace SonnetDB.Cdc;

/// <summary>
/// CDC 合同 DTO 的 Native AOT JSON 元数据。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CdcEvent))]
[JsonSerializable(typeof(CdcEventMetadata))]
[JsonSerializable(typeof(CdcCheckpoint))]
public sealed partial class CdcJsonContext : JsonSerializerContext;
