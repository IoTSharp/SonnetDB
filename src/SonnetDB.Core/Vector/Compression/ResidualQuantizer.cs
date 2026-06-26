using System.Buffers;
using System.Numerics.Tensors;
using SonnetDB.Vector.Index.Ivf;

namespace SonnetDB.Vector.Compression;

/// <summary>
/// 多级残差量化器（Residual Quantizer, RQ）。
/// </summary>
/// <remarks>
/// <para>
/// 训练流程：
/// <list type="number">
/// <item>令 <c>r₀ = x</c>。</item>
/// <item>对当前残差 <c>r_l</c> 做 K-Means（K = 256）得到第 <c>l</c> 级码本 <c>C_l</c>。</item>
/// <item>更新残差 <c>r_{l+1} = r_l - C_l[ argmin_k ||r_l - C_l[k]|| ]</c>。</item>
/// <item>共训练 <c>M</c> 级，编码长度 = <c>M</c> 字节。</item>
/// </list>
/// </para>
/// <para>
/// 解码 = 各级被选中心的累加。<see cref="BuildScorer"/> 采用「先解码再算 L2²」策略
/// （等价于 FAISS 的 <c>ST_decompress</c>），保证 ADC 与解码后 L2² 严格一致；
/// 简单且无跨级交叉项耦合。
/// </para>
/// <para>
/// 适用场景：8–16 字节预算下需要高召回（典型 RQ 2 级 8-bit 配置 Recall@10 ≥ 0.80）。
/// </para>
/// </remarks>
public sealed class ResidualQuantizer : IVectorQuantizer
{
    /// <summary>每级码本中心数（固定 256）。</summary>
    public const int Ksub = 256;

    /// <summary>触发栈分配的浮点缓冲阈值（≥ 该值时退回 <see cref="ArrayPool{T}"/>）。</summary>
    private const int StackallocThresholdFloats = 1024;

    private readonly int _dimensions;
    private readonly int _levels;
    private readonly int _maxIterations;
    private readonly int? _seed;
    private readonly float[] _centroids; // [levels * Ksub * dimensions]
    private bool _trained;

    /// <summary>
    /// 初始化新的 <see cref="ResidualQuantizer"/> 实例。
    /// </summary>
    /// <param name="dimensions">向量维度。</param>
    /// <param name="levels">残差级数（编码字节数 = <paramref name="levels"/>）。</param>
    /// <param name="maxIterations">每级 K-Means 最大迭代次数，默认 25。</param>
    /// <param name="seed">K-Means 种子，<see langword="null"/> 表示非确定性。</param>
    public ResidualQuantizer(int dimensions, int levels, int maxIterations = 25, int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(levels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);
        _dimensions = dimensions;
        _levels = levels;
        _maxIterations = maxIterations;
        _seed = seed;
        _centroids = new float[(long)levels * Ksub * dimensions];
    }

    /// <inheritdoc />
    public QuantizerKind Kind => QuantizerKind.Rq;

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public int CodeBytes => _levels;

    /// <inheritdoc />
    public bool IsTrained => _trained;

    /// <summary>残差级数。</summary>
    public int Levels => _levels;

    /// <summary>训练后的全部码本数据（<c>Levels × Ksub × Dimensions</c> 行优先）。</summary>
    internal ReadOnlySpan<float> Centroids => _centroids;

    /// <summary>
    /// 从持久化的 centroids 数据直接装载已训练状态（仅供 QuantizerSerializer 使用）。
    /// </summary>
    internal void LoadState(ReadOnlySpan<float> centroids)
    {
        if (centroids.Length != _centroids.Length)
        {
            throw new ArgumentException(
                $"centroids 长度（{centroids.Length}）与 RQ 码本布局（{_centroids.Length}）不一致。");
        }
        centroids.CopyTo(_centroids);
        _trained = true;
    }

