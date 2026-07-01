using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Data.ObjectStorage;

/// <summary>
/// SonnetDB 对象桶客户端，统一支持嵌入式与远程 SonnetDB。
/// </summary>
public sealed class SndbObjectStorageClient : IDisposable
{
    private readonly SndbConnectionStringBuilder _builder;
    private HttpClient? _http;
    private Tsdb? _embedded;
    private string _database = string.Empty;
    private bool _disposed;

    /// <summary>
    /// 使用 SonnetDB 连接字符串创建对象桶客户端。
    /// </summary>
    public SndbObjectStorageClient(string connectionString)
    {
        _builder = new SndbConnectionStringBuilder(connectionString);
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

        using var request = new HttpRequestMessage(HttpMethod.Delete, BucketUrl(bucket));
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

        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUrl(bucket, key))
        {
            Content = new StreamContent(content),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");
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
    /// 获取对象元数据。
    /// </summary>
    public async Task<SndbObjectInfo?> HeadObjectAsync(string bucket, string key, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).HeadObject(bucket, key);

        using var request = new HttpRequestMessage(HttpMethod.Head, ObjectUrl(bucket, key));
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

        using var request = new HttpRequestMessage(HttpMethod.Get, ObjectUrl(bucket, key));
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

        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUrl(destinationBucket, destinationKey));
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

        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUrl(bucket, key));
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

        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUrl(bucket, key) + $"?uploadId={Uri.EscapeDataString(uploadId)}&partNumber={partNumber}")
        {
            Content = new StreamContent(content),
        };
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

        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUrl(bucket, key) + "?uploadId=" + Uri.EscapeDataString(uploadId));
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
            if (string.IsNullOrWhiteSpace(_builder.DataSource))
                throw new InvalidOperationException("对象存储客户端缺少 Data Source。");

            _database = _builder.DataSource;
            _embedded = SharedSndbRegistry.Acquire(new TsdbOptions { RootDirectory = _builder.DataSource });
            return;
        }

        var (baseUrl, dbFromUrl) = ParseRemoteEndpoint(_builder.DataSource);
        _database = !string.IsNullOrWhiteSpace(_builder.Database) ? _builder.Database! : dbFromUrl;
        if (string.IsNullOrWhiteSpace(_database))
            throw new InvalidOperationException("远程对象存储客户端缺少数据库名。");

        _http = RemoteHttpClientFactory.Create(
            new Uri(baseUrl, UriKind.Absolute),
            _builder.Token,
            TimeSpan.FromSeconds(_builder.Timeout));
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
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
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
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);
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
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var error = await JsonSerializer.DeserializeAsync(stream, RemoteJsonContext.Default.ServerErrorBody, cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
                return new SndbServerException(error.Error, error.Message, response.StatusCode);
        }
        catch
        {
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

    private static (string BaseUrl, string Database) ParseRemoteEndpoint(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
            throw new InvalidOperationException("远程对象存储客户端缺少 Data Source。");

        var ds = dataSource.Trim();
        if (ds.StartsWith("sonnetdb+http://", StringComparison.OrdinalIgnoreCase))
            ds = "http://" + ds["sonnetdb+http://".Length..];
        else if (ds.StartsWith("sonnetdb+https://", StringComparison.OrdinalIgnoreCase))
            ds = "https://" + ds["sonnetdb+https://".Length..];

        if (!Uri.TryCreate(ds, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"远程 Data Source 不是合法 URL: {dataSource}");

        return ($"{uri.Scheme}://{uri.Authority}/", uri.AbsolutePath.Trim('/'));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

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
