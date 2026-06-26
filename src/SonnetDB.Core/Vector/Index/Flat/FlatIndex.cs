using System.Buffers;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Compute;
using SonnetDB.Vector.Core;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Flat;

/// <summary>
/// 基于线性扫描（Brute Force）的精确最近邻索引，类似 FAISS <c>IndexFlat</c> / Milvus <c>FLAT</c>。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// <para>
/// 实现要点：
/// <list type="bullet">
///   <item>向量数据按行优先存储在单一的连续 <see cref="float"/>[] 缓冲中，
///         通过 <see cref="CollectionsMarshal.AsSpan{T}(List{T})"/> 直接以 <see cref="Span{T}"/>
///         访问，确保 SIMD 距离函数（<see cref="Distance"/>）的快路径生效。</item>
///   <item>键到行号的映射使用 <see cref="Dictionary{TKey, TValue}"/>，<c>Remove</c> 采用 swap-with-last
///         策略，保持存储紧凑（O(1) 删除）。</item>
///   <item>使用 <see cref="ReaderWriterLockSlim"/> 实现"多读单写"并发：搜索可并行，写入互斥。</item>
///   <item>Top-K 选取使用 <see cref="PriorityQueue{TElement, TPriority}"/>（最小堆），
///         通过取负优先级的方式实现 K 大小受限的最大堆，复杂度 O(N log K)。</item>
/// </list>
/// </para>
/// <para>
/// 当前 (M2) 仅支持实数向量度量：<see cref="Metric.L2"/> / <see cref="Metric.Cosine"/>
/// / <see cref="Metric.InnerProduct"/> / <see cref="Metric.DotProduct"/>。
/// <see cref="Metric.Hamming"/> 需在二值向量索引（M11）中支持，调用会抛出 <see cref="NotSupportedException"/>。
/// </para>
/// </remarks>
public sealed class FlatIndex<TKey> : IIndex<TKey>, IDisposable
    where TKey : notnull
{
    private readonly int _dimensions;
    private readonly Metric _metric;
    private readonly IBatchScorer? _scorer;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // 行优先连续存储；长度始终是 dimensions 的整数倍。
    private readonly List<float> _vectors;

    // 行号 -> Key（与 _vectors 平行）。
    private readonly List<TKey> _keys;

    // Key -> 行号（O(1) 查找 / 删除）。
    private readonly Dictionary<TKey, int> _keyToRow;

    private bool _disposed;

    /// <summary>
    /// 初始化 <see cref="FlatIndex{TKey}"/> 的新实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须 &gt; 0。</param>
    /// <param name="metric">距离度量类型。</param>
    /// <param name="initialCapacity">初始向量条数预留容量，默认 0。</param>
    /// <param name="keyComparer">键比较器（可选）。</param>
    /// <param name="scorer">可选的批量打分内核（M14）；为 <see langword="null"/> 时走默认 CPU 逐行路径，
    /// 设置为 <see cref="CpuTensorPrimitivesScorer.Instance"/> 或自定义加速实现可包装入批量调用。</param>
    public FlatIndex(
        int dimensions,
        Metric metric,
        int initialCapacity = 0,
        IEqualityComparer<TKey>? keyComparer = null,
        IBatchScorer? scorer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (metric == Metric.Hamming)
        {
            throw new NotSupportedException(
                "FlatIndex 当前不支持 Hamming 度量；二值向量索引计划在 M11 实现。");
        }

        _dimensions = dimensions;
        _metric = metric;
        _scorer = scorer;
        _vectors = new List<float>(checked(initialCapacity * dimensions));
        _keys = new List<TKey>(initialCapacity);
        _keyToRow = new Dictionary<TKey, int>(initialCapacity, keyComparer);
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <summary>距离度量类型。</summary>
    public Metric Metric => _metric;

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
            // List<T>.AddRange(ReadOnlySpan<T>) 在 .NET 8+ 已可用。
            _vectors.AddRange(vector);
            _keyToRow.Add(key, row);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 批量插入向量。所有向量必须维度一致，按行优先打包在 <paramref name="vectors"/> 中，
    /// 行数等于 <paramref name="keys"/>.Count。整批操作在单次写锁下完成。
    /// </summary>
    /// <param name="keys">键集合，长度 N。</param>
    /// <param name="vectors">行优先打包的向量数据，长度 = N × <see cref="Dimensions"/>。</param>
    /// <exception cref="ArgumentException">
    /// 键数量与向量数据不一致、或存在重复键时抛出。
    /// </exception>
    public void AddBatch(IReadOnlyList<TKey> keys, ReadOnlySpan<float> vectors)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ThrowIfDisposed();

        int n = keys.Count;
        if (n == 0)
        {
            return;
        }

        long expected = (long)n * _dimensions;
        if (vectors.Length != expected)
        {
            throw new ArgumentException(
                $"vectors 长度（{vectors.Length}）与 keys.Count × Dimensions（{n} × {_dimensions} = {expected}）不一致。",
                nameof(vectors));
        }

        _lock.EnterWriteLock();
        try
        {
            // 先做重复检测，保持原子性（要么全部插入，要么全部不变）。
            for (int i = 0; i < n; i++)
            {
                TKey k = keys[i];
                ArgumentNullException.ThrowIfNull(k, nameof(keys));
                if (_keyToRow.ContainsKey(k))
                {
                    throw new ArgumentException($"键 '{k}' 已存在于索引中。", nameof(keys));
                }
            }

            int startRow = _keys.Count;
            _vectors.AddRange(vectors);
            for (int i = 0; i < n; i++)
            {
                _keys.Add(keys[i]);
                _keyToRow.Add(keys[i], startRow + i);
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
            Span<float> storage = CollectionsMarshal.AsSpan(_vectors);

            if (row != last)
            {
                // swap-with-last：将最后一行的数据复制到被删除的位置。
                Span<float> dst = storage.Slice(row * _dimensions, _dimensions);
                Span<float> src = storage.Slice(last * _dimensions, _dimensions);
                src.CopyTo(dst);

                TKey movedKey = _keys[last];
                _keys[row] = movedKey;
                _keyToRow[movedKey] = row;
            }

            _keys.RemoveAt(last);
            _vectors.RemoveRange(last * _dimensions, _dimensions);
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
            int n = _keys.Count;
            if (n == 0)
            {
                return 0;
            }

            ReadOnlySpan<float> storage = CollectionsMarshal.AsSpan(_vectors);
            int effectiveK = Math.Min(topK, n);
            bool largerBetter = _metric.IsLargerBetter();

            // 当注入了自定义 IBatchScorer（M14）时，先一次性批量打分；否则走逐行 SIMD 路径以避免分配。
            float[]? rentedScores = null;
            ReadOnlySpan<float> batchedScores = default;
            if (_scorer is not null)
            {
                rentedScores = ArrayPool<float>.Shared.Rent(n);
                _scorer.Score(query, storage.Slice(0, n * _dimensions), rentedScores.AsSpan(0, n), _metric);
                batchedScores = rentedScores.AsSpan(0, n);
            }

            try
            {
                // 使用 BCL 最小堆。要保留 K 个"最佳"候选，应让"最差"候选位于堆顶，
                // 这样新候选优于堆顶时即可整体替换。
                //  - smallerBetter（L2/Cosine/Hamming）：最差 = 分数最大 → priority = -score（最小堆顶 = max score）
                //  - largerBetter （InnerProduct/DotProduct）：最差 = 分数最小 → priority = +score（最小堆顶 = min score）
                var heap = new PriorityQueue<(int Row, float Score), float>(effectiveK);

                for (int row = 0; row < n; row++)
                {
                    float score = rentedScores is null
                        ? ComputeScore(query, storage.Slice(row * _dimensions, _dimensions))
                        : batchedScores[row];
                    float priority = largerBetter ? score : -score;

                    if (heap.Count < effectiveK)
                    {
                        heap.Enqueue((row, score), priority);
                    }
                    else
                    {
                        // EnqueueDequeue：先比较堆顶；若新值优于堆顶（priority 更小），则替换。
                        heap.EnqueueDequeue((row, score), priority);
                    }
                }

                // 从堆中取出，按"最佳到最差"顺序写入 results（堆当前从最差到最佳出栈，需反序）。
                int written = heap.Count;
                for (int i = written - 1; i >= 0; i--)
                {
                    var (row, score) = heap.Dequeue();
                    results[i] = (_keys[row], score);
                }
                return written;
            }
            finally
            {
                if (rentedScores is not null)
                {
                    ArrayPool<float>.Shared.Return(rentedScores);
                }
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 仅在指定的候选键集合内执行精确 Top-K 搜索（M11 — pre-filter 通路）。
    /// </summary>
    /// <param name="query">查询向量。</param>
    /// <param name="topK">返回结果数量。</param>
    /// <param name="candidateKeys">候选键集合；不在索引中的键会被静默忽略。</param>
    /// <param name="results">结果缓冲区，长度 ≥ <paramref name="topK"/>。</param>
    /// <returns>实际写入 <paramref name="results"/> 的结果数（≤ <paramref name="topK"/>）。</returns>
    /// <remarks>
    /// 该方法仅扫描候选行而非全部行，适用于高选择率的标量过滤场景。
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
            bool largerBetter = _metric.IsLargerBetter();
            var heap = new PriorityQueue<int, float>(effectiveK);

            foreach (TKey key in candidateKeys)
            {
                if (!_keyToRow.TryGetValue(key, out int row))
                {
                    continue;
                }
                ReadOnlySpan<float> v = storage.Slice(row * _dimensions, _dimensions);
                float score = ComputeScore(query, v);
                float priority = largerBetter ? score : -score;
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
                float score = ComputeScore(query, v);
                results[i] = (_keys[row], score);
            }
            return written;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 检查给定主键是否存在于索引中。
    /// </summary>
    /// <param name="key">要查询的主键。</param>
    public bool ContainsKey(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterReadLock();
        try { return _keyToRow.ContainsKey(key); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 尝试将指定主键对应的向量拷贝到目标缓冲区（M7.1）。
    /// </summary>
    /// <param name="key">要查询的主键。</param>
    /// <param name="destination">目标缓冲区，长度必须等于 <see cref="Dimensions"/>。</param>
    /// <returns>命中返回 <see langword="true"/> 并完成拷贝；未命中返回 <see langword="false"/>。</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度与维度不一致。</exception>
    public bool TryCopyVectorTo(TKey key, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (destination.Length != _dimensions)
        {
            throw new ArgumentException(
                $"目标缓冲区长度 {destination.Length} 与索引维度 {_dimensions} 不一致。",
                nameof(destination));
        }
        ThrowIfDisposed();

        _lock.EnterReadLock();
        try
        {
            if (!_keyToRow.TryGetValue(key, out int row))
            {
                return false;
            }
            ReadOnlySpan<float> all = CollectionsMarshal.AsSpan(_vectors);
            all.Slice(row * _dimensions, _dimensions).CopyTo(destination);
            return true;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 在读锁保护下生成索引内当前所有键和向量数据的快照副本。
    /// 用于 <see cref="Storage"/> 层的 Segment flush。
    /// </summary>
    /// <param name="keys">复制后的键列表（按行号顺序）。</param>
    /// <param name="vectors">行优先打包的向量副本，长度 = <c>keys.Count × Dimensions</c>。</param>
    public void Snapshot(out List<TKey> keys, out float[] vectors)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            keys = new List<TKey>(_keys);
            vectors = _vectors.ToArray();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// 在读锁保护下生成从 <paramref name="startRow"/> 起的增量快照副本（用于 Segment flush 的 delta）。
    /// 同时返回当前总行数，调用方下次 flush 时可作为新的 <paramref name="startRow"/>。
    /// </summary>
    /// <param name="startRow">起始行号（包含），通常是上次 Snapshot 后的 <c>endRowExclusive</c>。</param>
    /// <param name="keys">该区间内的键副本（按行号顺序）。</param>
    /// <param name="vectors">行优先打包的向量副本。</param>
    /// <param name="endRowExclusive">本次快照截止后的总行数（即下次的 <c>startRow</c>）。</param>
    public void SnapshotSince(int startRow, out List<TKey> keys, out float[] vectors, out int endRowExclusive)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(startRow);
        _lock.EnterReadLock();
        try
        {
            int total = _keys.Count;
            if (startRow > total)
            {
                throw new ArgumentOutOfRangeException(nameof(startRow),
                    $"startRow ({startRow}) 超过当前行数 ({total})。");
            }
            int n = total - startRow;
            keys = new List<TKey>(n);
            for (int i = startRow; i < total; i++) { keys.Add(_keys[i]); }
            vectors = new float[(long)n * _dimensions];
            if (n > 0)
            {
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_vectors)
                    .Slice(startRow * _dimensions, n * _dimensions)
                    .CopyTo(vectors);
            }
            endRowExclusive = total;
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

    private float ComputeScore(ReadOnlySpan<float> query, ReadOnlySpan<float> v)
        => Distance.Compute(query, v, _metric);

    private void EnsureDimension(int actual)
    {
        if (actual != _dimensions)
        {
            throw new ArgumentException(
                $"向量维度不匹配：期望 {_dimensions}，实际 {actual}。");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
