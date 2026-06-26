using System.Buffers;

namespace SonnetDB.Vector.Compression;

/// <summary>
/// 优化乘积量化（Optimized Product Quantization, OPQ）量化器。
/// </summary>
/// <remarks>
/// <para>
/// OPQ 在 <see cref="ProductQuantizer"/> 之外学习一个 D×D 正交旋转矩阵 R，
/// 把原向量 <c>x</c> 旋转到 <c>R·x</c> 后再做 PQ 编码，从而最大化 PQ 子空间内的
/// 信息保留度。当数据各维度方差不均衡或子空间相关时，相比纯 PQ 通常在相同
/// 字节预算下可显著提升召回。
/// </para>
/// <para>
/// 训练采用经典交替优化：
/// <list type="number">
/// <item>固定 R，在旋转后数据 <c>Y = X·R^T</c> 上训练 PQ；</item>
/// <item>固定 PQ 编码，求解正交 Procrustes 得到新的 R：
/// <c>R = V·U^T</c>，其中 <c>X^T·Ŷ = U·Σ·V^T</c>（参见
/// <see cref="JacobiSvd.SolveOrthogonalProcrustes"/>）。</item>
/// </list>
/// 反复迭代后，最后再用最终 R 训练一次 PQ 收尾，保证 R 与 PQ 一致。
/// </para>
/// <para>
/// 参考：Ge et al., <i>Optimized Product Quantization</i> (CVPR 2013)。
/// </para>
/// </remarks>
public sealed class OptimizedProductQuantizer : IVectorQuantizer
{
    private const int StackallocThresholdFloats = 1024;

    private readonly int _dimensions;
    private readonly int _m;
    private readonly int _opqIterations;
    private readonly int _pqMaxIterations;
    private readonly int? _seed;
    private readonly float[] _rotation; // D×D row-major, R[i,j] = _rotation[i*D+j]

    private ProductQuantizer _pq;
    private bool _trained;

    /// <summary>
    /// 初始化新的 <see cref="OptimizedProductQuantizer"/> 实例。
    /// </summary>
    /// <param name="dimensions">向量维度，必须能被 <paramref name="m"/> 整除。</param>
    /// <param name="m">子空间数量。</param>
    /// <param name="opqIterations">OPQ 外层交替迭代次数，默认 10。</param>
    /// <param name="pqMaxIterations">每次内部 PQ 训练的 K-Means 最大迭代次数，默认 25。</param>
    /// <param name="seed">随机种子，<see langword="null"/> 表示非确定性。</param>
    public OptimizedProductQuantizer(
        int dimensions,
        int m,
        int opqIterations = 10,
        int pqMaxIterations = 25,
        int? seed = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(m);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(opqIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pqMaxIterations);
        if (dimensions % m != 0)
        {
            throw new ArgumentException("dimensions 必须能被 m 整除。", nameof(m));
        }

        _dimensions = dimensions;
        _m = m;
        _opqIterations = opqIterations;
        _pqMaxIterations = pqMaxIterations;
        _seed = seed;
        _rotation = new float[dimensions * dimensions];
        // R 初值为单位阵
        for (int i = 0; i < dimensions; i++)
        {
            _rotation[i * dimensions + i] = 1f;
        }
        _pq = new ProductQuantizer(dimensions, m, pqMaxIterations, seed);
    }

    /// <inheritdoc/>
    public QuantizerKind Kind => QuantizerKind.Opq;

    /// <inheritdoc/>
    public int Dimensions => _dimensions;

    /// <inheritdoc/>
    public int CodeBytes => _pq.CodeBytes;

    /// <inheritdoc/>
    public bool IsTrained => _trained;

    /// <summary>子空间数量。</summary>
    public int M => _m;

    /// <summary>OPQ 外层交替迭代次数。</summary>
    public int OpqIterations => _opqIterations;

    /// <summary>正交旋转矩阵 R（行优先，仅供测试使用）。</summary>
    internal ReadOnlySpan<float> Rotation => _rotation;

    /// <summary>内部 PQ（仅供测试使用）。</summary>
    internal ProductQuantizer InnerPq => _pq;

    /// <summary>
    /// 从持久化数据装载已训练状态（仅供 QuantizerSerializer 使用）。
    /// </summary>
    /// <param name="rotation">D×D 行优先旋转矩阵。</param>
    /// <param name="pqCentroids">内部 PQ 的 <c>M × Ksub × SubDim</c> 行优先 centroids。</param>
    internal void LoadState(ReadOnlySpan<float> rotation, ReadOnlySpan<float> pqCentroids)
    {
        if (rotation.Length != _rotation.Length)
        {
            throw new ArgumentException(
                $"rotation 长度（{rotation.Length}）与 D×D（{_rotation.Length}）不一致。",
                nameof(rotation));
        }
        rotation.CopyTo(_rotation);
        _pq.LoadState(pqCentroids);
        _trained = true;
    }

