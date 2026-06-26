using SonnetDB.Vector.Index.Ivf;

namespace SonnetDB.Vector.Compression;

/// <summary>
/// 乘积量化（Product Quantization, PQ）码本。
/// </summary>
/// <remarks>
/// <para>
/// 将 <c>Dimensions</c> 维向量切分为 <c>M</c> 个子空间（每个子空间维度 <c>SubDim = Dimensions / M</c>），
/// 每个子空间独立训练 <c>K_sub = 2^NBits</c>（当前固定 256）个聚类中心。
/// </para>
/// <para>
/// 编码：每个子向量映射到最近中心索引，生成 <c>M</c> 字节编码。
/// 查询：构造 <c>M × K_sub</c> 的距离查找表（LUT），随后对每个候选向量做 M 次表查询求和（ADC）。
/// </para>
/// <para>
/// 当前固定使用 L2 平方距离训练与编码；与外层 IVF 度量解耦
/// （Cosine / InnerProduct 场景下需要外部完成归一化或改造为 IVF-PQ-IP 变体，
/// 后续 Milestone 处理）。
/// </para>
/// </remarks>
public sealed class PqCodebook
{
    /// <summary>K_sub 子量化器中心数（当前固定 256）。</summary>
    public const int Ksub = 256;

    private readonly int _dimensions;
    private readonly int _m;
    private readonly int _subDim;
    private readonly float[] _centroids; // [m * Ksub * subDim]

