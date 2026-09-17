using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Cloud.Unum.USearch;
using Microsoft.Extensions.Logging;
using SonnetDB.Documents;
using SonnetDB.Exceptions;
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
        out string? error,
        CancellationToken cancellationToken = default,
        Action? visitRebuildCandidate = null)
    {
        hits = [];
        if (!TryGetOrCreate(database, store, dimensions, out var index, out error, cancellationToken, visitRebuildCandidate))
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

    public bool TrySearchFiltered(
        string database,
        DocumentCollectionStore store,
        int dimensions,
        float[] query,
        int topK,
        IReadOnlySet<string> allowedIds,
        int candidateLimit,
        CancellationToken cancellationToken,
        out IReadOnlyList<(string Id, double Distance)> hits,
        out bool requiresExactFallback,
        out string? error,
        Action? visitRebuildCandidate = null)
    {
        hits = [];
        requiresExactFallback = false;
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryGetOrCreate(database, store, dimensions, out var index, out error, cancellationToken, visitRebuildCandidate))
            return false;

        try
        {
            hits = index.SearchFiltered(query, topK, allowedIds, candidateLimit, cancellationToken, out requiresExactFallback);
            return true;
        }
        catch (Exception ex) when (IsNativeFailure(ex))
        {
            Disable(ex);
            error = _runtimeFailure;
            return false;
        }
    }

    private bool TryGetOrCreate(
        string database,
        DocumentCollectionStore store,
        int dimensions,
        out USearchSemanticIndex index,
        out string? error,
        CancellationToken cancellationToken = default,
        Action? visitRebuildCandidate = null)
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
            for (int attempt = 0; attempt < 8; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = _indexes.GetOrAdd(
                    database,
                    _ => new IndexEntry(store, dimensions));
                if (ReferenceEquals(entry.Store, store))
                {
                    index = entry.GetValue(cancellationToken, visitRebuildCandidate);
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
                index = replacement.GetValue(cancellationToken, visitRebuildCandidate);
                return true;
            }
            error = "USearch 数据库实例持续变化，无法稳定创建派生索引。";
            return false;
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
        private readonly object _sync = new();
        private readonly int _dimensions;
        private USearchSemanticIndex? _index;
        private bool _disposed;

        public IndexEntry(DocumentCollectionStore store, int dimensions)
        {
            Store = store;
            _dimensions = dimensions;
        }

        public DocumentCollectionStore Store { get; }

        public USearchSemanticIndex GetValue(CancellationToken cancellationToken, Action? visitCandidate)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // 取消/预算异常不缓存为永久失败，后续查询可重新恢复派生索引。
                return _index ??= USearchSemanticIndex.Create(Store, _dimensions, cancellationToken, visitCandidate);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _index?.Dispose();
            }
        }
    }

    private sealed class USearchSemanticIndex : IDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, ulong> _keysById = new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, string> _idsByKey = [];
        private readonly USearchNativeIndex _index;

        private USearchSemanticIndex(int dimensions)
        {
            _index = new USearchNativeIndex(dimensions);
        }

        public static USearchSemanticIndex Create(
            DocumentCollectionStore store, int dimensions, CancellationToken cancellationToken, Action? visitCandidate)
        {
            var index = new USearchSemanticIndex(dimensions);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (!cancellationToken.CanBeCanceled)
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                string? afterId = null;
                int scanned = 0;
                // 写入触发重建也有独立上限；查询触发时还扣减共享查询扫描预算。
                for (int pageNumber = 0; pageNumber < 39_063; pageNumber++)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    var page = store.ScanAfter(afterId, 256);
                    foreach (var row in page)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        if (++scanned > 10_000_000)
                            throw new SemanticSearchBudgetExceededException("USearch 派生索引重建超过 10000000 行预算。");
                        visitCandidate?.Invoke();
                        var document = System.Text.Json.JsonSerializer.Deserialize(
                            row.Json, ServerJsonContext.Default.SemanticImageDocument);
                        if (document is not null && document.Embedding.Length == dimensions)
                            index.Upsert(document.Id, document.Embedding);
                    }
                    if (page.Count < 256)
                        return index;
                    afterId = page[^1].Id;
                }
                throw new SemanticSearchBudgetExceededException("USearch 派生索引重建超过分页预算。");
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

        public IReadOnlyList<(string Id, double Distance)> SearchFiltered(
            float[] query,
            int topK,
            IReadOnlySet<string> allowedIds,
            int candidateLimit,
            CancellationToken cancellationToken,
            out bool requiresExactFallback)
        {
            lock (_sync)
            {
                requiresExactFallback = false;
                if (allowedIds.Count == 0)
                    return [];
                var allowedKeys = new HashSet<ulong>();
                foreach (string id in allowedIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!_keysById.TryGetValue(id, out ulong key))
                    {
                        // 派生索引缺少允许的权威 ID 时不能把较短结果宣称为完整 filtered top-K。
                        requiresExactFallback = true;
                        return [];
                    }
                    allowedKeys.Add(key);
                }

                int take = Math.Min(topK, allowedKeys.Count);
                int count = _index.SearchFiltered(query, take, allowedKeys, candidateLimit, cancellationToken,
                    out ulong[] keys, out float[] distances, out bool budgetExceeded);
                requiresExactFallback = budgetExceeded || count < take;
                if (requiresExactFallback)
                    return [];
                var hits = new List<(string Id, double Distance)>(count);
                for (int i = 0; i < count; i++)
                    hits.Add((_idsByKey[keys[i]], distances[i]));
                return hits;
            }
        }

        private ulong AllocateKey(string id)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(id));
            ulong key = BinaryPrimitives.ReadUInt64LittleEndian(hash);
            for (int attempt = 0; attempt < 32; attempt++)
            {
                if (!_idsByKey.TryGetValue(key, out string? existingId)
                    || string.Equals(existingId, id, StringComparison.Ordinal))
                {
                    _keysById[id] = key;
                    _idsByKey[key] = id;
                    return key;
                }
                key = unchecked(key + 1);
            }
            throw new USearchException("USearch 业务 ID 哈希冲突次数超过预算。");
        }
    }
}
