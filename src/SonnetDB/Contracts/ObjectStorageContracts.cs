namespace SonnetDB.Contracts;

/// <summary>创建对象桶请求。</summary>
public sealed record ObjectBucketCreateRequest(string? Purpose = null);

/// <summary>对象桶响应。</summary>
public sealed record ObjectBucketResponse(
    string Name,
    string Purpose,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>对象元数据响应。</summary>
public sealed record ObjectInfoResponse(
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
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>对象列表响应。</summary>
public sealed record ObjectListResponse(
    string Bucket,
    string Prefix,
    int MaxKeys,
    string? ContinuationToken,
    string? NextContinuationToken,
    bool IsTruncated,
    IReadOnlyList<ObjectInfoResponse> Objects);

/// <summary>批量删除对象请求。</summary>
public sealed record ObjectDeleteManyRequest(IReadOnlyList<string> Keys);

/// <summary>批量删除单个对象响应。</summary>
public sealed record ObjectDeleteResultResponse(
    string Key,
    string VersionId,
    bool DeleteMarker,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>批量删除对象响应。</summary>
public sealed record ObjectDeleteManyResponse(
    string Bucket,
    IReadOnlyList<ObjectDeleteResultResponse> Deleted);

/// <summary>对象版本列表响应。</summary>
public sealed record ObjectVersionListResponse(
    string Bucket,
    string? Key,
    IReadOnlyList<ObjectInfoResponse> Versions);

/// <summary>复制对象响应。</summary>
public sealed record ObjectCopyResponse(string ETag, string Sha256, string VersionId);

/// <summary>对象标签请求。</summary>
public sealed record ObjectTagsRequest(IReadOnlyDictionary<string, string> Tags);

/// <summary>Multipart upload 初始化请求。</summary>
public sealed record MultipartUploadCreateRequest(
    string? ContentType = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyDictionary<string, string>? Tags = null,
    int? ExpiresHours = null);

/// <summary>Multipart upload 初始化响应。</summary>
public sealed record MultipartUploadCreateResponse(
    string Bucket,
    string Key,
    string UploadId,
    string ContentType,
    DateTimeOffset InitiatedUtc,
    DateTimeOffset ExpiresUtc,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Multipart 分片上传响应。</summary>
public sealed record MultipartPartResponse(int PartNumber, long SizeBytes, string ETag, string Sha256);

/// <summary>Multipart 完成请求。</summary>
public sealed record MultipartCompleteRequest(IReadOnlyList<int> PartNumbers);

/// <summary>预签名 URL 创建请求。</summary>
public sealed record PresignedObjectUrlCreateRequest(string Method, int ExpiresMinutes);

/// <summary>预签名 URL 响应。</summary>
public sealed record PresignedObjectUrlResponse(
    string Url,
    string Method,
    string Bucket,
    string Key,
    DateTimeOffset ExpiresUtc);

/// <summary>Bucket 生命周期策略请求。</summary>
public sealed record ObjectLifecycleRequest(
    int? ExpireCurrentAfterDays = null,
    int? ExpireNoncurrentAfterDays = null,
    int? ExpireDeleteMarkerAfterDays = null);

/// <summary>Bucket 生命周期策略响应。</summary>
public sealed record ObjectLifecycleResponse(
    string Bucket,
    int? ExpireCurrentAfterDays,
    int? ExpireNoncurrentAfterDays,
    int? ExpireDeleteMarkerAfterDays,
    DateTimeOffset UpdatedUtc);

/// <summary>Bucket 生命周期执行响应。</summary>
public sealed record ObjectLifecycleApplyResponse(
    string Bucket,
    int ExpiredCurrentObjects,
    int RemovedNoncurrentVersions,
    int RemovedDeleteMarkers);

/// <summary>对象桶审计记录响应。</summary>
public sealed record ObjectAuditEntryResponse(
    string Id,
    string Action,
    string Bucket,
    string? Key,
    string? VersionId,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Details);

/// <summary>对象桶审计列表响应。</summary>
public sealed record ObjectAuditListResponse(
    string Bucket,
    IReadOnlyList<ObjectAuditEntryResponse> Entries);
