using SonnetDB.ObjectStorage;

namespace SonnetDB.Data.ObjectStorage;

public sealed partial class SndbObjectStorageClient
{
    /// <summary>只执行精确对象的生命周期；旧服务端不支持时明确失败，不回退为全桶扫描。</summary>
    /// <param name="bucket">对象桶。</param>
    /// <param name="key">精确对象键。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>该对象的回收结果。</returns>
    public async Task<SndbBucketLifecycleApplyResult> ApplyObjectLifecycleAsync(
        string bucket, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ThrowIfDisposed();
        using var deadline = BeginOperation(ref cancellationToken);
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).ApplyObjectLifecycle(bucket, key, cancellationToken);

        using var request = CreateRequest(HttpMethod.Post,
            BucketUrl(bucket) + "?object-lifecycle&key=" + Uri.EscapeDataString(key));
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectLifecycleApplyResponse,
            cancellationToken).ConfigureAwait(false);
        return new SndbBucketLifecycleApplyResult(body.Bucket, body.ExpiredCurrentObjects,
            body.RemovedNoncurrentVersions, body.RemovedDeleteMarkers)
        {
            ExpiredObjects = body.ExpiredObjects?.Select(static item =>
                new SndbLifecycleExpiredObject(item.Key, item.VersionId, item.ContentType)).ToArray()
                ?? Array.Empty<SndbLifecycleExpiredObject>(),
            SemanticCleanupJobs = body.SemanticCleanupJobs,
        };
    }
}
