using System.IO.Hashing;

namespace SonnetDB.Query.Functions.Aggregates;

/// <summary>
/// HyperLogLog 基数估算器（精度 14，16384 个 6 位寄存器）；
/// 标准误差约 0.81%。
/// <para>
/// 实现细节：
/// <list type="bullet">
/// <item>使用 <see cref="XxHash64"/> 对输入做 64 位哈希。</item>
/// <item>取哈希值高 14 位作为寄存器索引；剩余 50 位计算前导零数 + 1 写入寄存器。</item>
/// <item>合并通过逐寄存器取最大值实现。</item>
/// <item>估算公式：原始 HLL（α_m * m² / Σ 2^−Mᵢ），小基数走线性计数修正。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class HyperLogLog
{
    public const int Precision = 14;
    public const int RegisterCount = 1 << Precision; // 16384

    private const int RemainingBits = 64 - Precision;
    private const double AlphaMM = 0.7213 / (1.0 + 1.079 / RegisterCount) * RegisterCount * RegisterCount;

    private readonly byte[] _registers = new byte[RegisterCount];

    /// <summary>用 64 位 hash 累加。</summary>
    public void AddHash(ulong hash)
    {
        int index = (int)(hash >> RemainingBits);
        ulong remaining = (hash << Precision) | (1UL << (Precision - 1));
        // remaining 一定非零；前导零数 + 1
        byte rank = (byte)(System.Numerics.BitOperations.LeadingZeroCount(remaining) + 1);
        if (rank > _registers[index])
            _registers[index] = rank;
    }

    /// <summary>累加 double 值（按位 reinterpret 后哈希）。</summary>
    public void Add(double value)
    {
        if (double.IsNaN(value))
            return;
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buf, value);
        AddHash(XxHash64.HashToUInt64(buf));
    }

    /// <summary>累加 Int64 的完整 little-endian 位模式，避免转换为 Double 丢失整数身份。</summary>
    /// <param name="value">整数值。</param>
    public void Add(long value)
    {
        Span<byte> buf = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        AddHash(XxHash64.HashToUInt64(buf));
    }

    /// <summary>累加字符串值（UTF-8 编码后哈希）。</summary>
    public void Add(string value)
    {
        if (value is null)
            return;
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(value);
        if (byteCount <= 256)
        {
            Span<byte> buf = stackalloc byte[byteCount];
            System.Text.Encoding.UTF8.GetBytes(value, buf);
            AddHash(XxHash64.HashToUInt64(buf));
        }
        else
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            AddHash(XxHash64.HashToUInt64(bytes));
        }
    }

    /// <summary>合并另一个 HLL（必须使用相同精度）。</summary>
    public void Merge(HyperLogLog other)
    {
        ArgumentNullException.ThrowIfNull(other);
        for (int i = 0; i < RegisterCount; i++)
        {
            if (other._registers[i] > _registers[i])
                _registers[i] = other._registers[i];
        }
    }

    /// <summary>
    /// 序列化寄存器快照。
    /// </summary>
    /// <returns>包含 magic、精度与寄存器内容的二进制载荷。</returns>
    public byte[] Serialize()
    {
        var bytes = new byte[12 + RegisterCount];
        bytes[0] = (byte)'H';
        bytes[1] = (byte)'L';
        bytes[2] = (byte)'0';
        bytes[3] = (byte)'1';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), Precision);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), RegisterCount);
        _registers.CopyTo(bytes.AsSpan(12));
        return bytes;
    }

    /// <summary>
    /// 从 <see cref="Serialize"/> 生成的二进制载荷恢复 HyperLogLog。
    /// </summary>
    /// <param name="bytes">二进制载荷。</param>
    /// <returns>恢复后的 HyperLogLog。</returns>
    /// <exception cref="InvalidDataException">载荷格式不合法。</exception>
    public static HyperLogLog Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 12 + RegisterCount
            || bytes[0] != (byte)'H'
            || bytes[1] != (byte)'L'
            || bytes[2] != (byte)'0'
            || bytes[3] != (byte)'1')
        {
            throw new InvalidDataException("HyperLogLog magic 或长度不匹配。");
        }

        int precision = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);
        int registerCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[8..12]);
        if (precision != Precision || registerCount != RegisterCount)
            throw new InvalidDataException("HyperLogLog 精度不匹配。");

        foreach (byte rank in bytes[12..])
        {
            if (rank > RemainingBits + 1)
                throw new InvalidDataException("HyperLogLog 寄存器值超出哈希位数范围。");
        }

        var hll = new HyperLogLog();
        bytes[12..].CopyTo(hll._registers.AsSpan());
        return hll;
    }

    /// <summary>估算基数。</summary>
    public long Estimate()
    {
        double sum = 0;
        int zeros = 0;
        for (int i = 0; i < RegisterCount; i++)
        {
            byte r = _registers[i];
            sum += 1.0 / (1UL << r);
            if (r == 0) zeros++;
        }

        double estimate = AlphaMM / sum;

        // 小基数线性计数修正：当 estimate ≤ 2.5m 且存在空寄存器时使用 m * ln(m / V)。
        if (estimate <= 2.5 * RegisterCount && zeros > 0)
            estimate = RegisterCount * Math.Log((double)RegisterCount / zeros);

        return (long)Math.Round(estimate);
    }
}
