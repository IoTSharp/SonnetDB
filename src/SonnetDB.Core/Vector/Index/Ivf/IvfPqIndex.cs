using System.Runtime.InteropServices;
using SonnetDB.Vector.Compression;
using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Core;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Ivf;

/// <summary>
/// IVF-PQ（倒排文件 + 乘积量化）近似最近邻索引。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// <para>
/// 算法对应 FAISS <c>IndexIVFPQ</c>：
/// <list type="bullet">
///   <item>训练阶段先做 IVF 粗量化 <c>NList</c> 个中心，再对 <i>残差</i>（vector - 中心）训练 PQ 码本。</item>
///   <item>每个向量仅存其残差的 <c>M</c> 字节 PQ 编码（<c>byte[NList][M]</c>），原始 float 不再保留。</item>
///   <item>查询时为每个被探测列表构造一次 LUT（基于残差查询），随后对该列表内所有编码做查表求和。</item>
/// </list>
/// </para>
/// <para>
/// <b>当前实现使用 L2 平方距离作为内部度量</b>，并按 <see cref="Metric"/> 在结果分数上做语义包装：
/// <list type="bullet">
///   <item><see cref="Metric.L2"/>：直接返回 ADC L2 平方近似。</item>
///   <item><see cref="Metric.Cosine"/> / <see cref="Metric.InnerProduct"/> /
///         <see cref="Metric.DotProduct"/>：基于 ADC 构造近似分数排序，召回质量取决于
///         调用方是否对向量做了归一化。完整的 IP 变体规划在后续 Milestone。</item>
/// </list>
/// </para>
/// <para>
/// <b>线程安全</b>：使用 <see cref="ReaderWriterLockSlim"/>，多读单写。持久化在 M5 完成。
/// </para>
/// </remarks>
public sealed class IvfPqIndex<TKey> : IIndex<TKey>, IDisposable
    where TKey : notnull
{
    private readonly int _dimensions;
    private readonly Metric _metric;
    private readonly bool _largerBetter;
    private IvfPqOptions _options;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly List<TKey> _keys;
    private readonly Dictionary<TKey, int> _keyToRow;
    private readonly List<int> _rowToList;

    // 训练前缓冲原始 float（训练完成后释放）。
    private List<float>? _trainingBuffer;

    private float[]? _centroids;          // [NList * Dim]
    private List<int>[]? _invertedLists;  // 每个 list 内的 row id
    private byte[]? _codes;               // [rowCount * M] — 行优先
    private int _codeCapacityRows;
    private int _rowCount;
    private PqCodebook? _codebook;
    private ProductQuantizer? _pq;        // 包装 _codebook，提供 IQuantizedScorer。
    private bool _isTrained;
    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="IvfPqIndex{TKey}"/> 的新实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须能被 <see cref="IvfPqOptions.M"/> 整除。</param>
    /// <param name="metric">距离度量（不支持 <see cref="Metric.Hamming"/>）。</param>
    /// <param name="options">IVF-PQ 参数；为 <see langword="null"/> 时使用 <see cref="IvfPqOptions.Default"/>。</param>
    /// <param name="initialCapacity">初始容量预留。</param>
    /// <param name="keyComparer">键比较器（可选）。</param>
    public IvfPqIndex(
        int dimensions,
        Metric metric,
        IvfPqOptions? options = null,
        int initialCapacity = 0,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (metric == Metric.Hamming)
        {
            throw new NotSupportedException(
                "IvfPqIndex 当前不支持 Hamming 度量；二值向量索引计划在 M11 实现。");
        }

        _dimensions = dimensions;
        _metric = metric;
        _largerBetter = metric.IsLargerBetter();
        _options = options ?? IvfPqOptions.Default;
        _options.Validate();
        if (dimensions % _options.M != 0)
        {
            throw new ArgumentException(
                $"dimensions（{dimensions}）必须能被 IvfPqOptions.M（{_options.M}）整除。",
                nameof(dimensions));
        }

        _keys = new List<TKey>(initialCapacity);
        _keyToRow = new Dictionary<TKey, int>(initialCapacity, keyComparer);
        _rowToList = new List<int>(initialCapacity);
        _trainingBuffer = new List<float>(checked(initialCapacity * dimensions));
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <summary>距离度量类型。</summary>
    public Metric Metric => _metric;

    /// <summary>当前生效的 IVF-PQ 参数。</summary>
    public IvfPqOptions Options => _options;

    /// <summary>是否已完成训练。</summary>
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

    /// <summary>主动触发训练。</summary>
    public void Train()
    {
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try { TrainCore(); }
        finally { _lock.ExitWriteLock(); }
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
            _keyToRow.Add(key, row);

            if (_isTrained)
            {
                int listId = KMeans.FindNearest(vector, _centroids!, _options.NList, _dimensions);
                _invertedLists![listId].Add(row);
                _rowToList.Add(listId);
                EnsureCodeCapacity(row + 1);
                EncodeResidual(vector, listId, _codes.AsSpan(row * _options.M, _options.M));
                _rowCount = row + 1;
            }
            else
            {
                _trainingBuffer!.AddRange(vector);
                _rowToList.Add(-1);
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

            int last = _keys.Count - 1;
            int removedListId = _rowToList[row];
            if (removedListId >= 0 && _invertedLists is not null)
            {
                _invertedLists[removedListId].Remove(row);
            }

            if (row != last)
            {
                if (_isTrained)
                {
                    Span<byte> codes = _codes.AsSpan();
                    codes.Slice(last * _options.M, _options.M)
                         .CopyTo(codes.Slice(row * _options.M, _options.M));
                }
                else if (_trainingBuffer is not null)
                {
                    Span<float> tb = CollectionsMarshal.AsSpan(_trainingBuffer);
                    tb.Slice(last * _dimensions, _dimensions)
                      .CopyTo(tb.Slice(row * _dimensions, _dimensions));
                }

                TKey movedKey = _keys[last];
                _keys[row] = movedKey;
                _keyToRow[movedKey] = row;

                int movedList = _rowToList[last];
                _rowToList[row] = movedList;
                if (movedList >= 0 && _invertedLists is not null)
                {
                    var bucket = _invertedLists[movedList];
                    int idx = bucket.IndexOf(last);
                    if (idx >= 0) { bucket[idx] = row; }
                }
            }

            _keys.RemoveAt(last);
            _rowToList.RemoveAt(last);
            if (_isTrained)
            {
                _rowCount = Math.Max(0, _rowCount - 1);
            }
            else if (_trainingBuffer is not null)
            {
                _trainingBuffer.RemoveRange(last * _dimensions, _dimensions);
            }
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
        ProductQuantizer pq = _pq!;
        int nList = _options.NList;
        int nProbe = Math.Min(_options.NProbe, nList);
        int m = _options.M;

        // 1) 选最近的 nProbe 个粗中心。
        var centroidHeap = new PriorityQueue<int, float>(nProbe);
        for (int c = 0; c < nList; c++)
        {
            float l2sq = Distance.L2Squared(query, centroids.Slice(c * _dimensions, _dimensions));
            // 距离越小越近。priority = -l2sq → 堆顶为最远；保留 nProbe 个最近。
            float proxy = -l2sq;
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

        // 2) 对每个被探测的列表：计算残差查询，构造 IQuantizedScorer，扫码取分数。
        int effectiveK = Math.Min(topK, _keys.Count);
        var kHeap = new PriorityQueue<int, float>(effectiveK);

        var residual = new float[_dimensions];
        ReadOnlySpan<byte> allCodes = _codes!;

        for (int p = 0; p < probedCount; p++)
        {
            int listId = probedLists[p];
            ReadOnlySpan<float> centroid = centroids.Slice(listId * _dimensions, _dimensions);
            // residual = query - centroid
            for (int i = 0; i < _dimensions; i++)
            {
                residual[i] = query[i] - centroid[i];
            }
            IQuantizedScorer scorer = pq.BuildScorer(residual);

            List<int> bucket = lists[listId];
            for (int j = 0; j < bucket.Count; j++)
            {
                int row = bucket[j];
                ReadOnlySpan<byte> code = allCodes.Slice(row * m, m);
                float adcL2Sq = scorer.Score(code);
                // ADC 给出的是残差 L2 平方 → 整体近似 L2 平方。
                // 对所有 metric，先按"距离越小越好"统一选 top-K；写出时再做语义换算。
                float proxy = -adcL2Sq;
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
        // 缓存每个 list 的 scorer，避免重复构造 LUT。
        var scorerCache = new Dictionary<int, IQuantizedScorer>(probedCount);
        // 倒序写入：results[0] = 最相似。
        for (int i = n - 1; i >= 0; i--)
        {
            int row = kHeap.Dequeue();
            int listId = _rowToList[row];
            if (!scorerCache.TryGetValue(listId, out IQuantizedScorer? scorer))
            {
                ReadOnlySpan<float> centroid = centroids.Slice(listId * _dimensions, _dimensions);
                for (int i2 = 0; i2 < _dimensions; i2++) { residual[i2] = query[i2] - centroid[i2]; }
                scorer = pq.BuildScorer(residual);
                scorerCache[listId] = scorer;
            }
            ReadOnlySpan<byte> code = allCodes.Slice(row * m, m);
            float adcL2Sq = scorer.Score(code);

            float reported = _metric switch
            {
                Metric.L2 => adcL2Sq,
                Metric.Cosine => 1f - adcL2Sq * 0.5f,
                Metric.InnerProduct => -adcL2Sq * 0.5f,
                Metric.DotProduct => -adcL2Sq * 0.5f,
                _ => adcL2Sq,
            };
            results[i] = (_keys[row], reported);
        }

        // 若上层度量为 largerBetter，结果当前按 ADC 距离升序 → 等价于"分数降序"，OK。
        // 若度量为 smallerBetter（L2），同样升序，OK。
        // 故无需再次排序。
        _ = _largerBetter;
        return n;
    }

    /// <summary>
    /// 取当前 IVF-PQ 索引的全量快照副本（含 centroids / inverted lists / PQ codebook / 编码字节）。
    /// </summary>
    internal IvfPqIndexSnapshot<TKey> CreateSnapshot()
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            var keys = _keys.ToArray();
            var rowToList = _rowToList.ToArray();
            float[]? centroids = null;
            int[][]? invertedLists = null;
            byte[]? codes = null;
            float[]? codebookCentroids = null;
            if (_isTrained)
            {
                centroids = (float[])_centroids!.Clone();
                invertedLists = new int[_options.NList][];
                for (int i = 0; i < _options.NList; i++)
                    invertedLists[i] = _invertedLists![i].ToArray();
                codes = new byte[_rowCount * _options.M];
                _codes.AsSpan(0, codes.Length).CopyTo(codes);
                codebookCentroids = _codebook!.Centroids.ToArray();
            }
            return new IvfPqIndexSnapshot<TKey>(
                _dimensions,
                _metric,
                _options,
                _isTrained,
                _rowCount,
                keys,
                rowToList,
                centroids,
                invertedLists,
                codes,
                codebookCentroids);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 用一份完整快照原地恢复当前空实例。仅在初始化阶段、无并发写入时调用。
    /// </summary>
    internal void RestoreBulk(IvfPqIndexSnapshot<TKey> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ThrowIfDisposed();
        _lock.EnterWriteLock();
        try
        {
            if (_keys.Count != 0)
                throw new InvalidOperationException("RestoreBulk 仅可在空 IvfPqIndex 上调用。");
            if (snapshot.Dimensions != _dimensions)
                throw new InvalidDataException(
                    $"IVF-PQ snapshot dimensions {snapshot.Dimensions} 与当前实例 {_dimensions} 不一致。");
            if (snapshot.Metric != _metric)
                throw new InvalidDataException(
                    $"IVF-PQ snapshot metric {snapshot.Metric} 与当前实例 {_metric} 不一致。");
            if (snapshot.Keys.Length != snapshot.RowToList.Length || snapshot.Keys.Length != snapshot.RowCount)
                throw new InvalidDataException("IVF-PQ snapshot row counts do not match.");

            // 采纳 snapshot 中的 options（catalog 不持久化 NList / NProbe / M / NBits / MaxIter / Seed）。
            _options = snapshot.Options;

            _keys.AddRange(snapshot.Keys);
            for (int row = 0; row < snapshot.Keys.Length; row++)
                _keyToRow.Add(snapshot.Keys[row], row);
            _rowToList.AddRange(snapshot.RowToList);
            _rowCount = snapshot.RowCount;

            if (snapshot.IsTrained)
            {
                if (snapshot.Centroids is null || snapshot.InvertedLists is null
                    || snapshot.Codes is null || snapshot.CodebookCentroids is null)
                {
                    throw new InvalidDataException("IVF-PQ snapshot 声明已训练但缺少 centroids / inverted lists / codes / codebook。");
                }
                if (snapshot.Centroids.Length != checked(_options.NList * _dimensions))
                    throw new InvalidDataException("IVF-PQ centroids 长度与 NList × Dimensions 不一致。");
                if (snapshot.InvertedLists.Length != _options.NList)
                    throw new InvalidDataException("IVF-PQ inverted lists 数量与 NList 不一致。");
                if (snapshot.Codes.Length != checked(_rowCount * _options.M))
                    throw new InvalidDataException("IVF-PQ codes 长度与 rowCount × M 不一致。");

                _centroids = (float[])snapshot.Centroids.Clone();
                _invertedLists = new List<int>[_options.NList];
                for (int i = 0; i < _options.NList; i++)
                    _invertedLists[i] = new List<int>(snapshot.InvertedLists[i]);
                EnsureCodeCapacity(_rowCount);
                snapshot.Codes.AsSpan().CopyTo(_codes.AsSpan(0, snapshot.Codes.Length));

                var codebook = new PqCodebook(_dimensions, _options.M);
                codebook.LoadCentroids(snapshot.CodebookCentroids);
                _codebook = codebook;
                _pq = new ProductQuantizer(codebook);
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
        if (_trainingBuffer is null || _keys.Count < _options.NList)
        {
            throw new InvalidOperationException(
                $"训练向量数（{_keys.Count}）少于 NList（{_options.NList}），无法完成 K-Means 聚类。");
        }
        if (_keys.Count < PqCodebook.Ksub)
        {
            throw new InvalidOperationException(
                $"训练向量数（{_keys.Count}）少于 PQ 子量化器中心数 Ksub（{PqCodebook.Ksub}），无法训练 PQ 码本。");
        }

        ReadOnlySpan<float> data = CollectionsMarshal.AsSpan(_trainingBuffer);

        // 1) 训练 IVF 粗量化。
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

        // 2) 计算所有训练向量的残差。
        int n = _keys.Count;
        var residuals = new float[(long)n * _dimensions];
        for (int row = 0; row < n; row++)
        {
            int listId = assignments[row];
            ReadOnlySpan<float> v = data.Slice(row * _dimensions, _dimensions);
            ReadOnlySpan<float> c = centroids.AsSpan(listId * _dimensions, _dimensions);
            Span<float> r = residuals.AsSpan(row * _dimensions, _dimensions);
            for (int i = 0; i < _dimensions; i++) { r[i] = v[i] - c[i]; }
        }

        // 3) 在残差上训练 PQ 码本。
        var codebook = new PqCodebook(_dimensions, _options.M);
        int? pqSeed = _options.Seed.HasValue ? _options.Seed.Value + 0x10000 : null;
        codebook.Train(residuals, n, _options.MaxIterations, pqSeed);
        _codebook = codebook;
        _pq = new ProductQuantizer(codebook);

        // 4) 编码全部训练向量。
        EnsureCodeCapacity(n);
        for (int row = 0; row < n; row++)
        {
            int listId = assignments[row];
            ReadOnlySpan<float> r = residuals.AsSpan(row * _dimensions, _dimensions);
            codebook.Encode(r, _codes.AsSpan(row * _options.M, _options.M));
            _invertedLists[listId].Add(row);
            _rowToList[row] = listId;
        }
        _rowCount = n;
        _trainingBuffer = null;
        _isTrained = true;
    }

    private void EnsureCodeCapacity(int rows)
    {
        int needed = rows * _options.M;
        if (_codes is null)
        {
            int cap = Math.Max(rows, 16);
            _codes = new byte[cap * _options.M];
            _codeCapacityRows = cap;
            return;
        }
        if (rows <= _codeCapacityRows) { return; }
        int newCap = Math.Max(_codeCapacityRows * 2, rows);
        var resized = new byte[newCap * _options.M];
        _codes.AsSpan(0, _rowCount * _options.M).CopyTo(resized);
        _codes = resized;
        _codeCapacityRows = newCap;
        _ = needed;
    }

    private void EncodeResidual(ReadOnlySpan<float> vector, int listId, Span<byte> code)
    {
        ReadOnlySpan<float> centroid = _centroids.AsSpan(listId * _dimensions, _dimensions);
        Span<float> residual = stackalloc float[_dimensions];
        for (int i = 0; i < _dimensions; i++)
        {
            residual[i] = vector[i] - centroid[i];
        }
        _codebook!.Encode(residual, code);
    }

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
