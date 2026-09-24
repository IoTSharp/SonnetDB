using System.Net;
using System.Net.Http.Headers;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Data.ObjectStorage;

public sealed partial class SndbObjectStorageClient
{
    /// <summary>
    /// 在当前对象满足条件时写入对象。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="key">对象键。</param>
    /// <param name="content">待写入内容流。</param>
    /// <param name="condition">写入前置条件。</param>
    /// <param name="contentType">对象内容类型。</param>
    /// <param name="metadata">对象元数据。</param>
    /// <param name="tags">对象标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新对象版本的元数据。</returns>
    public async Task<SndbObjectInfo> PutObjectConditionalAsync(
        string bucket,
        string key,
        Stream content,
        SndbObjectWriteCondition condition,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(condition);
        using var deadline = BeginOperation(ref cancellationToken);
        if (_embedded is not null)
        {
            return await new SndbObjectStore(_embedded).PutObjectConditionalAsync(
                bucket, key, content, condition, contentType, metadata, tags, cancellationToken).ConfigureAwait(false);
        }

        using var request = CreateRequest(HttpMethod.Put, ObjectUrl(bucket, key), new StreamContent(content));
        request.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/octet-stream");
        AddMetadataHeaders(request, metadata);
        AddTagHeader(request, tags);
        AddWriteConditionHeaders(request, condition);
        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await ReadJsonAsync(response, SndbObjectClientJsonContext.Default.ObjectInfoResponse, cancellationToken).ConfigureAwait(false);
        return ToInfo(body);
    }

    /// <summary>
    /// 按 HTTP 风格前置条件读取对象。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="key">对象键。</param>
    /// <param name="condition">读取前置条件。</param>
    /// <param name="range">可选的内容范围。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条件状态和满足时持有的内容流。</returns>
    public async Task<SndbObjectConditionalReadResult> OpenReadConditionalAsync(
        string bucket,
        string key,
        SndbObjectReadCondition condition,
        SndbObjectRange? range = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(condition);
        using var deadline = BeginOperation(ref cancellationToken);
        if (_embedded is not null)
            return new SndbObjectStore(_embedded).OpenReadConditional(bucket, key, condition, range);

        using var request = CreateRequest(HttpMethod.Get, ObjectUrl(bucket, key));
        AddReadConditionHeaders(request, condition);
        AddRangeHeader(request, range);
        HttpResponseMessage? response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new SndbObjectConditionalReadResult(SndbObjectConditionalReadStatus.NotFound, null);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new SndbObjectConditionalReadResult(SndbObjectConditionalReadStatus.NotModified, null);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                return new SndbObjectConditionalReadResult(SndbObjectConditionalReadStatus.PreconditionFailed, null);
            if (!response.IsSuccessStatusCode)
                throw await BuildHttpErrorAsync(response, cancellationToken).ConfigureAwait(false);

            var read = await CreateReadResultAsync(response, bucket, key, range, cancellationToken).ConfigureAwait(false);
            // 从这里开始由结果流接管响应，finally 不再释放成功响应。
            response = null;
            return new SndbObjectConditionalReadResult(SndbObjectConditionalReadStatus.Success, read);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// 以 continuation 异步游标逐项枚举当前可见对象。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="prefix">对象键前缀。</param>
    /// <param name="pageSize">每页最大对象数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>按对象键 ordinal 顺序输出的异步序列。</returns>
    public async IAsyncEnumerable<SndbObjectInfo> ListObjectsCursorAsync(
        string bucket,
        string? prefix = null,
        int pageSize = 256,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        string? continuationToken = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ListObjectsAsync(bucket, prefix, pageSize, continuationToken, cancellationToken).ConfigureAwait(false);
            foreach (var item in page.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
            continuationToken = page.NextContinuationToken;
        }
        while (continuationToken is not null);
    }

    private static void AddWriteConditionHeaders(HttpRequestMessage request, SndbObjectWriteCondition condition)
    {
        if (condition.IfNoneMatch && !string.IsNullOrEmpty(condition.IfNoneMatchEtags))
            throw new ArgumentException("IfNoneMatch 通配符不能与 IfNoneMatchEtags 同时指定。", nameof(condition));
        if (!string.IsNullOrEmpty(condition.IfNoneMatchEtags) && ContainsEntityTagWildcard(condition.IfNoneMatchEtags))
            throw new ArgumentException("IfNoneMatchEtags 不得包含通配符。", nameof(condition));
        if (!string.IsNullOrEmpty(condition.IfMatch))
            request.Headers.TryAddWithoutValidation("If-Match", condition.IfMatch);
        if (condition.IfNoneMatch)
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else if (!string.IsNullOrEmpty(condition.IfNoneMatchEtags))
            request.Headers.TryAddWithoutValidation("If-None-Match", condition.IfNoneMatchEtags);
    }

    private static bool ContainsEntityTagWildcard(string value)
    {
        int candidateStart = 0;
        bool inQuotes = false;
        bool escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (inQuotes)
            {
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inQuotes = false;
            }
            else if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                if (value[candidateStart..index].Trim() == "*")
                    return true;

                candidateStart = index + 1;
            }
        }

        return value[candidateStart..].Trim() == "*";
    }

    private static void AddReadConditionHeaders(HttpRequestMessage request, SndbObjectReadCondition condition)
    {
        if (!string.IsNullOrEmpty(condition.IfMatch)) request.Headers.TryAddWithoutValidation("If-Match", condition.IfMatch);
        if (!string.IsNullOrEmpty(condition.IfNoneMatch)) request.Headers.TryAddWithoutValidation("If-None-Match", condition.IfNoneMatch);
        if (condition.IfModifiedSince is { } modifiedSince) request.Headers.IfModifiedSince = modifiedSince;
        if (condition.IfUnmodifiedSince is { } unmodifiedSince) request.Headers.TryAddWithoutValidation("If-Unmodified-Since", unmodifiedSince.ToUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
    }

}
