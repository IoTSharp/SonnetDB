using System.Runtime.InteropServices;
using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Core;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Hnsw;

/// <summary>
/// HNSW（Hierarchical Navigable Small World）图索引。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// <para>
/// 算法实现参考 Malkov &amp; Yashunin (2016) 的论文，对应 hnswlib / FAISS 同名结构：
/// <list type="bullet">
///   <item>分层图：第 0 层包含所有节点，更高层节点按指数衰减抽样（mL = 1/ln(M)）。</item>
///   <item>插入：从顶层 entry point 贪心下降至 level+1 层，再在 level..0 各层使用
///         efConstruction 候选集做局部搜索，并采用启发式邻居选择（Algorithm 4）建立双向边。</item>
///   <item>搜索：从顶层 entry point 贪心下降至第 1 层，再在第 0 层用 ef = max(EfSearch, topK)
///         的候选集执行 K 近邻收敛。</item>
/// </list>
/// </para>
/// <para>
/// <b>线程安全</b>：使用 <see cref="ReaderWriterLockSlim"/>（多读单写），插入 / 删除互斥，搜索可并行。
/// </para>
/// <para>
/// <b>删除语义</b>：M3 仅支持 tombstone-based 删除（节点保留在图中保证连通性，搜索时跳过）。
/// 真正的图重建删除将在 M5+ 持久化阶段考虑。
/// </para>
/// <para>
/// <b>持久化</b>：M3 仅在内存中工作；持久化格式由 <see cref="SonnetDB.Vector.Format.HnswNodeHeader"/>
/// 预留，将在 M5 实现。
/// </para>
/// </remarks>
public sealed class HnswIndex<TKey> : IIndex<TKey>, IDisposable
    where TKey : notnull
{
    private readonly int _dimensions;
    private readonly Metric _metric;
    private readonly bool _largerBetter;
    private readonly HnswOptions _options;
    private readonly int _maxLayer0Connections;
    private readonly int _maxLayerNConnections;
    private readonly double _mlFactor;
    private readonly Random _rng;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly List<float> _vectors;
    private readonly List<TKey> _keys;
    private readonly Dictionary<TKey, int> _keyToRow;
    private readonly List<int> _levels;
    private readonly List<List<int>[]> _neighbors;
    private readonly HashSet<int> _tombstones;

    private int _entryPoint = -1;
    private int _entryLevel = -1;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="HnswIndex{TKey}"/> 的新实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须 &gt; 0。</param>
    /// <param name="metric">距离度量类型，<see cref="Metric.Hamming"/> 暂不支持。</param>
    /// <param name="options">HNSW 参数；为 <see langword="null"/> 时使用 <see cref="HnswOptions.Default"/>。</param>
    /// <param name="initialCapacity">初始容量预留，默认 0。</param>
    /// <param name="keyComparer">键比较器（可选）。</param>
    public HnswIndex(
        int dimensions,
        Metric metric,
        HnswOptions? options = null,
        int initialCapacity = 0,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (metric == Metric.Hamming)
        {
            throw new NotSupportedException(
                "HnswIndex 当前不支持 Hamming 度量；二值向量索引计划在 M11 实现。");
        }

        _dimensions = dimensions;
        _metric = metric;
        _largerBetter = metric.IsLargerBetter();
        _options = options ?? HnswOptions.Default;
        _options.Validate();
        _maxLayerNConnections = _options.M;
        _maxLayer0Connections = _options.M * 2;
        _mlFactor = 1.0 / Math.Log(_options.M);
        _rng = _options.Seed.HasValue ? new Random(_options.Seed.Value) : new Random();

        _vectors = new List<float>(checked(initialCapacity * dimensions));
        _keys = new List<TKey>(initialCapacity);
        _keyToRow = new Dictionary<TKey, int>(initialCapacity, keyComparer);
        _levels = new List<int>(initialCapacity);
        _neighbors = new List<List<int>[]>(initialCapacity);
        _tombstones = new HashSet<int>();
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <summary>距离度量类型。</summary>
    public Metric Metric => _metric;

    /// <summary>当前生效的 HNSW 参数。</summary>
    public HnswOptions Options => _options;

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

    /// <summary>
    /// 检查给定主键是否存在于索引中（已删除的视为不存在）。
    /// </summary>
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
            int level = SampleLevel();

            _keys.Add(key);
            _vectors.AddRange(vector);
            _keyToRow.Add(key, row);
            _levels.Add(level);

            var nodeNbrs = new List<int>[level + 1];
            for (int l = 0; l <= level; l++)
            {
                int cap = l == 0 ? _maxLayer0Connections + 1 : _maxLayerNConnections + 1;
                nodeNbrs[l] = new List<int>(cap);
            }
            _neighbors.Add(nodeNbrs);

            // 第一个节点直接成为入口点。
            if (_entryPoint < 0)
            {
                _entryPoint = row;
                _entryLevel = level;
                return;
            }

            // 1) 从顶层贪心下降到 level+1 层，找到最佳入口。
            int ep = _entryPoint;
            if (_entryLevel > level)
            {
                ep = GreedyDescend(vector, ep, _entryLevel, level);
            }

            // 2) 从 min(entryLevel, level) 层向下，每层做 efConstruction 候选搜索 + 启发式邻居选择。
            int startLayer = Math.Min(_entryLevel, level);
            var entryPoints = new List<int> { ep };
            var working = new List<(int Id, float Dist)>(_options.EfConstruction);

            for (int layer = startLayer; layer >= 0; layer--)
            {
                SearchLayerMulti(vector, entryPoints, _options.EfConstruction, layer, working);
                int maxConn = layer == 0 ? _maxLayer0Connections : _maxLayerNConnections;
                List<int> selected = SelectNeighborsHeuristic(vector, working, _options.M);

                // 双向链接，并对溢出邻居做 trim。
                for (int i = 0; i < selected.Count; i++)
                {
                    int nbr = selected[i];
                    nodeNbrs[layer].Add(nbr);
                    List<int> nbrList = _neighbors[nbr][layer];
                    nbrList.Add(row);
                    if (nbrList.Count > maxConn)
                    {
                        ShrinkConnections(nbr, layer, maxConn);
                    }
                }

                // 下一层（更低）的入口点 = 本层的所有候选（不只是 selected，提升下层连通性）。
                entryPoints = new List<int>(working.Count);
                for (int i = 0; i < working.Count; i++)
                {
                    entryPoints.Add(working[i].Id);
                }
            }

            if (level > _entryLevel)
            {
                _entryPoint = row;
                _entryLevel = level;
            }
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

            int ep = _entryPoint;
            if (_entryLevel > 0)
            {
                ep = GreedyDescend(query, ep, _entryLevel, 0);
            }

            int ef = Math.Max(_options.EfSearch, topK);
            var working = new List<(int Id, float Dist)>(ef);
            SearchLayerMulti(query, new int[] { ep }, ef, 0, working);

            // 升序排序（距离越小越近）后过滤 tombstone 取 top-K。
            working.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));

            int written = 0;
            for (int i = 0; i < working.Count && written < topK; i++)
            {
                int row = working[i].Id;
                if (_tombstones.Contains(row))
                {
                    continue;
                }
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lock.Dispose();
    }

    internal HnswIndexSnapshot<TKey> CreateSnapshot()
    {
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            var vectors = CollectionsMarshal.AsSpan(_vectors).ToArray();
            var keys = _keys.ToArray();
            var levels = _levels.ToArray();
            var tombstones = _tombstones.ToArray();
            var neighbors = new int[_neighbors.Count][][];
            for (int row = 0; row < _neighbors.Count; row++)
            {
                var rowLayers = _neighbors[row];
                neighbors[row] = new int[rowLayers.Length][];
                for (int layer = 0; layer < rowLayers.Length; layer++)
                    neighbors[row][layer] = rowLayers[layer].ToArray();
            }

            return new HnswIndexSnapshot<TKey>(
                _dimensions,
                _metric,
                _options,
                vectors,
                keys,
                levels,
                neighbors,
                tombstones,
                _entryPoint,
                _entryLevel);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    internal static HnswIndex<TKey> FromSnapshot(HnswIndexSnapshot<TKey> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Keys.Length != snapshot.Levels.Length || snapshot.Keys.Length != snapshot.Neighbors.Length)
            throw new InvalidDataException("HNSW snapshot row counts do not match.");
        if (snapshot.Vectors.Length != checked(snapshot.Keys.Length * snapshot.Dimensions))
            throw new InvalidDataException("HNSW snapshot vector length does not match metadata.");

        var index = new HnswIndex<TKey>(
            snapshot.Dimensions,
            snapshot.Metric,
            snapshot.Options,
            snapshot.Keys.Length);
        index.PopulateFromSnapshot(snapshot);
        return index;
    }

    /// <summary>
    /// 在现有空实例上原地批量恢复一份快照。要求实例自构造起未做过 <see cref="Add"/>。
    /// 用于持久化层加载时复用同一个 <see cref="Api.Collection{TKey}"/> 已经绑定的索引引用。
    /// </summary>
    internal void RestoreBulk(HnswIndexSnapshot<TKey> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();

        _lock.EnterWriteLock();
        try
        {
            if (_keys.Count != 0)
                throw new InvalidOperationException("RestoreBulk 仅可在空 HnswIndex 上调用。");
            if (snapshot.Dimensions != _dimensions)
                throw new InvalidDataException(
                    $"HNSW snapshot dimensions {snapshot.Dimensions} 与当前实例 {_dimensions} 不一致。");
            if (snapshot.Metric != _metric)
                throw new InvalidDataException(
                    $"HNSW snapshot metric {snapshot.Metric} 与当前实例 {_metric} 不一致。");
            if (snapshot.Keys.Length != snapshot.Levels.Length || snapshot.Keys.Length != snapshot.Neighbors.Length)
                throw new InvalidDataException("HNSW snapshot row counts do not match.");
            if (snapshot.Vectors.Length != checked(snapshot.Keys.Length * snapshot.Dimensions))
                throw new InvalidDataException("HNSW snapshot vector length does not match metadata.");

            PopulateFromSnapshot(snapshot);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void PopulateFromSnapshot(HnswIndexSnapshot<TKey> snapshot)
    {
        _vectors.AddRange(snapshot.Vectors);
        _keys.AddRange(snapshot.Keys);

        // 先登记 tombstone，再构建 _keyToRow 时跳过 tombstoned 行（#194→#193）：
        // 删除后重插同一 key 会让快照中该 key 出现在两行（旧 tombstoned 行 + 新活跃行），
        // 若对所有行无差别 _keyToRow.Add 会因重复 key 抛 ArgumentException 导致索引无法加载。
        // tombstoned 行的 key 不应存在于活跃映射（Remove 已将其移除），故跳过。
        foreach (int row in snapshot.Tombstones)
            _tombstones.Add(row);

        for (int row = 0; row < snapshot.Keys.Length; row++)
        {
            if (_tombstones.Contains(row))
                continue;
            // 防御：若同一活跃 key 因历史数据异常重复出现，保留最后一次（last-writer-wins），不抛。
            _keyToRow[snapshot.Keys[row]] = row;
        }

        _levels.AddRange(snapshot.Levels);
        foreach (var rowLayers in snapshot.Neighbors)
        {
            var layers = new List<int>[rowLayers.Length];
            for (int layer = 0; layer < rowLayers.Length; layer++)
                layers[layer] = new List<int>(rowLayers[layer]);
            _neighbors.Add(layers);
        }
        _entryPoint = snapshot.EntryPoint;
        _entryLevel = snapshot.EntryLevel;
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private ReadOnlySpan<float> Vec(int row)
        => CollectionsMarshal.AsSpan(_vectors).Slice(row * _dimensions, _dimensions);

    /// <summary>计算"越小越近"的内部距离（对 InnerProduct 取负）。</summary>
    private float InternalDist(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float s = Distance.Compute(a, b, _metric);
        return _largerBetter ? -s : s;
    }

    private int SampleLevel()
    {
        // level = floor(-ln(uniform(0,1)) * mL)
        double u;
        do { u = _rng.NextDouble(); } while (u <= 0.0);
        double lvl = -Math.Log(u) * _mlFactor;
        int level = (int)Math.Floor(lvl);
        // 限制最大层数避免极端值（与 HnswNodeHeader.NeighborCounts 上限一致）。
        if (level > 15) level = 15;
        if (level < 0) level = 0;
        return level;
    }

    private int GreedyDescend(
        ReadOnlySpan<float> q,
        int entryPoint,
        int fromLayer,
        int toLayerExclusive)
    {
        int curr = entryPoint;
        float currDist = InternalDist(q, Vec(curr));
        for (int layer = fromLayer; layer > toLayerExclusive; layer--)
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                List<int>[] nbrs = _neighbors[curr];
                if (layer >= nbrs.Length) { continue; }
                List<int> list = nbrs[layer];
                for (int i = 0; i < list.Count; i++)
                {
                    int n = list[i];
                    float d = InternalDist(q, Vec(n));
                    if (d < currDist)
                    {
                        currDist = d;
                        curr = n;
                        changed = true;
                    }
                }
            }
        }
        return curr;
    }

    /// <summary>
    /// 在指定层做有界 ef 候选搜索，结果以 (id, dist) 形式写入 <paramref name="result"/>。
    /// </summary>
    private void SearchLayerMulti(
        ReadOnlySpan<float> q,
        IReadOnlyList<int> entryPoints,
        int ef,
        int layer,
        List<(int Id, float Dist)> result)
    {
        var visited = new HashSet<int>(ef * 4);
        // 最小堆：堆顶 = 距离最小（最近）的候选，下一步要扩展。
        var candidates = new PriorityQueue<int, float>(ef);
        // 用负距离实现"最大堆"：堆顶 = 当前 top-ef 中最远者，便于剔除。
        var topResults = new PriorityQueue<int, float>(ef);

        foreach (int ep in entryPoints)
        {
            if (!visited.Add(ep)) { continue; }
            float d = InternalDist(q, Vec(ep));
            candidates.Enqueue(ep, d);
            topResults.Enqueue(ep, -d);
        }

        while (candidates.Count > 0)
        {
            candidates.TryPeek(out _, out float curDist);
            topResults.TryPeek(out _, out float furthestNeg);
            float furthestDist = -furthestNeg;
            if (topResults.Count >= ef && curDist > furthestDist)
            {
                break;
            }

            int curId = candidates.Dequeue();
            List<int>[] nbrs = _neighbors[curId];
            if (layer >= nbrs.Length) { continue; }
            List<int> list = nbrs[layer];
            for (int i = 0; i < list.Count; i++)
            {
                int nbr = list[i];
                if (!visited.Add(nbr)) { continue; }
                float d = InternalDist(q, Vec(nbr));
                topResults.TryPeek(out _, out float fNeg);
                float fDist = -fNeg;
                if (topResults.Count < ef || d < fDist)
                {
                    candidates.Enqueue(nbr, d);
                    topResults.Enqueue(nbr, -d);
                    if (topResults.Count > ef)
                    {
                        topResults.Dequeue();
                    }
                }
            }
        }

        result.Clear();
        while (topResults.TryDequeue(out int id, out float negDist))
        {
            result.Add((id, -negDist));
        }
    }

    /// <summary>
    /// 启发式邻居选择（Algorithm 4，extendCandidates=false，keepPrunedConnections=false 简化版）。
    /// 候选按距离升序遍历，仅当 e 到 q 的距离比 e 到任意已选邻居都更近时才被采纳。
    /// </summary>
    private List<int> SelectNeighborsHeuristic(
        ReadOnlySpan<float> q,
        List<(int Id, float Dist)> candidates,
        int m)
    {
        candidates.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
        var result = new List<int>(Math.Min(m, candidates.Count));
        for (int i = 0; i < candidates.Count; i++)
        {
            if (result.Count >= m) { break; }
            (int id, float distToQ) = candidates[i];
            ReadOnlySpan<float> idVec = Vec(id);
            bool good = true;
            for (int j = 0; j < result.Count; j++)
            {
                float distToSel = InternalDist(idVec, Vec(result[j]));
                if (distToSel < distToQ)
                {
                    good = false;
                    break;
                }
            }
            if (good)
            {
                result.Add(id);
            }
        }
        return result;
    }

    /// <summary>对节点 <paramref name="row"/> 在指定层的邻居列表做 trim，保留 max 个最优邻居。</summary>
    private void ShrinkConnections(int row, int layer, int max)
    {
        List<int> list = _neighbors[row][layer];
        if (list.Count <= max) { return; }

        // 复制焦点向量到本地数组，避免在 SelectNeighbors 内对 Span 与索引交叉调用导致重排。
        float[] focal = Vec(row).ToArray();
        var cands = new List<(int Id, float Dist)>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            int x = list[i];
            cands.Add((x, InternalDist(focal, Vec(x))));
        }
        List<int> pruned = SelectNeighborsHeuristic(focal, cands, max);
        list.Clear();
        list.AddRange(pruned);
    }
}
