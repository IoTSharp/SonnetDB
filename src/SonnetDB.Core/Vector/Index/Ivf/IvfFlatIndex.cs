using System.Runtime.InteropServices;
using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Core;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// IVF-Flat（倒排文件 + 原始 float 存储）近似最近邻索引。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// <para>
/// 算法对应 FAISS <c>IndexIVFFlat</c> / Milvus <c>IVF_FLAT</c>：
/// <list type="bullet">
///   <item>训练阶段使用 <see cref="KMeans"/> 在已有向量上聚类，得到 <c>NList</c> 个粗量化中心。</item>
///   <item>每个向量根据最近中心加入对应倒排列表（仅记录行号，向量本身仍存储于行优先连续缓冲）。</item>
///   <item>搜索阶段先按距离对所有中心排序，取最近的 <c>NProbe</c> 个列表线性扫描。</item>
///   <item>未训练时新插入的向量被缓冲；首次搜索会自动触发训练。</item>
/// </list>
/// </para>
/// <para>
/// <b>线程安全</b>：使用 <see cref="ReaderWriterLockSlim"/>，多读单写。
/// </para>
/// <para>
/// <b>持久化</b>：M4 仅在内存中工作，倒排列表持久化格式由
/// <see cref="SonnetDB.Vector.Format.IvfListHeader"/> 预留，将在 M5 实现。
/// </para>
/// </remarks>
public sealed class IvfFlatIndex<TKey> : IIndex<TKey>, IDisposable
    where TKey : notnull
{
    private readonly int _dimensions;
    private readonly Metric _metric;
    private readonly bool _largerBetter;
    // 用 init-once 语义：构造时来自调用方；RestoreBulk 可以一次性覆盖为 snapshot.Options。
    private IvfOptions _options;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly List<float> _vectors;
    private readonly List<TKey> _keys;
    private readonly Dictionary<TKey, int> _keyToRow;

    // 行号 -> 倒排列表 ID；未训练时全部为 -1。
    private readonly List<int> _rowToList;

    // 倒排列表，长度等于 _options.NList（训练完成后才分配）。
    private List<int>[]? _invertedLists;
    private float[]? _centroids;
    private bool _isTrained;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="IvfFlatIndex{TKey}"/> 的新实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须 &gt; 0。</param>
    /// <param name="metric">距离度量类型，<see cref="Metric.Hamming"/> 暂不支持。</param>
    /// <param name="options">IVF 参数；为 <see langword="null"/> 时使用 <see cref="IvfOptions.Default"/>。</param>
    /// <param name="initialCapacity">初始容量预留，默认 0。</param>
    /// <param name="keyComparer">键比较器（可选）。</param>
    public IvfFlatIndex(
        int dimensions,
        Metric metric,
        IvfOptions? options = null,
        int initialCapacity = 0,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (metric == Metric.Hamming)
        {
            throw new NotSupportedException(
                "IvfFlatIndex 当前不支持 Hamming 度量；二值向量索引计划在 M11 实现。");
        }

        _dimensions = dimensions;
        _metric = metric;
        _largerBetter = metric.IsLargerBetter();
        _options = options ?? IvfOptions.Default;
        _options.Validate();

        _vectors = new List<float>(checked(initialCapacity * dimensions));
        _keys = new List<TKey>(initialCapacity);
        _keyToRow = new Dictionary<TKey, int>(initialCapacity, keyComparer);
        _rowToList = new List<int>(initialCapacity);
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <summary>距离度量类型。</summary>
    public Metric Metric => _metric;

    /// <summary>当前生效的 IVF 参数。</summary>
    public IvfOptions Options => _options;

    /// <summary>是否已训练（聚类中心已建立）。</summary>
    public bool IsTrained
    {
        get
        {
            _lock.EnterReadLock();
            try { return _isTrained; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <inheritdoc />
    public long Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _keys.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>
    /// 主动触发训练。当前已插入的向量数必须 ≥ <see cref="IvfOptions.NList"/>。
    /// 后续新增向量会按最近中心实时分配到对应列表。
    /// </summary>
    /// <exception cref="InvalidOperationException">已训练或当前向量数不足时抛出。</exception>
    public void Train()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            TrainCore();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
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

            int listId = -1;
            if (_isTrained)
            {
                listId = KMeans.FindNearest(vector, _centroids!, _options.NList, _dimensions);
                _invertedLists![listId].Add(row);
            }
            _rowToList.Add(listId);
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

            int last = _keys.Count - 1;
            Span<float> storage = CollectionsMarshal.AsSpan(_vectors);

            int removedListId = _rowToList[row];
            if (removedListId >= 0 && _invertedLists is not null)
            {
                _invertedLists[removedListId].Remove(row);
            }

            if (row != last)
            {
                Span<float> dst = storage.Slice(row * _dimensions, _dimensions);
                Span<float> src = storage.Slice(last * _dimensions, _dimensions);
                src.CopyTo(dst);

                TKey movedKey = _keys[last];
                _keys[row] = movedKey;
                _keyToRow[movedKey] = row;

                int movedList = _rowToList[last];
                _rowToList[row] = movedList;
                if (movedList >= 0 && _invertedLists is not null)
                {
                    // 将 last 的引用更新为 row
                    var bucket = _invertedLists[movedList];
                    int idx = bucket.IndexOf(last);
                    if (idx >= 0) { bucket[idx] = row; }
                }
            }

            _keys.RemoveAt(last);
            _vectors.RemoveRange(last * _dimensions, _dimensions);
            _rowToList.RemoveAt(last);
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
        ThrowIfDisposed();
        if (results.Length < topK)
        {
            throw new ArgumentException(
                $"results 长度（{results.Length}）小于 topK（{topK}）。",
                nameof(results));
        }

        // 搜索路径：若未训练且有向量则升级到写锁触发自动训练。
        _lock.EnterUpgradeableReadLock();
        try
        {
            if (!_isTrained && _keys.Count > 0)
            {
                _lock.EnterWriteLock();
                try { TrainCore(); }
                finally { _lock.ExitWriteLock(); }
            }

            if (_keys.Count == 0)
            {
                return 0;
            }

            return SearchCore(query, topK, results);
        }
        finally
        {
            _lock.ExitUpgradeableReadLock();
        }
    }

    private int SearchCore(ReadOnlySpan<float> query, int topK, Span<(TKey Key, float Score)> results)
    {
        ReadOnlySpan<float> centroids = _centroids!;
        List<int>[] lists = _invertedLists!;
        int nList = _options.NList;
        int nProbe = Math.Min(_options.NProbe, nList);

        // 1) 选出与 query 最相似的 nProbe 个粗量化中心。
        //    PriorityQueue<int,float> 是小顶堆。保留 K 个"最佳"候选 = 让"最差"候选位于堆顶。
        //      smallerBetter：priority = -score（堆顶 = 最大 score = 最差）
        //      largerBetter ：priority = +score（堆顶 = 最小 score = 最差）
        var centroidHeap = new PriorityQueue<int, float>(nProbe);
        for (int c = 0; c < nList; c++)
        {
            float score = ComputeScore(query, centroids.Slice(c * _dimensions, _dimensions));
            float proxy = _largerBetter ? score : -score;
            if (centroidHeap.Count < nProbe)
            {
                centroidHeap.Enqueue(c, proxy);
            }
            else
            {
                centroidHeap.EnqueueDequeue(c, proxy);
            }
        }

        Span<int> probedLists = stackalloc int[nProbe];
        int probedCount = 0;
        while (centroidHeap.TryDequeue(out int listId, out _))
        {
            probedLists[probedCount++] = listId;
        }

        // 2) 在被探测的列表内做线性扫描，维护 top-K。
        ReadOnlySpan<float> storage = CollectionsMarshal.AsSpan(_vectors);
        int effectiveK = Math.Min(topK, _keys.Count);
        var kHeap = new PriorityQueue<int, float>(effectiveK);

        for (int p = 0; p < probedCount; p++)
        {
            List<int> bucket = lists[probedLists[p]];
            for (int j = 0; j < bucket.Count; j++)
            {
                int row = bucket[j];
                float score = ComputeScore(query, storage.Slice(row * _dimensions, _dimensions));
                float proxy = _largerBetter ? score : -score;
                if (kHeap.Count < effectiveK)
                {
                    kHeap.Enqueue(row, proxy);
                }
                else
                {
                    kHeap.EnqueueDequeue(row, proxy);
                }
            }
        }

        int n = kHeap.Count;
        // 倒序写入，使 results[0] 为最相似。
        for (int i = n - 1; i >= 0; i--)
        {
            int row = kHeap.Dequeue();
            results[i] = (_keys[row], ComputeScore(query, storage.Slice(row * _dimensions, _dimensions)));
        }
        return n;
    }

    /// <summary>
    /// 取当前 IVF-Flat 索引的快照副本，用于持久化层全量落盘。
    /// </summary>
    internal IvfFlatIndexSnapshot<TKey> CreateSnapshot()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var vectors = CollectionsMarshal.AsSpan(_vectors).ToArray();
            var keys = _keys.ToArray();
            var rowToList = _rowToList.ToArray();
            float[]? centroids = null;
            int[][]? invertedLists = null;
            if (_isTrained)
            {
                centroids = (float[])_centroids!.Clone();
                invertedLists = new int[_options.NList][];
                for (int i = 0; i < _options.NList; i++)
                    invertedLists[i] = _invertedLists![i].ToArray();
            }
            return new IvfFlatIndexSnapshot<TKey>(
                _dimensions,
                _metric,
                _options,
                _isTrained,
                vectors,
                keys,
                rowToList,
                centroids,
                invertedLists);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 用一份完整快照原地恢复当前空实例。仅在初始化阶段、无并发写入时调用。
    /// </summary>
    internal void RestoreBulk(IvfFlatIndexSnapshot<TKey> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            if (_keys.Count != 0)
                throw new InvalidOperationException("RestoreBulk 仅可在空 IvfFlatIndex 上调用。");
            if (snapshot.Dimensions != _dimensions)
                throw new InvalidDataException(
                    $"IVF-Flat snapshot dimensions {snapshot.Dimensions} 与当前实例 {_dimensions} 不一致。");
            if (snapshot.Metric != _metric)
                throw new InvalidDataException(
                    $"IVF-Flat snapshot metric {snapshot.Metric} 与当前实例 {_metric} 不一致。");
            if (snapshot.Keys.Length != snapshot.RowToList.Length)
                throw new InvalidDataException("IVF-Flat snapshot row counts do not match.");
            if (snapshot.Vectors.Length != checked(snapshot.Keys.Length * snapshot.Dimensions))
                throw new InvalidDataException("IVF-Flat snapshot vector length does not match metadata.");

            // 采纳 snapshot 中的 options——构造时调用方未必能从 catalog 拿到原始 options
            // （catalog 只持久化 IndexKind / Dim / Metric），所以这里以 snapshot 为准。
            _options = snapshot.Options;

            _vectors.AddRange(snapshot.Vectors);
            _keys.AddRange(snapshot.Keys);
            for (int row = 0; row < snapshot.Keys.Length; row++)
                _keyToRow.Add(snapshot.Keys[row], row);
            _rowToList.AddRange(snapshot.RowToList);

            if (snapshot.IsTrained)
            {
                if (snapshot.Centroids is null || snapshot.InvertedLists is null)
                    throw new InvalidDataException("IVF-Flat snapshot 声明已训练但缺少 centroids 或 inverted lists。");
                if (snapshot.Centroids.Length != checked(_options.NList * _dimensions))
                    throw new InvalidDataException("IVF-Flat centroids 长度与 NList × Dimensions 不一致。");
                if (snapshot.InvertedLists.Length != _options.NList)
                    throw new InvalidDataException("IVF-Flat inverted lists 数量与 NList 不一致。");
                _centroids = (float[])snapshot.Centroids.Clone();
                _invertedLists = new List<int>[_options.NList];
                for (int i = 0; i < _options.NList; i++)
                    _invertedLists[i] = new List<int>(snapshot.InvertedLists[i]);
                _isTrained = true;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private void TrainCore()
    {
        if (_isTrained)
        {
            throw new InvalidOperationException("索引已经训练过，无法重复训练。");
        }
        if (_keys.Count < _options.NList)
        {
            throw new InvalidOperationException(
                $"训练向量数（{_keys.Count}）少于 NList（{_options.NList}），无法完成 K-Means 聚类。");
        }

        ReadOnlySpan<float> data = CollectionsMarshal.AsSpan(_vectors);
        KMeans.Train(
            data,
            _keys.Count,
            _dimensions,
            _options.NList,
            _options.MaxIterations,
            _options.Seed,
            out float[] centroids,
            out int[] assignments);

        _centroids = centroids;
        _invertedLists = new List<int>[_options.NList];
        for (int i = 0; i < _options.NList; i++)
        {
            _invertedLists[i] = new List<int>();
        }
        for (int row = 0; row < assignments.Length; row++)
        {
            int listId = assignments[row];
            _invertedLists[listId].Add(row);
            _rowToList[row] = listId;
        }
        _isTrained = true;
    }

    private float ComputeScore(ReadOnlySpan<float> query, ReadOnlySpan<float> vector)
        => Distance.Compute(query, vector, _metric);

    private void EnsureDimension(int length)
    {
        if (length != _dimensions)
        {
            throw new ArgumentException(
                $"向量维度（{length}）与索引维度（{_dimensions}）不一致。",
                nameof(length));
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _lock.Dispose();
    }
}
