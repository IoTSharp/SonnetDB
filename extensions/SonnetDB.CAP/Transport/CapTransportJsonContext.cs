using System.Text.Json.Serialization;

namespace SonnetDB.CAP.Transport;

internal sealed record CapTransportEnvelope(int FormatVersion, Dictionary<string, string?> Headers, byte[] Body);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapTransportEnvelope))]
internal sealed partial class CapTransportJsonContext : JsonSerializerContext;
