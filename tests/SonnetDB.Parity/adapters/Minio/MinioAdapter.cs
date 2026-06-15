using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace SonnetDB.Parity.Adapters.Minio;

/// <summary>
/// MinIO 竞品适配器，使用 AWS S3 SDK 并指向 MinIO endpoint。
/// </summary>
public sealed class MinioAdapter : IDataPlane, IObjectOps
{
    private readonly AmazonS3Client _client;
    private readonly string _endpoint;

    /// <summary>使用 <c>PARITY_MINIO_*</c> 环境变量创建 MinIO 连接。</summary>
    public MinioAdapter()
    {
        _endpoint = Env("PARITY_MINIO_ENDPOINT", "http://127.0.0.1:25000");
        _client = CreateClient();
    }

    /// <inheritdoc />
    public string BackendName => "minio";

    /// <inheritdoc />
    public Capability Capabilities => Capability.Object | Capability.ObjectMultipart;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("MinIO 适配器不支持关系型操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => UnsupportedTimeSeriesOps.Instance;

    /// <inheritdoc />
    public IKvOps Kv => UnsupportedKvOps.Instance;

    /// <inheritdoc />
    public IObjectOps Objects => this;

    /// <inheritdoc />
    public IVectorOps Vector => UnsupportedVectorOps.Instance;

    /// <inheritdoc />
    public IMqOps Mq => UnsupportedMqOps.Instance;

    /// <inheritdoc />
    public IFullTextOps FullText => UnsupportedFullTextOps.Instance;

    /// <inheritdoc />
    public IAnalyticalOps Analytics => UnsupportedAnalyticalOps.Instance;

    /// <summary>探测 MinIO 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var client = CreateClient();
            _ = await client.ListBucketsAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is AmazonS3Exception or HttpRequestException or WebException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResetBucketAsync(string bucket, CancellationToken ct)
    {
        if (!await BucketExistsAsync(bucket, ct).ConfigureAwait(false))
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, ct).ConfigureAwait(false);
            return;
        }

