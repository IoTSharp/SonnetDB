using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;
using SonnetDB.Protocol;

namespace SonnetDB.Data.ObjectStorage;

/// <summary>
/// SonnetDB 对象桶客户端，统一支持嵌入式与远程 SonnetDB。
/// </summary>
public sealed class SndbObjectStorageClient : IDisposable
{
    /// <summary>帧 put 内容字节上限：单帧 payload 上限扣除元数据头空间（db/bucket/key/contentType/maps/varints）。</summary>
    private const long MaxFrameContentBytes = FrameHeader.MaxFramePayloadBytes - (64 * 1024);

    private static readonly IReadOnlyDictionary<string, string> EmptyMap = new Dictionary<string, string>(0);

    private readonly SndbConnectionStringBuilder _builder;
    private readonly bool _useDedicatedHttpHandler;
    private HttpClient? _http;
    private FrameChannel? _frames;
    private Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>
    /// 使用 SonnetDB 连接字符串创建对象桶客户端。
    /// </summary>
    /// <param name="connectionString">SonnetDB 连接字符串。</param>
    public SndbObjectStorageClient(string connectionString)
        : this(connectionString, useDedicatedHttpHandler: false)
    {
    }

    /// <summary>
    /// 使用 SonnetDB 连接字符串创建对象桶客户端，并可选择独立的 HTTP 连接池。
    /// </summary>
    /// <param name="connectionString">SonnetDB 连接字符串。</param>
    /// <param name="useDedicatedHttpHandler">
    /// 是否为此客户端创建独立 HTTP 连接池；默认构造函数仍复用进程级共享连接池。
    /// </param>
    public SndbObjectStorageClient(string connectionString, bool useDedicatedHttpHandler)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
        _useDedicatedHttpHandler = useDedicatedHttpHandler;
        Open();
    }

    /// <summary>
    /// 当前连接模式。
    /// </summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>
    /// 远程数据库名或嵌入式数据目录。
    /// </summary>
    public string Database => _database;

    /// <summary>
    /// 列出所有 bucket。
    /// </summary>
    public async Task<IReadOnlyList<SndbBucketInfo>> ListBucketsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).ListBuckets();

        using var response = await _http!.GetAsync($"v1/db/{Uri.EscapeDataString(_database)}/s3", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectBucketResponseArray, cancellationToken)
            .ConfigureAwait(false);
        return body.Select(static bucket => new SndbBucketInfo(bucket.Name, bucket.Purpose, bucket.CreatedUtc, bucket.UpdatedUtc))
            .ToArray();
    }

    /// <summary>
    /// 创建 bucket。
    /// </summary>
    public async Task<SndbBucketInfo> CreateBucketAsync(string bucket, string? purpose = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).CreateBucket(bucket, purpose);

        using var response = await PutJsonAsync(
            BucketUrl(bucket),
            new ObjectBucketCreateRequest(purpose),
            SndbObjectClientJsonContext.Default.ObjectBucketCreateRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectBucketResponse, cancellationToken).ConfigureAwait(false);
        return new SndbBucketInfo(body.Name, body.Purpose, body.CreatedUtc, body.UpdatedUtc);
    }

    /// <summary>
    /// 删除空 bucket。
    /// </summary>
    public async Task<bool> DeleteBucketAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).DeleteBucket(bucket);

        using var request = CreateRequest(HttpMethod.Delete, BucketUrl(bucket));
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return false;
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return true;
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
        ThrowIfDisposed();
        if (_embedded is not null)
            return await new SndbObjectStore(_embedded).PutObjectAsync(bucket, key, content, contentType, metadata, tags, cancellationToken).ConfigureAwait(false);

        // 帧 put：内容可 seek 且在单帧上限内 → 缓冲后原始字节零 Base64 直传；否则（不可 seek / 超大 / 帧关闭）走 REST 流式。
        if (_frames is { } fx && fx.ShouldTryFrames())
        {
            byte[]? buffered = await TryBufferForFrameAsync(content, cancellationToken).ConfigureAwait(false);
            if (buffered is not null)
            {
                var w = new ArrayBufferWriter<byte>();
                ObjectFrameCodec.EncodePutRequest(w, 1, _database, bucket, key, buffered, contentType, metadata, tags);
                var frame = await fx.SendUnaryAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
                if (frame is { } f)
                {
                    ObjectPutFrameResult result = ObjectFrameCodec.DecodePutResponse(f.Payload);
                    return new SndbObjectInfo(
                        bucket,
                        key,
                        result.VersionId,
                        NormalizeContentType(contentType),
                        result.SizeBytes,
                        result.ETag,
                        result.Sha256,
                        false,
                        DateTimeOffset.MinValue,
                        DateTimeOffset.MinValue,
                        metadata ?? EmptyMap,
                        tags ?? EmptyMap);
                }

                // 传输级回落：复用已缓冲字节走 REST。
                return await PutObjectRestAsync(bucket, key, new MemoryStream(buffered), contentType, metadata, tags, cancellationToken).ConfigureAwait(false);
            }
        }

        return await PutObjectRestAsync(bucket, key, content, contentType, metadata, tags, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SndbObjectInfo> PutObjectRestAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType,
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? tags,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Put, ObjectUrl(bucket, key), new StreamContent(content));
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");
        AddMetadataHeaders(request, metadata);
        AddTagHeader(request, tags);

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectInfoResponse, cancellationToken).ConfigureAwait(false);
        return ToInfo(body);
    }

    /// <summary>
    /// 列出 bucket 内当前可见对象。
    /// </summary>
    public async Task<SndbObjectListResult> ListObjectsAsync(
        string bucket,
        string? prefix = null,
        int maxKeys = 1000,
        CancellationToken cancellationToken = default)
        => await ListObjectsAsync(bucket, prefix, maxKeys, continuationToken: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 使用 ContinuationToken 列出 bucket 内当前可见对象。
    /// </summary>
    public async Task<SndbObjectListResult> ListObjectsAsync(
        string bucket,
        string? prefix,
        int maxKeys,
        string? continuationToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).ListObjects(bucket, prefix, maxKeys, continuationToken);

        string url = BucketUrl(bucket)
            + "?list-type=2&max-keys="
            + maxKeys.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(prefix))
            url += "&prefix=" + Uri.EscapeDataString(prefix.TrimStart('/'));
        if (!string.IsNullOrWhiteSpace(continuationToken))
            url += "&continuation-token=" + Uri.EscapeDataString(continuationToken);

        using var response = await _http!.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectListResponse, cancellationToken).ConfigureAwait(false);
        return new SndbObjectListResult(
            body.Bucket,
            body.Prefix,
            body.MaxKeys,
            body.ContinuationToken,
            body.NextContinuationToken,
            body.IsTruncated,
            body.Objects.Select(ToInfo).ToArray());
    }

    /// <summary>
    /// 列出对象版本；key 为空时列出整个 bucket 的版本。
    /// </summary>
    public async Task<SndbObjectVersionListResult> ListObjectVersionsAsync(
        string bucket,
        string? key = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).ListObjectVersions(bucket, key);

        string url = BucketUrl(bucket) + "?versions";
        if (!string.IsNullOrWhiteSpace(key))
            url += "&key=" + Uri.EscapeDataString(key);

        using var response = await _http!.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectVersionListResponse, cancellationToken).ConfigureAwait(false);
        return new SndbObjectVersionListResult(
            body.Bucket,
            body.Key,
            body.Versions.Select(ToInfo).ToArray());
    }

    /// <summary>
    /// 获取对象元数据。
    /// </summary>
    public async Task<SndbObjectInfo?> HeadObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).HeadObject(bucket, key);

        using var request = CreateRequest(HttpMethod.Head, ObjectUrl(bucket, key));
        using var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return new SndbObjectInfo(
            bucket,
            key,
            response.Headers.TryGetValues("x-amz-version-id", out var versionValues) ? versionValues.FirstOrDefault() ?? string.Empty : string.Empty,
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            response.Content.Headers.ContentLength ?? 0,
            response.Headers.ETag?.Tag ?? string.Empty,
            response.Headers.TryGetValues("x-amz-meta-sha256", out var shaValues) ? shaValues.FirstOrDefault() ?? string.Empty : string.Empty,
            response.Headers.TryGetValues("x-amz-delete-marker", out var deleteMarkerValues)
                && string.Equals(deleteMarkerValues.FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase),
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());
    }

    /// <summary>
    /// 读取对象。
    /// </summary>
    public async Task<SndbObjectReadResult?> OpenReadAsync(
        string bucket,
        string key,
        SndbObjectRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).OpenRead(bucket, key, range);

        // 帧 get：仅非 Range 全量读走帧（meta→data×N→end 流式分块）；Range 读恒走 REST。
        if (range is null && _frames is { } fx && fx.ShouldTryFrames())
        {
            FrameReadOutcome? framed = await TryOpenReadFrameAsync(bucket, key, cancellationToken).ConfigureAwait(false);
            if (framed is { } outcome)
                return outcome.Result; // NotFound → null；成功 → 结果；仅传输级回落返回 null 触发 REST
        }

        return await OpenReadRestAsync(bucket, key, range, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FrameReadOutcome?> TryOpenReadFrameAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        var w = new ArrayBufferWriter<byte>();
        ObjectFrameCodec.EncodeGetRequest(w, 1, _database, bucket, key);
        IReadOnlyList<FrameMessage>? frames = await _frames!.TrySendAsync(w.WrittenMemory, cancellationToken).ConfigureAwait(false);
        if (frames is null)
            return null; // 传输级回落 REST

        // 首帧为带内错误帧：*_not_found → 返回 null（与 REST 把 404 归一化为 null 的语义一致）；其余错误上抛。
        FrameMessage head = frames[0];
        if (head.Header.IsError)
        {
            (string code, string message) = FrameCodec.ReadErrorPayload(head.Payload);
            if (code.EndsWith("_not_found", StringComparison.Ordinal))
                return FrameReadOutcome.NotFound;
            throw new SndbServerException(code, message, System.Net.HttpStatusCode.OK);
        }

        ObjectGetFrameMeta meta = ObjectFrameCodec.DecodeGetMetaFrame(head.Payload);
        var content = new MemoryStream(meta.SizeBytes <= int.MaxValue ? (int)meta.SizeBytes : 0);
        long total = 0;
        for (int i = 1; i < frames.Count; i++)
        {
            ReadOnlySpan<byte> payload = frames[i].Payload;
            ObjectChunkKind kind = ObjectFrameCodec.PeekChunkKind(payload);
            if (kind == ObjectChunkKind.Data)
            {
                ReadOnlyMemory<byte> chunk = ObjectFrameCodec.DecodeGetDataFrame(frames[i].Payload);
                content.Write(chunk.Span);
                total += chunk.Length;
            }
            else if (kind == ObjectChunkKind.End)
            {
                long declared = ObjectFrameCodec.DecodeGetEndFrame(payload);
                if (declared != total)
                    throw new InvalidDataException($"对象 get 帧声明总字节数 {declared} 与实收 {total} 不一致。");
                break;
            }
        }

        content.Position = 0;
        var info = new SndbObjectInfo(
            bucket,
            key,
            meta.VersionId,
            meta.ContentType,
            meta.SizeBytes,
            meta.ETag,
            meta.Sha256,
            false,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            meta.Metadata,
            meta.Tags);
        return new FrameReadOutcome(new SndbObjectReadResult(info, content, 0, meta.SizeBytes, IsRange: false));
    }

    private async Task<SndbObjectReadResult?> OpenReadRestAsync(
        string bucket,
        string key,
        SndbObjectRange? range,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, ObjectUrl(bucket, key));
        if (range.HasValue)
        {
            long start = range.Value.Offset;
            long? length = range.Value.Length;
            request.Headers.Range = length.HasValue
                ? new RangeHeaderValue(start, start + length.Value - 1)
                : new RangeHeaderValue(start, null);
        }

        var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            response.Dispose();
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var error = await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var info = new SndbObjectInfo(
            bucket,
            key,
            response.Headers.TryGetValues("x-amz-version-id", out var versionValues) ? versionValues.FirstOrDefault() ?? string.Empty : string.Empty,
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            response.Content.Headers.ContentLength ?? 0,
            response.Headers.ETag?.Tag ?? string.Empty,
            response.Headers.TryGetValues("x-amz-meta-sha256", out var shaValues) ? shaValues.FirstOrDefault() ?? string.Empty : string.Empty,
            false,
            DateTimeOffset.MinValue,
            DateTimeOffset.MinValue,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        return new SndbObjectReadResult(
            info,
            new ResponseOwnedStream(response, stream),
            range?.Offset ?? 0,
            response.Content.Headers.ContentLength ?? 0,
            response.StatusCode == System.Net.HttpStatusCode.PartialContent);
    }

    /// <summary>
    /// 复制对象。
    /// </summary>
    public async Task<SndbObjectInfo> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return await new SndbObjectStore(_embedded).CopyObjectAsync(sourceBucket, sourceKey, destinationBucket, destinationKey, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var request = CreateRequest(HttpMethod.Put, ObjectUrl(destinationBucket, destinationKey));
        request.Headers.TryAddWithoutValidation("x-amz-copy-source", "/" + sourceBucket + "/" + sourceKey);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectCopyResponse, cancellationToken).ConfigureAwait(false);
        return await HeadObjectAsync(destinationBucket, destinationKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Copied object metadata was not returned.");
    }

    /// <summary>
    /// 删除对象并创建 delete marker。
    /// </summary>
    public async Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
        {
            new SndbObjectStore(_embedded).DeleteObject(bucket, key);
            return;
        }

        using var request = CreateRequest(HttpMethod.Delete, ObjectUrl(bucket, key));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 批量删除对象并创建 delete marker。
    /// </summary>
    public async Task<SndbObjectDeleteManyResult> DeleteObjectsAsync(
        string bucket,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).DeleteObjects(bucket, keys);

        using var response = await PostJsonAsync(
            BucketUrl(bucket) + "?delete",
            new ObjectDeleteManyRequest(keys),
            SndbObjectClientJsonContext.Default.ObjectDeleteManyRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectDeleteManyResponse, cancellationToken).ConfigureAwait(false);
        return new SndbObjectDeleteManyResult(
            body.Bucket,
            body.Deleted.Select(static item => new SndbObjectDeleteResult(
                item.Key,
                item.VersionId,
                item.DeleteMarker,
                item.ErrorCode,
                item.ErrorMessage)).ToArray());
    }

    /// <summary>
    /// 设置对象标签。
    /// </summary>
    public async Task<SndbObjectInfo> SetObjectTagsAsync(
        string bucket,
        string key,
        IReadOnlyDictionary<string, string> tags,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).SetObjectTags(bucket, key, tags);

        using var response = await PutJsonAsync(
            ObjectUrl(bucket, key) + "?tagging",
            new ObjectTagsRequest(new Dictionary<string, string>(tags, StringComparer.Ordinal)),
            SndbObjectClientJsonContext.Default.ObjectTagsRequest,
            cancellationToken).ConfigureAwait(false);
        return ToInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectInfoResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 获取 bucket policy 占位配置。
    /// </summary>
    public async Task<SndbBucketPolicyInfo> GetPolicyAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).GetPolicy(bucket);

        using var response = await _http!.GetAsync(BucketUrl(bucket) + "?policy", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return ToPolicyInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectBucketPolicyResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 设置 bucket policy 占位配置；当前仅持久化和审计。
    /// </summary>
    public async Task<SndbBucketPolicyInfo> SetPolicyAsync(string bucket, string? policyJson, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).SetPolicy(bucket, policyJson);

        using var response = await PutJsonAsync(
            BucketUrl(bucket) + "?policy",
            new ObjectBucketPolicyRequest(policyJson),
            SndbObjectClientJsonContext.Default.ObjectBucketPolicyRequest,
            cancellationToken).ConfigureAwait(false);
        return ToPolicyInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectBucketPolicyResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 获取 bucket 生命周期策略。
    /// </summary>
    public async Task<SndbBucketLifecycleInfo> GetLifecycleAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).GetLifecycle(bucket);

        using var response = await _http!.GetAsync(BucketUrl(bucket) + "?lifecycle", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return ToLifecycleInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectLifecycleResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 设置 bucket 生命周期策略。
    /// </summary>
    public async Task<SndbBucketLifecycleInfo> SetLifecycleAsync(
        string bucket,
        int? expireCurrentAfterDays = null,
        int? expireNoncurrentAfterDays = null,
        int? expireDeleteMarkerAfterDays = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).SetLifecycle(bucket, expireCurrentAfterDays, expireNoncurrentAfterDays, expireDeleteMarkerAfterDays);

        using var response = await PutJsonAsync(
            BucketUrl(bucket) + "?lifecycle",
            new ObjectLifecycleRequest(expireCurrentAfterDays, expireNoncurrentAfterDays, expireDeleteMarkerAfterDays),
            SndbObjectClientJsonContext.Default.ObjectLifecycleRequest,
            cancellationToken).ConfigureAwait(false);
        return ToLifecycleInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectLifecycleResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 执行 bucket 生命周期策略。
    /// </summary>
    public async Task<SndbBucketLifecycleApplyResult> ApplyLifecycleAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).ApplyLifecycle(bucket);

        using var response = await _http!.PostAsync(BucketUrl(bucket) + "?lifecycle", content: null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectLifecycleApplyResponse, cancellationToken).ConfigureAwait(false);
        return new SndbBucketLifecycleApplyResult(
            body.Bucket,
            body.ExpiredCurrentObjects,
            body.RemovedNoncurrentVersions,
            body.RemovedDeleteMarkers);
    }

    /// <summary>
    /// 获取 bucket 保留策略。
    /// </summary>
    public async Task<SndbBucketRetentionInfo> GetRetentionAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).GetRetention(bucket);

        using var response = await _http!.GetAsync(BucketUrl(bucket) + "?retention", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return ToRetentionInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectRetentionResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 设置 bucket 保留策略。
    /// </summary>
    public async Task<SndbBucketRetentionInfo> SetRetentionAsync(
        string bucket,
        int? retainCurrentForDays = null,
        int? retainNoncurrentForDays = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).SetRetention(bucket, retainCurrentForDays, retainNoncurrentForDays);

        using var response = await PutJsonAsync(
            BucketUrl(bucket) + "?retention",
            new ObjectRetentionRequest(retainCurrentForDays, retainNoncurrentForDays),
            SndbObjectClientJsonContext.Default.ObjectRetentionRequest,
            cancellationToken).ConfigureAwait(false);
        return ToRetentionInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectRetentionResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 获取 bucket 配额配置。
    /// </summary>
    public async Task<SndbBucketQuotaInfo> GetQuotaAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).GetQuota(bucket);

        using var response = await _http!.GetAsync(BucketUrl(bucket) + "?quota", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return ToQuotaInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectQuotaResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 设置 bucket 配额配置。
    /// </summary>
    public async Task<SndbBucketQuotaInfo> SetQuotaAsync(
        string bucket,
        long? maxSizeBytes = null,
        long? maxObjectVersions = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).SetQuota(bucket, maxSizeBytes, maxObjectVersions);

        using var response = await PutJsonAsync(
            BucketUrl(bucket) + "?quota",
            new ObjectQuotaRequest(maxSizeBytes, maxObjectVersions),
            SndbObjectClientJsonContext.Default.ObjectQuotaRequest,
            cancellationToken).ConfigureAwait(false);
        return ToQuotaInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectQuotaResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 获取 bucket 容量统计。
    /// </summary>
    public async Task<SndbBucketStatsInfo> GetStatsAsync(string bucket, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).GetStats(bucket);

        using var response = await _http!.GetAsync(BucketUrl(bucket) + "?stats", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return ToStatsInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectStatsResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 列出 bucket 审计记录。
    /// </summary>
    public async Task<IReadOnlyList<SndbObjectAuditEntry>> ListAuditAsync(
        string bucket,
        string? keyPrefix = null,
        int maxEntries = 1000,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).ListAudit(bucket, keyPrefix, maxEntries);

        string url = BucketUrl(bucket)
            + "?audit&max-entries="
            + maxEntries.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(keyPrefix))
            url += "&prefix=" + Uri.EscapeDataString(keyPrefix.TrimStart('/'));

        using var response = await _http!.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectAuditListResponse, cancellationToken).ConfigureAwait(false);
        return body.Entries
            .Select(static entry => new SndbObjectAuditEntry(
                entry.Id,
                entry.Action,
                entry.Bucket,
                entry.Key,
                entry.VersionId,
                entry.TimestampUtc,
                entry.Details))
            .ToArray();
    }

    /// <summary>
    /// 获取对象版本 legal hold 状态。
    /// </summary>
    public async Task<SndbObjectLegalHoldInfo> GetLegalHoldAsync(
        string bucket,
        string key,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).GetLegalHold(bucket, key, versionId);

        string url = ObjectUrl(bucket, key) + "?legal-hold";
        if (!string.IsNullOrWhiteSpace(versionId))
            url += "&versionId=" + Uri.EscapeDataString(versionId);

        using var response = await _http!.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

        return ToLegalHoldInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectLegalHoldResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 设置对象版本 legal hold 状态。
    /// </summary>
    public async Task<SndbObjectLegalHoldInfo> SetLegalHoldAsync(
        string bucket,
        string key,
        bool enabled,
        string? reason = null,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).SetLegalHold(bucket, key, enabled, reason, versionId);

        string url = ObjectUrl(bucket, key) + "?legal-hold";
        if (!string.IsNullOrWhiteSpace(versionId))
            url += "&versionId=" + Uri.EscapeDataString(versionId);

        using var response = await PutJsonAsync(
            url,
            new ObjectLegalHoldRequest(enabled, reason),
            SndbObjectClientJsonContext.Default.ObjectLegalHoldRequest,
            cancellationToken).ConfigureAwait(false);
        return ToLegalHoldInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectLegalHoldResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 创建 multipart upload。
    /// </summary>
    public async Task<SndbMultipartUploadInfo> InitiateMultipartUploadAsync(
        string bucket,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).InitiateMultipartUpload(bucket, key, contentType, metadata, tags);

        using var response = await PostJsonAsync(
            ObjectUrl(bucket, key) + "?uploads",
            new MultipartUploadCreateRequest(contentType, metadata, tags),
            SndbObjectClientJsonContext.Default.MultipartUploadCreateRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.MultipartUploadCreateResponse, cancellationToken).ConfigureAwait(false);
        return new SndbMultipartUploadInfo(body.Bucket, body.Key, body.UploadId, body.ContentType, body.InitiatedUtc, body.ExpiresUtc, body.Metadata, body.Tags);
    }

    /// <summary>
    /// 上传 multipart 分片。
    /// </summary>
    public async Task<SndbMultipartPartInfo> UploadPartAsync(
        string bucket,
        string key,
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return await new SndbObjectStore(_embedded).UploadPartAsync(uploadId, partNumber, content, cancellationToken).ConfigureAwait(false);

        using var request = CreateRequest(
            HttpMethod.Put,
            ObjectUrl(bucket, key) + $"?uploadId={Uri.EscapeDataString(uploadId)}&partNumber={partNumber}",
            new StreamContent(content));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.MultipartPartResponse, cancellationToken).ConfigureAwait(false);
        return new SndbMultipartPartInfo(body.PartNumber, body.SizeBytes, body.ETag, body.Sha256);
    }

    /// <summary>
    /// 完成 multipart upload。
    /// </summary>
    public async Task<SndbObjectInfo> CompleteMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        IReadOnlyList<int> partNumbers,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return await new SndbObjectStore(_embedded).CompleteMultipartUploadAsync(uploadId, partNumbers, cancellationToken).ConfigureAwait(false);

        using var response = await PostJsonAsync(
            ObjectUrl(bucket, key) + "?uploadId=" + Uri.EscapeDataString(uploadId),
            new MultipartCompleteRequest(partNumbers),
            SndbObjectClientJsonContext.Default.MultipartCompleteRequest,
            cancellationToken).ConfigureAwait(false);
        return ToInfo(await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectInfoResponse, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// 终止 multipart upload。
    /// </summary>
    public async Task AbortMultipartUploadAsync(
        string bucket,
        string key,
        string uploadId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
        {
            new SndbObjectStore(_embedded).AbortMultipartUpload(uploadId);
            return;
        }

        using var request = CreateRequest(
            HttpMethod.Delete,
            ObjectUrl(bucket, key) + "?uploadId=" + Uri.EscapeDataString(uploadId));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 创建预签名 URL。
    /// </summary>
    public async Task<SndbPresignedObjectUrl> CreatePresignedUrlAsync(
        string bucket,
        string key,
        string method,
        int expiresMinutes,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            throw new NotSupportedException("嵌入式对象存储不支持 HTTP 预签名 URL。");

        using var response = await PostJsonAsync(
            ObjectUrl(bucket, key) + "?presign",
            new PresignedObjectUrlCreateRequest(method, expiresMinutes),
            SndbObjectClientJsonContext.Default.PresignedObjectUrlCreateRequest,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.PresignedObjectUrlResponse, cancellationToken).ConfigureAwait(false);
        return new SndbPresignedObjectUrl(body.Url, body.Method, body.Bucket, body.Key, body.ExpiresUtc);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _http?.Dispose();
        var embedded = _embedded;
        _embedded = null;
        if (embedded is not null)
            SharedSndbRegistry.Release(embedded);
    }

    private void Open()
    {
        if (_builder.ResolveMode() == SndbProviderMode.Embedded)
        {
            var dataSource = _builder.ResolveEmbeddedDataSource();
            if (string.IsNullOrWhiteSpace(dataSource))
                throw new InvalidOperationException("对象存储客户端缺少 Data Source。");

            _database = dataSource;
            _embedded = SharedSndbRegistry.Acquire(_builder.CreateEmbeddedOptions(dataSource));
            return;
        }

        var baseUrl = _builder.ResolveBaseUrl();
        _database = _builder.ResolveDatabase();
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程对象存储客户端缺少数据库名。");

        var baseAddress = new Uri(baseUrl, UriKind.Absolute);
        var protocol = _builder.ResolveProtocol();
        _http = _useDedicatedHttpHandler
            ? RemoteHttpClientFactory.CreateDedicated(
                baseAddress,
                _builder.Username,
                _builder.Password,
                _builder.Token,
                TimeSpan.FromSeconds(_builder.Timeout))
            : RemoteHttpClientFactory.Create(
                baseAddress,
                _builder.Username,
                _builder.Password,
                _builder.Token,
                TimeSpan.FromSeconds(_builder.Timeout));
        if (protocol == SndbTransportProtocol.FrameHttp2)
        {
            _http.DefaultRequestVersion = HttpVersion.Version20;
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        else if (_useDedicatedHttpHandler && protocol == SndbTransportProtocol.Rest)
        {
            _http.DefaultRequestVersion = HttpVersion.Version11;
            _http.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        _frames = new FrameChannel(_http, protocol);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var http = _http ?? throw new InvalidOperationException("远程连接未打开。");
        return new HttpRequestMessage(method, url)
        {
            Version = http.DefaultRequestVersion,
            VersionPolicy = http.DefaultVersionPolicy,
            Content = content,
        };
    }

    private async Task<HttpResponseMessage> PostJsonAsync<T>(
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(value, typeInfo);
        var response = await _http!.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }
        return response;
    }

    private async Task<HttpResponseMessage> PutJsonAsync<T>(
        string url,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var content = JsonContent.Create(value, typeInfo);
        var response = await _http!.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw error;
        }
        return response;
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SonnetDB object storage response body is empty.");
    }

    private static async Task<SndbServerException> BuildHttpErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var error = JsonSerializer.Deserialize(body, RemoteJsonContext.Default.ServerErrorBody);
                if (error is not null)
                    return new SndbServerException(error.Error, error.Message, response.StatusCode);
            }
            catch
            {
            }

            return new SndbServerException("http_error", body.Trim(), response.StatusCode);
        }

        return new SndbServerException("http_error", response.ReasonPhrase ?? "SonnetDB HTTP error.", response.StatusCode);
    }

    private string BucketUrl(string bucket) => $"v1/db/{Uri.EscapeDataString(_database)}/s3/{Uri.EscapeDataString(bucket)}";

    private string ObjectUrl(string bucket, string key) =>
        BucketUrl(bucket) + "/" + Uri.EscapeDataString(key).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    private static void AddMetadataHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null)
            return;

        foreach (var pair in metadata)
            request.Headers.TryAddWithoutValidation("x-amz-meta-" + pair.Key, pair.Value);
    }

    private static void AddTagHeader(HttpRequestMessage request, IReadOnlyDictionary<string, string>? tags)
    {
        if (tags is null || tags.Count == 0)
            return;

        string value = string.Join("&", tags.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        request.Headers.TryAddWithoutValidation("x-amz-tagging", value);
    }

    private static SndbObjectInfo ToInfo(ObjectInfoResponse body) =>
        new(body.Bucket, body.Key, body.VersionId, body.ContentType, body.SizeBytes, body.ETag, body.Sha256, body.IsDeleteMarker, body.CreatedUtc, body.UpdatedUtc, body.Metadata, body.Tags);

    private static SndbBucketPolicyInfo ToPolicyInfo(ObjectBucketPolicyResponse body) =>
        new(body.Bucket, body.PolicyJson, body.UpdatedUtc);

    private static SndbBucketLifecycleInfo ToLifecycleInfo(ObjectLifecycleResponse body) =>
        new(body.Bucket, body.ExpireCurrentAfterDays, body.ExpireNoncurrentAfterDays, body.ExpireDeleteMarkerAfterDays, body.UpdatedUtc);

    private static SndbBucketRetentionInfo ToRetentionInfo(ObjectRetentionResponse body) =>
        new(body.Bucket, body.RetainCurrentForDays, body.RetainNoncurrentForDays, body.UpdatedUtc);

    private static SndbBucketQuotaInfo ToQuotaInfo(ObjectQuotaResponse body) =>
        new(body.Bucket, body.MaxSizeBytes, body.MaxObjectVersions, body.UpdatedUtc);

    private static SndbBucketStatsInfo ToStatsInfo(ObjectStatsResponse body) =>
        new(
            body.Bucket,
            body.CurrentObjectCount,
            body.CurrentSizeBytes,
            body.ObjectVersionCount,
            body.ObjectVersionSizeBytes,
            body.DeleteMarkerCount,
            body.MultipartUploadCount,
            body.MultipartPartCount,
            body.MultipartPartSizeBytes,
            body.QuotaMaxSizeBytes,
            body.QuotaMaxObjectVersions,
            body.QuotaRemainingSizeBytes,
            body.QuotaRemainingObjectVersions);

    private static SndbObjectLegalHoldInfo ToLegalHoldInfo(ObjectLegalHoldResponse body) =>
        new(body.Bucket, body.Key, body.VersionId, body.Enabled, body.Reason, body.UpdatedUtc);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// 尝试把内容缓冲成字节数组以走单帧 put：可 seek 且长度在帧上限内才缓冲；否则返回 null 走 REST 流式。
    /// 可 seek 的流在放弃帧路径时会 seek 回原位，保证 REST 回落读到完整内容。
    /// </summary>
    private static async Task<byte[]?> TryBufferForFrameAsync(Stream content, CancellationToken cancellationToken)
    {
        long originPosition = content.CanSeek ? content.Position : -1;
        if (content.CanSeek && content.Length - content.Position > MaxFrameContentBytes)
            return null; // 超帧上限：不消费，REST 从原位续读

        // 缓冲整段内容；边读边守上限，超限即放弃帧路径。
        var buffer = new MemoryStream(content.CanSeek ? (int)Math.Min(content.Length - content.Position, int.MaxValue) : 0);
        byte[] pool = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await content.ReadAsync(pool, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(pool, 0, read);
                if (buffer.Length > MaxFrameContentBytes)
                {
                    if (content.CanSeek)
                    {
                        content.Position = originPosition; // 回退，REST 读完整内容
                        return null;
                    }

                    // 不可 seek 且已部分消费，无法安全回落 → 要求改用 multipart。
                    throw new NotSupportedException(
                        $"对象内容超过帧上限 {MaxFrameContentBytes} 字节且源流不可 seek，请使用 multipart 上传或提供可 seek 的流。");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pool);
        }

        return buffer.ToArray();
    }

    private static string NormalizeContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

    /// <summary>
    /// 帧 get 的三态结果：<see cref="Result"/> 为 null 表示对象不存在（与 REST 404 一致）；
    /// 非 null 表示成功。传输级回落由 <see cref="TryOpenReadFrameAsync"/> 返回外层 null 表达。
    /// </summary>
    private readonly struct FrameReadOutcome
    {
        public static readonly FrameReadOutcome NotFound = new(null);

        public FrameReadOutcome(SndbObjectReadResult? result) => Result = result;

        public SndbObjectReadResult? Result { get; }
    }

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly HttpResponseMessage _response;
        private readonly Stream _inner;

        public ResponseOwnedStream(HttpResponseMessage response, Stream inner)
        {
            _response = response;
            _inner = inner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
