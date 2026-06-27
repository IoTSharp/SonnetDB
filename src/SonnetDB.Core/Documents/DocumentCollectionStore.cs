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
            return ScanRowsLocked(limit ?? int.MaxValue, skip);
        }
    }

    /// <summary>
    /// 返回当前集合的文档数量。
    /// </summary>
    /// <returns>当前集合中的文档数量。</returns>
    public int Count()
    {
        lock (_sync)
            return ScanRowsLocked(int.MaxValue).Count;
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
    {
        ArgumentNullException.ThrowIfNull(index);
        lock (_sync)
        {
            string? scalar = JsonPathEvaluator.ToIndexScalar(value);
            if (scalar is null)
                return [];

            byte[] prefix = DocumentIndexCodec.EncodeIndexPrefix(index, scalar);
            var entries = _keyspace.ScanPrefix(prefix, limit ?? int.MaxValue);
            var rows = new List<DocumentRow>(entries.Count);
            foreach (var entry in entries)
            {
                string id = DocumentIndexCodec.DecodeIndexEntryValue(entry.Value.Span);
                var row = GetLocked(id);
                if (row is not null)
                    rows.Add(row);
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
            return OpenFullTextStoreLocked(index, rebuildIfMissing: true).DocumentCount;
    }

    internal int RebuildFullTextIndex(DocumentFullTextIndex index, string indexDirectory)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(indexDirectory);
        lock (_sync)
        {
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
            var path = JsonPath.Parse(index.Path);
            if (!JsonPathEvaluator.TryResolve(document.RootElement, path, out var element))
                continue;

            object? value = element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out long longValue) ? longValue : element.GetDouble(),
                JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
                _ => null,
            };

            string? scalar = JsonPathEvaluator.ToIndexScalar(value);
            if (scalar is null)
                continue;

            entries.Add(new IndexEntry(
                DocumentIndexCodec.EncodeIndexEntryKey(index, scalar, row.Id),
                DocumentIndexCodec.EncodeIndexEntryValue(row.Id)));
        }

        return entries;
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
                _keyspace.Put(indexEntry.Key, indexEntry.Value);
        }
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

    private sealed record IndexEntry(byte[] Key, byte[] Value);
}
