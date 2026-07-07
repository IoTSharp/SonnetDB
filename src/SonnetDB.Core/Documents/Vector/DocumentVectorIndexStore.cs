using System.Runtime.InteropServices;
using SonnetDB.Kv;
using SonnetDB.Query;
using SonnetDB.Vector.Index.Hnsw;
using SonnetDB.Vector.Model;

namespace SonnetDB.Documents.Vector;

/// <summary>
/// 文档集合向量索引的 SonnetDB-backed 派生索引：KV keyspace 持久化 id→向量、内存 HNSW 图（<see cref="HnswIndex{TKey}"/>）
/// 提供 ANN 检索。打开时从 KV 全量重建图（与全文索引 open 时重建派生态同哲学），持久的是「声明 + 向量」，
/// 图拓扑派生重建，故崩溃后随集合重开自愈。
/// </summary>
public sealed class DocumentVectorIndexStore : IDisposable
{
    private readonly object _sync = new();
    private readonly KvKeyspace _keyspace;
    private readonly DocumentVectorIndex _definition;
    private readonly JsonPath _path;
    private HnswIndex<string> _graph;

    private DocumentVectorIndexStore(KvKeyspace keyspace, DocumentVectorIndex definition)
    {
        _keyspace = keyspace;
        _definition = definition;
        _path = JsonPath.Parse(definition.Path);
        _graph = BuildGraphFromKeyspace();
    }

    /// <summary>向量索引声明。</summary>
    public DocumentVectorIndex Definition => _definition;

    /// <summary>当前索引的向量数量。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _keyspace.CountPrefix(ReadOnlySpan<byte>.Empty);
        }
    }

    /// <summary>
    /// 打开向量索引目录，从持久化的向量 KV 重建 HNSW 图。
    /// </summary>
    /// <param name="directory">向量索引持久化目录。</param>
    /// <param name="definition">向量索引声明。</param>
    /// <param name="kvOptions">底层 KV 选项。</param>
    public static DocumentVectorIndexStore Open(string directory, DocumentVectorIndex definition, KvOptions kvOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(kvOptions);

        Directory.CreateDirectory(directory);
        var keyspace = KvKeyspace.Open("docvector." + definition.Name, directory, kvOptions);
        return new DocumentVectorIndexStore(keyspace, definition);
    }

    /// <summary>
    /// 将一条文档记录写入向量索引；文档缺少向量字段或维度不匹配时视作删除该 id。
    /// </summary>
    /// <param name="row">文档记录。</param>
    public void Upsert(DocumentRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        lock (_sync)
            UpsertLocked(row);
    }

    /// <summary>
    /// 批量将文档记录写入向量索引。
    /// </summary>
    /// <param name="rows">文档记录序列。</param>
    public void UpsertMany(IEnumerable<DocumentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_sync)
        {
            foreach (var row in rows)
                UpsertLocked(row);
        }
    }

    /// <summary>
    /// 从向量索引删除一条文档。
    /// </summary>
    /// <param name="id">文档 ID。</param>
    public void Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_sync)
            DeleteLocked(id);
    }

    /// <summary>
    /// 批量从向量索引删除文档。
    /// </summary>
    /// <param name="ids">文档 ID 序列。</param>
    public void DeleteMany(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_sync)
        {
            foreach (string id in ids)
                DeleteLocked(id);
        }
    }

    /// <summary>
    /// 从文档快照重建索引：清空并按当前文档全量重建向量 KV 与 HNSW 图。
    /// </summary>
    /// <param name="rows">要重建的文档快照。</param>
    public void Rebuild(IEnumerable<DocumentRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        lock (_sync)
        {
            foreach (var entry in _keyspace.ScanPrefix(ReadOnlySpan<byte>.Empty, int.MaxValue))
                _keyspace.Delete(entry.Key.Span);

            _graph.Dispose();
            _graph = CreateEmptyGraph();
            foreach (var row in rows)
                UpsertLocked(row);
        }
    }

    /// <summary>
    /// 用 ANN 图对查询向量做近邻搜索。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="k">返回结果上限。</param>
    /// <returns>按距离升序排列的 (文档 ID, 距离) 结果。</returns>
    public IReadOnlyList<(string Id, double Distance)> Search(float[] query, int k)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (k <= 0 || query.Length != _definition.Dimensions)
            return [];

        lock (_sync)
        {
            long count = _graph.Count;
            if (count == 0)
                return [];

            int take = (int)Math.Min(k, count);
            var buffer = new (string Key, float Score)[take];
            int written = _graph.Search(query, take, buffer);
            var result = new List<(string Id, double Distance)>(written);
            for (int i = 0; i < written; i++)
                result.Add((buffer[i].Key, buffer[i].Score));
            return result;
        }
    }

    /// <summary>
    /// 关闭底层 KV keyspace 与 HNSW 图。
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            _graph.Dispose();
            _keyspace.Dispose();
        }
    }

    private void UpsertLocked(DocumentRow row)
    {
        if (!TryReadOwnedVector(row, out float[]? vector))
        {
            DeleteLocked(row.Id);
            return;
        }

        _keyspace.Put(row.Id, MemoryMarshal.AsBytes(vector.AsSpan()));
        // Add 遇重复 key 抛，故先 Remove——满足批量路径「upsert 自替换」不变式；
        // churn 下 tombstone 由 HnswOptions.AutoCompactTombstoneRatio 自动重建回收。
        _graph.Remove(row.Id);
        _graph.Add(row.Id, vector);
    }

    private void DeleteLocked(string id)
    {
        _keyspace.Delete(id);
        _graph.Remove(id);
    }

    /// <summary>
    /// 从文档读取向量：字段缺失 / JSON null 返回 false（视作未索引），维度不匹配也返回 false（不索引）。
    /// 字段存在但不是 number array 时 <see cref="DocumentVectorReader.TryReadVector"/> 抛错——由写入方（对齐
    /// 全文索引对坏字段的处理）向上传播。
    /// </summary>
    private bool TryReadOwnedVector(DocumentRow row, out float[] vector)
    {
        vector = [];
        if (!DocumentVectorReader.TryReadVector(row.Json, _path, out var parsed))
            return false;
        if (parsed.Length != _definition.Dimensions)
            return false;

        vector = parsed;
        return true;
    }

    private HnswIndex<string> BuildGraphFromKeyspace()
    {
        var graph = CreateEmptyGraph();
        int dimensions = _definition.Dimensions;
        foreach (var entry in _keyspace.ScanPrefix(ReadOnlySpan<byte>.Empty, int.MaxValue))
        {
            var valueSpan = entry.Value.Span;
            if (valueSpan.Length != dimensions * sizeof(float))
                continue;

            string id = System.Text.Encoding.UTF8.GetString(entry.Key.Span);
            var vector = MemoryMarshal.Cast<byte, float>(valueSpan).ToArray();
            graph.Remove(id);
            graph.Add(id, vector);
        }

        return graph;
    }

    private HnswIndex<string> CreateEmptyGraph()
        => new(
            _definition.Dimensions,
            ToVectorMetric(_definition.Metric),
            new HnswOptions
            {
                M = _definition.M,
                EfConstruction = _definition.EfConstruction,
                EfSearch = _definition.EfSearch,
            },
            keyComparer: StringComparer.Ordinal);

    private static Metric ToVectorMetric(KnnMetric metric)
        => metric switch
        {
            KnnMetric.L2 => Metric.L2,
            KnnMetric.InnerProduct => Metric.InnerProduct,
            _ => Metric.Cosine,
        };
}
