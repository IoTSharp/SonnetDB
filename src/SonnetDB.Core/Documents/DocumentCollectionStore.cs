using System.Text;
using System.Text.Json;
using SonnetDB.FullText;
using SonnetDB.Kv;

namespace SonnetDB.Documents;

/// <summary>
/// 单个 JSON 文档集合的 KV-backed 主数据与 path 索引存储。
/// </summary>
public sealed class DocumentCollectionStore : IDisposable
{
    private readonly object _sync = new();
    private readonly KvKeyspace _keyspace;
    private readonly Func<DocumentFullTextIndex, DocumentFullTextIndexStore> _fullTextIndexFactory;
    private readonly Dictionary<string, DocumentFullTextIndexStore> _fullTextStores = new(StringComparer.Ordinal);
    private DocumentCollectionSchema _schema;

    internal DocumentCollectionStore(
        DocumentCollectionSchema schema,
        KvKeyspace keyspace,
        Func<DocumentFullTextIndex, DocumentFullTextIndexStore> fullTextIndexFactory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyspace);
        ArgumentNullException.ThrowIfNull(fullTextIndexFactory);
        _schema = schema;
        _keyspace = keyspace;
        _fullTextIndexFactory = fullTextIndexFactory;
        RebuildIndexesLocked();
        ReconcileFullTextStoresLocked(schema, rebuildAll: false);
        PurgeExpiredDocumentsLocked();
    }

    /// <summary>文档集合 schema。</summary>
    public DocumentCollectionSchema Schema
    {
        get
        {
            lock (_sync)
                return _schema;
        }
    }

    /// <summary>当前集合底层 KV 视图的最新版本。</summary>
    public long LastVersion => _keyspace.LastSequence;

    /// <summary>公开 <see cref="Scan(int?, int)"/> 全表扫描的累计调用次数，供惰性访问路径回归测试观测。</summary>
    internal long FullScanCount => Interlocked.Read(ref _fullScanCount);

    private long _fullScanCount;

    /// <summary>
    /// 按文档 ID 插入或覆盖 JSON 文档。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">JSON 文本。</param>
    public void Upsert(string id, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        string normalized = JsonPathEvaluator.NormalizeJson(json);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var schema = _schema;
            byte[] documentKey = DocumentIndexCodec.EncodeDocumentKey(id);
            var old = TryGetByDocumentKeyLocked(documentKey);
            var newRow = new DocumentRow(id, normalized, Version: 0);
            var validation = ValidateDocumentForWrite(schema, newRow);
            if (!validation.IsValid && schema.Validator?.Action == DocumentValidationAction.Error)
                throw new InvalidOperationException(DocumentValidatorExecutor.FormatFailures(validation.Failures));
            ApplyMutationLocked(schema, old, newRow, documentKey);
        }
    }

    /// <summary>
    /// 按文档 ID 插入一条新 JSON 文档；ID 已存在时返回 duplicate_key。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">JSON 文本文档。</param>
    /// <returns>文档写入结果。</returns>
    public DocumentWriteResult Insert(string id, string json)
        => InsertMany([new DocumentWriteRequest(id, json)], ordered: true);

    /// <summary>
    /// 批量插入 JSON 文档。
    /// </summary>
    /// <param name="documents">待插入文档列表。</param>
    /// <param name="ordered">为 true 时任一错误都会阻止本批提交；为 false 时跳过失败项并提交其余有效项。</param>
    /// <returns>文档写入结果，包含稳定错误码。</returns>
    public DocumentWriteResult InsertMany(IEnumerable<DocumentWriteRequest> documents, bool ordered = true)
    {
        ArgumentNullException.ThrowIfNull(documents);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return ExecuteWriteBatchLocked(documents, ordered, DocumentWriteBatchMode.Insert);
        }
    }

    /// <summary>
    /// 按文档 ID 整体替换一条已存在 JSON 文档；文档不存在时不写入。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    /// <param name="json">新的 JSON 文本文档。</param>
    /// <param name="expectedVersion">可选的预期文档版本；不匹配时返回 write_conflict。</param>
    /// <returns>文档写入结果。</returns>
    public DocumentWriteResult Replace(string id, string json, long? expectedVersion = null)
        => ReplaceMany([new DocumentWriteRequest(id, json, expectedVersion)], ordered: true);

    /// <summary>
    /// 批量整体替换已存在 JSON 文档；不存在的 ID 会按未匹配处理。
    /// </summary>
    /// <param name="documents">待替换文档列表。</param>
    /// <param name="ordered">为 true 时任一错误都会阻止本批提交；为 false 时跳过失败项并提交其余有效项。</param>
    /// <returns>文档写入结果，包含稳定错误码。</returns>
    public DocumentWriteResult ReplaceMany(IEnumerable<DocumentWriteRequest> documents, bool ordered = true)
    {
        ArgumentNullException.ThrowIfNull(documents);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return ExecuteWriteBatchLocked(documents, ordered, DocumentWriteBatchMode.Replace);
        }
    }

    /// <summary>
    /// 按文档 ID 读取 JSON 文档。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    /// <returns>找到时返回文档记录；否则返回 null。</returns>
    public DocumentRow? Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return TryGetByDocumentKeyLocked(DocumentIndexCodec.EncodeDocumentKey(id));
        }
    }

    /// <summary>
    /// 按文档 ID 顺序批量读取 JSON 文档。
    /// </summary>
    /// <param name="ids">文档 ID 序列。</param>
    /// <returns>按请求 ID 顺序返回的已存在文档。</returns>
    public IReadOnlyList<DocumentRow> GetMany(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var rows = new List<DocumentRow>();
            foreach (string id in ids)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                var row = TryGetByDocumentKeyLocked(DocumentIndexCodec.EncodeDocumentKey(id));
                if (row is not null)
                    rows.Add(row);
            }

            return rows;
        }
    }

    /// <summary>
    /// 按文档 ID 删除 JSON 文档。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
        {
            var schema = _schema;
            byte[] documentKey = DocumentIndexCodec.EncodeDocumentKey(id);
            var old = TryGetByDocumentKeyLocked(documentKey);
            if (old is null)
                return false;

            ApplyMutationLocked(schema, old, newRow: null, documentKey);
            return true;
        }
    }

    /// <summary>
    /// 对匹配到的第一条文档执行局部更新，可选在未匹配时插入新文档。
    /// </summary>
    /// <param name="filter">文档过滤条件；为 null 时匹配集合中的第一条文档。</param>
    /// <param name="update">局部更新操作符集合。</param>
    /// <param name="upsert">未匹配文档时是否插入新文档。</param>
    /// <param name="upsertId">upsert 新文档 ID；为 null 时尝试从 <paramref name="filter"/> 的 ID 等值条件推断。</param>
    /// <returns>更新执行结果。</returns>
    public DocumentUpdateResult UpdateOne(
        DocumentFilter? filter,
        DocumentUpdate update,
        bool upsert = false,
        string? upsertId = null)
    {
        var result = UpdateOneWrite(filter, update, upsert, upsertId);
        if (result.HasErrors)
            throw new InvalidOperationException(result.Errors.First(static error => error.Severity == DocumentWriteErrorSeverity.Error).Message);
        return result.ToUpdateResult();
    }

    /// <summary>
    /// 对匹配到的第一条文档执行局部更新，并以统一写入结果返回 validator 错误或警告。
    /// </summary>
    /// <param name="filter">文档过滤条件；为 null 时匹配集合中的第一条文档。</param>
    /// <param name="update">局部更新操作符集合。</param>
    /// <param name="upsert">未匹配文档时是否插入新文档。</param>
    /// <param name="upsertId">upsert 新文档 ID；为 null 时尝试从 <paramref name="filter"/> 的 ID 等值条件推断。</param>
    /// <returns>统一文档写入结果。</returns>
    public DocumentWriteResult UpdateOneWrite(
        DocumentFilter? filter,
        DocumentUpdate update,
        bool upsert = false,
        string? upsertId = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var matches = FindMatchingRowsLocked(filter, limit: 1);
            if (matches.Count == 0)
                return UpsertFromUpdateLocked(filter, update, upsert, upsertId);

            return ApplyUpdateRowsLocked(matches, update);
        }
    }

    /// <summary>
    /// 对所有匹配文档执行局部更新，可选在未匹配时插入新文档。
    /// </summary>
    /// <param name="filter">文档过滤条件；为 null 时匹配全部文档。</param>
    /// <param name="update">局部更新操作符集合。</param>
    /// <param name="upsert">未匹配文档时是否插入新文档。</param>
    /// <param name="upsertId">upsert 新文档 ID；为 null 时尝试从 <paramref name="filter"/> 的 ID 等值条件推断。</param>
    /// <returns>更新执行结果。</returns>
    public DocumentUpdateResult UpdateMany(
        DocumentFilter? filter,
        DocumentUpdate update,
        bool upsert = false,
        string? upsertId = null)
    {
        var result = UpdateManyWrite(filter, update, upsert, upsertId);
        if (result.HasErrors)
            throw new InvalidOperationException(result.Errors.First(static error => error.Severity == DocumentWriteErrorSeverity.Error).Message);
        return result.ToUpdateResult();
    }

    /// <summary>
    /// 对所有匹配文档执行局部更新，并以统一写入结果返回 validator 错误或警告。
    /// </summary>
    /// <param name="filter">文档过滤条件；为 null 时匹配全部文档。</param>
    /// <param name="update">局部更新操作符集合。</param>
    /// <param name="upsert">未匹配文档时是否插入新文档。</param>
    /// <param name="upsertId">upsert 新文档 ID；为 null 时尝试从 <paramref name="filter"/> 的 ID 等值条件推断。</param>
    /// <returns>统一文档写入结果。</returns>
    public DocumentWriteResult UpdateManyWrite(
        DocumentFilter? filter,
        DocumentUpdate update,
        bool upsert = false,
        string? upsertId = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var matches = FindMatchingRowsLocked(filter, limit: int.MaxValue);
            if (matches.Count == 0)
                return UpsertFromUpdateLocked(filter, update, upsert, upsertId);

            return ApplyUpdateRowsLocked(matches, update);
        }
    }

    /// <summary>
    /// 按文档 ID 批量删除 JSON 文档。
    /// </summary>
    /// <param name="ids">文档 ID 序列。</param>
    /// <returns>实际删除的文档数量。</returns>
    public int DeleteMany(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return DeleteMany(ids, ordered: true).Deleted;
    }

    /// <summary>
    /// 按文档 ID 批量删除 JSON 文档。
    /// </summary>
    /// <param name="ids">文档 ID 序列。</param>
    /// <param name="ordered">为 true 时任一校验错误都会阻止本批提交；为 false 时跳过失败项并提交其余有效项。</param>
    /// <returns>文档写入结果，包含稳定错误码。</returns>
    public DocumentWriteResult DeleteMany(IEnumerable<string> ids, bool ordered)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var errors = new List<DocumentWriteError>();
            var operations = new List<PendingDocumentMutation>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            int index = 0;
            foreach (string id in ids)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add(new DocumentWriteError(
                        index,
                        id,
                        DocumentWriteErrorCodes.ValidationFailed,
                        "document id 不能为空。"));
                    if (ordered)
                        return new DocumentWriteResult(errors: errors);
                    index++;
                    continue;
                }

                if (!seenIds.Add(id))
                {
                    errors.Add(new DocumentWriteError(
                        index,
                        id,
                        DocumentWriteErrorCodes.DuplicateKey,
                        $"批量删除中重复的 document id '{id}'。"));
                    if (ordered)
                        return new DocumentWriteResult(errors: errors);
                    index++;
                    continue;
                }

                byte[] documentKey = DocumentIndexCodec.EncodeDocumentKey(id);
                var old = TryGetByDocumentKeyLocked(documentKey);
                if (old is not null)
                    operations.Add(new PendingDocumentMutation(index, old, NewRow: null, documentKey));
                index++;
            }

            ApplyPlannedMutationsLocked(_schema, operations);

            return new DocumentWriteResult(deleted: operations.Count, errors: errors);
        }
    }

    /// <summary>
    /// 扫描当前集合的所有文档，按文档 ID 字节序升序返回。
    /// </summary>
    /// <param name="limit">最多返回行数。</param>
    /// <param name="skip">跳过的行数。</param>
    /// <returns>按文档 ID 字节序升序排列的文档列表。</returns>
    public IReadOnlyList<DocumentRow> Scan(int? limit = null, int skip = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        Interlocked.Increment(ref _fullScanCount);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return ScanRowsLocked(limit ?? int.MaxValue, skip);
        }
    }

    /// <summary>
    /// 从指定文档 ID 之后继续扫描集合。
    /// </summary>
    /// <param name="afterId">上一页最后一个文档 ID；为 null 时从集合开头扫描。</param>
    /// <param name="limit">最多返回行数。</param>
    /// <returns>按文档 ID 字节序升序排列的文档列表。</returns>
    public IReadOnlyList<DocumentRow> ScanAfter(string? afterId, int? limit = null)
    {
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            if (string.IsNullOrEmpty(afterId))
                return ScanRowsLocked(limit ?? int.MaxValue);

            return ScanRowsAfterLocked(afterId, limit ?? int.MaxValue);
        }
    }

    /// <summary>
    /// 返回当前集合的文档数量。
    /// </summary>
    /// <returns>当前集合中的文档数量。</returns>
    public int Count()
    {
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return _keyspace.CountPrefix(new byte[] { (byte)'d' });
        }
    }

    /// <summary>
    /// 读取指定 JSON path 上的 distinct 标量值。
    /// </summary>
    /// <param name="path">JSON path 表达式。</param>
    /// <param name="limit">最多返回的 distinct 值数量。</param>
    /// <param name="ids">可选的文档 ID 限定；为空时扫描整个集合。</param>
    /// <returns>按扫描顺序去重后的 path 标量值列表。</returns>
    public IReadOnlyList<object?> Distinct(string path, int? limit = null, IEnumerable<string>? ids = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        int take = limit ?? int.MaxValue;
        if (take <= 0)
            return [];

        var parsedPath = JsonPath.Parse(path);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var rows = ids is null
                ? ScanRowsLocked(int.MaxValue)
                : GetManyLocked(ids);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var values = new List<object?>();
            foreach (var row in rows)
            {
                object? value = JsonPathEvaluator.Evaluate(row.Json, parsedPath);
                string key = JsonPathEvaluator.ToIndexScalar(value) ?? "<null>";
                if (!seen.Add(key))
                    continue;

                values.Add(value);
                if (values.Count >= take)
                    break;
            }

            return values;
        }
    }

    /// <summary>
    /// 执行文档聚合管线。
    /// </summary>
    /// <param name="pipeline">聚合管线定义。</param>
    /// <returns>聚合输出文档。</returns>
    public DocumentAggregationResult Aggregate(DocumentAggregationPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return DocumentAggregationExecutor.Execute(ScanRowsLocked(int.MaxValue), pipeline);
        }
    }

    /// <summary>
    /// 按 JSON path 索引读取候选文档。
    /// </summary>
    /// <param name="index">JSON path 索引声明。</param>
    /// <param name="value">path 等值过滤值。</param>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<DocumentRow> GetByIndex(DocumentPathIndex index, object? value, int? limit = null)
        => GetByIndex(index, new[] { value }, limit);

    /// <summary>
    /// 按文档二级索引读取候选文档。
    /// </summary>
    /// <param name="index">文档二级索引声明。</param>
    /// <param name="values">与索引 path 数量一致的等值谓词值。</param>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<DocumentRow> GetByIndex(DocumentPathIndex index, IReadOnlyList<object?> values, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();

            var partSets = EncodeLookupPartSets(index, values);
            if (partSets.Count == 0)
                return [];

            int take = limit ?? int.MaxValue;
            var rows = new List<DocumentRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parts in partSets)
            {
                byte[] prefix = DocumentIndexCodec.EncodeIndexPrefix(index, parts);
                var entries = _keyspace.ScanPrefix(prefix, take - rows.Count);
                foreach (var entry in entries)
                {
                    string id = DocumentIndexCodec.DecodeIndexEntryValue(entry.Value.Span);
                    if (!seen.Add(id))
                        continue;

                    var row = GetLocked(id);
                    if (row is not null)
                        rows.Add(row);
                    if (rows.Count >= take)
                        break;
                }

                if (rows.Count >= take)
                    break;
            }

            return rows;
        }
    }

    /// <summary>
    /// 按文档二级索引的等值前缀读取候选文档。
    /// </summary>
    /// <param name="index">文档二级索引声明。</param>
    /// <param name="values">从索引首列开始连续匹配的等值谓词值。</param>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<DocumentRow> GetByIndexPrefix(DocumentPathIndex index, IReadOnlyList<object?> values, int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();

            var partSets = EncodeLookupPartSets(index, values, allowPrefix: true);
            if (partSets.Count == 0)
                return [];

            int take = limit ?? int.MaxValue;
            var rows = new List<DocumentRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parts in partSets)
            {
                byte[] prefix = DocumentIndexCodec.EncodeIndexPrefix(index, parts);
                var entries = _keyspace.ScanPrefix(prefix, take - rows.Count);
                foreach (var entry in entries)
                {
                    string id = DocumentIndexCodec.DecodeIndexEntryValue(entry.Value.Span);
                    if (!seen.Add(id))
                        continue;

                    var row = GetLocked(id);
                    if (row is not null)
                        rows.Add(row);
                    if (rows.Count >= take)
                        break;
                }

                if (rows.Count >= take)
                    break;
            }

            return rows;
        }
    }

    /// <summary>
    /// 按 JSON path 索引从指定文档 ID 之后继续读取候选文档。
    /// </summary>
    /// <param name="index">JSON path 索引声明。</param>
    /// <param name="value">path 等值过滤值。</param>
    /// <param name="afterId">上一页最后一个文档 ID；为 null 时从索引前缀起点读取。</param>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<DocumentRow> GetByIndexAfter(
        DocumentPathIndex index,
        object? value,
        string? afterId,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var partSets = EncodeLookupPartSets(index, new[] { value });
            if (partSets.Count == 0)
                return [];

            int take = limit ?? int.MaxValue;
            var rows = new List<DocumentRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var parts in partSets)
            {
                byte[] prefix = DocumentIndexCodec.EncodeIndexPrefix(index, parts);
                byte[]? afterKey = string.IsNullOrEmpty(afterId)
                    ? null
                    : DocumentIndexCodec.EncodeIndexEntryKey(index, parts, afterId);
                var entries = afterKey is null
                    ? _keyspace.ScanPrefix(prefix, take - rows.Count)
                    : _keyspace.ScanPrefixAfter(prefix, afterKey, take - rows.Count);
                foreach (var entry in entries)
                {
                    string id = DocumentIndexCodec.DecodeIndexEntryValue(entry.Value.Span);
                    if (!seen.Add(id))
                        continue;

                    var row = GetLocked(id);
                    if (row is not null)
                        rows.Add(row);
                    if (rows.Count >= take)
                        break;
                }

                if (rows.Count >= take)
                    break;
            }

            return rows;
        }
    }

    /// <summary>
    /// 统计文档二级索引等值命中的索引条目数量，不物化文档。
    /// </summary>
    /// <param name="index">文档二级索引声明。</param>
    /// <param name="values">与索引 path 数量一致的等值谓词值。</param>
    /// <returns>命中的索引条目数量。</returns>
    public int CountByIndex(DocumentPathIndex index, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return CountIndexEntriesLocked(index, values, allowPrefix: false);
        }
    }

    /// <summary>
    /// 统计文档二级索引等值前缀命中的索引条目数量，不物化文档。
    /// </summary>
    /// <param name="index">文档二级索引声明。</param>
    /// <param name="values">从索引首列开始连续匹配的等值谓词值。</param>
    /// <returns>命中的索引条目数量。</returns>
    public int CountByIndexPrefix(DocumentPathIndex index, IReadOnlyList<object?> values)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(values);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return CountIndexEntriesLocked(index, values, allowPrefix: true);
        }
    }

    /// <summary>
    /// 按全文索引读取候选文档 ID 和 BM25 分数。
    /// </summary>
    /// <param name="index">全文索引声明。</param>
    /// <param name="field">索引字段或 <c>*</c>。</param>
    /// <param name="queryText">查询文本。</param>
    /// <param name="topK">返回前 K 条。</param>
    public IReadOnlyList<DocumentFullTextSearchHit> SearchFullText(
        DocumentFullTextIndex index,
        string field,
        string queryText,
        int topK)
        => SearchFullText(index, field, queryText, topK, FullTextSearchMode.Exact);

    public IReadOnlyList<DocumentFullTextSearchHit> SearchFullText(
        DocumentFullTextIndex index,
        string field,
        string queryText,
        int topK,
        FullTextSearchMode mode)
    {
        ArgumentNullException.ThrowIfNull(index);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            var store = OpenFullTextStoreLocked(index, rebuildIfMissing: true);
            return store.Search(field, queryText, topK, mode);
        }
    }

    /// <summary>
    /// 返回指定全文索引当前可见文档数。
    /// </summary>
    /// <param name="index">全文索引声明。</param>
    public int GetFullTextDocumentCount(DocumentFullTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            return OpenFullTextStoreLocked(index, rebuildIfMissing: true).DocumentCount;
        }
    }

    internal int RebuildFullTextIndex(DocumentFullTextIndex index, string indexDirectory)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexDirectory);
        lock (_sync)
        {
            PurgeExpiredDocumentsLocked();
            _fullTextStores.Remove(index.Name);
            if (Directory.Exists(indexDirectory))
                Directory.Delete(indexDirectory, recursive: true);

            var store = OpenFullTextStoreLocked(index, rebuildIfMissing: false);
            store.Rebuild(ScanRowsLocked(int.MaxValue));
            return store.DocumentCount;
        }
    }

    internal void ApplySchema(DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            var previous = _schema;
            _schema = schema;
            try
            {
                RebuildIndexesLocked();
                ReconcileFullTextStoresLocked(previous, rebuildAll: true);
            }
            catch
            {
                _schema = previous;
                RebuildIndexesLocked();
                ReconcileFullTextStoresLocked(schema, rebuildAll: true);
                throw;
            }
        }
    }

    internal long CreateSnapshot() => _keyspace.CreateSnapshot();

    internal long Compact() => _keyspace.Compact();

    /// <summary>
    /// 关闭底层 KV keyspace。
    /// </summary>
    public void Dispose() => _keyspace.Dispose();

    private DocumentRow? GetLocked(string id)
        => TryGetByDocumentKeyLocked(DocumentIndexCodec.EncodeDocumentKey(id));

    private DocumentRow? TryGetByDocumentKeyLocked(ReadOnlySpan<byte> documentKey)
    {
        var entry = _keyspace.GetEntry(documentKey);
        if (entry is null)
            return null;

        string id = DocumentIndexCodec.DecodeIdFromDocumentKey(documentKey.ToArray());
        return new DocumentRow(id, Encoding.UTF8.GetString(entry.Value.Span), entry.Version);
    }

    private DocumentWriteResult ExecuteWriteBatchLocked(
        IEnumerable<DocumentWriteRequest> documents,
        bool ordered,
        DocumentWriteBatchMode mode)
    {
        var schema = _schema;
        var errors = new List<DocumentWriteError>();
        var warnings = new List<DocumentWriteError>();
        var operations = new List<PendingDocumentMutation>();
        var inputIds = new HashSet<string>(StringComparer.Ordinal);
        var plannedUniqueIdsByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        int matched = 0;
        int index = 0;

        foreach (var document in documents)
        {
            if (TryPrepareWriteRequestLocked(
                schema,
                document,
                index,
                mode,
                inputIds,
                plannedUniqueIdsByKey,
                out var operation,
                out bool itemMatched,
                out var error,
                out var warning))
            {
                if (itemMatched)
                    matched++;
                if (operation is not null)
                    operations.Add(operation);
                if (warning is not null)
                    warnings.Add(warning);
            }
            else
            {
                errors.Add(error!);
                if (ordered)
                    return new DocumentWriteResult(errors: errors);
            }

            index++;
        }

        ApplyPlannedMutationsLocked(schema, operations);

        return mode == DocumentWriteBatchMode.Insert
            ? new DocumentWriteResult(inserted: operations.Count, errors: Combine(errors, warnings))
            : new DocumentWriteResult(matched: matched, modified: operations.Count, errors: Combine(errors, warnings));
    }

    private bool TryPrepareWriteRequestLocked(
        DocumentCollectionSchema schema,
        DocumentWriteRequest request,
        int index,
        DocumentWriteBatchMode mode,
        ISet<string> inputIds,
        IDictionary<string, string> plannedUniqueIdsByKey,
        out PendingDocumentMutation? mutation,
        out bool matched,
        out DocumentWriteError? error,
        out DocumentWriteError? warning)
    {
        mutation = null;
        matched = false;
        error = null;
        warning = null;

        if (string.IsNullOrWhiteSpace(request.Id))
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                "document id 不能为空。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(request.Json))
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                "document JSON 不能为空。");
            return false;
        }

        if (request.ExpectedVersion is < 0)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                "expectedVersion 不能为负数。");
            return false;
        }

        if (!inputIds.Add(request.Id))
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.DuplicateKey,
                $"批量写中重复的 document id '{request.Id}'。");
            return false;
        }

        string normalized;
        try
        {
            normalized = JsonPathEvaluator.NormalizeJson(request.Json);
        }
        catch (JsonException ex)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                ex.Message);
            return false;
        }

        byte[] documentKey;
        try
        {
            documentKey = DocumentIndexCodec.EncodeDocumentKey(request.Id);
        }
        catch (ArgumentException ex)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                ex.Message);
            return false;
        }

        var old = TryGetByDocumentKeyLocked(documentKey);
        if (mode == DocumentWriteBatchMode.Insert && old is not null)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.DuplicateKey,
                $"document id '{request.Id}' 已存在。");
            return false;
        }

        if (request.ExpectedVersion.HasValue
            && (old?.Version ?? 0) != request.ExpectedVersion.Value)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.WriteConflict,
                $"document id '{request.Id}' version mismatch.");
            return false;
        }

        if (mode == DocumentWriteBatchMode.Replace && old is null)
            return true;

        matched = old is not null;
        var newRow = new DocumentRow(request.Id, normalized, Version: 0);
        var validation = ValidateDocumentForWrite(schema, newRow);
        if (!validation.IsValid)
        {
            string message = DocumentValidatorExecutor.FormatFailures(validation.Failures);
            if (schema.Validator?.Action == DocumentValidationAction.Warn)
            {
                warning = new DocumentWriteError(
                    index,
                    request.Id,
                    DocumentWriteErrorCodes.ValidationFailed,
                    message,
                    DocumentWriteErrorSeverity.Warning);
            }
            else
            {
                error = new DocumentWriteError(
                    index,
                    request.Id,
                    DocumentWriteErrorCodes.ValidationFailed,
                    message);
                return false;
            }
        }

        try
        {
            PrepareMutationLocked(schema, old, newRow, documentKey, plannedUniqueIdsByKey);
            TrackPlannedUniqueKeys(schema, newRow, plannedUniqueIdsByKey);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.DocumentTooLarge,
                ex.Message);
            return false;
        }
        catch (ArgumentException ex)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.ValidationFailed,
                ex.Message);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            error = new DocumentWriteError(
                index,
                request.Id,
                DocumentWriteErrorCodes.DuplicateKey,
                ex.Message);
            return false;
        }

        mutation = new PendingDocumentMutation(index, old, newRow, documentKey);
        return true;
    }

    private static void TrackPlannedUniqueKeys(
        DocumentCollectionSchema schema,
        DocumentRow row,
        IDictionary<string, string> plannedUniqueIdsByKey)
    {
        if (!schema.Indexes.Any(static i => i.IsUnique))
            return;

        using var document = JsonDocument.Parse(row.Json);
        foreach (var index in schema.Indexes.Where(static i => i.IsUnique))
        {
            if (!ShouldIndexDocument(index, document.RootElement))
                continue;

            var values = BuildIndexKeyParts(index, document.RootElement);
            if (values is null)
                continue;

            byte[] key = DocumentIndexCodec.EncodeIndexEntryKey(index, values, row.Id);
            plannedUniqueIdsByKey[Convert.ToBase64String(key)] = row.Id;
        }
    }

    private void ApplyMutationLocked(
        DocumentCollectionSchema schema,
        DocumentRow? oldRow,
        DocumentRow? newRow,
        byte[] documentKey)
    {
        PrepareMutationLocked(schema, oldRow, newRow, documentKey);
        ApplyPlannedMutationLocked(schema, new PendingDocumentMutation(-1, oldRow, newRow, documentKey));
    }

    private void PrepareMutationLocked(
        DocumentCollectionSchema schema,
        DocumentRow? oldRow,
        DocumentRow? newRow,
        byte[] documentKey,
        IDictionary<string, string>? pendingUniqueIdsByKey = null)
    {
        if (newRow is null)
            return;

        ValidateUniqueIndexesLocked(schema, oldRow, newRow, pendingUniqueIdsByKey);
        ValidateMutationSize(schema, newRow, documentKey);
    }

    private void ApplyPlannedMutationLocked(DocumentCollectionSchema schema, PendingDocumentMutation mutation)
    {
        ApplyPlannedMutationKvLocked(schema, mutation);

        if (mutation.OldRow is not null)
        {
            foreach (var index in schema.FullTextIndexes)
                OpenFullTextStoreLocked(index, rebuildIfMissing: false).Delete(mutation.OldRow.Id);
        }

        if (mutation.NewRow is not null)
        {
            foreach (var index in schema.FullTextIndexes)
                OpenFullTextStoreLocked(index, rebuildIfMissing: false).Upsert(mutation.NewRow);
        }
    }

    /// <summary>
    /// 批量应用变更：KV 逐条应用，全文索引整批成段（每索引一次 DeleteMany + 一次 UpsertMany），
    /// 避免每文档一个单文档段 + 一次 manifest 全量改写。
    /// </summary>
    private void ApplyPlannedMutationsLocked(DocumentCollectionSchema schema, IReadOnlyList<PendingDocumentMutation> operations)
    {
        if (operations.Count == 0)
            return;

        if (operations.Count == 1 || schema.FullTextIndexes.Count == 0)
        {
            foreach (var operation in operations)
                ApplyPlannedMutationLocked(schema, operation);
            return;
        }

        List<string>? fullTextDeletes = null;
        List<DocumentRow>? fullTextUpserts = null;
        foreach (var operation in operations)
        {
            ApplyPlannedMutationKvLocked(schema, operation);
            if (operation.OldRow is not null && operation.NewRow is null)
                (fullTextDeletes ??= new List<string>()).Add(operation.OldRow.Id);
            if (operation.NewRow is not null)
                (fullTextUpserts ??= new List<DocumentRow>()).Add(operation.NewRow);
        }

        foreach (var index in schema.FullTextIndexes)
        {
            var store = OpenFullTextStoreLocked(index, rebuildIfMissing: false);
            if (fullTextDeletes is not null)
                store.DeleteMany(fullTextDeletes);
            if (fullTextUpserts is not null)
                store.UpsertMany(fullTextUpserts);
        }
    }

    private void ApplyPlannedMutationKvLocked(DocumentCollectionSchema schema, PendingDocumentMutation mutation)
    {
        foreach (var indexEntry in mutation.OldRow is null ? [] : BuildIndexEntries(schema, mutation.OldRow))
            _keyspace.Delete(indexEntry.Key);

        if (mutation.NewRow is null)
        {
            _keyspace.Delete(mutation.DocumentKey);
            return;
        }

        _keyspace.Put(mutation.DocumentKey, Encoding.UTF8.GetBytes(mutation.NewRow.Json));
        foreach (var indexEntry in BuildIndexEntries(schema, mutation.NewRow))
            _keyspace.Put(indexEntry.Key, indexEntry.Value);
    }

    private static IReadOnlyList<IndexEntry> BuildIndexEntries(DocumentCollectionSchema schema, DocumentRow row)
    {
        if (schema.Indexes.Count == 0)
            return [];

        using var document = JsonDocument.Parse(row.Json);
        var entries = new List<IndexEntry>(schema.Indexes.Count);
        foreach (var index in schema.Indexes)
        {
            if (!ShouldIndexDocument(index, document.RootElement))
                continue;

            var values = BuildIndexKeyParts(index, document.RootElement);
            if (values is null)
                continue;

            entries.Add(new IndexEntry(
                index,
                DocumentIndexCodec.EncodeIndexEntryKey(index, values, row.Id),
                DocumentIndexCodec.EncodeIndexEntryValue(row.Id)));
        }

        return entries;
    }

    private void ValidateUniqueIndexesLocked(
        DocumentCollectionSchema schema,
        DocumentRow? oldRow,
        DocumentRow newRow,
        IDictionary<string, string>? pendingUniqueIdsByKey = null)
    {
        if (!schema.Indexes.Any(static i => i.IsUnique))
            return;

        using var document = JsonDocument.Parse(newRow.Json);
        foreach (var index in schema.Indexes.Where(static i => i.IsUnique))
        {
            if (!ShouldIndexDocument(index, document.RootElement))
                continue;

            var values = BuildIndexKeyParts(index, document.RootElement);
            if (values is null)
                continue;

            byte[] key = DocumentIndexCodec.EncodeIndexEntryKey(index, values, newRow.Id);
            string keyText = Convert.ToBase64String(key);
            if (pendingUniqueIdsByKey is not null
                && pendingUniqueIdsByKey.TryGetValue(keyText, out string? pendingId)
                && !string.Equals(pendingId, newRow.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"document collection '{schema.Name}' unique index '{index.Name}' duplicate key.");
            }

            byte[]? existing = _keyspace.Get(key);
            if (existing is null)
                continue;

            string existingId = DocumentIndexCodec.DecodeIndexEntryValue(existing);
            if (oldRow is not null && string.Equals(existingId, oldRow.Id, StringComparison.Ordinal))
                continue;

            throw new InvalidOperationException($"文档集合 '{schema.Name}' 的唯一索引 '{index.Name}' 冲突。");
        }
    }

    private void ValidateMutationSize(DocumentCollectionSchema schema, DocumentRow row, byte[] documentKey)
    {
        _keyspace.ValidateWrite(documentKey, Encoding.UTF8.GetBytes(row.Json));
        foreach (var indexEntry in BuildIndexEntries(schema, row))
            _keyspace.ValidateWrite(indexEntry.Key, indexEntry.Value);
    }

    private static IReadOnlyList<DocumentIndexKeyPart>? BuildIndexKeyParts(DocumentPathIndex index, JsonElement root)
    {
        var values = new DocumentIndexKeyPart[index.Paths.Count];
        for (int i = 0; i < index.Paths.Count; i++)
        {
            var path = JsonPath.Parse(index.Paths[i]);
            if (!JsonPathEvaluator.TryResolve(root, path, out var element))
            {
                if (index.IsSparse)
                    return null;

                values[i] = DocumentIndexKeyPart.Missing;
                continue;
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                if (index.IsSparse)
                    return null;

                values[i] = DocumentIndexKeyPart.Null;
                continue;
            }

            object? value = element.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long longValue) ? longValue : element.GetDouble(),
                JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
                _ => null,
            };

            string? scalar = JsonPathEvaluator.ToIndexScalar(value);
            if (scalar is null)
            {
                if (index.IsSparse)
                    return null;

                values[i] = DocumentIndexKeyPart.Null;
                continue;
            }

            values[i] = DocumentIndexKeyPart.FromScalar(scalar);
        }

        return values;
    }

    private static bool ShouldIndexDocument(DocumentPathIndex index, JsonElement root)
    {
        if (index.PartialFilter is null)
            return true;

        return MatchesPartialFilter(root, index.PartialFilter);
    }

    private static bool MatchesPartialFilter(JsonElement root, DocumentIndexPartialFilter filter)
    {
        bool exists = JsonPathEvaluator.TryResolve(root, JsonPath.Parse(filter.Path), out var element);
        if (filter.Operator == DocumentIndexPartialFilterOperator.Exists)
            return filter.ValueScalar is null or "true" ? exists : !exists;
        if (!exists)
            return false;

        string? actual = JsonPathEvaluator.ToIndexScalar(JsonPathEvaluator.ConvertElement(element));
        string? expected = filter.ValueScalar;
        int comparison = string.Compare(actual, expected, StringComparison.Ordinal);
        return filter.Operator switch
        {
            DocumentIndexPartialFilterOperator.Equal => string.Equals(actual, expected, StringComparison.Ordinal),
            DocumentIndexPartialFilterOperator.NotEqual => !string.Equals(actual, expected, StringComparison.Ordinal),
            DocumentIndexPartialFilterOperator.GreaterThan => comparison > 0,
            DocumentIndexPartialFilterOperator.GreaterThanOrEqual => comparison >= 0,
            DocumentIndexPartialFilterOperator.LessThan => comparison < 0,
            DocumentIndexPartialFilterOperator.LessThanOrEqual => comparison <= 0,
            _ => false,
        };
    }

    private static IReadOnlyList<IReadOnlyList<DocumentIndexKeyPart>> EncodeLookupPartSets(
        DocumentPathIndex index,
        IReadOnlyList<object?> values,
        bool allowPrefix = false)
    {
        if (allowPrefix ? values.Count > index.Paths.Count : values.Count != index.Paths.Count)
            throw new ArgumentException("索引值数量与索引 path 数量不一致。", nameof(values));

        var partSets = new List<DocumentIndexKeyPart[]> { new DocumentIndexKeyPart[values.Count] };
        for (int i = 0; i < values.Count; i++)
        {
            var variants = EncodeLookupPartVariants(index, values[i]);
            if (variants.Count == 0)
                return [];

            int existingCount = partSets.Count;
            for (int variantIndex = 1; variantIndex < variants.Count; variantIndex++)
            {
                for (int setIndex = 0; setIndex < existingCount; setIndex++)
                    partSets.Add((DocumentIndexKeyPart[])partSets[setIndex].Clone());
            }

            for (int setIndex = 0; setIndex < partSets.Count; setIndex++)
                partSets[setIndex][i] = variants[setIndex / existingCount];
        }

        return partSets;
    }

    private static IReadOnlyList<DocumentIndexKeyPart> EncodeLookupPartVariants(DocumentPathIndex index, object? value)
    {
        if (value is not null)
        {
            string? scalar = JsonPathEvaluator.ToIndexScalar(value);
            return scalar is null
                ? [DocumentIndexKeyPart.Null]
                : [DocumentIndexKeyPart.FromScalar(scalar)];
        }

        return index.IsSparse
            ? []
            : [DocumentIndexKeyPart.Null];
    }

    private void RebuildIndexesLocked()
    {
        foreach (var entry in _keyspace.ScanPrefix(new byte[] { (byte)'i' }, int.MaxValue))
            _keyspace.Delete(entry.Key.Span);

        if (_schema.Indexes.Count == 0)
            return;

        foreach (var rowEntry in _keyspace.ScanPrefix(new byte[] { (byte)'d' }, int.MaxValue))
        {
            string id = DocumentIndexCodec.DecodeIdFromDocumentKey(rowEntry.Key);
            var row = new DocumentRow(id, Encoding.UTF8.GetString(rowEntry.Value.Span), rowEntry.Version);
            foreach (var indexEntry in BuildIndexEntries(_schema, row))
            {
                if (indexEntry.Index.IsUnique && _keyspace.Get(indexEntry.Key) is not null)
                {
                    throw new InvalidOperationException($"文档集合 '{_schema.Name}' 的唯一索引 '{indexEntry.Index.Name}' 冲突，无法重建索引。");
                }

                _keyspace.Put(indexEntry.Key, indexEntry.Value);
            }
        }
    }

    private void PurgeExpiredDocumentsLocked()
    {
        var ttlIndexes = _schema.Indexes.Where(static i => i.IsTtl).ToArray();
        if (ttlIndexes.Length == 0)
            return;

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expired = new List<DocumentRow>();
        foreach (var row in ScanRowsLocked(int.MaxValue))
        {
            if (IsExpired(row, ttlIndexes, nowMs))
                expired.Add(row);
        }

        var expiredOperations = new List<PendingDocumentMutation>(expired.Count);
        foreach (var row in expired)
            expiredOperations.Add(new PendingDocumentMutation(-1, row, NewRow: null, DocumentIndexCodec.EncodeDocumentKey(row.Id)));
        ApplyPlannedMutationsLocked(_schema, expiredOperations);
    }

    private static bool IsExpired(DocumentRow row, IReadOnlyList<DocumentPathIndex> ttlIndexes, long nowMs)
    {
        using var document = JsonDocument.Parse(row.Json);
        foreach (var index in ttlIndexes)
        {
            string ttlPath = index.TtlPath ?? index.Path;
            if (!JsonPathEvaluator.TryResolve(document.RootElement, JsonPath.Parse(ttlPath), out var element))
                continue;
            if (!TryReadUnixMilliseconds(element, out long timestampMs))
                continue;

            long ttlMs = checked(index.TtlSeconds!.Value * 1000L);
            if (timestampMs + ttlMs <= nowMs)
                return true;
        }

        return false;
    }

    private static bool TryReadUnixMilliseconds(JsonElement element, out long timestampMs)
    {
        timestampMs = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out timestampMs))
            return true;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        string? text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out timestampMs))
            return true;
        if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
        {
            timestampMs = dto.ToUnixTimeMilliseconds();
            return true;
        }

        return false;
    }

    private void ReconcileFullTextStoresLocked(DocumentCollectionSchema previousSchema, bool rebuildAll)
    {
        var active = new HashSet<string>(_schema.FullTextIndexes.Select(static i => i.Name), StringComparer.Ordinal);
        foreach (var stale in _fullTextStores.Keys.Where(name => !active.Contains(name)).ToArray())
            _fullTextStores.Remove(stale);

        foreach (var index in _schema.FullTextIndexes)
        {
            bool existed = previousSchema.TryGetFullTextIndex(index.Name) is not null;
            var store = OpenFullTextStoreLocked(index, rebuildIfMissing: !rebuildAll);
            if (rebuildAll && !existed)
                store.Rebuild(ScanRowsLocked(int.MaxValue));
        }
    }

    private DocumentFullTextIndexStore OpenFullTextStoreLocked(DocumentFullTextIndex index, bool rebuildIfMissing)
    {
        if (_fullTextStores.TryGetValue(index.Name, out var existing))
            return existing;

        var store = _fullTextIndexFactory(index);
        _fullTextStores[index.Name] = store;
        if (rebuildIfMissing && store.DocumentCount == 0 && ScanRowsLocked(1).Count > 0)
            store.Rebuild(ScanRowsLocked(int.MaxValue));
        return store;
    }

    private IReadOnlyList<DocumentRow> GetManyLocked(IEnumerable<string> ids)
    {
        var rows = new List<DocumentRow>();
        foreach (string id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            var row = TryGetByDocumentKeyLocked(DocumentIndexCodec.EncodeDocumentKey(id));
            if (row is not null)
                rows.Add(row);
        }

        return rows;
    }

    private int CountIndexEntriesLocked(DocumentPathIndex index, IReadOnlyList<object?> values, bool allowPrefix)
    {
        var partSets = EncodeLookupPartSets(index, values, allowPrefix);
        int count = 0;
        foreach (var parts in partSets)
            count += _keyspace.CountPrefix(DocumentIndexCodec.EncodeIndexPrefix(index, parts));

        return count;
    }

    private IReadOnlyList<DocumentRow> FindMatchingRowsLocked(DocumentFilter? filter, int limit)
    {
        var rows = new List<DocumentRow>();
        foreach (var row in ScanRowsLocked(int.MaxValue))
        {
            if (!DocumentQueryPlanner.Matches(filter, row))
                continue;

            rows.Add(row);
            if (rows.Count >= limit)
                break;
        }

        return rows;
    }

    private DocumentWriteResult ApplyUpdateRowsLocked(IReadOnlyList<DocumentRow> rows, DocumentUpdate update)
    {
        var schema = _schema;
        var operations = new List<PendingDocumentMutation>(rows.Count);
        var warnings = new List<DocumentWriteError>();
        int itemIndex = 0;
        foreach (var row in rows)
        {
            string updated = DocumentUpdateExecutor.Apply(row.Json, update);
            if (string.Equals(row.Json, updated, StringComparison.Ordinal))
            {
                itemIndex++;
                continue;
            }

            var documentKey = DocumentIndexCodec.EncodeDocumentKey(row.Id);
            var newRow = new DocumentRow(row.Id, updated, Version: 0);
            var validation = ValidateDocumentForWrite(schema, newRow);
            if (!validation.IsValid)
            {
                var writeError = new DocumentWriteError(
                    itemIndex,
                    row.Id,
                    DocumentWriteErrorCodes.ValidationFailed,
                    DocumentValidatorExecutor.FormatFailures(validation.Failures),
                    schema.Validator?.Action == DocumentValidationAction.Warn
                        ? DocumentWriteErrorSeverity.Warning
                        : DocumentWriteErrorSeverity.Error);
                if (writeError.Severity == DocumentWriteErrorSeverity.Error)
                    return new DocumentWriteResult(matched: itemIndex + 1, modified: 0, errors: [writeError]);
                warnings.Add(writeError);
            }

            PrepareMutationLocked(schema, row, newRow, documentKey);
            operations.Add(new PendingDocumentMutation(-1, row, newRow, documentKey));
            itemIndex++;
        }

        ApplyPlannedMutationsLocked(schema, operations);

        return new DocumentWriteResult(matched: rows.Count, modified: operations.Count, errors: warnings);
    }

    private DocumentWriteResult UpsertFromUpdateLocked(
        DocumentFilter? filter,
        DocumentUpdate update,
        bool upsert,
        string? upsertId)
    {
        if (!upsert)
            return new DocumentWriteResult(matched: 0, modified: 0, inserted: 0);

        string id = upsertId ?? DocumentUpdateExecutor.TryInferUpsertId(filter)
            ?? throw new InvalidOperationException("document update upsert 需要提供 upsertId 或 _id/id 等值过滤条件。");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        byte[] documentKey = DocumentIndexCodec.EncodeDocumentKey(id);
        if (TryGetByDocumentKeyLocked(documentKey) is not null)
            throw new InvalidOperationException($"document update upsertId '{id}' 已存在，但过滤条件未匹配该文档。");

        string seed = DocumentUpdateExecutor.BuildUpsertSeed(filter);
        string updated = DocumentUpdateExecutor.Apply(seed, update);
        var newRow = new DocumentRow(id, updated, Version: 0);
        var validation = ValidateDocumentForWrite(_schema, newRow);
        DocumentWriteError? warning = null;
        if (!validation.IsValid)
        {
            var writeError = new DocumentWriteError(
                0,
                id,
                DocumentWriteErrorCodes.ValidationFailed,
                DocumentValidatorExecutor.FormatFailures(validation.Failures),
                _schema.Validator?.Action == DocumentValidationAction.Warn
                    ? DocumentWriteErrorSeverity.Warning
                    : DocumentWriteErrorSeverity.Error);
            if (writeError.Severity == DocumentWriteErrorSeverity.Error)
                return new DocumentWriteResult(errors: [writeError]);
            warning = writeError;
        }

        PrepareMutationLocked(
            _schema,
            oldRow: null,
            newRow,
            documentKey);
        ApplyPlannedMutationLocked(_schema, new PendingDocumentMutation(-1, OldRow: null, newRow, documentKey));
        return new DocumentWriteResult(inserted: 1, errors: warning is null ? null : [warning]);
    }

    private static DocumentValidationResult ValidateDocumentForWrite(DocumentCollectionSchema schema, DocumentRow row)
        => DocumentValidatorExecutor.Validate(schema.Validator, row.Json);

    private static IReadOnlyList<DocumentWriteError> Combine(
        IReadOnlyList<DocumentWriteError> errors,
        IReadOnlyList<DocumentWriteError> warnings)
    {
        if (errors.Count == 0)
            return warnings;
        if (warnings.Count == 0)
            return errors;

        return errors.Concat(warnings).ToArray();
    }

    private IReadOnlyList<DocumentRow> ScanRowsLocked(int limit, int skip = 0)
    {
        int take = limit == int.MaxValue ? int.MaxValue : checked(skip + limit);
        var entries = _keyspace.ScanPrefix(new byte[] { (byte)'d' }, take);
        var rows = new List<DocumentRow>(entries.Count);
        foreach (var entry in entries.Skip(skip))
        {
            string id = DocumentIndexCodec.DecodeIdFromDocumentKey(entry.Key);
            rows.Add(new DocumentRow(id, Encoding.UTF8.GetString(entry.Value.Span), entry.Version));
        }

        return rows;
    }

    private IReadOnlyList<DocumentRow> ScanRowsAfterLocked(string afterId, int limit)
    {
        byte[] afterKey = DocumentIndexCodec.EncodeDocumentKey(afterId);
        var entries = _keyspace.ScanPrefixAfter(new byte[] { (byte)'d' }, afterKey, limit);
        var rows = new List<DocumentRow>(entries.Count);
        foreach (var entry in entries)
        {
            string id = DocumentIndexCodec.DecodeIdFromDocumentKey(entry.Key);
            rows.Add(new DocumentRow(id, Encoding.UTF8.GetString(entry.Value.Span), entry.Version));
        }

        return rows;
    }

    private sealed record PendingDocumentMutation(
        int Index,
        DocumentRow? OldRow,
        DocumentRow? NewRow,
        byte[] DocumentKey);

    private sealed record IndexEntry(DocumentPathIndex Index, byte[] Key, byte[] Value);

    private enum DocumentWriteBatchMode
    {
        Insert,
        Replace,
    }
}
