using System.Text.Json.Serialization;

namespace SonnetDB.ObjectStorage;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SndbBucketRecord))]
[JsonSerializable(typeof(SndbObjectRecord))]
[JsonSerializable(typeof(SndbBucketLifecycleRecord))]
[JsonSerializable(typeof(SndbObjectAuditRecord))]
[JsonSerializable(typeof(SndbMultipartUploadRecord))]
[JsonSerializable(typeof(SndbMultipartPartRecord))]
[JsonSerializable(typeof(SndbPresignedTokenRecord))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SndbObjectStoreJsonContext : JsonSerializerContext;

internal sealed record SndbBucketRecord(
    string Name,
    string Purpose,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed record SndbObjectRecord(
    string Bucket,
    string Key,
    string VersionId,
    string ContentType,
    long SizeBytes,
    string ETag,
    string Sha256,
    string StoragePath,
    bool IsDeleteMarker,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    Dictionary<string, string> Metadata,
    Dictionary<string, string> Tags);

internal sealed record SndbBucketLifecycleRecord(
    string Bucket,
    int? ExpireCurrentAfterDays,
    int? ExpireNoncurrentAfterDays,
    int? ExpireDeleteMarkerAfterDays,
    DateTimeOffset UpdatedUtc);

internal sealed record SndbObjectAuditRecord(
    string Id,
    string Action,
    string Bucket,
    string? Key,
    string? VersionId,
    DateTimeOffset TimestampUtc,
    Dictionary<string, string> Details);

internal sealed record SndbMultipartUploadRecord(
    string Bucket,
    string Key,
    string UploadId,
    string ContentType,
    DateTimeOffset InitiatedUtc,
    DateTimeOffset ExpiresUtc,
    string Status,
    Dictionary<string, string> Metadata,
    Dictionary<string, string> Tags);

internal sealed record SndbMultipartPartRecord(
    string UploadId,
    int PartNumber,
    long SizeBytes,
    string ETag,
    string Sha256,
    string StoragePath,
    DateTimeOffset CreatedUtc);

internal sealed record SndbPresignedTokenRecord(
    string TokenHash,
    string Method,
    string Bucket,
    string Key,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ExpiresUtc);
