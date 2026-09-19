namespace SonnetDB.ObjectStorage;

public sealed partial class SndbObjectStore
{
    /// <summary>
    /// 在当前对象满足条件时写入对象。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="key">对象键。</param>
    /// <param name="content">待写入的内容流。</param>
    /// <param name="condition">写入前置条件。</param>
    /// <param name="contentType">对象内容类型。</param>
    /// <param name="metadata">对象元数据。</param>
    /// <param name="tags">对象标签。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>新版本的对象元数据。</returns>
    /// <remarks>条件检查与版本发布在同一个 bucket gate 内完成；内容会先流入临时文件。</remarks>
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
        ArgumentNullException.ThrowIfNull(condition);
        return await PutObjectCoreAsync(bucket, key, content, contentType, metadata, tags, cancellationToken, condition).ConfigureAwait(false);
    }

    /// <summary>
    /// 按 HTTP 风格前置条件读取对象。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="key">对象键。</param>
    /// <param name="condition">读取前置条件。</param>
    /// <param name="range">可选的内容范围。</param>
    /// <param name="versionId">可选的对象版本。</param>
    /// <returns>状态和成功时持有的内容流。</returns>
    public SndbObjectConditionalReadResult OpenReadConditional(
        string bucket,
        string key,
        SndbObjectReadCondition condition,
        SndbObjectRange? range = null,
        string? versionId = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var mutation = GetBucketMutationState(bucket);
        lock (mutation.Gate)
        {
            var info = HeadObject(bucket, key, versionId);
            if (info is null)
            {
                // RFC 9110: 缺失目标资源不能满足 If-Match（包括通配符）。
                var missingStatus = string.IsNullOrEmpty(condition.IfMatch)
                    ? SndbObjectConditionalReadStatus.NotFound
                    : SndbObjectConditionalReadStatus.PreconditionFailed;
                return new SndbObjectConditionalReadResult(missingStatus, null);
            }

            SndbObjectConditionalReadStatus status = EvaluateReadCondition(info, condition);
            if (status != SndbObjectConditionalReadStatus.Success)
                return new SndbObjectConditionalReadResult(status, null, info);

            // 使用已经核验的版本，并与对象发布共用 bucket gate，避免条件检查和打开内容之间换到另一版本。
            var read = OpenRead(bucket, key, range, info.VersionId);
            return read is null
                ? new SndbObjectConditionalReadResult(SndbObjectConditionalReadStatus.NotFound, null, info)
                : new SndbObjectConditionalReadResult(SndbObjectConditionalReadStatus.Success, read, info);
        }
    }

    /// <summary>
    /// 以异步游标逐项枚举当前可见对象。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="prefix">对象键前缀。</param>
    /// <param name="pageSize">每次请求的最大对象数。</param>
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
            var page = ListObjects(bucket, prefix, pageSize, continuationToken, delimiter: null, cancellationToken);
            foreach (var item in page.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }

            continuationToken = page.NextContinuationToken;
            await Task.Yield();
        }
        while (continuationToken is not null);
    }

    private void EnsureWriteConditionUnderLock(string bucket, string key, SndbObjectWriteCondition condition)
    {
        var current = LoadObjectRecord(bucket, key);
        bool exists = current is { IsDeleteMarker: false };
        if (!string.IsNullOrEmpty(condition.IfMatch)
            && (!exists
                || !MatchesIfMatch(current!.ETag, condition.IfMatch)))
        {
            throw new SndbObjectStorageException("object_precondition_failed", "Object ETag does not match IfMatch.");
        }
        if (condition.IfNoneMatch && exists)
            throw new SndbObjectStorageException("object_precondition_failed", "Object already exists.");
        if (exists
            && !string.IsNullOrEmpty(condition.IfNoneMatchEtags)
            && MatchesIfNoneMatch(current!.ETag, condition.IfNoneMatchEtags))
        {
            throw new SndbObjectStorageException("object_precondition_failed", "Object ETag matches IfNoneMatch.");
        }
    }

    private static void ValidateWriteCondition(SndbObjectWriteCondition? condition)
    {
        if (condition is null)
            return;

        if (condition.IfNoneMatch && !string.IsNullOrEmpty(condition.IfNoneMatchEtags))
            throw new ArgumentException("IfNoneMatch 通配符不能与 IfNoneMatchEtags 同时指定。", nameof(condition));

        if (!string.IsNullOrEmpty(condition.IfNoneMatchEtags)
            && EnumerateEntityTags(condition.IfNoneMatchEtags).Any(static candidate => candidate == "*"))
        {
            throw new ArgumentException("IfNoneMatchEtags 不得包含通配符。", nameof(condition));
        }
    }

    /// <summary>
    /// 判定对象元数据是否满足 HTTP 风格的读取前置条件，不会打开对象内容流。
    /// </summary>
    /// <param name="info">已定位的对象元数据。</param>
    /// <param name="condition">读取前置条件。</param>
    /// <returns>读取应返回的条件状态。</returns>
    public static SndbObjectConditionalReadStatus EvaluateReadCondition(
        SndbObjectInfo info,
        SndbObjectReadCondition condition)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(condition);

        if (!string.IsNullOrEmpty(condition.IfMatch) && !MatchesIfMatch(info.ETag, condition.IfMatch))
        {
            return SndbObjectConditionalReadStatus.PreconditionFailed;
        }
        // RFC 9110: If-Match 存在时，If-Unmodified-Since 不参与前置条件判断。
        if (string.IsNullOrEmpty(condition.IfMatch)
            && condition.IfUnmodifiedSince is { } unmodifiedSince
            && TruncateToHttpSeconds(info.UpdatedUtc) > TruncateToHttpSeconds(unmodifiedSince))
        {
            return SndbObjectConditionalReadStatus.PreconditionFailed;
        }
        // RFC 9110: If-None-Match 存在时，If-Modified-Since 不参与前置条件判断。
        if (!string.IsNullOrEmpty(condition.IfNoneMatch) && MatchesIfNoneMatch(info.ETag, condition.IfNoneMatch))
        {
            return SndbObjectConditionalReadStatus.NotModified;
        }
        if (string.IsNullOrEmpty(condition.IfNoneMatch)
            && condition.IfModifiedSince is { } modifiedSince
            && TruncateToHttpSeconds(info.UpdatedUtc) <= TruncateToHttpSeconds(modifiedSince))
        {
            return SndbObjectConditionalReadStatus.NotModified;
        }

        return SndbObjectConditionalReadStatus.Success;
    }

    private static bool MatchesIfMatch(string currentEtag, string headerValue)
    {
        foreach (string candidate in EnumerateEntityTags(headerValue))
        {
            if (candidate == "*")
                return true;

            // If-Match 使用 strong comparison，弱 ETag 不能匹配。
            if (!IsWeakEntityTag(candidate) && string.Equals(currentEtag, candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool MatchesIfNoneMatch(string currentEtag, string headerValue)
    {
        foreach (string candidate in EnumerateEntityTags(headerValue))
        {
            if (candidate == "*" || string.Equals(NormalizeWeakEntityTag(candidate), NormalizeWeakEntityTag(currentEtag), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateEntityTags(string headerValue)
    {
        int candidateStart = 0;
        bool inQuotes = false;
        bool escaped = false;

        for (int index = 0; index < headerValue.Length; index++)
        {
            char character = headerValue[index];
            if (inQuotes)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inQuotes = false;
                }

                continue;
            }

            if (character == '"')
            {
                inQuotes = true;
            }
            else if (character == ',')
            {
                string candidate = headerValue[candidateStart..index].Trim();
                if (candidate.Length > 0)
                    yield return candidate;

                candidateStart = index + 1;
            }
        }

        string finalCandidate = headerValue[candidateStart..].Trim();
        if (finalCandidate.Length > 0)
            yield return finalCandidate;
    }

    private static bool IsWeakEntityTag(string value) => value.StartsWith("W/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWeakEntityTag(string value) =>
        IsWeakEntityTag(value) ? value[2..].TrimStart() : value;

    private static DateTimeOffset TruncateToHttpSeconds(DateTimeOffset value)
    {
        DateTime utc = value.UtcDateTime;
        utc = utc.AddTicks(-(utc.Ticks % TimeSpan.TicksPerSecond));
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }
}
