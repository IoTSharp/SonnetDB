using System.Numerics.Tensors;

namespace SonnetDB.Vector.Compression;

/// <summary>
/// 逐维 min / max 标量量化到 uint8（SQ8）。
/// </summary>
/// <remarks>
/// <para>
/// 对每个维度 <c>d</c> 在训练集上统计最小值 <c>min[d]</c> 与最大值 <c>max[d]</c>，
/// 量化公式：<c>q = clamp(round((x - min) / (max - min) × 255), 0, 255)</c>，
/// 反量化：<c>x ≈ min + q × (max - min) / 255</c>。
/// </para>
/// <para>
/// 内存压缩比 <c>4×</c>（fp32 → uint8）。一般 Recall@10 损失 &lt; 3pp（dim ≥ 64 时）。
/// 对维度间方差差异极大的数据集（如 BERT embedding）建议先做 L2 归一化。
/// </para>
/// </remarks>
public sealed class ScalarQuantizer8 : IVectorQuantizer
{
    private readonly int _dimensions;
    private readonly float[] _min;
    private readonly float[] _scale;     // (max - min) / 255
    private readonly float[] _invScale;  // 255 / (max - min)，零方差维度为 0
    private bool _trained;

    /// <summary>
    /// 初始化新的 <see cref="ScalarQuantizer8"/> 实例。
    /// </summary>
    /// <param name="dimensions">向量维度。</param>
    public ScalarQuantizer8(int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        _dimensions = dimensions;
        _min = new float[dimensions];
        _scale = new float[dimensions];
        _invScale = new float[dimensions];
    }

    /// <inheritdoc />
    public QuantizerKind Kind => QuantizerKind.Sq8;

    /// <inheritdoc />
    public int Dimensions => _dimensions;

    /// <inheritdoc />
    public int CodeBytes => _dimensions;

    /// <inheritdoc />
    public bool IsTrained => _trained;

    /// <summary>逐维最小值（训练后只读快照）。</summary>
    internal ReadOnlySpan<float> Min => _min;

    /// <summary>逐维 step = (max - min) / 255（训练后只读快照）。</summary>
    internal ReadOnlySpan<float> Scale => _scale;

    /// <summary>
    /// 从持久化的 min/scale 数组直接装载已训练状态（仅供 QuantizerSerializer 使用）。
    /// </summary>
    internal void LoadState(ReadOnlySpan<float> min, ReadOnlySpan<float> scale)
    {
        if (min.Length != _dimensions || scale.Length != _dimensions)
        {
            throw new ArgumentException("min / scale 长度与 Dimensions 不一致。");
        }
        min.CopyTo(_min);
        scale.CopyTo(_scale);
        for (int d = 0; d < _dimensions; d++)
        {
            _invScale[d] = scale[d] > 0f ? 1f / scale[d] : 0f;
        }
        _trained = true;
    }

    /// <inheritdoc />
    public void Train(ReadOnlySpan<float> data, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        long expected = (long)count * _dimensions;
        if (data.Length != expected)
        {
            throw new ArgumentException(
                $"data 长度（{data.Length}）与 count × dimensions（{expected}）不一致。",
                nameof(data));
        }

        // 初始化 min/max 为首向量
        ReadOnlySpan<float> first = data[.._dimensions];
        first.CopyTo(_min);
        float[] maxArr = new float[_dimensions];
        first.CopyTo(maxArr);

        // 逐行更新 min / max；TensorPrimitives 提供逐元素 Min/Max
        for (int i = 1; i < count; i++)
        {
            ReadOnlySpan<float> row = data.Slice(i * _dimensions, _dimensions);
            TensorPrimitives.Min(_min, row, _min);
            TensorPrimitives.Max(maxArr, row, maxArr);
        }

        for (int d = 0; d < _dimensions; d++)
        {
            float range = maxArr[d] - _min[d];
            if (range <= 0f)
            {
                _scale[d] = 0f;
                _invScale[d] = 0f;
            }
            else
            {
                _scale[d] = range / 255f;
                _invScale[d] = 255f / range;
            }
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
                $"向量维度（{vector.Length}）与量化器维度（{_dimensions}）不一致。",
                nameof(vector));
        }
        if (code.Length < _dimensions)
        {
            throw new ArgumentException(
                $"code 缓冲过小：需要 ≥ {_dimensions}，实际 {code.Length}。",
                nameof(code));
        }

        for (int d = 0; d < _dimensions; d++)
        {
            float inv = _invScale[d];
            if (inv == 0f)
            {
                code[d] = 0;
                continue;
            }
            float q = MathF.Round((vector[d] - _min[d]) * inv);
            if (q < 0f)
            {
                code[d] = 0;
            }
            else if (q > 255f)
            {
                code[d] = 255;
            }
            else
            {
                code[d] = (byte)q;
            }
        }
    }

    /// <inheritdoc />
    public void Decode(ReadOnlySpan<byte> code, Span<float> vector)
    {
        EnsureTrained();
        if (code.Length < _dimensions)
        {
            throw new ArgumentException(
                $"code 长度（{code.Length}）与量化器维度（{_dimensions}）不一致。",
                nameof(code));
        }
        if (vector.Length < _dimensions)
        {
            throw new ArgumentException(
                $"vector 缓冲过小：需要 ≥ {_dimensions}，实际 {vector.Length}。",
                nameof(vector));
        }

        for (int d = 0; d < _dimensions; d++)
        {
            vector[d] = _min[d] + code[d] * _scale[d];
        }
    }

    /// <inheritdoc />
    public IQuantizedScorer BuildScorer(ReadOnlySpan<float> query)
    {
        EnsureTrained();
        if (query.Length != _dimensions)
        {
            throw new ArgumentException(
                $"query 维度（{query.Length}）与量化器维度（{_dimensions}）不一致。",
                nameof(query));
        }
        return new Sq8DecompressScorer(this, query);
    }

    private sealed class Sq8DecompressScorer : IQuantizedScorer
    {
        private readonly ScalarQuantizer8 _q;
        private readonly float[] _query;

        public Sq8DecompressScorer(ScalarQuantizer8 q, ReadOnlySpan<float> query)
        {
            _q = q;
            _query = query.ToArray();
        }

        public int CodeBytes => _q._dimensions;

        public float Score(ReadOnlySpan<byte> code)
        {
            int dim = _q._dimensions;
            float[] min = _q._min;
            float[] scale = _q._scale;
            float sum = 0f;
            for (int d = 0; d < dim; d++)
            {
                float reconstructed = min[d] + code[d] * scale[d];
                float diff = _query[d] - reconstructed;
                sum += diff * diff;
            }
            return sum;
        }
    }

    private void EnsureTrained()
    {
        if (!_trained)
        {
            throw new InvalidOperationException("ScalarQuantizer8 尚未训练，请先调用 Train。");
        }
    }
}
