namespace SonnetDB.ObjectStorage;

/// <summary>
/// 对象桶摘要。
/// </summary>
public sealed record SndbBucketInfo(
    string Name,
    string Purpose,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// 对象元数据。
/// </summary>
public sealed record SndbObjectInfo(
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

/// <summary>
/// 对象列表结果。
/// </summary>
public sealed record SndbObjectListResult(
    string Bucket,
    string Prefix,
    int MaxKeys,
    IReadOnlyList<SndbObjectInfo> Objects);

/// <summary>
/// 对象读取结果。
/// </summary>
public sealed record SndbObjectReadResult(
    SndbObjectInfo Info,
    Stream Content,
    long Offset,
    long Length,
    bool IsRange);

/// <summary>
/// Multipart upload 会话摘要。
/// </summary>
public sealed record SndbMultipartUploadInfo(
    string Bucket,
    string Key,
    string UploadId,
    string ContentType,
    DateTimeOffset InitiatedUtc,
    DateTimeOffset ExpiresUtc,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// Multipart upload 分片元数据。
/// </summary>
public sealed record SndbMultipartPartInfo(
    int PartNumber,
    long SizeBytes,
    string ETag,
    string Sha256);

/// <summary>
/// 预签名对象 URL 信息。
/// </summary>
public sealed record SndbPresignedObjectUrl(
    string Url,
    string Method,
    string Bucket,
    string Key,
    DateTimeOffset ExpiresUtc);

/// <summary>
/// 对象读取范围。
/// </summary>
public readonly record struct SndbObjectRange(long Offset, long? Length)
{
    /// <summary>
    /// 按对象长度计算最终读取边界。
    /// </summary>
    public (long Offset, long Length) Resolve(long objectLength)
    {
        if (objectLength < 0)
            throw new ArgumentOutOfRangeException(nameof(objectLength));
        if (Offset < 0)
            throw new ArgumentOutOfRangeException(nameof(Offset));
        if (Offset >= objectLength)
            return (Offset, 0);

        long remaining = objectLength - Offset;
        long length = Length is null ? remaining : Math.Min(Length.Value, remaining);
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(Length));

        return (Offset, length);
    }
}

/// <summary>
/// 对象桶用途常量。
/// </summary>
public static class SndbBucketPurpose
{
    /// <summary>通用对象桶。</summary>
    public const string General = "general";

    /// <summary>IoTSharp BlobStorage 对象。</summary>
    public const string IoTSharpBlobStorage = "iotsharp-blob-storage";

    /// <summary>固件对象。</summary>
    public const string Firmware = "firmware";

    /// <summary>附件对象。</summary>
    public const string Attachment = "attachment";

    /// <summary>工件对象。</summary>
    public const string Artifact = "artifact";

    /// <summary>备份对象。</summary>
    public const string Backup = "backup";
}

/// <summary>
/// 对象存储异常。
/// </summary>
public sealed class SndbObjectStorageException : Exception
{
    /// <summary>
    /// 构造对象存储异常。
    /// </summary>
    public SndbObjectStorageException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>机器可读错误码。</summary>
    public string Code { get; }
}
