using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Endpoints;

/// <summary>
/// SonnetDB S3-compatible 对象桶 API 第一版。
/// </summary>
internal static class ObjectStorageEndpointHandler
{
    public static async Task HandleBucketsAsync(HttpContext ctx, Tsdb tsdb)
    {
        var store = new SndbObjectStore(tsdb);
        var buckets = store.ListBuckets()
            .Select(ToBucketResponse)
            .ToArray();
        await Results.Json(buckets, ServerJsonContext.Default.ObjectBucketResponseArray).ExecuteAsync(ctx).ConfigureAwait(false);
    }

    public static async Task HandleBucketAsync(HttpContext ctx, Tsdb tsdb, string bucket)
    {
        try
        {
            var store = new SndbObjectStore(tsdb);

            if (HttpMethods.IsPut(ctx.Request.Method) && ctx.Request.Query.ContainsKey("lifecycle"))
            {
                var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.ObjectLifecycleRequest).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "Lifecycle request body is required.").ConfigureAwait(false);
                    return;
                }

                var lifecycle = store.SetLifecycle(
                    bucket,
                    request.ExpireCurrentAfterDays,
                    request.ExpireNoncurrentAfterDays,
                    request.ExpireDeleteMarkerAfterDays);
                await Results.Json(ToLifecycleResponse(lifecycle), ServerJsonContext.Default.ObjectLifecycleResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Query.ContainsKey("lifecycle"))
            {
                var applied = store.ApplyLifecycle(bucket);
                await Results.Json(ToLifecycleApplyResponse(applied), ServerJsonContext.Default.ObjectLifecycleApplyResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Query.ContainsKey("delete"))
            {
                var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.ObjectDeleteManyRequest).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "DeleteObjects request body is required.").ConfigureAwait(false);
                    return;
                }

                var deleted = store.DeleteObjects(bucket, request.Keys);
                await Results.Json(ToDeleteManyResponse(deleted), ServerJsonContext.Default.ObjectDeleteManyResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsPut(ctx.Request.Method))
            {
                ObjectBucketCreateRequest? request = null;
                if (ctx.Request.ContentLength is > 0)
                    request = await ReadJsonAsync(ctx, ServerJsonContext.Default.ObjectBucketCreateRequest).ConfigureAwait(false);
                var created = store.CreateBucket(bucket, request?.Purpose ?? ctx.Request.Query["purpose"].ToString());
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                await JsonSerializer.SerializeAsync(
                    ctx.Response.Body,
                    ToBucketResponse(created),
                    ServerJsonContext.Default.ObjectBucketResponse,
                    ctx.RequestAborted).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                if (ctx.Request.Query.ContainsKey("list-type"))
                {
                    int maxKeys = 1000;
                    if (ctx.Request.Query.TryGetValue("max-keys", out var maxKeyValues)
                        && int.TryParse(maxKeyValues.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedMaxKeys))
                    {
                        maxKeys = Math.Clamp(parsedMaxKeys, 1, 10_000);
                    }

                    var listed = store.ListObjects(
                        bucket,
                        ctx.Request.Query["prefix"].ToString(),
                        maxKeys,
                        ctx.Request.Query["continuation-token"].ToString());
                    await Results.Json(ToListResponse(listed), ServerJsonContext.Default.ObjectListResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                    return;
                }

                if (ctx.Request.Query.ContainsKey("versions"))
                {
                    var versions = store.ListObjectVersions(bucket, ctx.Request.Query["key"].ToString());
                    await Results.Json(ToVersionListResponse(versions), ServerJsonContext.Default.ObjectVersionListResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                    return;
                }

                if (ctx.Request.Query.ContainsKey("lifecycle"))
                {
                    var lifecycle = store.GetLifecycle(bucket);
                    await Results.Json(ToLifecycleResponse(lifecycle), ServerJsonContext.Default.ObjectLifecycleResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                    return;
                }

                if (ctx.Request.Query.ContainsKey("audit"))
                {
                    int maxEntries = 1000;
                    if (ctx.Request.Query.TryGetValue("max-entries", out var maxEntryValues)
                        && int.TryParse(maxEntryValues.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedMaxEntries))
                    {
                        maxEntries = Math.Clamp(parsedMaxEntries, 1, 10_000);
                    }

                    var audit = store.ListAudit(bucket, ctx.Request.Query["prefix"].ToString(), maxEntries);
                    await Results.Json(ToAuditListResponse(bucket, audit), ServerJsonContext.Default.ObjectAuditListResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                    return;
                }

                var existing = store.GetBucket(bucket);
                if (existing is null)
                {
                    await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "bucket_not_found", $"Bucket '{bucket}' was not found.").ConfigureAwait(false);
                    return;
                }

                await Results.Json(ToBucketResponse(existing), ServerJsonContext.Default.ObjectBucketResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsDelete(ctx.Request.Method))
            {
                bool removed = store.DeleteBucket(bucket);
                ctx.Response.StatusCode = removed ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
                return;
            }

            await WriteErrorAsync(ctx, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Unsupported bucket method.").ConfigureAwait(false);
        }
        catch (Exception ex) when (TryMapException(ctx, ex, out var task))
        {
            await task.ConfigureAwait(false);
        }
    }

    public static async Task HandleObjectAsync(HttpContext ctx, Tsdb tsdb, string bucket, string key)
    {
        try
        {
            var store = new SndbObjectStore(tsdb);
            if (ctx.Request.Query.ContainsKey("presign") && HttpMethods.IsPost(ctx.Request.Method))
            {
                await CreatePresignedUrlAsync(ctx, store, bucket, key).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.Query.TryGetValue("uploadId", out var uploadIdValues))
            {
                await HandleMultipartAsync(ctx, store, bucket, key, uploadIdValues.ToString()).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.Query.ContainsKey("uploads") && HttpMethods.IsPost(ctx.Request.Method))
            {
                await InitiateMultipartUploadAsync(ctx, store, bucket, key).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.Query.ContainsKey("tagging"))
            {
                await HandleTagsAsync(ctx, store, bucket, key).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.Headers.TryGetValue("x-amz-copy-source", out var copySource) && HttpMethods.IsPut(ctx.Request.Method))
            {
                await CopyObjectAsync(ctx, store, bucket, key, copySource.ToString()).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsPut(ctx.Request.Method))
            {
                var info = await store.PutObjectAsync(
                    bucket,
                    key,
                    ctx.Request.Body,
                    ctx.Request.ContentType,
                    ReadMetadataHeaders(ctx),
                    ReadTagsFromHeader(ctx),
                    ctx.RequestAborted).ConfigureAwait(false);
                WriteObjectHeaders(ctx, info);
                await Results.Json(ToObjectResponse(info), ServerJsonContext.Default.ObjectInfoResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsHead(ctx.Request.Method))
            {
                var info = store.HeadObject(bucket, key, ctx.Request.Query["versionId"].ToString());
                if (info is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                WriteObjectHeaders(ctx, info);
                ctx.Response.ContentType = info.ContentType;
                ctx.Response.ContentLength = info.SizeBytes;
                ctx.Response.StatusCode = StatusCodes.Status200OK;
                return;
            }

            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                var result = store.OpenRead(bucket, key, ParseRange(ctx.Request.Headers.Range), ctx.Request.Query["versionId"].ToString());
                if (result is null)
                {
                    await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "object_not_found", $"Object '{bucket}/{key}' was not found.").ConfigureAwait(false);
                    return;
                }

                await using (result.Content)
                {
                    WriteObjectHeaders(ctx, result.Info);
                    ctx.Response.ContentType = result.Info.ContentType;
                    ctx.Response.ContentLength = result.Length;
                    if (result.IsRange)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status206PartialContent;
                        ctx.Response.Headers.ContentRange = $"bytes {result.Offset}-{result.Offset + result.Length - 1}/{result.Info.SizeBytes}";
                    }
                    else
                    {
                        ctx.Response.StatusCode = StatusCodes.Status200OK;
                    }

                    await result.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
                }
                return;
            }

            if (HttpMethods.IsDelete(ctx.Request.Method))
            {
                var marker = store.DeleteObject(bucket, key);
                ctx.Response.Headers.ETag = marker.ETag;
                ctx.Response.Headers["x-amz-delete-marker"] = "true";
                ctx.Response.Headers["x-amz-version-id"] = marker.VersionId;
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await WriteErrorAsync(ctx, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Unsupported object method.").ConfigureAwait(false);
        }
        catch (Exception ex) when (TryMapException(ctx, ex, out var task))
        {
            await task.ConfigureAwait(false);
        }
    }

    public static async Task HandlePresignedObjectAsync(HttpContext ctx, Tsdb tsdb, string bucket, string key)
    {
        var token = ctx.Request.Query["sndb-presigned"].ToString();
        var store = new SndbObjectStore(tsdb);
        if (!store.TryValidatePresignedToken(token, ctx.Request.Method, bucket, key))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status403Forbidden, "presigned_url_invalid", "Presigned URL is invalid or expired.").ConfigureAwait(false);
            return;
        }

        await HandleObjectAsync(ctx, tsdb, bucket, key).ConfigureAwait(false);
    }

    private static async Task HandleMultipartAsync(HttpContext ctx, SndbObjectStore store, string bucket, string key, string uploadId)
    {
        if (ctx.Request.Query.TryGetValue("partNumber", out var partNumberValues) && HttpMethods.IsPut(ctx.Request.Method))
        {
            if (!int.TryParse(partNumberValues.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int partNumber))
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "partNumber must be an integer.").ConfigureAwait(false);
                return;
            }

            var part = await store.UploadPartAsync(uploadId, partNumber, ctx.Request.Body, ctx.RequestAborted).ConfigureAwait(false);
            ctx.Response.Headers.ETag = part.ETag;
            await Results.Json(new MultipartPartResponse(part.PartNumber, part.SizeBytes, part.ETag, part.Sha256), ServerJsonContext.Default.MultipartPartResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.MultipartCompleteRequest).ConfigureAwait(false);
            if (request is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "Multipart complete request body is required.").ConfigureAwait(false);
                return;
            }

            var info = await store.CompleteMultipartUploadAsync(uploadId, request.PartNumbers, ctx.RequestAborted).ConfigureAwait(false);
            WriteObjectHeaders(ctx, info);
            await Results.Json(ToObjectResponse(info), ServerJsonContext.Default.ObjectInfoResponse).ExecuteAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsDelete(ctx.Request.Method))
        {
            store.AbortMultipartUpload(uploadId);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }

        await WriteErrorAsync(ctx, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Unsupported multipart method.").ConfigureAwait(false);
    }

    private static async Task InitiateMultipartUploadAsync(HttpContext ctx, SndbObjectStore store, string bucket, string key)
    {
        MultipartUploadCreateRequest? request = null;
        if (ctx.Request.ContentLength is > 0)
            request = await ReadJsonAsync(ctx, ServerJsonContext.Default.MultipartUploadCreateRequest).ConfigureAwait(false);

        var upload = store.InitiateMultipartUpload(
            bucket,
            key,
            request?.ContentType ?? ctx.Request.ContentType,
            request?.Metadata ?? ReadMetadataHeaders(ctx),
            request?.Tags ?? ReadTagsFromHeader(ctx),
            request?.ExpiresHours is > 0 ? TimeSpan.FromHours(request.ExpiresHours.Value) : null);

        var response = new MultipartUploadCreateResponse(
            upload.Bucket,
            upload.Key,
            upload.UploadId,
            upload.ContentType,
            upload.InitiatedUtc,
            upload.ExpiresUtc,
            upload.Metadata,
            upload.Tags);
        await Results.Json(response, ServerJsonContext.Default.MultipartUploadCreateResponse).ExecuteAsync(ctx).ConfigureAwait(false);
    }

    private static async Task HandleTagsAsync(HttpContext ctx, SndbObjectStore store, string bucket, string key)
    {
        if (HttpMethods.IsGet(ctx.Request.Method))
        {
            var info = store.HeadObject(bucket, key);
            if (info is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "object_not_found", $"Object '{bucket}/{key}' was not found.").ConfigureAwait(false);
                return;
            }

            await Results.Json(new ObjectTagsRequest(info.Tags), ServerJsonContext.Default.ObjectTagsRequest).ExecuteAsync(ctx).ConfigureAwait(false);
            return;
        }

        if (HttpMethods.IsPut(ctx.Request.Method))
        {
            var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.ObjectTagsRequest).ConfigureAwait(false);
            if (request is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "Tag request body is required.").ConfigureAwait(false);
                return;
            }

            var info = store.SetObjectTags(bucket, key, request.Tags);
            await Results.Json(ToObjectResponse(info), ServerJsonContext.Default.ObjectInfoResponse).ExecuteAsync(ctx).ConfigureAwait(false);
            return;
        }

        await WriteErrorAsync(ctx, StatusCodes.Status405MethodNotAllowed, "method_not_allowed", "Unsupported tagging method.").ConfigureAwait(false);
    }

    private static async Task CopyObjectAsync(HttpContext ctx, SndbObjectStore store, string bucket, string key, string source)
    {
        var parsed = ParseCopySource(source);
        if (parsed is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "x-amz-copy-source must be /bucket/key.").ConfigureAwait(false);
            return;
        }

        var info = await store.CopyObjectAsync(
            parsed.Value.Bucket,
            parsed.Value.Key,
            bucket,
            key,
            ReadMetadataHeadersOrNull(ctx),
            ReadTagsFromHeaderOrNull(ctx),
            ctx.RequestAborted).ConfigureAwait(false);

        WriteObjectHeaders(ctx, info);
        await Results.Json(new ObjectCopyResponse(info.ETag, info.Sha256, info.VersionId), ServerJsonContext.Default.ObjectCopyResponse).ExecuteAsync(ctx).ConfigureAwait(false);
    }

    private static async Task CreatePresignedUrlAsync(HttpContext ctx, SndbObjectStore store, string bucket, string key)
    {
        var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.PresignedObjectUrlCreateRequest).ConfigureAwait(false);
        if (request is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "Presigned URL request body is required.").ConfigureAwait(false);
            return;
        }

        string encodedKey = string.Join(
            '/',
            key.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        string baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/s3/{ctx.Request.RouteValues["db"]}/{Uri.EscapeDataString(bucket)}/{encodedKey}";
        var url = store.CreatePresignedUrl(
            baseUrl,
            request.Method,
            bucket,
            key,
            TimeSpan.FromMinutes(Math.Clamp(request.ExpiresMinutes, 1, 24 * 60)));
        await Results.Json(ToPresignedResponse(url), ServerJsonContext.Default.PresignedObjectUrlResponse).ExecuteAsync(ctx).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ReadMetadataHeaders(HttpContext ctx)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in ctx.Request.Headers)
        {
            if (header.Key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                result[header.Key["x-amz-meta-".Length..]] = header.Value.ToString();
        }

        return result;
    }

    private static Dictionary<string, string>? ReadMetadataHeadersOrNull(HttpContext ctx)
    {
        var headers = ReadMetadataHeaders(ctx);
        return headers.Count == 0 ? null : headers;
    }

    private static Dictionary<string, string> ReadTagsFromHeader(HttpContext ctx)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string raw = ctx.Request.Headers["x-amz-tagging"].ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (string pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
                continue;

            string name = Uri.UnescapeDataString(pair[..separator]);
            string value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            result[name] = value;
        }

        return result;
    }

    private static Dictionary<string, string>? ReadTagsFromHeaderOrNull(HttpContext ctx)
    {
        var tags = ReadTagsFromHeader(ctx);
        return tags.Count == 0 ? null : tags;
    }

    private static SndbObjectRange? ParseRange(string? rangeHeader)
    {
        if (string.IsNullOrWhiteSpace(rangeHeader))
            return null;

        const string prefix = "bytes=";
        if (!rangeHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string range = rangeHeader[prefix.Length..];
        int separator = range.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
            return null;

        if (!long.TryParse(range[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out long start))
            return null;

        long? length = null;
        string endText = range[(separator + 1)..];
        if (!string.IsNullOrWhiteSpace(endText)
            && long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out long end)
            && end >= start)
        {
            length = end - start + 1;
        }

        return new SndbObjectRange(start, length);
    }

    private static (string Bucket, string Key)? ParseCopySource(string source)
    {
        string normalized = Uri.UnescapeDataString(source.Trim().TrimStart('/'));
        int separator = normalized.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == normalized.Length - 1)
            return null;

        return (normalized[..separator], normalized[(separator + 1)..]);
    }

    private static void WriteObjectHeaders(HttpContext ctx, SndbObjectInfo info)
    {
        ctx.Response.Headers.ETag = info.ETag;
        ctx.Response.Headers["x-amz-version-id"] = info.VersionId;
        ctx.Response.Headers["x-amz-meta-sha256"] = info.Sha256;
        ctx.Response.Headers["x-amz-delete-marker"] = info.IsDeleteMarker ? "true" : "false";
        foreach (var pair in info.Metadata)
            ctx.Response.Headers["x-amz-meta-" + pair.Key] = pair.Value;
    }

    private static ObjectBucketResponse ToBucketResponse(SndbBucketInfo bucket) =>
        new(bucket.Name, bucket.Purpose, bucket.CreatedUtc, bucket.UpdatedUtc);

    private static ObjectInfoResponse ToObjectResponse(SndbObjectInfo info) =>
        new(info.Bucket, info.Key, info.VersionId, info.ContentType, info.SizeBytes, info.ETag, info.Sha256, info.IsDeleteMarker, info.CreatedUtc, info.UpdatedUtc, info.Metadata, info.Tags);

    private static ObjectListResponse ToListResponse(SndbObjectListResult result) =>
        new(
            result.Bucket,
            result.Prefix,
            result.MaxKeys,
            result.ContinuationToken,
            result.NextContinuationToken,
            result.IsTruncated,
            result.Objects.Select(ToObjectResponse).ToArray());

    private static ObjectDeleteManyResponse ToDeleteManyResponse(SndbObjectDeleteManyResult result) =>
        new(
            result.Bucket,
            result.Deleted.Select(static item => new ObjectDeleteResultResponse(
                item.Key,
                item.VersionId,
                item.DeleteMarker,
                item.ErrorCode,
                item.ErrorMessage)).ToArray());

    private static ObjectVersionListResponse ToVersionListResponse(SndbObjectVersionListResult result) =>
        new(result.Bucket, result.Key, result.Versions.Select(ToObjectResponse).ToArray());

    private static PresignedObjectUrlResponse ToPresignedResponse(SndbPresignedObjectUrl url) =>
        new(url.Url, url.Method, url.Bucket, url.Key, url.ExpiresUtc);

    private static ObjectLifecycleResponse ToLifecycleResponse(SndbBucketLifecycleInfo lifecycle) =>
        new(
            lifecycle.Bucket,
            lifecycle.ExpireCurrentAfterDays,
            lifecycle.ExpireNoncurrentAfterDays,
            lifecycle.ExpireDeleteMarkerAfterDays,
            lifecycle.UpdatedUtc);

    private static ObjectLifecycleApplyResponse ToLifecycleApplyResponse(SndbBucketLifecycleApplyResult result) =>
        new(result.Bucket, result.ExpiredCurrentObjects, result.RemovedNoncurrentVersions, result.RemovedDeleteMarkers);

    private static ObjectAuditListResponse ToAuditListResponse(string bucket, IReadOnlyList<SndbObjectAuditEntry> entries) =>
        new(bucket, entries.Select(static entry => new ObjectAuditEntryResponse(
            entry.Id,
            entry.Action,
            entry.Bucket,
            entry.Key,
            entry.VersionId,
            entry.TimestampUtc,
            entry.Details)).ToArray());

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, typeInfo, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryMapException(HttpContext ctx, Exception exception, out Task task)
    {
        task = exception switch
        {
            SndbObjectStorageException ex when ex.Code.EndsWith("_not_found", StringComparison.Ordinal) =>
                WriteErrorAsync(ctx, StatusCodes.Status404NotFound, ex.Code, ex.Message),
            SndbObjectStorageException ex when ex.Code.Contains("expired", StringComparison.Ordinal) =>
                WriteErrorAsync(ctx, StatusCodes.Status409Conflict, ex.Code, ex.Message),
            SndbObjectStorageException ex =>
                WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, ex.Code, ex.Message),
            ArgumentException ex =>
                WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message),
            InvalidDataException ex =>
                WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "object_storage_corrupt", ex.Message),
            _ => Task.CompletedTask,
        };

        return task != Task.CompletedTask;
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int statusCode, string code, string message)
    {
        if (ctx.Response.HasStarted)
            return;

        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(
            ctx.Response.Body,
            new ErrorResponse(code, message),
            ServerJsonContext.Default.ErrorResponse,
            ctx.RequestAborted).ConfigureAwait(false);
    }
}
