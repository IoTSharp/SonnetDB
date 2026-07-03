using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Catalog;
using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Cjk;
using SonnetDB.FullText.Tokenizers.Jieba;
using SonnetDB.FullText.Tokenizers.Unicode;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using SonnetMQ;

namespace SonnetDB.Endpoints;

/// <summary>
/// M29 A #245 多模型只读管理契约：为 KV / 向量 / 全文 / MQ 补最小只读 metadata + browse 端点。
/// 全部 <see cref="DatabasePermission.Read"/>，不新增任何查询 / 写入 / 索引 / 存储语义；
/// 写操作复用既有 data-plane API。对象模型的 list / metadata 已由既有 S3 端点覆盖，不在此重复。
/// </summary>
internal static partial class SonnetDbEndpoints
{
    private const int ManagementScanDefaultLimit = 100;
    private const int ManagementScanMaxLimit = 1000;
    private const int ManagementSearchDefaultTopK = 10;
    private const int ManagementSearchMaxTopK = 100;

    private static void MapManagementContractEndpoints(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var grants = app.Services.GetRequiredService<GrantsStore>();

        MapKvManagementEndpoints(app, registry, grants);
        MapVectorManagementEndpoints(app, registry, grants);
        MapFullTextManagementEndpoints(app, registry, grants);
        MapMqManagementEndpoints(app, registry, grants);
    }

    // ---- KV ----

    private static void MapKvManagementEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        app.MapPost("/v1/db/{db}/kv/keyspaces", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);
            var keyspaces = tsdb.Keyspaces.List();
            await Results.Json(new KvKeyspaceListResponse(keyspaces), ServerJsonContext.Default.KvKeyspaceListResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/kv/{keyspace}/scan", async (HttpContext ctx, string db, string keyspace) =>
        {
            if (!await TryResolveKvAsync(ctx, registry, grants, db, keyspace, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.KvScanCursorRequest).ConfigureAwait(false)
                ?? new KvScanCursorRequest();

            int limit = req.Limit is null or <= 0
                ? ManagementScanDefaultLimit
                : Math.Min(req.Limit.Value, ManagementScanMaxLimit);
            string prefix = req.Prefix ?? string.Empty;
            string? afterKey = DecodeKvCursor(req.Cursor);

            registry.TryGet(db, out var tsdb);
            // 多取 1 行以判定是否还有下一页，返回时截断。
            var rows = tsdb.Keyspaces.Open(keyspace).ScanPrefixAfter(prefix, afterKey, limit + 1);
            bool hasMore = rows.Count > limit;
            int take = hasMore ? limit : rows.Count;

            var entries = new List<KvEntryResponse>(take);
            string? lastKey = null;
            for (int i = 0; i < take; i++)
            {
                var entry = rows[i];
                string key = Encoding.UTF8.GetString(entry.Key.Span);
                entries.Add(new KvEntryResponse(key, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc));
                lastKey = key;
            }

            string? nextCursor = hasMore && lastKey is not null ? EncodeKvCursor(lastKey) : null;
            await Results.Json(new KvScanCursorResponse(entries, nextCursor, hasMore), ServerJsonContext.Default.KvScanCursorResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }

    private static string EncodeKvCursor(string key)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(key));

    private static string? DecodeKvCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return null;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ---- 向量 ----

    private static void MapVectorManagementEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        app.MapPost("/v1/db/{db}/vector/indexes", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);

            var indexes = new List<VectorIndexStat>();
            foreach (var measurement in tsdb.Measurements.Snapshot())
            {
                foreach (var column in measurement.Columns)
                {
                    if (column.DataType != FieldType.Vector || column.VectorIndex is null)
                        continue;
                    indexes.Add(new VectorIndexStat(
                        measurement.Name,
                        column.Name,
                        column.VectorIndex.Kind.ToString(),
                        column.VectorDimension,
                        // 引擎构建时固定 cosine（VectorIndexAdapter），如实回显。
                        "cosine",
                        BuildVectorIndexParams(column.VectorIndex)));
                }
            }

            await Results.Json(new VectorIndexStatResponse(indexes), ServerJsonContext.Default.VectorIndexStatResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/vector/search-preview", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.VectorSearchPreviewRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Measurement) || string.IsNullOrWhiteSpace(req.Column) || req.Query is null or { Length: 0 })
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 measurement、column 与非空 query 向量。").ConfigureAwait(false);
                return;
            }
            if (!IsValidSqlIdentifier(req.Measurement) || !IsValidSqlIdentifier(req.Column))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "measurement / column 名非法。").ConfigureAwait(false);
                return;
            }

            int topK = req.TopK is null or <= 0
                ? ManagementSearchDefaultTopK
                : Math.Min(req.TopK.Value, ManagementSearchMaxTopK);

