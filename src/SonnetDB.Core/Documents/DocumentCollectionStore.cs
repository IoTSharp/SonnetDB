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
            ApplyMutationLocked(schema, old, newRow, documentKey);
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
        int deleted = 0;
        foreach (string id in ids)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (Delete(id))
                deleted++;
        }

        return deleted;
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
            return ScanRowsLocked(int.MaxValue).Count;
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

    private void ApplyMutationLocked(
        DocumentCollectionSchema schema,
        DocumentRow? oldRow,
        DocumentRow? newRow,
        byte[] documentKey)
    {
        if (newRow is not null)
            ValidateUniqueIndexesLocked(schema, oldRow, newRow);

        foreach (var indexEntry in oldRow is null ? [] : BuildIndexEntries(schema, oldRow))
            _keyspace.Delete(indexEntry.Key);
        if (oldRow is not null)
        {
            foreach (var index in schema.FullTextIndexes)
                OpenFullTextStoreLocked(index, rebuildIfMissing: false).Delete(oldRow.Id);
        }

        if (newRow is null)
        {
            _keyspace.Delete(documentKey);
            return;
        }

        _keyspace.Put(documentKey, Encoding.UTF8.GetBytes(newRow.Json));
        foreach (var indexEntry in BuildIndexEntries(schema, newRow))
            _keyspace.Put(indexEntry.Key, indexEntry.Value);
        foreach (var index in schema.FullTextIndexes)
            OpenFullTextStoreLocked(index, rebuildIfMissing: false).Upsert(newRow);
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

    private void ValidateUniqueIndexesLocked(DocumentCollectionSchema schema, DocumentRow? oldRow, DocumentRow newRow)
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
            byte[]? existing = _keyspace.Get(key);
            if (existing is null)
                continue;

            string existingId = DocumentIndexCodec.DecodeIndexEntryValue(existing);
            if (oldRow is not null && string.Equals(existingId, oldRow.Id, StringComparison.Ordinal))
                continue;

            throw new InvalidOperationException($"文档集合 '{schema.Name}' 的唯一索引 '{index.Name}' 冲突。");
        }
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

        foreach (var row in expired)
            ApplyMutationLocked(_schema, row, newRow: null, DocumentIndexCodec.EncodeDocumentKey(row.Id));
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

    private DocumentUpdateResult ApplyUpdateRowsLocked(IReadOnlyList<DocumentRow> rows, DocumentUpdate update)
    {
        int modified = 0;
        var schema = _schema;
        foreach (var row in rows)
        {
            string updated = DocumentUpdateExecutor.Apply(row.Json, update);
            if (string.Equals(row.Json, updated, StringComparison.Ordinal))
                continue;

            ApplyMutationLocked(
                schema,
                row,
                new DocumentRow(row.Id, updated, Version: 0),
                DocumentIndexCodec.EncodeDocumentKey(row.Id));
            modified++;
        }

        return new DocumentUpdateResult(rows.Count, modified, Inserted: 0);
    }

    private DocumentUpdateResult UpsertFromUpdateLocked(
        DocumentFilter? filter,
        DocumentUpdate update,
        bool upsert,
        string? upsertId)
    {
        if (!upsert)
            return new DocumentUpdateResult(Matched: 0, Modified: 0, Inserted: 0);

        string id = upsertId ?? DocumentUpdateExecutor.TryInferUpsertId(filter)
            ?? throw new InvalidOperationException("document update upsert 需要提供 upsertId 或 _id/id 等值过滤条件。");
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        byte[] documentKey = DocumentIndexCodec.EncodeDocumentKey(id);
        if (TryGetByDocumentKeyLocked(documentKey) is not null)
            throw new InvalidOperationException($"document update upsertId '{id}' 已存在，但过滤条件未匹配该文档。");

        string seed = DocumentUpdateExecutor.BuildUpsertSeed(filter);
        string updated = DocumentUpdateExecutor.Apply(seed, update);
        ApplyMutationLocked(
            _schema,
            oldRow: null,
            new DocumentRow(id, updated, Version: 0),
            documentKey);
        return new DocumentUpdateResult(Matched: 0, Modified: 0, Inserted: 1);
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

    private sealed record IndexEntry(DocumentPathIndex Index, byte[] Key, byte[] Value);
}
