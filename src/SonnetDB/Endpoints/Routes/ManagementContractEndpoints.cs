using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Catalog;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Cjk;
using SonnetDB.FullText.Tokenizers.Jieba;
using SonnetDB.FullText.Tokenizers.Unicode;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Query;
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
                        FormatKnnMetric(column.VectorIndex.Metric),
                        BuildVectorIndexParams(column.VectorIndex),
                        CountVectorRows(tsdb, measurement.Name, column.Name)));
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
            var schema = tsdb.Measurements.TryGet(req.Measurement);
            if (schema is null)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status404NotFound, "measurement_not_found",
                    $"Measurement '{req.Measurement}' 不存在。").ConfigureAwait(false);
                return;
            }

            var vectorColumn = schema.TryGetColumn(req.Column);
            if (vectorColumn is null || vectorColumn.Role != MeasurementColumnRole.Field || vectorColumn.DataType != FieldType.Vector)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request",
                    $"列 '{req.Column}' 必须是 VECTOR FIELD。").ConfigureAwait(false);
                return;
            }

            if (!TryNormalizeKnnMetric(req.Metric, out var metric, out var metricError))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", metricError).ConfigureAwait(false);
                return;
            }

            if (!TryNormalizeVectorFilter(req.Filter, schema, out var filter, out var filterError))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", filterError).ConfigureAwait(false);
                return;
            }

            string sql = BuildKnnSql(req.Measurement, req.Column, req.Query, topK, metric, filter);

            try
            {
                var result = SqlExecutor.Execute(tsdb, db, sql) as SelectExecutionResult;
                var hits = MapVectorHits(result, schema);
                await Results.Json(new VectorSearchPreviewResponse(hits), ServerJsonContext.Default.VectorSearchPreviewResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "vector_search_error", ex.Message).ConfigureAwait(false);
            }
        });

        app.MapPost("/v1/db/{db}/vector/embed-preview", async (HttpContext ctx, string db) =>
        {
            if (!await TryResolveObjectStorageAsync(ctx, registry, grants, db, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var req = await ReadJsonAsync(ctx, ServerJsonContext.Default.VectorEmbedPreviewRequest).ConfigureAwait(false);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", "请求体需包含非空 text。").ConfigureAwait(false);
                return;
            }

            var readiness = app.Services.GetRequiredService<CopilotReadiness>().Evaluate();
            if (!readiness.Enabled)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status409Conflict, "copilot_disabled", "Copilot 子系统已禁用。").ConfigureAwait(false);
                return;
            }
            if (!readiness.EmbeddingReady)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_not_ready",
                    $"Embedding provider 未就绪：{readiness.Reason ?? "unknown"}。").ConfigureAwait(false);
                return;
            }

            try
            {
                var embedding = await app.Services.GetRequiredService<IEmbeddingProvider>()
                    .EmbedAsync(req.Text, ctx.RequestAborted).ConfigureAwait(false);
                await Results.Json(new VectorEmbedPreviewResponse(embedding, embedding.Length), ServerJsonContext.Default.VectorEmbedPreviewResponse)
                    .ExecuteAsync(ctx).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or HttpRequestException)
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status503ServiceUnavailable, "embedding_failed", ex.Message).ConfigureAwait(false);
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
                options.Add(new KeyValueInfo("efConstruction", index.Hnsw.EfConstruction.ToString(CultureInfo.InvariantCulture)));
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

    private static long CountVectorRows(Tsdb tsdb, string measurement, string column)
    {
        long count = 0;
        foreach (var series in tsdb.Catalog.Find(measurement, null))
            count += tsdb.Query.Execute(new PointQuery(series.Id, column, TimeRange.All)).LongCount();
        return count;
    }

    private static string BuildKnnSql(string measurement, string column, float[] query, int topK, string metric, string? filter)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT * FROM knn(").Append(measurement).Append(", ").Append(column).Append(", [");
        for (int i = 0; i < query.Length; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(query[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append("], ").Append(topK.ToString(CultureInfo.InvariantCulture))
            .Append(", '").Append(metric).Append("')");
        if (!string.IsNullOrWhiteSpace(filter))
            sb.Append(" WHERE ").Append(filter);
        return sb.ToString();
    }

    private static List<VectorSearchPreviewHit> MapVectorHits(SelectExecutionResult? result, MeasurementSchema schema)
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
            var tags = new List<KeyValueInfo>();
            var fields = new List<KeyValueInfo>();
            for (int i = 0; i < result.Columns.Count && i < row.Count; i++)
            {
                if (i == timeIdx || i == distIdx)
                    continue;

                var column = schema.TryGetColumn(result.Columns[i]);
                if (column is null)
                    continue;

                var value = FormatVectorHitValue(row[i]);
                if (column.Role == MeasurementColumnRole.Tag)
                    tags.Add(new KeyValueInfo(column.Name, value));
                else if (column.Role == MeasurementColumnRole.Field)
                    fields.Add(new KeyValueInfo(column.Name, value));
            }
            hits.Add(new VectorSearchPreviewHit(ts, dist, tags, fields));
        }
        return hits;
    }

    private static string FormatVectorHitValue(object? value)
    {
        if (value is null)
            return "null";
        if (value is float[] vector)
            return "[" + string.Join(", ", vector.Select(static f => f.ToString("R", CultureInfo.InvariantCulture))) + "]";
        if (value is double d)
            return d.ToString("R", CultureInfo.InvariantCulture);
        if (value is float f)
            return f.ToString("R", CultureInfo.InvariantCulture);
        if (value is IFormattable formattable)
            return formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty;
        return value.ToString() ?? string.Empty;
    }

    private static string FormatKnnMetric(KnnMetric metric) => metric switch
    {
        KnnMetric.L2 => "l2",
        KnnMetric.InnerProduct => "inner_product",
        _ => "cosine",
    };

    private static bool TryNormalizeKnnMetric(string? raw, out string metric, out string error)
    {
        metric = "cosine";
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "cosine":
            case "cosine_distance":
                metric = "cosine";
                return true;
            case "l2":
            case "l2_distance":
            case "euclidean":
                metric = "l2";
                return true;
            case "inner_product":
            case "dot":
            case "ip":
                metric = "inner_product";
                return true;
            default:
                error = "metric 仅支持 cosine / l2 / inner_product。";
                return false;
        }
    }

    private static bool TryNormalizeVectorFilter(
        string? raw,
        MeasurementSchema schema,
        out string? normalized,
        out string error)
    {
        normalized = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        var text = raw.Trim();
        if (text.Length > 512)
        {
            error = "filter 不能超过 512 个字符。";
            return false;
        }
        if (text.Contains(';', StringComparison.Ordinal)
            || text.Contains("--", StringComparison.Ordinal)
            || text.Contains("/*", StringComparison.Ordinal)
            || text.Contains("*/", StringComparison.Ordinal))
        {
            error = "filter 仅支持 TAG 等值与 time 范围条件，不允许 SQL 分隔符或注释。";
            return false;
        }

        var clauses = SplitAndClauses(text);
        if (clauses.Count == 0)
            return true;

        var normalizedClauses = new List<string>(clauses.Count);
        foreach (var clause in clauses)
        {
            if (!TryNormalizeVectorFilterClause(clause, schema, out var normalizedClause, out error))
                return false;
            normalizedClauses.Add(normalizedClause);
        }

        normalized = string.Join(" AND ", normalizedClauses);
        return true;
    }

    private static List<string> SplitAndClauses(string text)
    {
        var clauses = new List<string>();
        int start = 0;
        bool inString = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\'')
            {
                if (inString && i + 1 < text.Length && text[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }

            if (!inString && IsAndSeparatorAt(text, i))
            {
                var clause = text[start..i].Trim();
                if (clause.Length > 0)
                    clauses.Add(clause);
                i += 2;
                start = i + 1;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
            clauses.Add(tail);
        return clauses;
    }

    private static bool IsAndSeparatorAt(string text, int index)
    {
        if (index + 3 > text.Length)
            return false;
        if (!text.AsSpan(index, 3).Equals("AND".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        bool left = index == 0 || char.IsWhiteSpace(text[index - 1]);
        bool right = index + 3 == text.Length || char.IsWhiteSpace(text[index + 3]);
        return left && right;
    }

    private static bool TryNormalizeVectorFilterClause(
        string clause,
        MeasurementSchema schema,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var op = FindFilterOperator(clause, out var opIndex);
        if (op is null)
        {
            error = $"不支持的 filter 条件：{clause}";
            return false;
        }

        var left = clause[..opIndex].Trim();
        var right = clause[(opIndex + op.Length)..].Trim();
        if (!IsValidSqlIdentifier(left) || right.Length == 0)
        {
            error = $"filter 条件格式非法：{clause}";
            return false;
        }

        if (string.Equals(left, "time", StringComparison.OrdinalIgnoreCase))
        {
            if (op == "!=")
            {
                error = "filter 暂不支持 time !=。";
                return false;
            }
            if (!long.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            {
                error = "time filter 右值必须是 Unix 毫秒整数。";
                return false;
            }
            normalized = $"time {op} {timestamp.ToString(CultureInfo.InvariantCulture)}";
            return true;
        }

        var column = schema.TryGetColumn(left);
        if (column is null || column.Role != MeasurementColumnRole.Tag)
        {
            error = $"filter 只支持 TAG 列等值和 time 范围，'{left}' 不是 TAG 列。";
            return false;
        }
        if (op != "=")
        {
            error = "TAG filter 仅支持等值比较。";
            return false;
        }
        if (!TryParseSqlStringLiteral(right, out var tagValue))
        {
            error = "TAG filter 右值必须是单引号字符串字面量。";
            return false;
        }

        normalized = $"{left} = {EscapeSqlString(tagValue)}";
        return true;
    }

    private static string? FindFilterOperator(string clause, out int index)
    {
        string[] operators = [">=", "<=", "!=", "=", ">", "<"];
        bool inString = false;
        for (int i = 0; i < clause.Length; i++)
        {
            if (clause[i] == '\'')
            {
                if (inString && i + 1 < clause.Length && clause[i + 1] == '\'')
                {
                    i++;
                    continue;
                }
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            foreach (var op in operators)
            {
                if (i + op.Length <= clause.Length
                    && clause.AsSpan(i, op.Length).Equals(op.AsSpan(), StringComparison.Ordinal))
                {
                    index = i;
                    return op;
                }
            }
        }

        index = -1;
        return null;
    }

    private static bool TryParseSqlStringLiteral(string text, out string value)
    {
        value = string.Empty;
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '\'' || trimmed[^1] != '\'')
            return false;

        var inner = trimmed[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\'')
            {
                if (i + 1 < inner.Length && inner[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                    continue;
                }
                return false;
            }
            sb.Append(inner[i]);
        }

        value = sb.ToString();
        return true;
    }

    private static string EscapeSqlString(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

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
                        store.GetFullTextDocumentCount(index),
                        store.GetFullTextTermCount(index)));
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
            if (!TryNormalizeFullTextQueryKind(req.QueryKind, mode, out var queryKind, out var queryKindError))
            {
                await WriteSimpleErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad_request", queryKindError).ConfigureAwait(false);
                return;
            }

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
                var hits = store.SearchFullText(indexDef, req.Field, req.Query, topK, mode, queryKind)
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

    private static bool TryNormalizeFullTextQueryKind(
        string? raw,
        FullTextSearchMode mode,
        out FullTextQueryKind queryKind,
        out string error)
    {
        queryKind = FullTextQueryKind.All;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return true;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "all":
            case "and":
            case "must":
                queryKind = FullTextQueryKind.All;
                return true;
            case "any":
            case "or":
            case "should":
                queryKind = FullTextQueryKind.Any;
                return true;
            case "phrase":
                if (mode == FullTextSearchMode.Fuzzy)
                {
                    error = "phrase 查询当前仅支持 exact mode；模糊短语不在管理契约内。";
                    return false;
                }
                queryKind = FullTextQueryKind.Phrase;
                return true;
            default:
                error = "queryKind 仅支持 all / any / phrase。";
                return false;
        }
    }

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

        app.MapPost("/v1/db/{db}/mq/{topic}/retention", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var mq = app.Services.GetRequiredService<SonnetMqStore>();
            var stats = mq.GetStats(QualifyMqTopic(db, topic));
            var options = mq.Options;
            long retainedStart = Math.Max(0, stats.NextOffset - stats.MessageCount);
            long retainedEnd = stats.MessageCount > 0 ? stats.NextOffset - 1 : retainedStart - 1;
            var response = new MqRetentionResponse(
                topic,
                retainedStart,
                retainedEnd,
                stats.MessageCount,
                retainedStart,
                options.RetentionMaxAge?.TotalSeconds,
                options.RetentionMaxBytes,
                options.RetentionInterval.TotalSeconds,
                options.TrimAcknowledgedMessages,
                options.AckRetentionMinOffsetDelta,
                options.SegmentMaxBytes,
                options.HotTailMaxBytes,
                options.SegmentCacheSize);
            await Results.Json(response, ServerJsonContext.Default.MqRetentionResponse)
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

        app.MapPost("/v1/db/{db}/mq/{topic}/monitor", async (HttpContext ctx, string db, string topic) =>
        {
            if (!await TryResolveMqAsync(ctx, registry, grants, db, topic, DatabasePermission.Read).ConfigureAwait(false))
                return;

            var mq = app.Services.GetRequiredService<SonnetMqStore>();
            var options = mq.Options;
            string qualifiedTopic = QualifyMqTopic(db, topic);
            var stats = mq.GetStats(qualifiedTopic);
            long retainedStart = Math.Max(0, stats.NextOffset - stats.MessageCount);
            var consumers = stats.ConsumerOffsets
                .OrderBy(static c => c.Key, StringComparer.Ordinal)
                .Select(c =>
                {
                    long lag = Math.Max(0, stats.NextOffset - c.Value);
                    double progress = stats.NextOffset <= 0
                        ? 1d
                        : Math.Clamp(c.Value / (double)stats.NextOffset, 0d, 1d);
                    string status = c.Value < retainedStart
                        ? "beyond_retention"
                        : lag == 0
                            ? "caught_up"
                            : "lagging";
                    return new MqConsumerMonitorInfo(c.Key, c.Value, lag, progress, status);
                })
                .ToArray();

            var retention = new MqRetentionPolicyInfo(
                options.RetentionMaxAge is null ? null : (long)Math.Ceiling(options.RetentionMaxAge.Value.TotalMilliseconds),
                options.RetentionMaxBytes,
                (long)Math.Ceiling(options.RetentionInterval.TotalMilliseconds),
                options.TrimAcknowledgedMessages,
                options.AckRetentionMinOffsetDelta,
                options.SegmentMaxBytes,
                options.HotTailMaxBytes,
                options.SegmentCacheSize);

            var allTopics = mq.ListTopicStats()
                .Where(s => s.Topic.StartsWith(db + ".", StringComparison.Ordinal))
                .Select(s => s.Topic[(db.Length + 1)..])
                .ToArray();
            var dlq = BuildMqDeadLetterInfo(topic, allTopics);
            var response = new MqMonitorResponse(topic, stats.MessageCount, stats.NextOffset, retainedStart, consumers, retention, dlq);
            await Results.Json(response, ServerJsonContext.Default.MqMonitorResponse)
                .ExecuteAsync(ctx).ConfigureAwait(false);
        });
    }

    private static MqDeadLetterInfo BuildMqDeadLetterInfo(string topic, IReadOnlyList<string> allTopics)
    {
        var candidates = allTopics
            .Where(candidate => !string.Equals(candidate, topic, StringComparison.Ordinal)
                && IsMqDeadLetterCandidate(topic, candidate))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new MqDeadLetterInfo(
            candidates.Length > 0 ? "topic_convention" : "not_configured",
            candidates,
            candidates.Length > 0 ? candidates[0] : null);
    }

    private static bool IsMqDeadLetterCandidate(string topic, string candidate)
    {
        if (candidate.Equals(topic + ".dlq", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(topic + "-dlq", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(topic + "_dlq", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals(topic + ".dead-letter", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("dlq." + topic, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("dead-letter." + topic, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return candidate.StartsWith(topic + ".", StringComparison.OrdinalIgnoreCase)
            && (candidate.Contains(".dlq", StringComparison.OrdinalIgnoreCase)
                || candidate.Contains(".dead", StringComparison.OrdinalIgnoreCase));
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
