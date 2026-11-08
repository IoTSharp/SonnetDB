namespace SonnetDB.Kv.Lsm;

/// <summary>
/// KV-LSM 文件格式使用的无符号 LEB128 变长整数编解码。
/// </summary>
/// <remarks>
/// 解码严格校验：截断、超过 10 字节、超出 64 位或非最短编码均抛出 <see cref="InvalidDataException"/>，
/// 避免损坏文件被解释成另一组合法值。
/// </remarks>
internal static class KvLsmVarint
{
    /// <summary>单个 64 位值编码后的最大字节数。</summary>
    internal const int MaxLength = 10;

    /// <summary>计算 <paramref name="value"/> 编码后的字节数。</summary>
    internal static int Measure(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            length++;
        }
        return length;
    }

    /// <summary>把 <paramref name="value"/> 写入 <paramref name="destination"/>，返回写入字节数。</summary>
    internal static int Write(Span<byte> destination, ulong value)
    {
        int index = 0;
        while (value >= 0x80)
        {
            destination[index++] = (byte)(value | 0x80);
            value >>= 7;
        }
        destination[index++] = (byte)value;
        return index;
    }

    /// <summary>
    /// 从 <paramref name="source"/> 的 <paramref name="position"/> 处读取一个值并推进位置。
    /// </summary>
    /// <exception cref="InvalidDataException">编码截断、过长或非最短编码。</exception>
    internal static ulong Read(ReadOnlySpan<byte> source, ref int position)
    {
        ulong result = 0;
        int shift = 0;
        for (int i = 0; i < MaxLength; i++)
        {
            if (position >= source.Length)
                throw new InvalidDataException("KV-LSM varint is truncated.");
            byte current = source[position++];
            if (i == MaxLength - 1 && current > 1)
                throw new InvalidDataException("KV-LSM varint overflows 64 bits.");
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                if (current == 0 && i > 0)
                    throw new InvalidDataException("KV-LSM varint is not minimally encoded.");
                return result;
            }
            shift += 7;
        }
        throw new InvalidDataException("KV-LSM varint is longer than 10 bytes.");
    }

    /// <summary>读取一个不超过 <see cref="int.MaxValue"/> 的非负长度或计数。</summary>
    /// <exception cref="InvalidDataException">编码损坏或值超过 <see cref="int.MaxValue"/>。</exception>
    internal static int ReadInt32(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = Read(source, ref position);
        if (value > int.MaxValue)
            throw new InvalidDataException("KV-LSM varint length exceeds Int32.MaxValue.");
        return (int)value;
    }

    /// <summary>读取一个不超过 <see cref="long.MaxValue"/> 的非负 64 位值。</summary>
    /// <exception cref="InvalidDataException">编码损坏或值超过 <see cref="long.MaxValue"/>。</exception>
    internal static long ReadInt64(ReadOnlySpan<byte> source, ref int position)
    {
        ulong value = Read(source, ref position);
        if (value > long.MaxValue)
            throw new InvalidDataException("KV-LSM varint value exceeds Int64.MaxValue.");
        return (long)value;
    }
}
