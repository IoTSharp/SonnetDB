using System.Globalization;
using System.Text;

namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>
/// 简化版 t-digest（Ben Haim &amp; Tom-Tov 风格的 merging digest），
/// 用于在有限内存下估算分位数。
/// <para>
/// 算法概要：维护一组按均值排序的 centroid（mean, weight），新点先附加到缓冲区，
/// 缓冲区到达阈值或显式 <see cref="Flush"/> 时与现有 centroid 合并；
/// 合并时按 q ∈ [0,1] 的位置限制每个 centroid 的最大权重
/// k(q) ≈ δ * q * (1−q)，δ 即 <see cref="Compression"/>。
/// </para>
/// <para>
/// 该实现以正确性与可合并性为先；性能足以服务 SQL 聚合（百万级点 / 秒级延迟）。
/// 跨段合并通过 <see cref="Merge(TDigest)"/> 实现：把对方的 centroid 当作高权重点
/// 重新合并到本实例即可。
/// </para>
/// </summary>
internal sealed class TDigest
{
    private const int DefaultCompression = 100;
    private const int BufferFactor = 5;

    private readonly int _compression;
    private readonly List<Centroid> _centroids;
    private readonly List<Centroid> _buffer;
    private readonly int _bufferLimit;

    public TDigest(int compression = DefaultCompression)
    {
        if (compression < 10)
            throw new ArgumentOutOfRangeException(nameof(compression), "压缩参数必须 ≥ 10。");

        _compression = compression;
        _centroids = new List<Centroid>(compression * 2);
        _buffer = new List<Centroid>(compression * BufferFactor);
        _bufferLimit = compression * BufferFactor;
    }

    /// <summary>已累加的样本数（等价于所有 centroid 权重之和）。</summary>
    public long Count { get; private set; }

    /// <summary>当前 centroid 数（仅供测试 / 诊断）。</summary>
    internal int CentroidCount => _centroids.Count + _buffer.Count;

    /// <summary>累加一个数值（权重为 1）。</summary>
    public void Add(double value)
    {
        if (double.IsNaN(value))
            return;

        _buffer.Add(new Centroid(value, 1));
        Count++;
        if (_buffer.Count >= _bufferLimit)
            Flush();
    }

    /// <summary>合并另一个 t-digest。</summary>
    public void Merge(TDigest other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Count == 0)
            return;

