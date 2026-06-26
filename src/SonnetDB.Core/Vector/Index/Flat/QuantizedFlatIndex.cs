using System.Runtime.InteropServices;
using SonnetDB.Vector.Compression;
using SonnetDB.Vector.Core;
using SonnetDB.Vector.Model;

namespace SonnetDB.Vector.Index.Flat;

/// <summary>
/// 量化压缩版本的精确扫描索引：底层不存原始 <see cref="float"/> 向量，
/// 而是存储 <see cref="IVectorQuantizer"/> 编码出的紧凑字节码。
/// </summary>
/// <typeparam name="TKey">记录主键类型。</typeparam>
/// <remarks>
/// <para>
/// 适用场景：内存受限的中等规模数据集（万 ~ 千万级），SQ8 (~4×) / PQ / OPQ / RQ
/// (~8 ~ 32×) 字节预算下用近似 L2² 距离做线性扫描，仍可达到比 HNSW
/// 显著更小的内存占用与稳定的尾延迟。
/// </para>
/// <para>
/// 实现要点：
/// <list type="bullet">
/// <item>编码缓冲为单个 <see cref="List{Byte}"/>，行偏移 = <c>row × CodeBytes</c>，
/// 通过 <see cref="CollectionsMarshal.AsSpan{T}(List{T})"/> 暴露 <see cref="Span{T}"/> 给打分器。</item>
/// <item>查询路径每查询一次构造一次 <see cref="IQuantizedScorer"/>（持有该查询的预计算
/// LUT / 解码缓冲），打分器为线程独占。</item>
/// <item>当前 (M11) 仅支持 <see cref="Metric.L2"/>；其他度量在量化语义未明确前暂不支持。</item>
/// <item><see cref="ReaderWriterLockSlim"/> 提供多读单写并发，与 <see cref="FlatIndex{TKey}"/> 一致。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class QuantizedFlatIndex<TKey> : IIndex<TKey>, IDisposable
    where TKey : notnull
{
    private readonly int _dimensions;
    private readonly int _codeBytes;
    private readonly Metric _metric;
    private readonly IVectorQuantizer _quantizer;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    // 编码字节缓冲（行优先，长度始终 = _keys.Count * _codeBytes）。
    private readonly List<byte> _codes;
    private readonly List<TKey> _keys;
    private readonly Dictionary<TKey, int> _keyToRow;

    private bool _disposed;

    /// <summary>
    /// 初始化新的 <see cref="QuantizedFlatIndex{TKey}"/> 实例。
    /// </summary>
    /// <param name="quantizer">已训练的量化器（决定 <see cref="Dimensions"/> 与 <see cref="CodeBytes"/>）。</param>
    /// <param name="metric">距离度量；当前仅支持 <see cref="Metric.L2"/>。</param>
    /// <param name="initialCapacity">初始预留的行数容量，默认 0。</param>
    /// <param name="keyComparer">键比较器（可选）。</param>
    public QuantizedFlatIndex(
        IVectorQuantizer quantizer,
        Metric metric = Metric.L2,
        int initialCapacity = 0,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(quantizer);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        if (!quantizer.IsTrained)
        {
            throw new ArgumentException("quantizer 必须已完成 Train。", nameof(quantizer));
        }
        if (metric != Metric.L2)
        {
            throw new NotSupportedException(
                $"QuantizedFlatIndex 当前仅支持 L2 度量，未支持 {metric}。");
        }

        _quantizer = quantizer;
        _dimensions = quantizer.Dimensions;
        _codeBytes = quantizer.CodeBytes;
        _metric = metric;
        _codes = new List<byte>(checked(initialCapacity * _codeBytes));
        _keys = new List<TKey>(initialCapacity);
        _keyToRow = new Dictionary<TKey, int>(initialCapacity, keyComparer);
    }

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <summary>每条编码的字节数（= 量化器 <see cref="IVectorQuantizer.CodeBytes"/>）。</summary>
    public int CodeBytes => _codeBytes;

    /// <summary>距离度量。</summary>
    public Metric Metric => _metric;

    /// <summary>底层量化器（已训练，只读使用）。</summary>
    public IVectorQuantizer Quantizer => _quantizer;

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

        Span<byte> codeBuf = _codeBytes <= 256
            ? stackalloc byte[256].Slice(0, _codeBytes)
            : new byte[_codeBytes];
        _quantizer.Encode(vector, codeBuf);

        _lock.EnterWriteLock();
        try
        {
            if (_keyToRow.ContainsKey(key))
            {
                throw new ArgumentException($"键 '{key}' 已存在于索引中。", nameof(key));
            }
            int row = _keys.Count;
            _keys.Add(key);
            for (int i = 0; i < _codeBytes; i++)
            {
                _codes.Add(codeBuf[i]);
            }
            _keyToRow.Add(key, row);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// 批量插入向量；所有向量先在写锁外完成编码再一次性追加。
    /// </summary>
    /// <param name="keys">键集合，长度 N。</param>
    /// <param name="vectors">行优先打包的原始向量数据，长度 = N × <see cref="Dimensions"/>。</param>
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

        // 锁外完成编码，避免长写锁。
        byte[] encoded = new byte[(long)n * _codeBytes];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<float> src = vectors.Slice(i * _dimensions, _dimensions);
            Span<byte> dst = encoded.AsSpan(i * _codeBytes, _codeBytes);
            _quantizer.Encode(src, dst);
        }

        _lock.EnterWriteLock();
        try
        {
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
            _codes.AddRange(encoded);
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
            Span<byte> storage = CollectionsMarshal.AsSpan(_codes);
            if (row != last)
            {
                Span<byte> dst = storage.Slice(row * _codeBytes, _codeBytes);
                Span<byte> src = storage.Slice(last * _codeBytes, _codeBytes);
                src.CopyTo(dst);

                TKey movedKey = _keys[last];
                _keys[row] = movedKey;
                _keyToRow[movedKey] = row;
            }
            _keys.RemoveAt(last);
            _codes.RemoveRange(last * _codeBytes, _codeBytes);
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

        IQuantizedScorer scorer = _quantizer.BuildScorer(query);

        _lock.EnterReadLock();
        try
        {
            int n = _keys.Count;
            if (n == 0)
            {
                return 0;
            }
            ReadOnlySpan<byte> storage = CollectionsMarshal.AsSpan(_codes);
            int effectiveK = Math.Min(topK, n);

            // L2 → smaller better；priority = -score，最小堆顶 = 最差候选。
            var heap = new PriorityQueue<int, float>(effectiveK);
            for (int row = 0; row < n; row++)
            {
                ReadOnlySpan<byte> code = storage.Slice(row * _codeBytes, _codeBytes);
                float score = scorer.Score(code);
                float priority = -score;
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
                ReadOnlySpan<byte> code = storage.Slice(row * _codeBytes, _codeBytes);
                float score = scorer.Score(code);
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
    /// <param name="key">主键。</param>
    public bool ContainsKey(TKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        _lock.EnterReadLock();
        try { return _keyToRow.ContainsKey(key); }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// 在读锁保护下生成索引内当前所有键和编码字节的快照副本（用于 Segment flush）。
    /// </summary>
    /// <param name="keys">键列表副本（按行号顺序）。</param>
    /// <param name="codes">行优先打包的编码字节副本，长度 = <c>keys.Count × CodeBytes</c>。</param>
    public void Snapshot(out List<TKey> keys, out byte[] codes)
    {
        ThrowIfDisposed();
        _lock.EnterReadLock();
        try
        {
            keys = new List<TKey>(_keys);
            codes = _codes.ToArray();
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