    /// <inheritdoc />
    public void Train(ReadOnlySpan<float> data, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (count < Ksub)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count,
                $"RQ 训练向量数（{count}）少于每级中心数 Ksub（{Ksub}）。");
        }
        long expected = (long)count * _dimensions;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"data 长度（{data.Length}）与 count × dimensions（{expected}）不一致。",
                nameof(data));
        }

        int totalFloats = count * _dimensions;
        float[] residualBuf = ArrayPool<float>.Shared.Rent(totalFloats);
        try
        {
            data.CopyTo(residualBuf.AsSpan(0, totalFloats));

            for (int level = 0; level < _levels; level++)
            {
                int? levelSeed = _seed.HasValue ? _seed.Value + level : null;
                KMeans.Train(
                    residualBuf.AsSpan(0, totalFloats),
                    count,
                    _dimensions,
                    Ksub,
                    _maxIterations,
                    levelSeed,
                    out float[] levelCentroids,
                    out int[] assignments);

                levelCentroids.AsSpan().CopyTo(
                    _centroids.AsSpan(level * Ksub * _dimensions, Ksub * _dimensions));

                // 更新残差：r_{l+1} = r_l - centroid[ assignment[i] ]。
                for (int i = 0; i < count; i++)
                {
                    Span<float> r = residualBuf.AsSpan(i * _dimensions, _dimensions);
                    ReadOnlySpan<float> c = levelCentroids.AsSpan(assignments[i] * _dimensions, _dimensions);
                    TensorPrimitives.Subtract(r, c, r);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(residualBuf);
        }

        _trained = true;
    }

    /// <inheritdoc />
    public void Encode(ReadOnlySpan<float> vector, Span<byte> code)
    {
        EnsureTrained();
        if (vector.Length != _dimensions)
        {
            throw new ArgumentException(
                $"vector 长度（{vector.Length}）与维度（{_dimensions}）不一致。",
                nameof(vector));
        }
        if (code.Length < _levels)
        {
            throw new ArgumentException(
                $"code 缓冲过小：需要 ≥ {_levels}，实际 {code.Length}。",
                nameof(code));
        }

        bool useStack = _dimensions <= StackallocThresholdFloats;
        float[]? rented = useStack ? null : ArrayPool<float>.Shared.Rent(_dimensions);
        Span<float> residual = useStack
            ? stackalloc float[StackallocThresholdFloats].Slice(0, _dimensions)
            : rented!.AsSpan(0, _dimensions);

        try
        {
            vector.CopyTo(residual);
            for (int level = 0; level < _levels; level++)
            {
                ReadOnlySpan<float> levelCentroids =
                    _centroids.AsSpan(level * Ksub * _dimensions, Ksub * _dimensions);
                int best = KMeans.FindNearest(residual, levelCentroids, Ksub, _dimensions);
                code[level] = (byte)best;

                ReadOnlySpan<float> chosen = levelCentroids.Slice(best * _dimensions, _dimensions);
                TensorPrimitives.Subtract(residual, chosen, residual);
            }
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<float>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> code, Span<float> vector)
    {
        EnsureTrained();
        if (vector.Length != _dimensions)
        {
            throw new ArgumentException(
                $"vector 长度（{vector.Length}）与维度（{_dimensions}）不一致。",
                nameof(vector));
        }
        if (code.Length < _levels)
        {
            throw new ArgumentException(
                $"code 缓冲过小：需要 ≥ {_levels}，实际 {code.Length}。",
                nameof(code));
        }

        vector.Clear();
        for (int level = 0; level < _levels; level++)
        {
            int k = code[level];
            ReadOnlySpan<float> chosen =
                _centroids.AsSpan(level * Ksub * _dimensions + k * _dimensions, _dimensions);
            TensorPrimitives.Add(vector, chosen, vector);
        }
    }

    /// <summary>
    /// 为查询向量构建 ADC 打分内核。
    /// </summary>
    /// <param name="query">查询向量，长度 = <see cref="Dimensions"/>。</param>
    /// <returns>对单条 RQ 编码计算近似 L2² 的 <see cref="IQuantizedScorer"/>。</returns>
    public IQuantizedScorer BuildScorer(ReadOnlySpan<float> query)
    {
        EnsureTrained();
        if (query.Length != _dimensions)
        {
            throw new ArgumentException(
                $"query 长度（{query.Length}）与维度（{_dimensions}）不一致。",
                nameof(query));
        }

        return new RqDecompressScorer(this, query);
    }

    private void EnsureTrained()
    {
        if (!_trained)
        {
            throw new InvalidOperationException("ResidualQuantizer 尚未调用 Train。");
        }
    }

    private sealed class RqDecompressScorer : IQuantizedScorer
    {
        private readonly ResidualQuantizer _owner;
        private readonly float[] _query;

        public RqDecompressScorer(ResidualQuantizer owner, ReadOnlySpan<float> query)
        {
            _owner = owner;
            _query = query.ToArray();
        }

        public int CodeBytes => _owner._levels;

        public float Score(ReadOnlySpan<byte> code)
        {
            int dim = _owner._dimensions;
            int levels = _owner._levels;
            if (code.Length < levels)
            {
                throw new ArgumentException(
                    $"code 缓冲过小：需要 ≥ {levels}，实际 {code.Length}。",
                    nameof(code));
            }

            bool useStack = dim <= StackallocThresholdFloats;
            float[]? rented = useStack ? null : ArrayPool<float>.Shared.Rent(dim);
            Span<float> recon = useStack
                ? stackalloc float[StackallocThresholdFloats].Slice(0, dim)
                : rented!.AsSpan(0, dim);

            try
            {
                recon.Clear();
                ReadOnlySpan<float> centroids = _owner._centroids;
                for (int level = 0; level < levels; level++)
                {
                    int k = code[level];
                    ReadOnlySpan<float> chosen =
                        centroids.Slice(level * Ksub * dim + k * dim, dim);
                    TensorPrimitives.Add(recon, chosen, recon);
                }

                // L2² (q - recon)
                TensorPrimitives.Subtract(_query, recon, recon);
                return TensorPrimitives.SumOfSquares<float>(recon);
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<float>.Shared.Return(rented);
                }
            }
        }
    }
}