            registry.TryGet(db, out var tsdb);
            string sql = BuildKnnSql(req.Measurement, req.Column, req.Query, topK);

            try
            {
                var result = SqlExecutor.Execute(tsdb, db, sql) as SelectExecutionResult;
                var hits = MapVectorHits(result);
                await Results.Json(new VectorSearchPreviewResponse(hits), ServerJsonContext.Default.VectorSearchPreviewResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "vector_search_error", ex.Message).ConfigureAwait(false);
            }
        });
    }

    private static List<KeyValueInfo> BuildVectorIndexParams(VectorIndexDefinition index)
    {
        var options = new List<KeyValueInfo>();
        switch (index.Kind)
        {
            case VectorIndexKind.Hnsw when index.Hnsw is not null:
                options.Add(new KeyValueInfo("m", index.Hnsw.M.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("ef", index.Hnsw.Ef.ToString(CultureInfo.InvariantCulture)));
                break;
            case VectorIndexKind.IvfFlat when index.Ivf is not null:
                options.Add(new KeyValueInfo("nlist", index.Ivf.NList.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("nprobe", index.Ivf.NProbe.ToString(CultureInfo.InvariantCulture)));
                break;
            case VectorIndexKind.IvfPq when index.IvfPq is not null:
                options.Add(new KeyValueInfo("nlist", index.IvfPq.NList.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("nprobe", index.IvfPq.NProbe.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("m", index.IvfPq.M.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("nbits", index.IvfPq.NBits.ToString(CultureInfo.InvariantCulture)));
                break;
            case VectorIndexKind.Vamana when index.Vamana is not null:
                options.Add(new KeyValueInfo("max_degree", index.Vamana.MaxDegree.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("search_list_size", index.Vamana.SearchListSize.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("alpha", index.Vamana.Alpha.ToString(CultureInfo.InvariantCulture)));
                options.Add(new KeyValueInfo("beam_width", index.Vamana.BeamWidth.ToString(CultureInfo.InvariantCulture)));
                break;
        }
        return options;
    }

    private static string BuildKnnSql(string measurement, string column, float[] query, int topK)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT * FROM knn(").Append(measurement).Append(", ").Append(column).Append(", [");
        for (int i = 0; i < query.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(query[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append("], ").Append(topK.ToString(CultureInfo.InvariantCulture)).Append(')');
        return sb.ToString();
    }

    private static List<VectorSearchPreviewHit> MapVectorHits(SelectExecutionResult? result)
    {
        if (result is null)
            return new List<VectorSearchPreviewHit>();

        int timeIdx = -1, distIdx = -1;
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], "time", StringComparison.OrdinalIgnoreCase))
                timeIdx = i;
            else if (string.Equals(result.Columns[i], "distance", StringComparison.OrdinalIgnoreCase))
                distIdx = i;
        }

        var hits = new List<VectorSearchPreviewHit>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            long ts = timeIdx >= 0 && row[timeIdx] is not null ? Convert.ToInt64(row[timeIdx], CultureInfo.InvariantCulture) : 0;
            double dist = distIdx >= 0 && row[distIdx] is not null ? Convert.ToDouble(row[distIdx], CultureInfo.InvariantCulture) : 0;
            hits.Add(new VectorSearchPreviewHit(ts, dist));
        }
        return hits;
    }

    // ---- 全文 ----

    private static void MapFullTextManagementEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        app.MapPost("/v1/db/{db}/fulltext/indexes", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            registry.TryGet(db, out var tsdb);

            var indexes = new List<FullTextIndexStat>();
            foreach (var collection in tsdb.Documents.Catalog.Snapshot())
            {
                if (collection.FullTextIndexes.Count == 0)
                    continue;
                var store = tsdb.Documents.Open(collection.Name);
                foreach (var index in collection.FullTextIndexes)
                {
                    indexes.Add(new FullTextIndexStat(
                        collection.Name,
                        index.Name,
                        index.Fields.ToArray(),
                        index.Tokenizer,
                        store.GetFullTextDocumentCount(index)));
                }
            }

            await Results.Json(new FullTextIndexStatResponse(indexes), ServerJsonContext.Default.FullTextIndexStatResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/fulltext/search-preview", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.FullTextSearchPreviewRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Collection) || string.IsNullOrWhiteSpace(req.Index)
                || string.IsNullOrWhiteSpace(req.Field) || string.IsNullOrWhiteSpace(req.Query))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 collection、index、field 与 query。").ConfigureAwait(false);
                return;
            }

            int topK = req.TopK is null or <= 0
                ? ManagementSearchDefaultTopK
                : Math.Min(req.TopK.Value, ManagementSearchMaxTopK);
            var mode = string.Equals(req.Mode, "fuzzy", StringComparison.OrdinalIgnoreCase)
                ? FullTextSearchMode.Fuzzy
                : FullTextSearchMode.Exact;

            registry.TryGet(db, out var tsdb);
            var schema = tsdb.Documents.Catalog.TryGet(req.Collection);
            var indexDef = schema?.TryGetFullTextIndex(req.Index);
            if (schema is null || indexDef is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "fulltext_index_not_found", $"全文索引 '{req.Index}' 不存在于集合 '{req.Collection}'。").ConfigureAwait(false);
                return;
            }

            try
            {
                var store = tsdb.Documents.Open(req.Collection);
                var hits = store.SearchFullText(indexDef, req.Field, req.Query, topK, mode)
                    .Select(static h => new FullTextSearchPreviewHit(h.DocumentId, h.Score))
                    .ToArray();
                await Results.Json(new FullTextSearchPreviewResponse(hits), ServerJsonContext.Default.FullTextSearchPreviewResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "fulltext_search_error", ex.Message).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "fulltext_search_error", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/fulltext/analyze", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;
            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.FullTextAnalyzeRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Tokenizer) || req.Text is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含 tokenizer 与 text。").ConfigureAwait(false);
                return;
            }

            var tokenizer = CreateTokenizer(req.Tokenizer);
            if (tokenizer is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", $"未知分词器 '{req.Tokenizer}'，支持 unicode / cjk / jieba。").ConfigureAwait(false);
                return;
            }

            var sink = new CollectingTokenSink();
            tokenizer.Tokenize(req.Text, sink);
            var tokens = sink.Tokens
                .Select(static t => new FullTextTokenInfo(t.Text, t.StartOffset, t.EndOffset, t.PositionIncrement))
                .ToArray();
            await Results.Json(new FullTextAnalyzeResponse(tokens), ServerJsonContext.Default.FullTextAnalyzeResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }

    private static ITokenizer? CreateTokenizer(string name) => name.ToLowerInvariant() switch
    {
        "unicode" => new UnicodeTokenizer(),
        "cjk" => new CjkBigramTokenizer(),
        "jieba" => new ChineseTokenizer(),
        _ => null,
    };

    // ---- MQ ----

    private static void MapMqManagementEndpoints(WebApplication app, TsdbRegistry registry, GrantsStore grants)
    {
        app.MapPost("/v1/db/{db}/mq/topics", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var mq = app.Services.GetRequiredService<SonnetMqStore>();
            string qualifiedPrefix = db + ".";
            var topics = mq.ListTopicStats()
                .Where(s => s.Topic.StartsWith(qualifiedPrefix, StringComparison.Ordinal))
                .Select(s => new MqTopicInfo(s.Topic[qualifiedPrefix.Length..], s.MessageCount, s.NextOffset))
                .ToArray();
            await Results.Json(new MqTopicListResponse(topics), ServerJsonContext.Default.MqTopicListResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/mq/{topic}/offsets", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var mq = app.Services.GetRequiredService<SonnetMqStore>();
            var stats = mq.GetStats(QualifyMqTopic(db, topic));
            var consumers = stats.ConsumerOffsets
                .OrderBy(static c => c.Key, StringComparer.Ordinal)
                .Select(c => new MqConsumerLag(c.Key, c.Value, Math.Max(0, stats.NextOffset - c.Value)))
                .ToArray();
            await Results.Json(new MqOffsetsResponse(topic, stats.NextOffset, consumers), ServerJsonContext.Default.MqOffsetsResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });

        app.MapPost("/v1/db/{db}/mq/{topic}/browse", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.MqBrowseRequest).ConfigureAwait(false)
                ?? new MqBrowseRequest();
            long fromOffset = req.FromOffset is null or < 0 ? 0 : req.FromOffset.Value;
            int maxCount = req.MaxCount is null or <= 0
                ? ManagementScanDefaultLimit
                : Math.Min(req.MaxCount.Value, ManagementScanMaxLimit);

            try
            {
                var mq = app.Services.GetRequiredService<SonnetMqStore>();
                // Pull(topic, offset, maxCount) 按 offset 只读浏览，不改变任何消费者组状态。
                var messages = mq.Pull(QualifyMqTopic(db, topic), fromOffset, maxCount)
                    .Select(m => new MqMessageResponse(topic, m.Offset, m.TimestampUtc, m.Headers, m.Payload))
                    .ToArray();
                await Results.Json(new MqBrowseResponse(messages), ServerJsonContext.Default.MqBrowseResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", ex.Message).ConfigureAwait(false);
            }
        });
    }

    private static bool IsValidSqlIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128)
            return false;
        for (int i = 0; i < name.Length; i++)
        {
            char ch = name[i];
            bool valid =
                ch is >= 'a' and <= 'z' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= '0' and <= '9' ||
                ch is '_';
            if (!valid)
                return false;
        }
        return !(name[0] is >= '0' and <= '9');
    }
}
