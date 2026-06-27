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
            var rows = FindRows(tsdb.Documents.Open(collection), req);
            var docs = rows.Select(ToDocumentItemResponse).ToArray();
            await Results.Json(
                new DocumentFindResponse(collection, docs, docs.Length, req.Limit, req.Skip),
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

            if (!await ValidateDocumentIdAsync(ctx, req.Id).ConfigureAwait(false))
                return;

            registry.TryGet(db, out var tsdb);
            var store = tsdb.Documents.Open(collection);
            if (store.Get(req.Id) is null)
            {
                await Results.Json(new DocumentWriteResponse(collection), ServerJsonContext.Default.DocumentWriteResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
                return;
            }

            store.Upsert(req.Id, req.Document.GetRawText());
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
        if (!string.IsNullOrWhiteSpace(req.Id) && req.Ids is { Count: > 0 })
        {
            await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "id 与 ids 不能同时提供。").ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static IReadOnlyList<DocumentRow> FindRows(DocumentCollectionStore store, DocumentFindRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Id))
        {
            var row = store.Get(req.Id);
            return row is null ? [] : [row];
        }

        if (req.Ids is { Count: > 0 })
            return store.GetMany(req.Ids);

        return store.Scan(NormalizeLimit(req.Limit) ?? DefaultDocumentFindLimit, req.Skip);
    }

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
}
