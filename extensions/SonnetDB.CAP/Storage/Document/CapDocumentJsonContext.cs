using System.Text.Json.Serialization;

namespace SonnetDB.CAP.Storage.Document;

internal sealed record CapStoredMessage(
    int FormatVersion, string Kind, string Version, string Bucket, string DbId,
    string Name, string? Group, string Content, int Retries, long Added,
    long? ExpiresAt, string StatusName);

internal sealed record CapStoredLock(int FormatVersion, string Instance, long ExpiresAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapStoredMessage))]
[JsonSerializable(typeof(CapStoredLock))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(long))]
internal sealed partial class CapDocumentJsonContext : JsonSerializerContext;