    /// <inheritdoc/>
    public void Train(ReadOnlySpan<float> data, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (data.Length < (long)count * _dimensions)
        {
            throw new ArgumentException("data 长度不足 count × Dimensions。", nameof(data));
        }

        int d = _dimensions;
        long total = (long)count * d;

        // 旋转后训练数据缓冲 Y = X · R^T，长度 count × D
        float[] rotated = ArrayPool<float>.Shared.Rent((int)total);
        float[] reconstructed = ArrayPool<float>.Shared.Rent((int)total);
        float[] crossMat = new float[d * d];
        float[] newRotation = new float[d * d];

        // 编码缓冲：避免 in-loop stackalloc。M = _m 字节即可（每子空间 1 字节索引）。
        Span<byte> codeScratch = stackalloc byte[256];
        Span<byte> code = _m <= codeScratch.Length ? codeScratch[.._m] : new byte[_m];

        try
        {
            for (int iter = 0; iter < _opqIterations; iter++)
            {
                // 1. Y = X · R^T : y_i = R · x_i
                ApplyRotationBatch(_rotation, d, data, count, rotated.AsSpan(0, (int)total));

                // 2. 训练 PQ on Y（每次新建实例避免状态污染）
                var pq = new ProductQuantizer(d, _m, _pqMaxIterations, _seed);
                pq.Train(rotated.AsSpan(0, (int)total), count);

                // 3. 重建 Ŷ = decode(encode(Y))
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<float> yi = rotated.AsSpan(i * d, d);
                    pq.Encode(yi, code);
                    pq.Decode(code, reconstructed.AsSpan(i * d, d));
                }

                _pq = pq;

                // 4. 计算 A = X^T · Ŷ  (D × D) ：A[j,k] = Σ_i X[i,j] · Ŷ[i,k]
                Array.Clear(crossMat);
                for (int i = 0; i < count; i++)
                {
                    int xBase = i * d;
                    int yBase = i * d;
                    for (int j = 0; j < d; j++)
                    {
                        float xij = data[xBase + j];
                        if (xij == 0f) continue;
                        int aBase = j * d;
                        for (int k = 0; k < d; k++)
                        {
                            crossMat[aBase + k] += xij * reconstructed[yBase + k];
                        }
                    }
                }

                // 5. R_new = V · U^T，其中 SVD(A) = U Σ V^T
                JacobiSvd.SolveOrthogonalProcrustes(d, crossMat, newRotation);
                Array.Copy(newRotation, _rotation, d * d);
            }

            // 收尾：用最终 R 重训一次 PQ，保证 R 与 PQ 配对一致
            ApplyRotationBatch(_rotation, d, data, count, rotated.AsSpan(0, (int)total));
            var finalPq = new ProductQuantizer(d, _m, _pqMaxIterations, _seed);
            finalPq.Train(rotated.AsSpan(0, (int)total), count);
            _pq = finalPq;
            _trained = true;
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rotated);
            ArrayPool<float>.Shared.Return(reconstructed);
        }
    }

    /// <inheritdoc/>
    public void Encode(ReadOnlySpan<float> vector, Span<byte> code)
    {
        EnsureTrained();
        if (vector.Length < _dimensions)
        {
            throw new ArgumentException("vector 长度不足 Dimensions。", nameof(vector));
        }
        Span<float> rotated = _dimensions <= StackallocThresholdFloats
            ? stackalloc float[_dimensions]
            : new float[_dimensions];
        ApplyRotation(_rotation, _dimensions, vector, rotated);
        _pq.Encode(rotated, code);
    }

    /// <inheritdoc/>
    public void Decode(ReadOnlySpan<byte> code, Span<float> vector)
    {
        EnsureTrained();
        if (vector.Length < _dimensions)
        {
            throw new ArgumentException("vector 长度不足 Dimensions。", nameof(vector));
        }
        Span<float> rotated = _dimensions <= StackallocThresholdFloats
            ? stackalloc float[_dimensions]
            : new float[_dimensions];
        _pq.Decode(code, rotated);
        // x = R^T · y
        ApplyTransposeRotation(_rotation, _dimensions, rotated, vector);
    }

    /// <summary>
    /// 基于查询向量构造 ADC 打分内核。查询会先被旋转到 PQ 训练所在空间。
    /// </summary>
    /// <param name="query">查询向量，长度 = <see cref="Dimensions"/>。</param>
    /// <returns>用于扫描压缩编码的 <see cref="IQuantizedScorer"/>。</returns>
    public IQuantizedScorer BuildScorer(ReadOnlySpan<float> query)
    {
        EnsureTrained();
        if (query.Length < _dimensions)
        {
            throw new ArgumentException("query 长度不足 Dimensions。", nameof(query));
        }
        Span<float> rotated = _dimensions <= StackallocThresholdFloats
            ? stackalloc float[_dimensions]
            : new float[_dimensions];
        ApplyRotation(_rotation, _dimensions, query, rotated);
        return _pq.BuildScorer(rotated);
    }

    private void EnsureTrained()
    {
        if (!_trained)
        {
            throw new InvalidOperationException("OptimizedProductQuantizer 尚未训练，请先调用 Train。");
        }
    }

    private static void ApplyRotation(ReadOnlySpan<float> r, int d, ReadOnlySpan<float> x, Span<float> y)
    {
        // y = R · x : y[i] = Σ_j R[i,j] · x[j]
        for (int i = 0; i < d; i++)
        {
            float sum = 0f;
            int rowBase = i * d;
            for (int j = 0; j < d; j++)
            {
                sum += r[rowBase + j] * x[j];
            }
            y[i] = sum;
        }
    }

    private static void ApplyTransposeRotation(ReadOnlySpan<float> r, int d, ReadOnlySpan<float> y, Span<float> x)
    {
        // x = R^T · y : x[j] = Σ_i R[i,j] · y[i]
        x[..d].Clear();
        for (int i = 0; i < d; i++)
        {
            float yi = y[i];
            int rowBase = i * d;
            for (int j = 0; j < d; j++)
            {
                x[j] += r[rowBase + j] * yi;
            }
        }
    }

    private static void ApplyRotationBatch(ReadOnlySpan<float> r, int d, ReadOnlySpan<float> data, int count, Span<float> output)
    {
        for (int i = 0; i < count; i++)
        {
            ApplyRotation(r, d, data.Slice(i * d, d), output.Slice(i * d, d));
        }
    }
}
