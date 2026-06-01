using System.Text;
using System.Text.Json;
using SonnetDB.Kv;

namespace SonnetDB.Documents;

/// <summary>
/// 单个 JSON 文档集合的 KV-backed 主数据与 path 索引存储。
/// </summary>
public sealed class DocumentCollectionStore : IDisposable
{
    private readonly object _sync = new();
    private readonly KvKeyspace _keyspace;
    private DocumentCollectionSchema _schema;

    internal DocumentCollectionStore(DocumentCollectionSchema schema, KvKeyspace keyspace)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keyspace);
        _schema = schema;
        _keyspace = keyspace;
        RebuildIndexesLocked();
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
    /// 扫描当前集合的所有文档，按文档 ID 字节序升序返回。
    /// </summary>
    /// <param name="limit">最多返回行数。</param>
    public IReadOnlyList<DocumentRow> Scan(int? limit = null)
    {
        lock (_sync)
        {
            var entries = _keyspace.ScanPrefix(new byte[] { (byte)'d' }, limit ?? int.MaxValue);
            var rows = new List<DocumentRow>(entries.Count);
            foreach (var entry in entries)
            {
                string id = DocumentIndexCodec.DecodeIdFromDocumentKey(entry.Key);
                rows.Add(new DocumentRow(id, Encoding.UTF8.GetString(entry.Value.Span), entry.Version));
            }

            return rows;
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
            }
            catch
            {
                _schema = previous;
                RebuildIndexesLocked();
                throw;
            }
        }
    }

    /// <summary>
    /// 关闭底层 KV keyspace。
    /// </summary>
    public void Dispose() => _keyspace.Dispose();

    private DocumentRow? GetLocked(string id)
        => TryGetByDocumentKeyLocked(DocumentIndexCodec.EncodeDocumentKey(id));

    private DocumentRow? TryGetByDocumentKeyLocked(ReadOnlySpan<byte> documentKey)
    {
        byte[]? payload = _keyspace.Get(documentKey);
        if (payload is null)
            return null;

        string id = DocumentIndexCodec.DecodeIdFromDocumentKey(documentKey.ToArray());
        return new DocumentRow(id, Encoding.UTF8.GetString(payload), Version: 0);
    }

    private void ApplyMutationLocked(
        DocumentCollectionSchema schema,
        DocumentRow? oldRow,
        DocumentRow? newRow,
        byte[] documentKey)
    {
        foreach (var indexEntry in oldRow is null ? [] : BuildIndexEntries(schema, oldRow))
            _keyspace.Delete(indexEntry.Key);

        if (newRow is null)
        {
            _keyspace.Delete(documentKey);
            return;
        }

        _keyspace.Put(documentKey, Encoding.UTF8.GetBytes(newRow.Json));
        foreach (var indexEntry in BuildIndexEntries(schema, newRow))
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

    private sealed record IndexEntry(byte[] Key, byte[] Value);
}
