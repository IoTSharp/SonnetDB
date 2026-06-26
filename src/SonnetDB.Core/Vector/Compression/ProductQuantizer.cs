namespace SonnetDB.Vector.Compression;

/// <summary>
/// 乘积量化（Product Quantization, PQ）量化器，封装 <see cref="PqCodebook"/>
/// 实现统一 <see cref="IVectorQuantizer"/> 契约。
/// </summary>
/// <remarks>
/// <para>
/// 训练后每个向量被切分为 <c>M</c> 个子向量并各自映射到 256 个聚类中心之一，
/// 形成 <c>M</c> 字节编码（<see cref="IVectorQuantizer.CodeBytes"/> = <c>M</c>）。
/// </para>
/// <para>
/// 查询路径通过 <see cref="BuildScorer"/> 构造 <see cref="IQuantizedScorer"/>，
/// 该 scorer 持有 <c>M × 256</c> 距离查找表（LUT），按 ADC（Asymmetric Distance
/// Computation）方式对每条编码做 M 次表查询求和。
/// </para>
/// </remarks>
public sealed class ProductQuantizer : IVectorQuantizer
{
    private readonly PqCodebook _codebook;
    private readonly int _maxIterations;
    private readonly int? _seed;
    private bool _trained;

    /// <summary>
    /// 初始化新的 <see cref="ProductQuantizer"/> 实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须能被 <paramref name="m"/> 整除。</param>
    /// <param name="m">子空间数量。</param>
    /// <param name="maxIterations">K-Means 最大迭代次数，默认 25。</param>
    /// <param name="seed">K-Means 种子，<see langword="null"/> 表示非确定性。</param>
    public ProductQuantizer(int dimensions, int m, int maxIterations = 25, int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);
        _codebook = new PqCodebook(dimensions, m);
        _maxIterations = maxIterations;
        _seed = seed;
    }

    /// <summary>
    /// 包装一个已训练的 <see cref="PqCodebook"/>（仅供 IvfPqIndex 等内部组件复用，
    /// 不复制 centroids，所有权由调用方保持）。
    /// </summary>
    internal ProductQuantizer(PqCodebook trainedCodebook)
    {
        ArgumentNullException.ThrowIfNull(trainedCodebook);
        _codebook = trainedCodebook;
        _maxIterations = 1;
        _seed = null;
        _trained = true;
    }

    /// <inheritdoc/>
    public QuantizerKind Kind => QuantizerKind.Pq;

    /// <inheritdoc/>
    public int Dimensions => _codebook.Dimensions;

    /// <summary>子空间数量。</summary>
    public int M => _codebook.M;

    /// <summary>每个子空间的维度。</summary>
    public int SubDim => _codebook.SubDim;

    /// <inheritdoc/>
    public int CodeBytes => _codebook.CodeBytes;

    /// <inheritdoc/>
    public bool IsTrained => _trained;

    /// <summary>底层码本（仅用于诊断 / 持久化集成）。</summary>
    internal PqCodebook Codebook => _codebook;

    /// <summary>
    /// 从持久化数据装载已训练状态（仅供 QuantizerSerializer 使用）。
    /// </summary>
    internal void LoadState(ReadOnlySpan<float> centroids)
    {
        _codebook.LoadCentroids(centroids);
        _trained = true;
    }

    /// <inheritdoc/>
    public void Train(ReadOnlySpan<float> data, int count)
    {
        _codebook.Train(data, count, _maxIterations, _seed);
        _trained = true;
    }

    /// <inheritdoc/>
    public void Encode(ReadOnlySpan<float> vector, Span<byte> code)
    {
        EnsureTrained();
        _codebook.Encode(vector, code);
    }

    /// <inheritdoc/>
    public void Decode(ReadOnlySpan<byte> code, Span<float> vector)
    {
        EnsureTrained();
        if (code.Length < _codebook.CodeBytes)
        {
            throw new ArgumentException(
                $"code 缓冲过小：需要 ≥ {_codebook.CodeBytes}，实际 {code.Length}。",
                nameof(code));
        }
        if (vector.Length != _codebook.Dimensions)
        {
            throw new ArgumentException(
                $"vector 长度（{vector.Length}）与维度（{_codebook.Dimensions}）不一致。",
                nameof(vector));
        }

        int subDim = _codebook.SubDim;
        int m = _codebook.M;
        ReadOnlySpan<float> centroids = _codebook.Centroids;
        for (int sub = 0; sub < m; sub++)
        {
            int k = code[sub];
            ReadOnlySpan<float> centroid = centroids.Slice(sub * PqCodebook.Ksub * subDim + k * subDim, subDim);
            centroid.CopyTo(vector.Slice(sub * subDim, subDim));
        }
    }

    /// <summary>
    /// 为查询向量构建 ADC 打分内核。
    /// </summary>
    /// <param name="query">查询向量，长度 = <see cref="Dimensions"/>。</param>
    /// <returns>持有预计算 LUT 的 <see cref="IQuantizedScorer"/>。</returns>
    public IQuantizedScorer BuildScorer(ReadOnlySpan<float> query)
    {
        EnsureTrained();
        var lut = new float[_codebook.M * PqCodebook.Ksub];
        _codebook.BuildLookupTable(query, lut);
        return new PqAdcScorer(lut, _codebook.M);
    }

    private void EnsureTrained()
    {
        if (!_trained)
        {
            throw new InvalidOperationException("ProductQuantizer 尚未调用 Train。");
        }
    }

    private sealed class PqAdcScorer : IQuantizedScorer
    {
        private readonly float[] _lut;
        private readonly int _m;

        public PqAdcScorer(float[] lut, int m)
        {
            _lut = lut;
            _m = m;
        }

        public int CodeBytes => _m;

        public float Score(ReadOnlySpan<byte> code)
        {
            if (code.Length < _m)
            {
                throw new ArgumentException(
                    $"code 缓冲过小：需要 ≥ {_m}，实际 {code.Length}。",
                    nameof(code));
            }

            ReadOnlySpan<float> lut = _lut;
            float sum = 0f;
            int m = _m;
            // 4 路展开降低循环开销；M 通常为 4/8/16/32。
            int i = 0;
            int unrollEnd = m - (m % 4);
            for (; i < unrollEnd; i += 4)
            {
                sum += lut[(i + 0) * PqCodebook.Ksub + code[i + 0]]
                     + lut[(i + 1) * PqCodebook.Ksub + code[i + 1]]
                     + lut[(i + 2) * PqCodebook.Ksub + code[i + 2]]
                     + lut[(i + 3) * PqCodebook.Ksub + code[i + 3]];
            }
            for (; i < m; i++)
            {
                sum += lut[i * PqCodebook.Ksub + code[i]];
            }
            return sum;
        }
    }
}
