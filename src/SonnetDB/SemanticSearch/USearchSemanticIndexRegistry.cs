using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Cloud.Unum.USearch;
using Microsoft.Extensions.Logging;
using SonnetDB.Documents;
using SonnetDB.Json;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 管理按数据库隔离的 USearch 内存派生索引。持久化权威数据仍是 Document 向量索引。
/// </summary>
internal sealed class USearchSemanticIndexRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, IndexEntry> _indexes = new(StringComparer.Ordinal);
    private readonly ILogger<USearchSemanticIndexRegistry> _logger;
    private string? _runtimeFailure;

    public USearchSemanticIndexRegistry(ILogger<USearchSemanticIndexRegistry> logger)
    {
        _logger = logger;
    }

    public static bool IsSupportedPlatform
        => RuntimeInformation.ProcessArchitecture == Architecture.X64
            && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsMacOS();

    public string? RuntimeFailure => Volatile.Read(ref _runtimeFailure);

    public bool TryUpsert(
        string database,
        DocumentCollectionStore store,
        int dimensions,
        string id,
        float[] embedding,
        out string? error)
    {
        if (!TryGetOrCreate(database, store, dimensions, out var index, out error))
            return false;

        try
        {
            index.Upsert(id, embedding);
            return true;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            Disable(ex);
            error = _runtimeFailure;
            return false;
        }
    }

    public bool TryRemove(
        string database,
        DocumentCollectionStore store,
        int dimensions,
        string id,
        out string? error)
    {
        if (!TryGetOrCreate(database, store, dimensions, out var index, out error))
            return false;

        try
        {
            index.Remove(id);
            return true;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            Disable(ex);
            error = _runtimeFailure;
            return false;
        }
    }

    public bool TrySearch(
        string database,
        DocumentCollectionStore store,
        int dimensions,
        float[] query,
        int topK,
        out IReadOnlyList<(string Id, double Distance)> hits,
        out string? error)
    {
        hits = [];
        if (!TryGetOrCreate(database, store, dimensions, out var index, out error))
            return false;

        try
        {
            hits = index.Search(query, topK);
            return true;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            Disable(ex);
            error = _runtimeFailure;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var index in _indexes.Values)
            index.Dispose();
        _indexes.Clear();
    }

    private bool TryGetOrCreate(
        string database,
        DocumentCollectionStore store,
        int dimensions,
        out USearchSemanticIndex index,
        out string? error)
    {
        index = null!;
        error = RuntimeFailure;
        if (error is not null)
            return false;
        if (!IsSupportedPlatform)
        {
            error = "Cloud.Unum.USearch 2.26.0 没有当前 OS/CPU 的原生资产。";
            return false;
        }

        try
        {
            while (true)
            {
                var entry = _indexes.GetOrAdd(
                    database,
                    _ => new IndexEntry(store, dimensions));
                if (ReferenceEquals(entry.Store, store))
                {
                    index = entry.Value;
                    return true;
                }

                // 同名数据库删除后可重新创建；store 实例变化时必须丢弃旧库的派生向量。
                var replacement = new IndexEntry(store, dimensions);
                if (!_indexes.TryUpdate(database, replacement, entry))
                {
                    replacement.Dispose();
                    continue;
                }

                entry.Dispose();
                index = replacement.Value;
                return true;
            }
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            Disable(ex);
            error = _runtimeFailure;
            return false;
        }
    }

    private void Disable(Exception exception)
    {
        string reason = $"USearch 原生后端不可用：{exception.GetBaseException().Message}";
        if (Interlocked.CompareExchange(ref _runtimeFailure, reason, null) is null)
            _logger.LogWarning(exception, "USearch backend disabled; semantic image search will use managed HNSW when fallback is enabled.");
    }

    private static bool IsNativeFailure(Exception exception)
        => exception is DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or TypeInitializationException
            or USearchException;

    private sealed class IndexEntry : IDisposable
    {
        private readonly Lazy<USearchSemanticIndex> _index;

        public IndexEntry(DocumentCollectionStore store, int dimensions)
        {
            Store = store;
            _index = new Lazy<USearchSemanticIndex>(
                () => USearchSemanticIndex.Create(store, dimensions),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public DocumentCollectionStore Store { get; }

        public USearchSemanticIndex Value => _index.Value;

        public void Dispose()
        {
            if (_index.IsValueCreated)
                _index.Value.Dispose();
        }
    }

    private sealed class USearchSemanticIndex : IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ulong> _keysById = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, string> _idsByKey = [];
        private readonly USearchIndex _index;

        private USearchSemanticIndex(int dimensions)
        {
            _index = new USearchIndex(
                MetricKind.Cos,
                ScalarKind.Float32,
                checked((ulong)dimensions),
                connectivity: 16,
                expansionAdd: 200,
                expansionSearch: 64);
        }

        public static USearchSemanticIndex Create(DocumentCollectionStore store, int dimensions)
        {
            var index = new USearchSemanticIndex(dimensions);
            try
            {
                foreach (var row in store.Scan())
                {
                    var document = System.Text.Json.JsonSerializer.Deserialize(
                        row.Json,
                        ServerJsonContext.Default.SemanticImageDocument);
                    if (document is not null && document.Embedding.Length == dimensions)
                        index.Upsert(document.Id, document.Embedding);
                }

                return index;
            }
            catch
            {
                index.Dispose();
                throw;
            }
        }

        public void Upsert(string id, float[] embedding)
        {
            lock (_sync)
            {
                if (_keysById.TryGetValue(id, out ulong existingKey))
                    _index.Remove(existingKey);
                else
                    existingKey = AllocateKey(id);

                _index.Add(existingKey, embedding);
            }
        }

        public void Remove(string id)
        {
            lock (_sync)
            {
                if (!_keysById.Remove(id, out ulong key))
                    return;
                _idsByKey.Remove(key);
                _index.Remove(key);
            }
        }

        public IReadOnlyList<(string Id, double Distance)> Search(float[] query, int topK)
        {
            lock (_sync)
            {
                int matches = _index.Search(query, topK, out ulong[] keys, out float[] distances);
                var result = new List<(string Id, double Distance)>(matches);
                for (int i = 0; i < matches; i++)
                {
                    if (_idsByKey.TryGetValue(keys[i], out string? id))
                        result.Add((id, distances[i]));
                }
                return result;
            }
        }

        public void Dispose()
        {
            lock (_sync)
                _index.Dispose();
        }

        private ulong AllocateKey(string id)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(hash);
            while (_idsByKey.TryGetValue(key, out string? existingId)
                   && !string.Equals(existingId, id, StringComparison.Ordinal))
            {
                key++;
            }

            _keysById[id] = key;
            _idsByKey[key] = id;
            return key;
        }
    }
}
