using System.Text.Json.Serialization;

namespace SonnetDB.Data.ObjectStorage;

internal sealed record ObjectBucketCreateRequest(string? Purpose = null);

internal sealed record ObjectBucketResponse(
    string Name,
    string Purpose,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

internal sealed record ObjectInfoResponse(
    string Bucket,
    string Key,
    string VersionId,
    string ContentType,
    long SizeBytes,
    string ETag,
    string Sha256,
    bool IsDeleteMarker,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    Dictionary<string, string> Metadata,
    Dictionary<string, string> Tags);

internal sealed record ObjectListResponse(
    string Bucket,
    string Prefix,
    int MaxKeys,
    string? ContinuationToken,
    string? NextContinuationToken,
    bool IsTruncated,
    ObjectInfoResponse[] Objects);

internal sealed record ObjectDeleteManyRequest(IReadOnlyList<string> Keys);

internal sealed record ObjectDeleteResultResponse(
    string Key,
    string VersionId,
    bool DeleteMarker,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record ObjectDeleteManyResponse(
    string Bucket,
    ObjectDeleteResultResponse[] Deleted);

internal sealed record ObjectCopyResponse(string ETag, string Sha256, string VersionId);

internal sealed record ObjectTagsRequest(Dictionary<string, string> Tags);

internal sealed record MultipartUploadCreateRequest(
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    int? ExpiresHours = null);

internal sealed record MultipartUploadCreateResponse(
    string Bucket,
    string Key,
    string UploadId,
    string ContentType,
    DateTimeOffset InitiatedUtc,
    DateTimeOffset ExpiresUtc,
    Dictionary<string, string> Metadata,
    Dictionary<string, string> Tags);

internal sealed record MultipartPartResponse(int PartNumber, long SizeBytes, string ETag, string Sha256);

internal sealed record MultipartCompleteRequest(IReadOnlyList<int> PartNumbers);

internal sealed record PresignedObjectUrlCreateRequest(string Method, int ExpiresMinutes);

internal sealed record PresignedObjectUrlResponse(
    string Url,
    string Method,
    string Bucket,
    string Key,
    DateTimeOffset ExpiresUtc);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ObjectBucketCreateRequest))]
[JsonSerializable(typeof(ObjectBucketResponse))]
[JsonSerializable(typeof(ObjectBucketResponse[]))]
[JsonSerializable(typeof(ObjectInfoResponse))]
[JsonSerializable(typeof(ObjectListResponse))]
[JsonSerializable(typeof(ObjectDeleteManyRequest))]
[JsonSerializable(typeof(ObjectDeleteResultResponse))]
[JsonSerializable(typeof(ObjectDeleteManyResponse))]
[JsonSerializable(typeof(ObjectInfoResponse[]))]
[JsonSerializable(typeof(ObjectCopyResponse))]
[JsonSerializable(typeof(ObjectTagsRequest))]
[JsonSerializable(typeof(MultipartUploadCreateRequest))]
[JsonSerializable(typeof(MultipartUploadCreateResponse))]
[JsonSerializable(typeof(MultipartPartResponse))]
[JsonSerializable(typeof(MultipartCompleteRequest))]
[JsonSerializable(typeof(PresignedObjectUrlCreateRequest))]
[JsonSerializable(typeof(PresignedObjectUrlResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class SndbObjectClientJsonContext : JsonSerializerContext;
