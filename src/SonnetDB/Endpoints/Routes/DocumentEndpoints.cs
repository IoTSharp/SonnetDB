using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Kv;
using SonnetDB.Mcp;

namespace SonnetDB.Endpoints;

internal static partial class SonnetDbEndpoints
{
    private const int DefaultDocumentFindLimit = 100;
    private const int MaxDocumentFindLimit = 1000;
    private static readonly TimeSpan DocumentCursorTtl = TimeSpan.FromMinutes(15);

    private static void MapDocumentEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        app.MapPost("/v1/db/{db}/documents/{collection}", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: false).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentCollectionCreateRequest).ConfigureAwait(false)
                ?? new DocumentCollectionCreateRequest();

            registry.TryGet(db, out var tsdb);
            if (tsdb.Documents.Catalog.TryGet(collection) is not null)
            {
                if (!req.IfNotExists)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "collection_exists",
                        $"document collection '{collection}' 已存在。").ConfigureAwait(false);
                    return;
                }

                await Results.Json(
                    new DocumentCollectionOperationResponse(collection, "exists"),
                    ServerJsonContext.Default.DocumentCollectionOperationResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            tsdb.Documents.Create(DocumentCollectionSchema.Create(collection));
            await Results.Json(
                new DocumentCollectionOperationResponse(collection, "created"),
                ServerJsonContext.Default.DocumentCollectionOperationResponse,
                statusCode: StatusCodes.Status201Created).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapDelete("/v1/db/{db}/documents/{collection}", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: false).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            bool dropped = tsdb.Documents.Drop(collection);
            await Results.Json(
                new DocumentCollectionOperationResponse(collection, dropped ? "dropped" : "missing"),
                ServerJsonContext.Default.DocumentCollectionOperationResponse,
                statusCode: dropped ? StatusCodes.Status200OK : StatusCodes.Status404NotFound).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/insert-one", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentWriteItem).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            if (!await ValidateDocumentIdAsync(ctx, req.Id).ConfigureAwait(false))
                return;

            registry.TryGet(db, out var tsdb);
            tsdb.Documents.Open(collection).Upsert(req.Id, req.Document.GetRawText());
            await Results.Json(new DocumentWriteResponse(collection, Inserted: 1), ServerJsonContext.Default.DocumentWriteResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/insert-many", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentInsertManyRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var store = tsdb.Documents.Open(collection);
            int inserted = 0;
            foreach (var item in req.Documents)
            {
                if (!await ValidateDocumentIdAsync(ctx, item.Id).ConfigureAwait(false))
                    return;
                store.Upsert(item.Id, item.Document.GetRawText());
                inserted++;
            }

            await Results.Json(new DocumentWriteResponse(collection, Inserted: inserted), ServerJsonContext.Default.DocumentWriteResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/find", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Read, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentFindRequest).ConfigureAwait(false)
                ?? new DocumentFindRequest();
            if (!await ValidateFindRequestAsync(ctx, req).ConfigureAwait(false))
                return;

            registry.TryGet(db, out var tsdb);
            var store = tsdb.Documents.Open(collection);
            DocumentFindPage page;
            try
            {
                page = FindDocumentPage(collection, store, req);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                return;
            }
            catch (InvalidOperationException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                return;
            }

            await Results.Json(
                new DocumentFindResponse(
                    collection,
                    page.Documents,
                    page.Documents.Count,
                    page.Limit,
                    req.Skip,
                    page.ContinuationToken,
                    page.HasMore,
                    page.Limit,
                    page.SnapshotVersion,
                    page.CursorExpiresAtUtc),
                ServerJsonContext.Default.DocumentFindResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/find-one", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Read, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentFindRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "find-one 需要提供 id。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var row = tsdb.Documents.Open(collection).Get(req.Id);
            await Results.Json(
                new DocumentFindOneResponse(collection, row is not null, row is null ? null : ToDocumentItemResponse(row)),
                ServerJsonContext.Default.DocumentFindOneResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/update-one", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentUpdateOneRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var store = tsdb.Documents.Open(collection);
            if (req.Update is not null)
            {
                try
                {
                    var result = store.UpdateOne(
                        MergeUpdateRequestFilters(req.Id, req.Filter),
                        ToCoreUpdate(req.Update),
                        req.Upsert,
                        req.UpsertId ?? req.Id);
                    await Results.Json(
                        new DocumentWriteResponse(collection, Inserted: result.Inserted, Matched: result.Matched, Modified: result.Modified),
                        ServerJsonContext.Default.DocumentWriteResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(req.Id) || req.Document is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "update-one 需要提供 id 和 document，或提供 update 操作符。").ConfigureAwait(false);
                return;
            }
            if (!await ValidateDocumentIdAsync(ctx, req.Id).ConfigureAwait(false))
                return;
            if (store.Get(req.Id) is null)
            {
                await Results.Json(new DocumentWriteResponse(collection), ServerJsonContext.Default.DocumentWriteResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            store.Upsert(req.Id, req.Document.Value.GetRawText());
            await Results.Json(new DocumentWriteResponse(collection, Matched: 1, Modified: 1), ServerJsonContext.Default.DocumentWriteResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/update-many", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentUpdateManyRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            var store = tsdb.Documents.Open(collection);
            if (req.Update is not null)
            {
                try
                {
                    var result = store.UpdateMany(
                        ToCoreFilter(req.Filter),
                        ToCoreUpdate(req.Update),
                        req.Upsert,
                        req.UpsertId);
                    await Results.Json(
                        new DocumentWriteResponse(collection, Inserted: result.Inserted, Matched: result.Matched, Modified: result.Modified),
                        ServerJsonContext.Default.DocumentWriteResponse).ExecuteAsync(ctx).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                }
                return;
            }

            if (req.Documents is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "update-many 需要提供 documents 或 update 操作符。").ConfigureAwait(false);
                return;
            }

            int matched = 0;
            foreach (var item in req.Documents)
            {
                if (!await ValidateDocumentIdAsync(ctx, item.Id).ConfigureAwait(false))
                    return;
                if (store.Get(item.Id) is null)
                    continue;

                store.Upsert(item.Id, item.Document.GetRawText());
                matched++;
            }

            await Results.Json(new DocumentWriteResponse(collection, Matched: matched, Modified: matched), ServerJsonContext.Default.DocumentWriteResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/delete-one", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentDeleteOneRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Id))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "delete-one 需要提供 id。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            int deleted = tsdb.Documents.Open(collection).Delete(req.Id) ? 1 : 0;
            await Results.Json(new DocumentWriteResponse(collection, Deleted: deleted), ServerJsonContext.Default.DocumentWriteResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/delete-many", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Write, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentDeleteManyRequest).ConfigureAwait(false);
            if (req is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体不可为空。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            int deleted = tsdb.Documents.Open(collection).DeleteMany(req.Ids);
            await Results.Json(new DocumentWriteResponse(collection, Deleted: deleted), ServerJsonContext.Default.DocumentWriteResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/count", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Read, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentCountRequest).ConfigureAwait(false)
                ?? new DocumentCountRequest();

            registry.TryGet(db, out var tsdb);
            var store = tsdb.Documents.Open(collection);
            long count = req.Ids is null ? store.Count() : store.GetMany(req.Ids).Count;
            await Results.Json(new DocumentCountResponse(collection, count), ServerJsonContext.Default.DocumentCountResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/documents/{collection}/distinct", async (HttpContext ctx, string db, string collection) =>
        {
            if (!await TryResolveDocumentCollectionAsync(ctx, registry, grants, db, collection, DatabasePermission.Read, mustExist: true).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.DocumentDistinctRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Path))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "distinct 需要提供 path。").ConfigureAwait(false);
                return;
            }

            registry.TryGet(db, out var tsdb);
            IReadOnlyList<object?> values;
            try
            {
                values = tsdb.Documents.Open(collection).Distinct(req.Path, NormalizeLimit(req.Limit), req.Ids);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
                return;
            }

            await Results.Json(
                new DocumentDistinctResponse(collection, req.Path, values.Select(ToJsonElementValue).ToArray()),
                ServerJsonContext.Default.DocumentDistinctResponse).ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }

    private static async Task<bool> TryResolveDocumentCollectionAsync(
        HttpContext ctx,
        TsdbRegistry registry,
        GrantsStore grants,
        string db,
        string collection,
        DatabasePermission requiredPermission,
        bool mustExist)
    {
        if (!TryResolveDatabase(ctx, registry, db, out var tsdb))
            return false;
        if (!IsValidKeyspaceName(collection))
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                $"非法 document collection 名 '{collection}'。").ConfigureAwait(false);
            return false;
        }

        var databasePermission = DatabaseAccessEvaluator.GetEffectivePermission(ctx, grants, db);
        if (!await TryRequireDatabasePermissionAsync(ctx, db, databasePermission, requiredPermission).ConfigureAwait(false))
            return false;
        if (mustExist && tsdb.Documents.Catalog.TryGet(collection) is null)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "collection_not_found",
                $"document collection '{collection}' 不存在。").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static async Task<bool> ValidateDocumentIdAsync(HttpContext ctx, string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            return true;

        await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "document id 不能为空。").ConfigureAwait(false);
        return false;
    }

    private static async Task<bool> ValidateFindRequestAsync(HttpContext ctx, DocumentFindRequest req)
    {
        if (req.Skip < 0)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "skip 不能为负数。").ConfigureAwait(false);
            return false;
        }
        if (req.Limit is <= 0)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "limit 必须大于 0。").ConfigureAwait(false);
            return false;
        }
        if (req.Limit > MaxDocumentFindLimit)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"limit 不能超过 {MaxDocumentFindLimit}。").ConfigureAwait(false);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(req.ContinuationToken) && req.Skip != 0)
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "continuationToken cannot be combined with skip.").ConfigureAwait(false);
            return false;
        }
        if (!string.IsNullOrWhiteSpace(req.Id) && req.Ids is { Count: > 0 })
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "id 与 ids 不能同时提供。").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static DocumentFindPage FindDocumentPage(string collection, DocumentCollectionStore store, DocumentFindRequest req)
    {
        int limit = NormalizeLimit(req.Limit) ?? DefaultDocumentFindLimit;
        var query = BuildDocumentQuery(req, limit);
        string fingerprint = DocumentCursorToken.Fingerprint(collection, query);
        DocumentCursorState? cursor = DecodeAndValidateCursor(req.ContinuationToken, collection, fingerprint, store.LastVersion);

        if (!HasAdvancedDocumentQuery(req) && !string.IsNullOrWhiteSpace(req.Id))
        {
            var row = store.Get(req.Id);
            var idRows = row is null ? Array.Empty<DocumentRow>() : new[] { row };
            int effectiveSkip = cursor?.Offset ?? req.Skip;
            var idPageRows = idRows.Skip(effectiveSkip).Take(limit + 1).ToArray();
            return BuildPage(
                collection,
                fingerprint,
                store.LastVersion,
                limit,
                idPageRows.Take(limit).ToArray(),
                idPageRows.Length > limit,
                nextOffset: checked(effectiveSkip + Math.Min(idPageRows.Length, limit)),
                nextLastId: null);
        }

        if (!HasAdvancedDocumentQuery(req) && req.Ids is { Count: > 0 })
        {
            int effectiveSkip = cursor?.Offset ?? req.Skip;
            var idsPageRows = store.GetMany(req.Ids).Skip(effectiveSkip).Take(limit + 1).ToArray();
            return BuildPage(
                collection,
                fingerprint,
                store.LastVersion,
                limit,
                idsPageRows.Take(limit).ToArray(),
                idsPageRows.Length > limit,
                nextOffset: checked(effectiveSkip + Math.Min(idsPageRows.Length, limit)),
                nextLastId: null);
        }

        if (HasAdvancedDocumentQuery(req) || !string.IsNullOrWhiteSpace(req.Id) || req.Ids is { Count: > 0 })
        {
            int effectiveSkip = cursor?.Offset ?? req.Skip;
            var pageQuery = query with { Limit = limit + 1, Skip = effectiveSkip };
            var result = DocumentQueryPlanner.Execute(store, store.Schema, pageQuery);
            var pageItems = result.Items.Take(limit).ToArray();
            bool hasMore = result.Items.Count > limit;
            return BuildPage(
                collection,
                fingerprint,
                store.LastVersion,
                limit,
                pageItems.Select(static item => new DocumentRow(item.Id, item.Json, item.Version)).ToArray(),
                hasMore,
                nextOffset: checked(effectiveSkip + pageItems.Length),
                nextLastId: null);
        }

        IReadOnlyList<DocumentRow> scanRows = cursor is null
            ? store.Scan(limit + 1, req.Skip)
            : store.ScanAfter(cursor.LastId, limit + 1);
        var scanPageRows = scanRows.Take(limit).ToArray();
        bool scanHasMore = scanRows.Count > limit;
        return BuildPage(
            collection,
            fingerprint,
            store.LastVersion,
            limit,
            scanPageRows,
            scanHasMore,
            nextOffset: checked((cursor?.Offset ?? req.Skip) + scanPageRows.Length),
            nextLastId: scanPageRows.Length == 0 ? cursor?.LastId : scanPageRows[^1].Id);
    }

    private static DocumentQuery BuildDocumentQuery(DocumentFindRequest req, int limit)
        => new(
            Filter: MergeRequestFilters(req),
            Projection: ToCoreProjection(req.Projection),
            Sort: ToCoreSort(req.Sort),
            Limit: limit,
            Skip: req.Skip);

    private static DocumentCursorState? DecodeAndValidateCursor(
        string? token,
        string collection,
        string fingerprint,
        long currentVersion)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var state = DocumentCursorToken.Decode(token);
        if (!string.Equals(state.Collection, collection, StringComparison.Ordinal)
            || !string.Equals(state.QueryFingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("document cursor token does not match this find request.");
        }

        if (state.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("document cursor token has expired.");
        if (state.SnapshotVersion != currentVersion)
            throw new InvalidOperationException("document cursor snapshot is stale; restart the find request.");

        return state;
    }

    private static DocumentFindPage BuildPage(
        string collection,
        string fingerprint,
        long snapshotVersion,
        int limit,
        IReadOnlyList<DocumentRow> rows,
        bool hasMore,
        int nextOffset,
        string? nextLastId)
    {
        DateTimeOffset? expiresAt = hasMore ? DateTimeOffset.UtcNow.Add(DocumentCursorTtl) : null;
        string? token = hasMore
            ? DocumentCursorToken.Encode(new DocumentCursorState(
                collection,
                fingerprint,
                snapshotVersion,
                expiresAt!.Value,
                nextOffset,
                nextLastId))
            : null;

        return new DocumentFindPage(
            rows.Select(ToDocumentItemResponse).ToArray(),
            limit,
            hasMore,
            token,
            snapshotVersion,
            expiresAt);
    }

    private static bool HasAdvancedDocumentQuery(DocumentFindRequest req)
        => req.Filter is not null
            || req.Projection is { Count: > 0 }
            || req.Sort is { Count: > 0 };

    private static DocumentFilter? MergeRequestFilters(DocumentFindRequest req)
    {
        var filters = new List<DocumentFilter>();
        if (!string.IsNullOrWhiteSpace(req.Id))
            filters.Add(new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, req.Id));
        if (req.Ids is { Count: > 0 })
            filters.Add(new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.In, req.Ids));
        if (ToCoreFilter(req.Filter) is { } filter)
            filters.Add(filter);

        return filters.Count switch
        {
            0 => null,
            1 => filters[0],
            _ => new DocumentAndFilter(filters),
        };
    }

    private static DocumentFilter? MergeUpdateRequestFilters(string? id, DocumentFilterContract? filter)
    {
        var filters = new List<DocumentFilter>();
        if (!string.IsNullOrWhiteSpace(id))
            filters.Add(new DocumentFieldFilter(DocumentFieldRef.Id, DocumentFilterOperator.Equal, id));
        if (ToCoreFilter(filter) is { } coreFilter)
            filters.Add(coreFilter);

        return filters.Count switch
        {
            0 => null,
            1 => filters[0],
            _ => new DocumentAndFilter(filters),
        };
    }

    private static DocumentFilter? ToCoreFilter(DocumentFilterContract? filter)
    {
        if (filter is null)
            return null;

        if (filter.And is { Count: > 0 })
            return new DocumentAndFilter(filter.And.Select(ToRequiredCoreFilter).ToArray());
        if (filter.Or is { Count: > 0 })
            return new DocumentOrFilter(filter.Or.Select(ToRequiredCoreFilter).ToArray());
        if (filter.Not is not null)
            return new DocumentNotFilter(ToRequiredCoreFilter(filter.Not));

        var op = ParseFilterOperator(filter.Op);
        return new DocumentFieldFilter(
            ToCoreField(filter.Path),
            op,
            op == DocumentFilterOperator.Exists
                ? ToBooleanOrDefault(filter.Value)
                : ToCoreValue(filter.Value));
    }

    private static DocumentFilter ToRequiredCoreFilter(DocumentFilterContract filter)
        => ToCoreFilter(filter) ?? throw new InvalidOperationException("document filter 不能为空。");

    private static DocumentProjection? ToCoreProjection(IReadOnlyList<DocumentProjectionContract>? projection)
    {
        if (projection is not { Count: > 0 })
            return null;

        return new DocumentProjection(projection
            .Select(static item =>
            {
                var field = ToCoreField(item.Path);
                return new DocumentProjectionField(item.Name ?? DefaultProjectionName(field), field);
            })
            .ToArray());
    }

    private static IReadOnlyList<DocumentSort> ToCoreSort(IReadOnlyList<DocumentSortContract>? sort)
        => sort is { Count: > 0 }
            ? sort.Select(static item => new DocumentSort(ToCoreField(item.Path), item.Descending)).ToArray()
            : Array.Empty<DocumentSort>();

    private static DocumentFieldRef ToCoreField(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.Equals(path, "_id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "id", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFieldRef.Id;
        }

        if (string.Equals(path, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "json", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentFieldRef.Document;
        }

        return DocumentFieldRef.JsonPath(path);
    }

    private static string DefaultProjectionName(DocumentFieldRef field)
    {
        if (field.Kind == DocumentFieldKind.Id)
            return "_id";
        if (field.Kind == DocumentFieldKind.Document)
            return "document";

        string path = field.Path!;
        int dot = path.LastIndexOf('.');
        int bracket = path.LastIndexOf('[');
        int start = Math.Max(dot, bracket) + 1;
        return path[start..].TrimEnd(']').Trim('\'');
    }

    private static DocumentFilterOperator ParseFilterOperator(string? op)
        => (op ?? "eq").ToLowerInvariant() switch
        {
            "eq" => DocumentFilterOperator.Equal,
            "ne" => DocumentFilterOperator.NotEqual,
            "gt" => DocumentFilterOperator.GreaterThan,
            "gte" => DocumentFilterOperator.GreaterThanOrEqual,
            "lt" => DocumentFilterOperator.LessThan,
            "lte" => DocumentFilterOperator.LessThanOrEqual,
            "in" => DocumentFilterOperator.In,
            "nin" => DocumentFilterOperator.NotIn,
            "exists" => DocumentFilterOperator.Exists,
            "contains" => DocumentFilterOperator.Contains,
            _ => throw new InvalidOperationException($"不支持的 document filter op '{op}'。"),
        };

    private static object? ToCoreValue(JsonElement? value)
    {
        if (value is null)
            return null;

        return ToCoreValue(value.Value);
    }

    private static object? ToCoreValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.TryGetInt64(out long longValue) ? longValue : value.GetDouble(),
            JsonValueKind.Array => value.EnumerateArray().Select(ToCoreValue).ToArray(),
            JsonValueKind.Object => value.GetRawText(),
            _ => null,
        };

    private static bool ToBooleanOrDefault(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind == JsonValueKind.Null)
            return true;
        if (value.Value.ValueKind == JsonValueKind.True || value.Value.ValueKind == JsonValueKind.False)
            return value.Value.GetBoolean();
        return false;
    }

    private static DocumentUpdate ToCoreUpdate(DocumentUpdateContract update)
        => new(
            update.Set,
            update.Unset,
            update.Inc,
            update.Min,
            update.Max,
            update.Rename,
            update.Push,
            update.Pull,
            update.AddToSet,
            update.CurrentDate);

    private static int? NormalizeLimit(int? limit)
        => limit is null ? null : Math.Min(limit.Value, MaxDocumentFindLimit);

    private static DocumentItemResponse ToDocumentItemResponse(DocumentRow row)
    {
        using var document = JsonDocument.Parse(row.Json);
        return new DocumentItemResponse(row.Id, document.RootElement.Clone(), row.Version);
    }

    private static JsonElementValue ToJsonElementValue(object? value)
        => value switch
        {
            null => new JsonElementValue(ScalarKind.Null),
            bool b => new JsonElementValue(ScalarKind.Boolean, BooleanValue: b),
            byte or sbyte or short or ushort or int or uint or long or ulong => new JsonElementValue(
                ScalarKind.Integer,
                IntegerValue: Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            float or double or decimal => new JsonElementValue(
                ScalarKind.Double,
                DoubleValue: Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => new JsonElementValue(ScalarKind.String, StringValue: value.ToString()),
        };

    private sealed record DocumentFindPage(
        IReadOnlyList<DocumentItemResponse> Documents,
        int Limit,
        bool HasMore,
        string? ContinuationToken,
        long SnapshotVersion,
        DateTimeOffset? CursorExpiresAtUtc);
}
