using System.Runtime.InteropServices;
using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Core;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.DiskAnn;

/// <summary>
/// Vamana / DiskANN 单层图索引（内存版，M12.1 / M12.2）。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// <para>
/// 算法实现参考 Subramanya et al. (2019) "DiskANN" 及其官方开源实现：
/// <list type="bullet">
///   <item>单层图：所有节点位于同一层，每节点最多 <see cref="VamanaOptions.MaxDegree"/>(R) 个邻居。</item>
///   <item>插入：从入口点出发的 BeamSearch（候选列表大小 L）收集访问集 V，
///         RobustPrune(p, V, alpha, R) 得到 N(p)；对每个 j ∈ N(p) 反向链接，超 R 时再次 RobustPrune。</item>
///   <item>RobustPrune：按距离升序贪心保留 p*，剔除被 alpha · d(p*, v) ≤ d(p, v) 占据的候选。</item>
///   <item>搜索：从入口点 BeamSearch，max(L, topK) 候选收敛后取 top-K。</item>
/// </list>
/// </para>
/// <para>
/// <b>线程安全</b>：使用 <see cref="ReaderWriterLockSlim"/>（多读单写），插入 / 删除互斥，搜索可并行。
/// </para>
/// <para>
/// <b>删除语义</b>：M12.2 仅支持 tombstone-based 删除（节点保留在图中保证连通性，搜索时跳过）。
/// </para>
/// <para>
/// <b>持久化</b>：M12.2 仅在内存中工作；持久化结构由 <see cref="SonnetDB.Vector.Format.VamanaFileHeader"/>
/// 与 <see cref="SonnetDB.Vector.Format.VamanaNodeHeader"/> 预留，将在 M12.3 实现。
/// </para>
/// </remarks>
public sealed class VamanaIndex<TKey> : IIndex<TKey>, IDisposable
    where TKey : notnull
{
    private readonly int _dimensions;
    private readonly Metric _metric;
    private readonly bool _largerBetter;
    private readonly VamanaOptions _options;
    private readonly Random _rng;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly List<float> _vectors;
    private readonly List<TKey> _keys;
    private readonly Dictionary<TKey, int> _keyToRow;
    private readonly List<List<int>> _neighbors;
    private readonly HashSet<int> _tombstones;

    private int _entryPoint = -1;
    private bool _disposed;

    /// <summary>初始化 <see cref="VamanaIndex{TKey}"/> 的新实例。</summary>
    /// <param name="dimensions">向量维度，必须 &gt; 0。</param>
    /// <param name="metric">距离度量类型，<see cref="Metric.Hamming"/> 暂不支持。</param>
    /// <param name="options">Vamana 参数；为 <see langword="null"/> 时使用 <see cref="VamanaOptions.Default"/>。</param>
    /// <param name="initialCapacity">初始容量预留，默认 0。</param>
    /// <param name="keyComparer">键比较器（可选）。</param>
    public VamanaIndex(
        int dimensions,
        Metric metric,
        VamanaOptions? options = null,
        int initialCapacity = 0,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (metric == Metric.Hamming)
        {
            throw new NotSupportedException(
                "VamanaIndex 当前不支持 Hamming 度量；二值向量索引计划在后续 milestone 实现。");
        }

        _dimensions = dimensions;
        _metric = metric;
        _largerBetter = metric.IsLargerBetter();
        _options = options ?? VamanaOptions.Default;
        _options.Validate();
        _rng = _options.Seed.HasValue ? new Random(_options.Seed.Value) : new Random();

        _vectors = new List<float>(checked(initialCapacity * dimensions));
        _keys = new List<TKey>(initialCapacity);
        _keyToRow = new Dictionary<TKey, int>(initialCapacity, keyComparer);
        _neighbors = new List<List<int>>(initialCapacity);
        _tombstones = new HashSet<int>();
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <summary>距离度量类型。</summary>
    public Metric Metric => _metric;

    /// <summary>当前生效的 Vamana 参数。</summary>
    public VamanaOptions Options => _options;

    /// <inheritdoc />
    public long Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _keys.Count - _tombstones.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>检查给定主键是否存在于索引中（已删除的视为不存在）。</summary>
    public bool ContainsKey(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterReadLock();
        try { return _keyToRow.ContainsKey(key); }
        finally { _lock.ExitReadLock(); }
    }

    /// <inheritdoc />
    public void Add(TKey key, ReadOnlySpan<float> vector)
    {
        ArgumentNullException.ThrowIfNull(key);
        EnsureDimension(vector.Length);
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            if (_keyToRow.ContainsKey(key))
            {
                throw new ArgumentException($"键 '{key}' 已存在于索引中。", nameof(key));
            }

            int row = _keys.Count;
            _keys.Add(key);
            _vectors.AddRange(vector);
            _keyToRow.Add(key, row);
            _neighbors.Add(new List<int>(_options.MaxDegree + 1));

            if (_entryPoint < 0)
            {
                _entryPoint = row;
                return;
            }

            // 1) BeamSearch 收集 L 个候选作为剪枝输入。
            int L = Math.Max(_options.SearchListSize, _options.MaxDegree);
            var visited = new List<(int Id, float Dist)>(L * 2);
            BeamSearch(vector, _entryPoint, L, visited, includeVisited: true);

            // 2) RobustPrune 得到节点 row 的最终邻居集合。
            List<int> selected = RobustPrune(row, visited, _options.MaxDegree, _options.Alpha);
            _neighbors[row].AddRange(selected);

            // 3) 反向链接：对每个 j ∈ N(row) 添加 row；越界时再次 RobustPrune(j)。
            ReadOnlySpan<float> rowVec = Vec(row);
            for (int i = 0; i < selected.Count; i++)
            {
                int j = selected[i];
                List<int> nbrJ = _neighbors[j];
                if (nbrJ.Contains(row))
                {
                    continue;
                }
                nbrJ.Add(row);
                if (nbrJ.Count > _options.MaxDegree)
                {
                    // 以 j 为焦点对其当前邻居做 RobustPrune。
                    var cand = new List<(int Id, float Dist)>(nbrJ.Count);
                    float[] focal = Vec(j).ToArray();
                    for (int k = 0; k < nbrJ.Count; k++)
                    {
                        int n = nbrJ[k];
                        if (n == j) { continue; }
                        cand.Add((n, InternalDist(focal, Vec(n))));
                    }
                    List<int> pruned = RobustPruneFromFocal(focal, j, cand, _options.MaxDegree, _options.Alpha);
                    nbrJ.Clear();
                    nbrJ.AddRange(pruned);
                }
            }
            _ = rowVec;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public bool Remove(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            if (!_keyToRow.Remove(key, out int row))
            {
                return false;
            }
            _tombstones.Add(row);
            return true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    public int Search(ReadOnlySpan<float> query, int topK, Span<(TKey Key, float Score)> results)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        EnsureDimension(query.Length);
        if (results.Length < topK)
        {
            throw new ArgumentException(
                $"results 缓冲区过小：需要 ≥ {topK}，实际 {results.Length}。",
                nameof(results));
        }
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            if (_entryPoint < 0 || _keys.Count == _tombstones.Count)
            {
                return 0;
            }

            int L = Math.Max(_options.SearchListSize, topK);
            var visited = new List<(int Id, float Dist)>(L);
            BeamSearch(query, _entryPoint, L, visited, includeVisited: false);

            // 升序：距离越小越近（InternalDist 已对 IP 取负）。
            visited.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));

            int written = 0;
            for (int i = 0; i < visited.Count && written < topK; i++)
            {
                int row = visited[i].Id;
                if (_tombstones.Contains(row)) { continue; }
                ReadOnlySpan<float> v = Vec(row);
                float score = Distance.Compute(query, v, _metric);
                results[written++] = (_keys[row], score);
            }
            return written;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 仅在指定的候选键集合内执行 Top-K 搜索（M12.4 — 与 M11 ScalarIndex pre-filter 联动）。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量。</param>
    /// <param name="candidateKeys">候选键集合；不在索引或已被 tombstone 的键会被静默忽略。</param>
    /// <param name="results">结果缓冲区，长度 ≥ <paramref name="topK"/>。</param>
    /// <returns>实际写入 <paramref name="results"/> 的结果数（≤ <paramref name="topK"/>）。</returns>
    /// <remarks>
    /// <para>
    /// 当前实现在候选集合上做精确扫描（与 <see cref="SonnetDB.Vector.Index.Flat.FlatIndex{TKey}.SearchSubset"/>
    /// 行为一致），保证 100% 召回。这适用于绝大多数标量过滤场景（pre-filter 选择率
    /// 较高时候选集远小于全集，距离计算总开销低于一次 BeamSearch + post-filter）。
    /// </para>
    /// <para>
    /// 大候选集（接近全集）时若需要进一步加速，可在后续 milestone 把图导航
    /// 与候选集投影结合（DiskANN-Filter 风格的 FilteredBeamSearch）。
    /// </para>
    /// </remarks>
    public int SearchSubset(
        ReadOnlySpan<float> query,
        int topK,
        IReadOnlyCollection<TKey> candidateKeys,
        Span<(TKey Key, float Score)> results)
    {
        ArgumentNullException.ThrowIfNull(candidateKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        EnsureDimension(query.Length);
        if (results.Length < topK)
        {
            throw new ArgumentException(
                $"results 缓冲区过小：需要 ≥ {topK}，实际 {results.Length}。",
                nameof(results));
        }
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            if (_keys.Count == 0 || candidateKeys.Count == 0)
            {
                return 0;
            }

            ReadOnlySpan<float> storage = CollectionsMarshal.AsSpan(_vectors);
            int effectiveK = Math.Min(topK, candidateKeys.Count);
            // 注意：内部统一以 InternalDist（"越小越近"）为优先级，最终再换算回原始 score。
            var heap = new PriorityQueue<int, float>(effectiveK);

            foreach (TKey key in candidateKeys)
            {
                if (!_keyToRow.TryGetValue(key, out int row))
                {
                    continue;
                }
                if (_tombstones.Contains(row))
                {
                    continue;
                }
                ReadOnlySpan<float> v = storage.Slice(row * _dimensions, _dimensions);
                float internalDist = InternalDist(query, v);
                // 堆顶为"当前 K-best 中最差者"，最差 = 内部距离最大者。
                // 这里用负距离把最大堆变成最小堆。
                float priority = -internalDist;
                if (heap.Count < effectiveK)
                {
                    heap.Enqueue(row, priority);
                }
                else
                {
                    heap.EnqueueDequeue(row, priority);
                }
            }

            int written = heap.Count;
            for (int i = written - 1; i >= 0; i--)
            {
                int row = heap.Dequeue();
                ReadOnlySpan<float> v = storage.Slice(row * _dimensions, _dimensions);
                float score = Distance.Compute(query, v, _metric);
                results[i] = (_keys[row], score);
            }
            return written;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lock.Dispose();
    }

    // -------- M12.3：持久化快照 / 恢复 --------

    /// <summary>
    /// 在持有读锁的前提下导出图的完整快照，供 <see cref="SonnetDB.Vector.Storage.PersistentDirectory"/>
    /// 在 Flush 时写入 <c>vamana.bin</c>。
    /// </summary>
    /// <param name="keys">输出：所有行的键序列（按 row 升序）。</param>
    /// <param name="vectors">输出：行优先连续向量缓冲，长度 = keys.Count * <see cref="Dimensions"/>。</param>
    /// <param name="entryPoint">输出：入口点 row，未初始化时为 -1。</param>
    /// <param name="neighbors">输出：每行邻居 row 列表的副本（顺序保留）。</param>
    /// <param name="tombstones">输出：被 tombstone 的 row 集合副本。</param>
    internal void Snapshot(
        out List<TKey> keys,
        out float[] vectors,
        out int entryPoint,
        out List<int[]> neighbors,
        out HashSet<int> tombstones)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            keys = new List<TKey>(_keys);
            vectors = new float[_keys.Count * _dimensions];
            CollectionsMarshal.AsSpan(_vectors).CopyTo(vectors);
            entryPoint = _entryPoint;
            neighbors = new List<int[]>(_neighbors.Count);
            for (int i = 0; i < _neighbors.Count; i++)
            {
                neighbors.Add(_neighbors[i].ToArray());
            }
            tombstones = new HashSet<int>(_tombstones);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 从持久化数据批量恢复索引；调用前索引必须为空（否则抛出）。
    /// </summary>
    /// <param name="keys">键序列。</param>
    /// <param name="vectors">行优先向量缓冲，长度 = keys.Count * <see cref="Dimensions"/>。</param>
    /// <param name="entryPoint">入口点 row（-1 表示空索引）。</param>
    /// <param name="neighbors">每行邻居 row 列表（顺序保留，长度需等于 keys.Count）。</param>
    /// <param name="tombstones">tombstone row 集合。</param>
    internal void RestoreBulk(
        IReadOnlyList<TKey> keys,
        ReadOnlySpan<float> vectors,
        int entryPoint,
        IReadOnlyList<int[]> neighbors,
        IReadOnlyCollection<int> tombstones)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(neighbors);
        ArgumentNullException.ThrowIfNull(tombstones);
        ThrowIfDisposed();

        if (vectors.Length != keys.Count * _dimensions)
        {
            throw new ArgumentException(
                $"vectors 长度 {vectors.Length} 与 keys.Count * Dimensions = {keys.Count * _dimensions} 不一致。",
                nameof(vectors));
        }
        if (neighbors.Count != keys.Count)
        {
            throw new ArgumentException(
                $"neighbors 长度 {neighbors.Count} 与 keys.Count {keys.Count} 不一致。",
                nameof(neighbors));
        }

        _lock.EnterWriteLock();
        try
        {
            if (_keys.Count > 0)
            {
                throw new InvalidOperationException("RestoreBulk 仅可在空 VamanaIndex 上调用。");
            }

            _vectors.Capacity = Math.Max(_vectors.Capacity, vectors.Length);
            for (int i = 0; i < vectors.Length; i++)
            {
                _vectors.Add(vectors[i]);
            }
            for (int row = 0; row < keys.Count; row++)
            {
                TKey key = keys[row];
                _keys.Add(key);
                if (!_keyToRow.TryAdd(key, row))
                {
                    throw new InvalidOperationException($"恢复阶段发现重复键：'{key}'。");
                }
                int[] src = neighbors[row];
                List<int> nbr = new(src.Length);
                nbr.AddRange(src);
                _neighbors.Add(nbr);
            }
            foreach (int t in tombstones)
            {
                if (t < 0 || t >= _keys.Count)
                {
                    throw new InvalidOperationException($"tombstone row {t} 越界。");
                }
                _tombstones.Add(t);
                _keyToRow.Remove(_keys[t]);
            }
            if (entryPoint >= _keys.Count || entryPoint < -1)
            {
                throw new InvalidOperationException($"entryPoint {entryPoint} 越界。");
            }
            _entryPoint = entryPoint;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    // -------- internals --------

    private void EnsureDimension(int actual)
    {
        if (actual != _dimensions)
        {
            throw new ArgumentException(
                $"向量维度不匹配：期望 {_dimensions}，实际 {actual}。",
                nameof(actual));
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private ReadOnlySpan<float> Vec(int row)
        => CollectionsMarshal.AsSpan(_vectors).Slice(row * _dimensions, _dimensions);

    /// <summary>"越小越近"的内部距离（对 InnerProduct 取负）。</summary>
    private float InternalDist(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float s = Distance.Compute(a, b, _metric);
        return _largerBetter ? -s : s;
    }

    /// <summary>
    /// Vamana BeamSearch：从 <paramref name="entry"/> 出发用 L-best 候选集贪心扩展。
    /// </summary>
    /// <param name="q">查询向量。</param>
    /// <param name="entry">入口点 row。</param>
    /// <param name="L">候选列表大小。</param>
    /// <param name="result">输出候选 (id, internalDist)。当 <paramref name="includeVisited"/> 为 true
    /// 时返回所有扩展过的节点（用于 RobustPrune 的 V 集合）；否则仅返回最终 L-best。</param>
    /// <param name="includeVisited">是否在结果中包含整个访问集 V。</param>
    private void BeamSearch(
        ReadOnlySpan<float> q,
        int entry,
        int L,
        List<(int Id, float Dist)> result,
        bool includeVisited)
    {
        var visited = new HashSet<int>(L * 4);
        // 候选最小堆：堆顶 = 距 q 最近且未扩展节点。
        var candidates = new PriorityQueue<int, float>(L);
        // L-best 最大堆（用负距离实现）：堆顶 = 当前 L-best 中距 q 最远者。
        var best = new PriorityQueue<int, float>(L);
        // 访问集（仅当 includeVisited=true 时记录）。
        List<(int Id, float Dist)>? visitedList = includeVisited
            ? new List<(int Id, float Dist)>(L * 2)
            : null;

        float ed = InternalDist(q, Vec(entry));
        visited.Add(entry);
        candidates.Enqueue(entry, ed);
        best.Enqueue(entry, -ed);
        visitedList?.Add((entry, ed));

        while (candidates.Count > 0)
        {
            candidates.TryPeek(out _, out float curDist);
            best.TryPeek(out _, out float worstNeg);
            float worstBest = -worstNeg;
            if (best.Count >= L && curDist > worstBest)
            {
                break;
            }

            int curId = candidates.Dequeue();
            List<int> nbrs = _neighbors[curId];
            for (int i = 0; i < nbrs.Count; i++)
            {
                int n = nbrs[i];
                if (!visited.Add(n)) { continue; }
                float d = InternalDist(q, Vec(n));
                visitedList?.Add((n, d));

                best.TryPeek(out _, out float wNeg);
                float w = -wNeg;
                if (best.Count < L || d < w)
                {
                    candidates.Enqueue(n, d);
                    best.Enqueue(n, -d);
                    if (best.Count > L)
                    {
                        best.Dequeue();
                    }
                }
            }
        }

        result.Clear();
        if (includeVisited && visitedList is not null)
        {
            result.AddRange(visitedList);
        }
        else
        {
            while (best.TryDequeue(out int id, out float negDist))
            {
                result.Add((id, -negDist));
            }
        }
    }

    /// <summary>
    /// RobustPrune（DiskANN Algorithm 2）以 row p 自身向量为焦点。
    /// </summary>
    private List<int> RobustPrune(int p, List<(int Id, float Dist)> candidates, int R, float alpha)
    {
        // 候选中需排除 p 自身。
        for (int i = candidates.Count - 1; i >= 0; i--)
        {
            if (candidates[i].Id == p)
            {
                candidates.RemoveAt(i);
            }
        }
        // 复制 p 向量到本地数组（避免后续 Vec(j) 共享底层 List 缓冲触发重排）。
        float[] focal = Vec(p).ToArray();
        return RobustPruneFromFocal(focal, p, candidates, R, alpha);
    }

    private List<int> RobustPruneFromFocal(
        float[] focal,
        int focalId,
        List<(int Id, float Dist)> candidates,
        int R,
        float alpha)
    {
        // 升序，距 focal 最近优先。
        candidates.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));

        var result = new List<int>(Math.Min(R, candidates.Count));
        // 用 bool 数组记录候选是否被剔除，避免反复创建集合。
        bool[] pruned = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
        {
            if (pruned[i]) { continue; }
            int pStarId = candidates[i].Id;
            if (pStarId == focalId) { continue; }
            float dPFocal = candidates[i].Dist;
            result.Add(pStarId);
            if (result.Count >= R) { break; }

            ReadOnlySpan<float> pStarVec = Vec(pStarId);
            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (pruned[j]) { continue; }
                int vId = candidates[j].Id;
                float dVFocal = candidates[j].Dist;
                float dVPStar = InternalDist(pStarVec, Vec(vId));
                if (alpha * dVPStar <= dVFocal)
                {
                    pruned[j] = true;
                }
            }
            _ = dPFocal;
        }
        return result;
    }
}
