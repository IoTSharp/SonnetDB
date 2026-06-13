using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Kv;

namespace SonnetDB.ObjectStorage;

/// <summary>
/// SonnetDB 数据库内置对象桶存储。
/// </summary>
public sealed class SndbObjectStore
{
    private const string MetadataKeyspace = "__object_storage";
    private const string BucketPrefix = "bucket:";
    private const string ObjectPrefix = "object:";
    private const string LatestPrefix = "latest:";
    private const string UploadPrefix = "multipart:";
    private const string PartPrefix = "part:";
    private const string PresignPrefix = "presign:";
    private const string Active = "active";
    private const string Completed = "completed";
    private const string Aborted = "aborted";
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    private readonly KvKeyspace _metadata;
    private readonly string _contentRoot;

    /// <summary>
    /// 构造对象存储门面。
    /// </summary>
    public SndbObjectStore(Tsdb tsdb)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        _metadata = tsdb.Keyspaces.Open(MetadataKeyspace);
        _contentRoot = Path.Combine(tsdb.RootDirectory, "objects");
        Directory.CreateDirectory(_contentRoot);
    }

    /// <summary>
    /// 列出所有 bucket。
    /// </summary>
    public IReadOnlyList<SndbBucketInfo> ListBuckets()
    {
        return _metadata.ScanPrefix(BucketPrefix)
            .Select(static entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRecord))
            .Select(static record => new SndbBucketInfo(record.Name, record.Purpose, record.CreatedUtc, record.UpdatedUtc))
            .OrderBy(static bucket => bucket.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 创建 bucket；已存在时返回当前 bucket。
    /// </summary>
    public SndbBucketInfo CreateBucket(string bucket, string? purpose = null)
    {
        ValidateBucket(bucket);
        string normalizedPurpose = NormalizePurpose(purpose);
        string key = BucketKey(bucket);
        var existing = _metadata.GetEntry(key);
        if (existing is not null)
        {
            var record = Deserialize(existing.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRecord);
            return new SndbBucketInfo(record.Name, record.Purpose, record.CreatedUtc, record.UpdatedUtc);
        }

        var now = DateTimeOffset.UtcNow;
        var created = new SndbBucketRecord(bucket, normalizedPurpose, now, now);
        _metadata.Put(key, Serialize(created, SndbObjectStoreJsonContext.Default.SndbBucketRecord));
        Directory.CreateDirectory(Path.Combine(_contentRoot, BucketHash(bucket)));
        return new SndbBucketInfo(bucket, normalizedPurpose, now, now);
    }

    /// <summary>
    /// 获取 bucket。
    /// </summary>
    public SndbBucketInfo? GetBucket(string bucket)
    {
        ValidateBucket(bucket);
        var entry = _metadata.GetEntry(BucketKey(bucket));
        if (entry is null)
            return null;

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRecord);
        return new SndbBucketInfo(record.Name, record.Purpose, record.CreatedUtc, record.UpdatedUtc);
    }

    /// <summary>
    /// 删除空 bucket。
    /// </summary>
    public bool DeleteBucket(string bucket)
    {
        EnsureBucket(bucket);
        if (_metadata.ScanPrefix(LatestObjectPrefix(bucket), limit: 1).Count > 0)
            throw new SndbObjectStorageException("bucket_not_empty", $"Bucket '{bucket}' is not empty.");

        return _metadata.Delete(BucketKey(bucket));
    }

    /// <summary>
    /// 写入对象。
    /// </summary>
    public async Task<SndbObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        ArgumentNullException.ThrowIfNull(content);

        string versionId = CreateVersionId();
        string storagePath = BuildObjectStoragePath(bucket, key, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);

        var (size, etag, sha256) = await WriteContentAndHashAsync(content, storagePath, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new SndbObjectRecord(
            bucket,
            key,
            versionId,
            NormalizeContentType(contentType),
            size,
            etag,
            sha256,
            ToRelativeStoragePath(storagePath),
            IsDeleteMarker: false,
            now,
            now,
            NormalizeMap(metadata),
            NormalizeMap(tags));

        PersistObjectRecord(record);
        return ToInfo(record);
    }

    /// <summary>
    /// 列出 bucket 内当前可见对象。
    /// </summary>
    public SndbObjectListResult ListObjects(string bucket, string? prefix = null, int maxKeys = 1000)
    {
        EnsureBucket(bucket);
        if (maxKeys <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxKeys));

        string normalizedPrefix = prefix?.TrimStart('/') ?? string.Empty;
        var objects = new List<SndbObjectInfo>();
        foreach (var latest in _metadata.ScanPrefix(LatestObjectPrefix(bucket), limit: int.MaxValue))
        {
            string key = UnescapeKey(Utf8.GetString(latest.Key.Span)[LatestObjectPrefix(bucket).Length..]);
            if (!string.IsNullOrEmpty(normalizedPrefix) && !key.StartsWith(normalizedPrefix, StringComparison.Ordinal))
                continue;

            string versionId = Utf8.GetString(latest.Value.Span);
            var record = LoadObjectRecord(bucket, key, versionId);
            if (record is null || record.IsDeleteMarker)
                continue;

            objects.Add(ToInfo(record));
        }

        objects.Sort(static (left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new SndbObjectListResult(bucket, normalizedPrefix, maxKeys, objects.Take(maxKeys).ToArray());
    }

    /// <summary>
    /// 获取对象元数据。
    /// </summary>
    public SndbObjectInfo? HeadObject(string bucket, string key, string? versionId = null)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        var record = LoadObjectRecord(bucket, key, versionId);
        return record is null || record.IsDeleteMarker ? null : ToInfo(record);
    }

    /// <summary>
    /// 读取对象内容。
    /// </summary>
    public SndbObjectReadResult? OpenRead(string bucket, string key, SndbObjectRange? range = null, string? versionId = null)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        var record = LoadObjectRecord(bucket, key, versionId);
        if (record is null || record.IsDeleteMarker)
            return null;

        var path = ResolveStoragePath(record.StoragePath);
        if (!File.Exists(path))
            throw new SndbObjectStorageException("object_content_missing", $"Object content for '{bucket}/{key}' is missing.");

        var (offset, length) = range?.Resolve(record.SizeBytes) ?? (0, record.SizeBytes);
        Stream stream = File.OpenRead(path);
        if (offset > 0)
            stream.Seek(offset, SeekOrigin.Begin);

        return new SndbObjectReadResult(ToInfo(record), new BoundedReadStream(stream, length), offset, length, range.HasValue);
    }

    /// <summary>
    /// 复制对象。
    /// </summary>
    public async Task<SndbObjectInfo> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var source = OpenRead(sourceBucket, sourceKey)
            ?? throw new SndbObjectStorageException("object_not_found", $"Object '{sourceBucket}/{sourceKey}' was not found.");
        await using (source.Content)
        {
            return await PutObjectAsync(
                destinationBucket,
                destinationKey,
                source.Content,
                source.Info.ContentType,
                metadata ?? source.Info.Metadata,
                tags ?? source.Info.Tags,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 创建 delete marker。
    /// </summary>
    public SndbObjectInfo DeleteObject(string bucket, string key)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        var now = DateTimeOffset.UtcNow;
        var record = new SndbObjectRecord(
            bucket,
            key,
            CreateVersionId(),
            "application/x-sonnetdb-delete-marker",
            0,
            "\"delete-marker\"",
            new string('0', 64),
            string.Empty,
            IsDeleteMarker: true,
            now,
            now,
            [],
            []);

        PersistObjectRecord(record);
        return ToInfo(record);
    }

    /// <summary>
    /// 设置对象标签。
    /// </summary>
    public SndbObjectInfo SetObjectTags(string bucket, string key, IReadOnlyDictionary<string, string> tags)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        ArgumentNullException.ThrowIfNull(tags);
        var record = LoadObjectRecord(bucket, key)
            ?? throw new SndbObjectStorageException("object_not_found", $"Object '{bucket}/{key}' was not found.");
        if (record.IsDeleteMarker)
            throw new SndbObjectStorageException("object_not_found", $"Object '{bucket}/{key}' was deleted.");

        var updated = record with { Tags = NormalizeMap(tags), UpdatedUtc = DateTimeOffset.UtcNow };
        PersistObjectRecord(updated);
        return ToInfo(updated);
    }

    /// <summary>
    /// 创建 multipart upload 会话。
    /// </summary>
    public SndbMultipartUploadInfo InitiateMultipartUpload(
        string bucket,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        TimeSpan? expiresAfter = null)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        string uploadId = "mpu_" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var record = new SndbMultipartUploadRecord(
            bucket,
            key,
            uploadId,
            NormalizeContentType(contentType),
            now,
            now.Add(expiresAfter ?? TimeSpan.FromHours(24)),
            Active,
            NormalizeMap(metadata),
            NormalizeMap(tags));

        _metadata.Put(UploadKey(uploadId), Serialize(record, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord));
        return ToUploadInfo(record);
    }

    /// <summary>
    /// 上传 multipart 分片。
    /// </summary>
    public async Task<SndbMultipartPartInfo> UploadPartAsync(
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var upload = LoadUpload(uploadId);
        EnsureActiveUpload(upload);
        if (partNumber is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(partNumber), "Part number must be between 1 and 10000.");
        ArgumentNullException.ThrowIfNull(content);

        string storagePath = BuildMultipartStoragePath(upload.Bucket, upload.UploadId, partNumber);
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
        var (size, etag, sha256) = await WriteContentAndHashAsync(content, storagePath, cancellationToken).ConfigureAwait(false);
        var record = new SndbMultipartPartRecord(upload.UploadId, partNumber, size, etag, sha256, ToRelativeStoragePath(storagePath), DateTimeOffset.UtcNow);
        _metadata.Put(PartKey(upload.UploadId, partNumber), Serialize(record, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord));
        return new SndbMultipartPartInfo(partNumber, size, etag, sha256);
    }

    /// <summary>
    /// 完成 multipart upload。
    /// </summary>
    public async Task<SndbObjectInfo> CompleteMultipartUploadAsync(
        string uploadId,
        IReadOnlyList<int> partNumbers,
        CancellationToken cancellationToken = default)
    {
        var upload = LoadUpload(uploadId);
        EnsureActiveUpload(upload);
        if (partNumbers.Count == 0)
            throw new SndbObjectStorageException("multipart_parts_required", "At least one multipart part is required.");

        string versionId = CreateVersionId();
        string storagePath = BuildObjectStoragePath(upload.Bucket, upload.Key, versionId);
        Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);

        await using (var output = File.Create(storagePath))
        {
            foreach (int partNumber in partNumbers.Distinct().Order())
            {
                var part = LoadPart(upload.UploadId, partNumber)
                    ?? throw new SndbObjectStorageException("multipart_part_not_found", $"Multipart part {partNumber} was not found.");
                await using var input = File.OpenRead(ResolveStoragePath(part.StoragePath));
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
        }

        var (size, etag, sha256) = await HashFileAsync(storagePath, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new SndbObjectRecord(
            upload.Bucket,
            upload.Key,
            versionId,
            upload.ContentType,
            size,
            etag,
            sha256,
            ToRelativeStoragePath(storagePath),
            IsDeleteMarker: false,
            now,
            now,
            upload.Metadata,
            upload.Tags);

        PersistObjectRecord(record);
        _metadata.Put(UploadKey(upload.UploadId), Serialize(upload with { Status = Completed }, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord));
        CleanupParts(upload.UploadId);
        return ToInfo(record);
    }

    /// <summary>
    /// 中止 multipart upload。
    /// </summary>
    public void AbortMultipartUpload(string uploadId)
    {
        var upload = LoadUpload(uploadId);
        if (upload.Status == Completed)
            throw new SndbObjectStorageException("multipart_already_completed", "Multipart upload has already completed.");

        _metadata.Put(UploadKey(upload.UploadId), Serialize(upload with { Status = Aborted }, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord));
        CleanupParts(upload.UploadId);
    }

    /// <summary>
    /// 创建预签名访问令牌。
    /// </summary>
    public SndbPresignedObjectUrl CreatePresignedUrl(
        string baseUrl,
        string method,
        string bucket,
        string key,
        TimeSpan expiresAfter)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        method = NormalizeMethod(method);
        if (expiresAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiresAfter));

        string token = "sop_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        string tokenHash = Sha256Hex(Utf8.GetBytes(token));
        var now = DateTimeOffset.UtcNow;
        var record = new SndbPresignedTokenRecord(tokenHash, method, bucket, key, now, now.Add(expiresAfter));
        _metadata.Put(PresignKey(tokenHash), Serialize(record, SndbObjectStoreJsonContext.Default.SndbPresignedTokenRecord), record.ExpiresUtc);

        string separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        string url = baseUrl + separator + "sndb-presigned=" + Uri.EscapeDataString(token);
        return new SndbPresignedObjectUrl(url, method, bucket, key, record.ExpiresUtc);
    }

    /// <summary>
    /// 校验并解析预签名令牌。
    /// </summary>
    public bool TryValidatePresignedToken(string token, string method, string bucket, string key)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string tokenHash = Sha256Hex(Utf8.GetBytes(token.Trim()));
        var entry = _metadata.GetEntry(PresignKey(tokenHash));
        if (entry is null)
            return false;

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbPresignedTokenRecord);
        return record.ExpiresUtc > DateTimeOffset.UtcNow
            && string.Equals(record.Method, NormalizeMethod(method), StringComparison.Ordinal)
            && string.Equals(record.Bucket, bucket, StringComparison.Ordinal)
            && string.Equals(record.Key, key, StringComparison.Ordinal);
    }

    private void EnsureBucket(string bucket)
    {
        ValidateBucket(bucket);
        if (_metadata.GetEntry(BucketKey(bucket)) is null)
            throw new SndbObjectStorageException("bucket_not_found", $"Bucket '{bucket}' was not found.");
    }

    private SndbObjectRecord? LoadObjectRecord(string bucket, string key, string? versionId = null)
    {
        string? resolvedVersion = versionId;
        if (string.IsNullOrWhiteSpace(resolvedVersion))
        {
            var latest = _metadata.Get(LatestObjectKey(bucket, key));
            if (latest is null)
                return null;
            resolvedVersion = Utf8.GetString(latest);
        }

        var entry = _metadata.GetEntry(ObjectKey(bucket, key, resolvedVersion));
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectRecord);
    }

    private void PersistObjectRecord(SndbObjectRecord record)
    {
        _metadata.Put(ObjectKey(record.Bucket, record.Key, record.VersionId), Serialize(record, SndbObjectStoreJsonContext.Default.SndbObjectRecord));
        _metadata.Put(LatestObjectKey(record.Bucket, record.Key), Utf8.GetBytes(record.VersionId));
    }

    private SndbMultipartUploadRecord LoadUpload(string uploadId)
    {
        ValidateUploadId(uploadId);
        var entry = _metadata.GetEntry(UploadKey(uploadId));
        if (entry is null)
            throw new SndbObjectStorageException("multipart_not_found", $"Multipart upload '{uploadId}' was not found.");

        return Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord);
    }

    private SndbMultipartPartRecord? LoadPart(string uploadId, int partNumber)
    {
        var entry = _metadata.GetEntry(PartKey(uploadId, partNumber));
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
    }

    private void CleanupParts(string uploadId)
    {
        foreach (var entry in _metadata.ScanPrefix(PartPrefix + uploadId + ":", limit: int.MaxValue))
        {
            string key = Utf8.GetString(entry.Key.Span);
            var part = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
            TryDeleteFile(ResolveStoragePath(part.StoragePath));
            _metadata.Delete(key);
        }
    }

    private static void EnsureActiveUpload(SndbMultipartUploadRecord upload)
    {
        if (upload.Status != Active)
            throw new SndbObjectStorageException("multipart_not_active", "Multipart upload is not active.");
        if (upload.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new SndbObjectStorageException("multipart_expired", "Multipart upload has expired.");
    }

    private static SndbObjectInfo ToInfo(SndbObjectRecord record) =>
        new(
            record.Bucket,
            record.Key,
            record.VersionId,
            record.ContentType,
            record.SizeBytes,
            record.ETag,
            record.Sha256,
            record.IsDeleteMarker,
            record.CreatedUtc,
            record.UpdatedUtc,
            record.Metadata,
            record.Tags);

    private static SndbMultipartUploadInfo ToUploadInfo(SndbMultipartUploadRecord record) =>
        new(record.Bucket, record.Key, record.UploadId, record.ContentType, record.InitiatedUtc, record.ExpiresUtc, record.Metadata, record.Tags);

    private static async Task<(long Size, string ETag, string Sha256)> WriteContentAndHashAsync(
        Stream content,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var destination = File.Create(destinationPath);
        using var md5 = MD5.Create();
        using var sha256 = SHA256.Create();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                int read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                md5.TransformBlock(buffer, 0, read, null, 0);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }

            md5.TransformFinalBlock([], 0, 0);
            sha256.TransformFinalBlock([], 0, 0);
            return (size, QuoteHex(md5.Hash!), Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<(long Size, string ETag, string Sha256)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = File.OpenRead(path);
        using var md5 = MD5.Create();
        using var sha256 = SHA256.Create();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                md5.TransformBlock(buffer, 0, read, null, 0);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }

            md5.TransformFinalBlock([], 0, 0);
            sha256.TransformFinalBlock([], 0, 0);
            return (size, QuoteHex(md5.Hash!), Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string BuildObjectStoragePath(string bucket, string key, string versionId)
    {
        string objectHash = Sha256Hex(Utf8.GetBytes(bucket + "/" + key));
        return Path.Combine(_contentRoot, BucketHash(bucket), objectHash[..2], objectHash[2..4], versionId + ".bin");
    }

    private string BuildMultipartStoragePath(string bucket, string uploadId, int partNumber)
    {
        string uploadHash = Sha256Hex(Utf8.GetBytes(uploadId));
        return Path.Combine(_contentRoot, BucketHash(bucket), "multipart", uploadHash[..2], uploadId, partNumber.ToString("D5") + ".part");
    }

    private string ToRelativeStoragePath(string fullPath) =>
        Path.GetRelativePath(_contentRoot, fullPath).Replace('\\', '/');

    private string ResolveStoragePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(_contentRoot);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new SndbObjectStorageException("invalid_storage_path", "Object storage path is invalid.");

        return path;
    }

    private static string BucketKey(string bucket) => BucketPrefix + bucket;

    private static string LatestObjectPrefix(string bucket) => LatestPrefix + bucket + "/";

    private static string LatestObjectKey(string bucket, string key) => LatestObjectPrefix(bucket) + EscapeKey(key);

    private static string ObjectKey(string bucket, string key, string versionId) =>
        ObjectPrefix + bucket + "/" + EscapeKey(key) + "/" + versionId;

    private static string UploadKey(string uploadId) => UploadPrefix + uploadId;

    private static string PartKey(string uploadId, int partNumber) => PartPrefix + uploadId + ":" + partNumber.ToString("D5");

    private static string PresignKey(string tokenHash) => PresignPrefix + tokenHash;

    private static string CreateVersionId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D13") + "-" + Guid.NewGuid().ToString("N");

    private static string BucketHash(string bucket) => Sha256Hex(Utf8.GetBytes(bucket))[..16];

    private static string EscapeKey(string key) => Convert.ToBase64String(Utf8.GetBytes(key)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string UnescapeKey(string key)
    {
        string padded = key.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Utf8.GetString(Convert.FromBase64String(padded));
    }

    private static string NormalizeContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

    private static string NormalizePurpose(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) ? SndbBucketPurpose.General : purpose.Trim();

    private static string NormalizeMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        string normalized = method.Trim().ToUpperInvariant();
        return normalized is "GET" or "HEAD" or "PUT" or "DELETE"
            ? normalized
            : throw new ArgumentException($"Unsupported object method '{method}'.", nameof(method));
    }

    private static Dictionary<string, string> NormalizeMap(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null)
            return result;

        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;
            result[pair.Key.Trim()] = pair.Value ?? string.Empty;
        }

        return result;
    }

    private static void ValidateBucket(string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        if (bucket.Length is < 3 or > 63)
            throw new ArgumentException("Bucket name length must be between 3 and 63.", nameof(bucket));

        foreach (char ch in bucket)
        {
            bool valid = ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.';
            if (!valid)
                throw new ArgumentException("Bucket name must contain only lowercase letters, digits, '-' or '.'.", nameof(bucket));
        }
    }

    private static void ValidateObjectKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 1_024)
            throw new ArgumentOutOfRangeException(nameof(key), "Object key cannot exceed 1024 characters.");
    }

    private static void ValidateUploadId(string uploadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        if (!uploadId.StartsWith("mpu_", StringComparison.Ordinal) || uploadId.Length > 80)
            throw new ArgumentException("Invalid multipart upload id.", nameof(uploadId));
    }

    private static string QuoteHex(byte[] hash) => "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static T Deserialize<T>(ReadOnlySpan<byte> json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo)
        ?? throw new InvalidDataException("Object storage metadata is empty.");

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;
            int toRead = (int)Math.Min(count, _remaining);
            int read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return 0;
            int toRead = (int)Math.Min(buffer.Length, _remaining);
            int read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