    /// <summary>
    /// 初始化新的 <see cref="PqCodebook"/> 实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须能被 <paramref name="m"/> 整除。</param>
    /// <param name="m">子空间数量。</param>
    public PqCodebook(int dimensions, int m)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m);
        if (dimensions % m != 0)
        {
            throw new ArgumentException(
                $"dimensions（{dimensions}）必须能被 m（{m}）整除。",
                nameof(dimensions));
        }
        _dimensions = dimensions;
        _m = m;
        _subDim = dimensions / m;
        _centroids = new float[(long)m * Ksub * _subDim];
    }

    /// <summary>向量维度。</summary>
    public int Dimensions => _dimensions;

    /// <summary>子空间数量。</summary>
    public int M => _m;

    /// <summary>每个子空间的维度。</summary>
    public int SubDim => _subDim;

    /// <summary>每条编码占用字节数（与 <see cref="M"/> 相等）。</summary>
    public int CodeBytes => _m;

    /// <summary>训练后的码本数据（<c>M × Ksub × SubDim</c> 行优先）。</summary>
    internal ReadOnlySpan<float> Centroids => _centroids;

    /// <summary>
    /// 从持久化的 centroids 数据直接装载（仅供 QuantizerSerializer 使用）。
    /// </summary>
    internal void LoadCentroids(ReadOnlySpan<float> centroids)
    {
        if (centroids.Length != _centroids.Length)
        {
            throw new ArgumentException(
                $"centroids 长度（{centroids.Length}）与码本布局（{_centroids.Length}）不一致。");
        }
        centroids.CopyTo(_centroids);
    }

    /// <summary>
    /// 在训练数据上训练码本。
    /// </summary>
    /// <param name="data">行优先训练数据，长度 = <paramref name="count"/> × <see cref="Dimensions"/>。</param>
    /// <param name="count">训练向量数。</param>
    /// <param name="maxIterations">K-Means 最大迭代次数。</param>
    /// <param name="seed">K-Means 种子，<see langword="null"/> 表示非确定性。</param>
    public void Train(ReadOnlySpan<float> data, int count, int maxIterations, int? seed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);
        if (count < Ksub)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count, $"PQ 训练向量数（{count}）少于子量化器中心数 Ksub（{Ksub}）。");
        }
        long expected = (long)count * _dimensions;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"data 长度（{data.Length}）与 count × dimensions（{expected}）不一致。",
                nameof(data));
        }

        // 为每个子空间复制训练子向量，分别训练。
        var subBuffer = new float[(long)count * _subDim];
        for (int sub = 0; sub < _m; sub++)
        {
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<float> src = data.Slice(i * _dimensions + sub * _subDim, _subDim);
                src.CopyTo(subBuffer.AsSpan(i * _subDim, _subDim));
            }

            int? subSeed = seed.HasValue ? seed.Value + sub : null;
            KMeans.Train(
                subBuffer,
                count,
                _subDim,
                Ksub,
                maxIterations,
                subSeed,
                out float[] subCentroids,
                out _);

            subCentroids.AsSpan().CopyTo(_centroids.AsSpan(sub * Ksub * _subDim, Ksub * _subDim));
        }
    }

    /// <summary>
    /// 将单个向量编码为 <see cref="CodeBytes"/> 字节。
    /// </summary>
    /// <param name="vector">输入向量，长度 = <see cref="Dimensions"/>。</param>
    /// <param name="code">输出编码缓冲，长度 ≥ <see cref="CodeBytes"/>。</param>
    public void Encode(ReadOnlySpan<float> vector, Span<byte> code)
    {
        if (vector.Length != _dimensions)
        {
            throw new ArgumentException(
                $"向量维度（{vector.Length}）与码本维度（{_dimensions}）不一致。",
                nameof(vector));
        }
        if (code.Length < _m)
        {
            throw new ArgumentException(
                $"code 缓冲过小：需要 ≥ {_m}，实际 {code.Length}。",
                nameof(code));
        }

        for (int sub = 0; sub < _m; sub++)
        {
            ReadOnlySpan<float> subVec = vector.Slice(sub * _subDim, _subDim);
            ReadOnlySpan<float> subCentroids = _centroids.AsSpan(sub * Ksub * _subDim, Ksub * _subDim);
            int best = KMeans.FindNearest(subVec, subCentroids, Ksub, _subDim);
            code[sub] = (byte)best;
        }
    }

    /// <summary>
    /// 为单个查询向量构建 ADC 距离查找表（L2 平方距离）。
    /// </summary>
    /// <param name="query">查询向量，长度 = <see cref="Dimensions"/>。</param>
    /// <param name="lut">输出查找表，长度 ≥ <see cref="M"/> × <see cref="Ksub"/>，按 <c>[sub, k]</c> 行优先布局。</param>
    public void BuildLookupTable(ReadOnlySpan<float> query, Span<float> lut)
    {
        if (query.Length != _dimensions)
        {
            throw new ArgumentException(
                $"查询维度（{query.Length}）与码本维度（{_dimensions}）不一致。",
                nameof(query));
        }
        int needed = _m * Ksub;
        if (lut.Length < needed)
        {
            throw new ArgumentException(
                $"lut 缓冲过小：需要 ≥ {needed}，实际 {lut.Length}。",
                nameof(lut));
        }

        for (int sub = 0; sub < _m; sub++)
        {
            ReadOnlySpan<float> subQuery = query.Slice(sub * _subDim, _subDim);
            ReadOnlySpan<float> subCentroids = _centroids.AsSpan(sub * Ksub * _subDim, Ksub * _subDim);
            Span<float> row = lut.Slice(sub * Ksub, Ksub);
            for (int k = 0; k < Ksub; k++)
            {
                row[k] = KMeans.L2Squared(subQuery, subCentroids.Slice(k * _subDim, _subDim));
            }
        }
    }

    /// <summary>
    /// 使用查找表对单条编码累加 ADC 距离。
    /// </summary>
    /// <param name="lut">由 <see cref="BuildLookupTable"/> 生成的查找表。</param>
    /// <param name="code">PQ 编码（长度 = <see cref="CodeBytes"/>）。</param>
    /// <returns>近似 L2 平方距离。</returns>
    public float ScoreWithLookup(ReadOnlySpan<float> lut, ReadOnlySpan<byte> code)
    {
        float sum = 0f;
        for (int sub = 0; sub < _m; sub++)
        {
            sum += lut[sub * Ksub + code[sub]];
        }
        return sum;
    }
}