        // 把对方的 centroid（包括缓冲区）当作高权重点附加到本缓冲区。
        foreach (var c in other._centroids)
            _buffer.Add(c);
        foreach (var c in other._buffer)
            _buffer.Add(c);
        Count += other.Count;
        Flush();
    }

    /// <summary>
    /// 估算分位数 q（0 &lt; q ≤ 1）。空 digest 返回 <see cref="double.NaN"/>。
    /// </summary>
    public double Quantile(double q)
    {
        if (q <= 0 || q > 1)
            throw new ArgumentOutOfRangeException(nameof(q), "分位点必须落在 (0, 1] 区间。");

        Flush();
        if (_centroids.Count == 0)
            return double.NaN;
        if (_centroids.Count == 1)
            return _centroids[0].Mean;

        double targetWeight = q * Count;
        double cumulative = 0;
        for (int i = 0; i < _centroids.Count; i++)
        {
            var c = _centroids[i];
            double half = c.Weight / 2.0;

            if (cumulative + half >= targetWeight)
            {
                if (i == 0)
                    return c.Mean;

                var prev = _centroids[i - 1];
                double prevHalf = prev.Weight / 2.0;
                double leftEdge = cumulative - prevHalf + prev.Weight;
                // 在 prev 与 c 之间线性插值：cumulative 是 prev 中心位置的权重位置，
                // c 中心位置在 cumulative + half。
                double prevCenter = cumulative - half + prev.Weight / 2.0; // 即 prev 的累积位置
                double cCenter = cumulative + half;
                double t = (targetWeight - prevCenter) / (cCenter - prevCenter);
                return prev.Mean + t * (c.Mean - prev.Mean);
            }

            cumulative += c.Weight;
        }

        return _centroids[^1].Mean;
    }

    /// <summary>序列化为紧凑二进制（用于 tdigest_agg 输出）。布局：</summary>
    /// <remarks>
    /// <para>头部 4 字节："TD01"；4 字节 little-endian centroid 数 N；接 N × (8 + 8) 字节（mean、weight）。</para>
    /// </remarks>
    public byte[] Serialize()
    {
        Flush();
        int n = _centroids.Count;
        var bytes = new byte[8 + n * 16];
        bytes[0] = (byte)'T';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'0';
        bytes[3] = (byte)'1';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), n);
        for (int i = 0; i < n; i++)
        {
            int o = 8 + i * 16;
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(o, 8), _centroids[i].Mean);
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(o + 8, 8), _centroids[i].Weight);
        }
        return bytes;
    }

    /// <summary>
    /// 从 <see cref="Serialize"/> 生成的二进制载荷恢复 t-digest。
    /// </summary>
    /// <param name="bytes">二进制载荷。</param>
    /// <returns>恢复后的 t-digest。</returns>
    /// <exception cref="InvalidDataException">载荷格式不合法。</exception>
    public static TDigest Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8
            || bytes[0] != (byte)'T'
            || bytes[1] != (byte)'D'
            || bytes[2] != (byte)'0'
            || bytes[3] != (byte)'1')
        {
            throw new InvalidDataException("TDigest magic 不匹配。");
        }

        int n = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);
        long expectedLength = 8L + (long)n * 16L;
        if (n < 0 || bytes.Length != expectedLength)
            throw new InvalidDataException("TDigest centroid 数量或长度非法。");

        var digest = new TDigest();
        long totalCount = 0;
        double previousMean = double.NegativeInfinity;
        for (int i = 0; i < n; i++)
        {
            int offset = 8 + i * 16;
            double mean = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[offset..(offset + 8)]);
            double weight = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes[(offset + 8)..(offset + 16)]);
            if (double.IsNaN(mean) || mean < previousMean
                || !double.IsFinite(weight) || weight <= 0 || weight != Math.Truncate(weight)
                || weight >= 9223372036854775808d)
                throw new InvalidDataException("TDigest centroid 内容非法。");

            long count = (long)weight;
            if (count > long.MaxValue - totalCount)
                throw new InvalidDataException("TDigest 样本总数超出 Int64 范围。");

            digest._centroids.Add(new Centroid(mean, weight));
            totalCount += count;
            previousMean = mean;
        }

        digest.Count = totalCount;
        return digest;
    }

    /// <summary>序列化为人类可读 JSON 字符串。</summary>
    public string ToJson()
    {
        Flush();
        var sb = new StringBuilder();
        sb.Append("{\"compression\":").Append(_compression);
        sb.Append(",\"count\":").Append(Count);
        sb.Append(",\"centroids\":[");
        for (int i = 0; i < _centroids.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[')
              .Append(_centroids[i].Mean.ToString("R", CultureInfo.InvariantCulture))
              .Append(',')
              .Append(_centroids[i].Weight.ToString("R", CultureInfo.InvariantCulture))
              .Append(']');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    /// <summary>把缓冲区合并入主 centroid 列表，并按 k(q) 限重压缩。</summary>
    public void Flush()
    {
        if (_buffer.Count == 0)
            return;

        // 1. 合并 + 排序
        var all = new List<Centroid>(_centroids.Count + _buffer.Count);
        all.AddRange(_centroids);
        all.AddRange(_buffer);
        _buffer.Clear();
        all.Sort(static (a, b) => a.Mean.CompareTo(b.Mean));

        _centroids.Clear();

        // 2. 单遍合并：累积 q ∈ [0,1]，把 centroid 合并到当前桶直到 q 超过 k(q+w/n) 限制。
        double total = 0;
        foreach (var c in all)
            total += c.Weight;

        double cumulative = 0;
        Centroid current = all[0];
        cumulative = current.Weight;
        for (int i = 1; i < all.Count; i++)
        {
            var c = all[i];
            double q1 = (cumulative - current.Weight) / total;
            double q2 = (cumulative + c.Weight) / total;
            double maxWeight = total * MaxWeightRatio(q1, q2);
            if (current.Weight + c.Weight <= maxWeight)
            {
                double newWeight = current.Weight + c.Weight;
                double newMean = current.Mean + (c.Mean - current.Mean) * c.Weight / newWeight;
                current = new Centroid(newMean, newWeight);
                cumulative += c.Weight;
            }
            else
            {
                _centroids.Add(current);
                current = c;
                cumulative += c.Weight;
            }
        }
        _centroids.Add(current);
    }

    private double MaxWeightRatio(double q1, double q2)
    {
        // k(q) = δ / (2π) * 2 arcsin(2q − 1) 是标准形式；
        // 这里用更廉价的近似 4 * q * (1−q) / δ，效果对常用分位点（p50/p90/p95/p99）足够。
        double q = (q1 + q2) / 2.0;
        return 4.0 * q * (1 - q) / _compression;
    }

    private readonly record struct Centroid(double Mean, double Weight);
}
