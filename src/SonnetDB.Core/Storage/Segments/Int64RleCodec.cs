using System.Buffers.Binary;
using SonnetDB.Model;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// v7 Int64 游程载荷：每段为 8 字节小端原值和正的 UInt32 varint 长度。
/// 独立于旧 DeltaValue Int64 的原始 8 字节合同；全值域均无损。
/// </summary>
internal static class Int64RleCodec
{
    internal static int Measure(ReadOnlySpan<DataPoint> points)
    {
        int size = 0;
        for (int start = 0; start < points.Length;)
        {
            int end = FindRunEnd(points, start);
            size = checked(size + sizeof(long) + MeasureLength(end - start));
            start = end;
        }
        return size;
    }

    internal static void Write(ReadOnlySpan<DataPoint> points, Span<byte> destination)
    {
        if (destination.Length != Measure(points))
            throw new ArgumentException("目标长度不等于 Int64 RLE 载荷长度。", nameof(destination));

        int offset = 0;
        for (int start = 0; start < points.Length;)
        {
            int end = FindRunEnd(points, start);
            BinaryPrimitives.WriteInt64LittleEndian(destination.Slice(offset, sizeof(long)), points[start].Value.AsLong());
            offset += sizeof(long);
            uint length = (uint)(end - start);
            do
            {
                byte current = (byte)(length & 0x7f);
                length >>= 7;
                destination[offset++] = length == 0 ? current : (byte)(current | 0x80);
            }
            while (length != 0);
            start = end;
        }
    }

    internal static void DecodeRangeInto(
        ReadOnlySpan<byte> payload,
        int totalCount,
        int start,
        int rangeCount,
        Span<DataPoint> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalCount);
        if ((uint)start > (uint)totalCount || (uint)rangeCount > (uint)(totalCount - start))
            throw new ArgumentOutOfRangeException(nameof(start), "解码范围超出点数。");
        if (destination.Length < rangeCount)
            throw new ArgumentException("目标缓冲区长度小于范围点数。", nameof(destination));

        int offset = 0;
        int decoded = 0;
        int rangeEnd = start + rangeCount;
        while (decoded < totalCount)
        {
            if (payload.Length - offset < sizeof(long))
                throw new InvalidDataException("Int64 RLE 原值被截断。");
            long value = BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            uint length = ReadLength(payload, ref offset);
            if (length == 0 || length > (uint)(totalCount - decoded))
                throw new InvalidDataException("Int64 RLE 游程长度为零或超出声明点数。");

            int end = decoded + (int)length;
            int writeStart = Math.Max(decoded, start);
            int writeEnd = Math.Min(end, rangeEnd);
            for (int index = writeStart; index < writeEnd; index++)
            {
                int target = index - start;
                destination[target] = new DataPoint(destination[target].Timestamp, FieldValue.FromLong(value));
            }
            decoded = end;
        }
        // 范围读取也验证完整载荷，不能让目标区间之外的非法 run 或尾字节成为静默成功。
        if (offset != payload.Length)
            throw new InvalidDataException("Int64 RLE 载荷包含尾部字节。");
    }

    private static int FindRunEnd(ReadOnlySpan<DataPoint> points, int start)
    {
        long value = points[start].Value.AsLong();
        int end = start + 1;
        while (end < points.Length && points[end].Value.AsLong() == value)
            end++;
        return end;
    }

    private static int MeasureLength(int length)
    {
        int bytes = 1;
        for (uint remaining = (uint)length >> 7; remaining != 0; remaining >>= 7)
            bytes++;
        return bytes;
    }

    private static uint ReadLength(ReadOnlySpan<byte> payload, ref int offset)
    {
        uint value = 0;
        for (int shift = 0; shift <= 28; shift += 7)
        {
            if (offset >= payload.Length)
                throw new InvalidDataException("Int64 RLE 长度 varint 被截断。");
            byte current = payload[offset++];
            if (shift == 28 && (current & 0xf0) != 0)
                throw new InvalidDataException("Int64 RLE 长度 varint 溢出。");
            value |= (uint)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                if (shift > 0 && current == 0)
                    throw new InvalidDataException("Int64 RLE 长度 varint 不是最短编码。");
                return value;
            }
        }
        throw new InvalidDataException("Int64 RLE 长度 varint 超过五字节。");
    }
}
