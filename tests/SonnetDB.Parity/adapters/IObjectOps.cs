namespace SonnetDB.Parity.Adapters;

/// <summary>
/// 对象桶支柱的语义操作集合。
/// </summary>
public interface IObjectOps
{
    /// <summary>重建场景 bucket。</summary>
    Task ResetBucketAsync(string bucket, CancellationToken ct);

    /// <summary>写入对象。</summary>
    Task<ObjectPutResult> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct);

    /// <summary>读取对象。</summary>
    Task<ObjectReadResult?> GetAsync(string bucket, string key, ObjectRange? range, CancellationToken ct);

    /// <summary>列出对象。</summary>
    Task<ObjectListPage> ListAsync(string bucket, string prefix, int maxKeys, string? continuationToken, CancellationToken ct);

    /// <summary>复制对象。</summary>
    Task<ObjectPutResult> CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken ct);

    /// <summary>删除对象并创建等价 delete marker。</summary>
    Task DeleteAsync(string bucket, string key, CancellationToken ct);

    /// <summary>批量删除对象。</summary>
    Task<IReadOnlyList<ObjectDeleteResult>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct);

    /// <summary>创建 multipart upload。</summary>
    Task<string> InitiateMultipartAsync(string bucket, string key, string contentType, CancellationToken ct);

    /// <summary>上传 multipart 分片。</summary>
    Task UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream content, CancellationToken ct);

    /// <summary>完成 multipart upload。</summary>
    Task<ObjectPutResult> CompleteMultipartAsync(string bucket, string key, string uploadId, IReadOnlyList<int> partNumbers, CancellationToken ct);

    /// <summary>创建可生命周期内访问的 GET URL。</summary>
    Task<string> CreatePresignedGetUrlAsync(string bucket, string key, TimeSpan expiresAfter, CancellationToken ct);
}

/// <summary>
/// 不支持对象桶能力的空操作对象。
/// </summary>
public sealed class UnsupportedObjectOps : IObjectOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedObjectOps Instance { get; } = new();

    private UnsupportedObjectOps() { }

    /// <inheritdoc />
    public Task ResetBucketAsync(string bucket, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<ObjectPutResult> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct) => Unsupported<ObjectPutResult>();

    /// <inheritdoc />
    public Task<ObjectReadResult?> GetAsync(string bucket, string key, ObjectRange? range, CancellationToken ct) => Unsupported<ObjectReadResult?>();

    /// <inheritdoc />
    public Task<ObjectListPage> ListAsync(string bucket, string prefix, int maxKeys, string? continuationToken, CancellationToken ct) => Unsupported<ObjectListPage>();

    /// <inheritdoc />
    public Task<ObjectPutResult> CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken ct) => Unsupported<ObjectPutResult>();

    /// <inheritdoc />
    public Task DeleteAsync(string bucket, string key, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<IReadOnlyList<ObjectDeleteResult>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct) => Unsupported<IReadOnlyList<ObjectDeleteResult>>();

    /// <inheritdoc />
    public Task<string> InitiateMultipartAsync(string bucket, string key, string contentType, CancellationToken ct) => Unsupported<string>();

    /// <inheritdoc />
    public Task UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream content, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<ObjectPutResult> CompleteMultipartAsync(string bucket, string key, string uploadId, IReadOnlyList<int> partNumbers, CancellationToken ct) => Unsupported<ObjectPutResult>();

    /// <inheritdoc />
    public Task<string> CreatePresignedGetUrlAsync(string bucket, string key, TimeSpan expiresAfter, CancellationToken ct) => Unsupported<string>();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持对象桶操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持对象桶操作。");
}

/// <summary>规范化对象范围。</summary>
public readonly record struct ObjectRange(long Offset, long? Length);

/// <summary>规范化对象写入结果。</summary>
public sealed record ObjectPutResult(string Key, long SizeBytes, string ETag);

/// <summary>规范化对象读取结果。</summary>
public sealed record ObjectReadResult(byte[] Content, string ContentType, long SizeBytes);

/// <summary>规范化对象列表页。</summary>
public sealed record ObjectListPage(
    IReadOnlyList<ObjectListItem> Objects,
    bool IsTruncated,
    string? NextContinuationToken);

/// <summary>规范化对象列表项。</summary>
public sealed record ObjectListItem(string Key, long SizeBytes);

/// <summary>规范化对象删除结果。</summary>
public sealed record ObjectDeleteResult(string Key, bool DeleteMarker, string? ErrorCode);
