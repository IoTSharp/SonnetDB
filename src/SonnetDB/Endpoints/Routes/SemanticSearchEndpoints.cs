using System.Buffers;
using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML.OnnxRuntime;
using SixLabors.ImageSharp;
using SonnetDB.Auth;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticSearch;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private static void MapSemanticSearchEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapGet("/v1/semantic-search/status", async (HttpContext ctx, CancellationToken cancellationToken) =>
        {
            _ = cancellationToken;
            var status = ctx.RequestServices.GetRequiredService<SemanticImageSearchService>().GetStatus();
            await Results.Json(status, ServerJsonContext.Default.SemanticSearchStatusResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPut("/v1/db/{db}/images/{id}", async (HttpContext ctx, string db, string id) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);

            try
            {
                var service = ctx.RequestServices.GetRequiredService<SemanticImageSearchService>();
                int maxBytes = ctx.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<SonnetDB.Configuration.ServerOptions>>()
                    .Value.SemanticSearch.MaxImageBytes;
                byte[] image = await ReadBoundedBodyAsync(ctx.Request, maxBytes, ctx.RequestAborted).ConfigureAwait(false);
                string contentType = NormalizeMediaType(ctx.Request.ContentType);
                var result = await service.IngestAsync(
                    db,
                    tsdb,
                    id,
                    image,
                    contentType,
                    ctx.Request.Query["fileName"].FirstOrDefault(),
                    ctx.Request.Query["sourceUri"].FirstOrDefault(),
                    ctx.RequestAborted).ConfigureAwait(false);
                await Results.Json(result, ServerJsonContext.Default.ImageIngestResponse, statusCode: StatusCodes.Status201Created)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticBadRequest(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_image", ex.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticProviderFailure(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "semantic_provider_unavailable", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/images/search/text", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.ImageTextSearchRequest).ConfigureAwait(false);
            if (request is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不能为空。").ConfigureAwait(false);
                return;
            }
            registry.TryGet(db, out var tsdb);

            try
            {
                var result = await ctx.RequestServices.GetRequiredService<SemanticImageSearchService>()
                    .SearchTextAsync(
                        db,
                        tsdb,
                        request.Text,
                        request.TopK,
                        request.MinScore,
                        request.Filter,
                        request.Explain,
                        ctx.RequestAborted)
                    .ConfigureAwait(false);
                await Results.Json(result, ServerJsonContext.Default.ImageSearchResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticBadRequest(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticProviderFailure(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "semantic_provider_unavailable", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/images/search/image", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);

            try
            {
                var options = ctx.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<SonnetDB.Configuration.ServerOptions>>()
                    .Value.SemanticSearch;
                byte[] image = await ReadBoundedBodyAsync(ctx.Request, options.MaxImageBytes, ctx.RequestAborted).ConfigureAwait(false);
                int? topK = ParseOptionalInt(ctx.Request.Query["topK"].FirstOrDefault(), "topK");
                double? minScore = ParseOptionalDouble(ctx.Request.Query["minScore"].FirstOrDefault(), "minScore");
                ImageSearchFilter? filter = ParseImageSearchFilter(ctx.Request.Query);
                bool explain = ParseOptionalBool(ctx.Request.Query["explain"].FirstOrDefault(), "explain") ?? false;
                var result = await ctx.RequestServices.GetRequiredService<SemanticImageSearchService>()
                    .SearchImageAsync(db, tsdb, image, topK, minScore, filter, explain, ctx.RequestAborted)
                    .ConfigureAwait(false);
                await Results.Json(result, ServerJsonContext.Default.ImageSearchResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticBadRequest(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "invalid_image", ex.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticProviderFailure(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "semantic_provider_unavailable", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/images/{id}/similar", async (HttpContext ctx, string db, string id) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var request = await ReadJsonAsync(ctx, ServerJsonContext.Default.SimilarImageSearchRequest).ConfigureAwait(false);
            if (request is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不能为空。").ConfigureAwait(false);
                return;
            }
            registry.TryGet(db, out var tsdb);

            try
            {
                var result = await ctx.RequestServices.GetRequiredService<SemanticImageSearchService>()
                    .SearchSimilarAsync(
                        db,
                        tsdb,
                        id,
                        request.TopK,
                        request.MinScore,
                        request.Filter,
                        request.Explain,
                        ctx.RequestAborted)
                    .ConfigureAwait(false);
                if (result is null)
                {
                    await WriteSimpleErrorAsync(
                        ctx,
                        StatusCodes.Status404NotFound,
                        "image_not_found",
                        $"图片 '{id}' 不存在。").ConfigureAwait(false);
                    return;
                }

                await Results.Json(result, ServerJsonContext.Default.ImageSearchResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticBadRequest(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsSemanticProviderFailure(ex))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "semantic_provider_unavailable", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/images/{id}", async (HttpContext ctx, string db, string id) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            try
            {
                var info = ctx.RequestServices.GetRequiredService<SemanticImageSearchService>()
                    .GetInfo(db, tsdb.Documents, id);
                if (info is null)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "image_not_found", $"图片 '{id}' 不存在。").ConfigureAwait(false);
                    return;
                }
                await Results.Json(info, ServerJsonContext.Default.ImageInfoResponse).ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/images/{id}/content", async (HttpContext ctx, string db, string id) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            try
            {
                var read = ctx.RequestServices.GetRequiredService<SemanticImageSearchService>().OpenContent(tsdb, id);
                if (read is null)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "image_not_found", $"图片 '{id}' 不存在。").ConfigureAwait(false);
                    return;
                }

                await using (read.Content)
                {
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    ctx.Response.ContentType = read.Info.ContentType;
                    ctx.Response.ContentLength = read.Length;
                    ctx.Response.Headers.ETag = read.Info.ETag;
                    await read.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapGet("/v1/db/{db}/images/{id}/thumbnail", async (HttpContext ctx, string db, string id) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            try
            {
                var read = ctx.RequestServices.GetRequiredService<SemanticImageSearchService>()
                    .OpenThumbnail(tsdb, id);
                if (read is null)
                {
                    await WriteSimpleErrorAsync(
                        ctx,
                        StatusCodes.Status404NotFound,
                        "thumbnail_not_found",
                        $"图片 '{id}' 没有可用缩略图。").ConfigureAwait(false);
                    return;
                }

                await using (read.Content)
                {
                    ctx.Response.StatusCode = StatusCodes.Status200OK;
                    ctx.Response.ContentType = read.Info.ContentType;
                    ctx.Response.ContentLength = read.Length;
                    ctx.Response.Headers.ETag = read.Info.ETag;
                    await read.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapDelete("/v1/db/{db}/images/{id}", async (HttpContext ctx, string db, string id) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Write).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            try
            {
                bool deleted = await ctx.RequestServices.GetRequiredService<SemanticImageSearchService>()
                    .DeleteAsync(db, tsdb, id, ctx.RequestAborted).ConfigureAwait(false);
                await Results.Json(new ImageDeleteResponse(id, deleted), ServerJsonContext.Default.ImageDeleteResponse,
                        statusCode: deleted ? StatusCodes.Status200OK : StatusCodes.Status404NotFound)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (maxBytes <= 0)
            throw new InvalidOperationException("语义图片检索未配置有效的 MaxImageBytes。");
        if (request.ContentLength is > 0 && request.ContentLength > maxBytes)
            throw new ArgumentOutOfRangeException(nameof(request), $"图片不能超过 {maxBytes} 字节。");

        using var output = new MemoryStream(Math.Min(maxBytes, 256 * 1024));
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int total = 0;
            while (true)
            {
                int read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total = checked(total + read);
                if (total > maxBytes)
                    throw new ArgumentOutOfRangeException(nameof(request), $"图片不能超过 {maxBytes} 字节。");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total == 0)
                throw new ArgumentException("图片内容不能为空。", nameof(request));
            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string NormalizeMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "application/octet-stream";
        int separator = contentType.IndexOf(';', StringComparison.Ordinal);
        return (separator < 0 ? contentType : contentType[..separator]).Trim();
    }

    private static int? ParseOptionalInt(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            throw new ArgumentException($"{name} 必须是整数。");
        return parsed;
    }

    private static double? ParseOptionalDouble(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            throw new ArgumentException($"{name} 必须是数字。");
        return parsed;
    }

    private static bool? ParseOptionalBool(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!bool.TryParse(value, out bool parsed))
            throw new ArgumentException($"{name} 必须是 true 或 false。");
        return parsed;
    }

    private static ImageSearchFilter? ParseImageSearchFilter(IQueryCollection query)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query)
        {
            if (pair.Key.StartsWith("metadata.", StringComparison.OrdinalIgnoreCase))
            {
                AddFilterValue(metadata, pair.Key["metadata.".Length..], pair.Value.FirstOrDefault());
            }
            else if (pair.Key.StartsWith("tag.", StringComparison.OrdinalIgnoreCase))
            {
                AddFilterValue(tags, pair.Key["tag.".Length..], pair.Value.FirstOrDefault());
            }
        }

        string? sourceBucket = NormalizeFilterValue(query["sourceBucket"].FirstOrDefault());
        string? sourceKeyPrefix = NormalizeFilterValue(query["sourceKeyPrefix"].FirstOrDefault());
        string? contentType = NormalizeFilterValue(query["contentType"].FirstOrDefault());
        if (sourceBucket is null
            && sourceKeyPrefix is null
            && contentType is null
            && metadata.Count == 0
            && tags.Count == 0)
        {
            return null;
        }

        return new ImageSearchFilter(
            sourceBucket,
            sourceKeyPrefix,
            contentType,
            metadata.Count == 0 ? null : metadata,
            tags.Count == 0 ? null : tags);
    }

    private static void AddFilterValue(Dictionary<string, string> target, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("metadata/tag 过滤键不能为空。");
        target[key] = value ?? string.Empty;
    }

    private static string? NormalizeFilterValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSemanticBadRequest(Exception exception)
        => exception is ArgumentException
            or UnknownImageFormatException
            or InvalidImageContentException;

    private static bool IsSemanticProviderFailure(Exception exception)
        => exception is InvalidOperationException
            or InvalidDataException
            or NotSupportedException
            or OnnxRuntimeException;
}