        string? token = null;
        do
        {
            var listed = await _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                MaxKeys = 1000,
                ContinuationToken = token,
            }, ct).ConfigureAwait(false);

            var objects = listed.S3Objects ?? [];
            if (objects.Count > 0)
            {
                foreach (var item in objects)
                    await _client.DeleteObjectAsync(bucket, item.Key, ct).ConfigureAwait(false);
            }

            token = listed.NextContinuationToken;
        }
        while (!string.IsNullOrWhiteSpace(token));
    }

    /// <inheritdoc />
    public async Task<ObjectPutResult> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct)
    {
        var response = await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
        }, ct).ConfigureAwait(false);
        return new ObjectPutResult(key, content.CanSeek ? content.Length : -1, response.ETag);
    }

    /// <inheritdoc />
    public async Task<ObjectReadResult?> GetAsync(string bucket, string key, ObjectRange? range, CancellationToken ct)
    {
        try
        {
            var request = new GetObjectRequest
            {
                BucketName = bucket,
                Key = key,
            };
            if (range.HasValue)
            {
                request.ByteRange = range.Value.Length.HasValue
                    ? new ByteRange(range.Value.Offset, range.Value.Offset + range.Value.Length.Value - 1)
                    : new ByteRange(range.Value.Offset, long.MaxValue);
            }

            using var response = await _client.GetObjectAsync(request, ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            await response.ResponseStream.CopyToAsync(output, ct).ConfigureAwait(false);
            return new ObjectReadResult(output.ToArray(), response.Headers.ContentType, output.Length);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<ObjectListPage> ListAsync(string bucket, string prefix, int maxKeys, string? continuationToken, CancellationToken ct)
    {
        var response = await _client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucket,
            Prefix = prefix,
            MaxKeys = maxKeys,
            ContinuationToken = continuationToken,
        }, ct).ConfigureAwait(false);
        return new ObjectListPage(
            (response.S3Objects ?? []).Select(static item => new ObjectListItem(item.Key, item.Size ?? 0L)).ToArray(),
            response.IsTruncated ?? false,
            response.NextContinuationToken);
    }

    /// <inheritdoc />
    public async Task<ObjectPutResult> CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken ct)
    {
        var response = await _client.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = bucket,
            SourceKey = sourceKey,
            DestinationBucket = bucket,
            DestinationKey = destinationKey,
        }, ct).ConfigureAwait(false);
        var metadata = await _client.GetObjectMetadataAsync(bucket, destinationKey, ct).ConfigureAwait(false);
        return new ObjectPutResult(destinationKey, metadata.ContentLength, response.ETag);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string bucket, string key, CancellationToken ct)
        => _client.DeleteObjectAsync(bucket, key, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ObjectDeleteResult>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct)
    {
        var deleted = new List<ObjectDeleteResult>(keys.Count);
        foreach (string key in keys)
        {
            await _client.DeleteObjectAsync(bucket, key, ct).ConfigureAwait(false);
            deleted.Add(new ObjectDeleteResult(key, DeleteMarker: true, ErrorCode: null));
        }

        return deleted;
    }

    /// <inheritdoc />
    public async Task<string> InitiateMultipartAsync(string bucket, string key, string contentType, CancellationToken ct)
    {
        var response = await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            ContentType = contentType,
        }, ct).ConfigureAwait(false);
        return response.UploadId;
    }

    /// <inheritdoc />
    public async Task UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream content, CancellationToken ct)
    {
        _ = await _client.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            InputStream = content,
            PartSize = content.CanSeek ? content.Length : 0,
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ObjectPutResult> CompleteMultipartAsync(string bucket, string key, string uploadId, IReadOnlyList<int> partNumbers, CancellationToken ct)
    {
        var parts = await _client.ListPartsAsync(new ListPartsRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
        }, ct).ConfigureAwait(false);
        var response = await _client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = partNumbers
                .Select(partNumber => (parts.Parts ?? []).First(part => part.PartNumber == partNumber))
                .Select(part => new PartETag(part.PartNumber.GetValueOrDefault(), part.ETag))
                .ToList(),
        }, ct).ConfigureAwait(false);
        var metadata = await _client.GetObjectMetadataAsync(bucket, key, ct).ConfigureAwait(false);
        return new ObjectPutResult(key, metadata.ContentLength, response.ETag);
    }

    /// <inheritdoc />
    public Task<string> CreatePresignedGetUrlAsync(string bucket, string key, TimeSpan expiresAfter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresAfter),
        });
        return Task.FromResult(NormalizePresignedEndpoint(url, _endpoint));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private static AmazonS3Client CreateClient()
    {
        var endpoint = Env("PARITY_MINIO_ENDPOINT", "http://127.0.0.1:25000");
        var accessKey = Env("PARITY_MINIO_ACCESS_KEY", Env("PARITY_MINIO_ROOT_USER", "parity"));
        var secretKey = Env("PARITY_MINIO_SECRET_KEY", Env("PARITY_MINIO_ROOT_PASSWORD", "parity12345"));
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            ForcePathStyle = true,
            AuthenticationRegion = RegionEndpoint.USEast1.SystemName,
        };
        return new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }

    private async Task<bool> BucketExistsAsync(string bucket, CancellationToken ct)
    {
        try
        {
            var buckets = await _client.ListBucketsAsync(ct).ConfigureAwait(false);
            return buckets.Buckets?.Any(item => string.Equals(item.BucketName, bucket, StringComparison.Ordinal)) == true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string NormalizePresignedEndpoint(string url, string endpoint)
    {
        var original = new Uri(url);
        var target = new Uri(endpoint);
        var builder = new UriBuilder(original)
        {
            Scheme = target.Scheme,
            Host = target.Host,
            Port = target.IsDefaultPort ? -1 : target.Port,
        };
        return builder.Uri.ToString();
    }
}
