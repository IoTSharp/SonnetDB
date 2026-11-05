using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

namespace SonnetDB.Storage.Segments;

/// <summary>
/// <see cref="ValuePayloadCodec"/> 的对偶：从二进制载荷解码出 <see cref="DataPoint"/> 序列。
/// 所有数值通过 <see cref="BinaryPrimitives"/> LE 读取，保证跨平台一致性。
/// </summary>
internal static class BlockDecoder
{
    /// <summary>
    /// 解码指定 Block 的全部 DataPoint。
    /// </summary>
    /// <param name="d">Block 描述符（含 Count / FieldType）。</param>
    /// <param name="tsPayload">时间戳载荷字节视图。</param>
    /// <param name="valPayload">值载荷字节视图。</param>
    /// <returns>按时间戳升序排列的 DataPoint 数组。</returns>
    public static DataPoint[] Decode(
        in BlockDescriptor d,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload)
    {
        int count = d.Count;
        if (count == 0)
            return [];

        var result = new DataPoint[count];
        ReadTimestamps(d.TimestampEncoding, tsPayload, count, result);
        ReadValues(d.FieldType, d.ValueEncoding, valPayload, count, result);
        return result;
    }

    /// <summary>
    /// 仅解码时间戳序列。
    /// </summary>
    /// <param name="d">Block 描述符。</param>
    /// <param name="tsPayload">时间戳载荷字节视图。</param>
    /// <returns>按点位顺序排列的时间戳数组。</returns>
    internal static long[] DecodeTimestamps(in BlockDescriptor d, ReadOnlySpan<byte> tsPayload)
    {
        if (d.Count == 0)
            return [];

        var result = new long[d.Count];
        if ((d.TimestampEncoding & BlockEncoding.DeltaTimestamp) != 0)
        {
            TimestampCodec.ReadDeltaOfDelta(tsPayload, result);
            return result;
        }

        for (int i = 0; i < result.Length; i++)
            result[i] = ReadRawTimestamp(tsPayload, i);
        return result;
    }

    /// <summary>
    /// 解码 Block 内 [<paramref name="from"/>, <paramref name="toInclusive"/>] 时间范围的 DataPoint。
    /// </summary>
    /// <param name="d">Block 描述符。</param>
    /// <param name="tsPayload">时间戳载荷字节视图。</param>
    /// <param name="valPayload">值载荷字节视图。</param>
    /// <param name="from">起始时间戳（含）。</param>
    /// <param name="toInclusive">结束时间戳（含）。</param>
    /// <returns>在时间范围内的 DataPoint 数组（可能为空）。</returns>
    public static DataPoint[] DecodeRange(
        in BlockDescriptor d,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        long from,
        long toInclusive)
    {
        int count = d.Count;
        if (count == 0)
            return [];

        if (from <= d.MinTimestamp && toInclusive >= d.MaxTimestamp)
            return Decode(d, tsPayload, valPayload);

        if ((d.TimestampEncoding & BlockEncoding.DeltaTimestamp) == 0)
            return DecodeRawTimestampRange(d, tsPayload, valPayload, count, from, toInclusive);

        // Delta-of-delta 时间戳不支持随机访问；使用 ArrayPool 避免每次查询分配整块 long[]。
        long[] rented = ArrayPool<long>.Shared.Rent(count);
        try
        {
            Span<long> timestamps = rented.AsSpan(0, count);
            TimestampCodec.ReadDeltaOfDelta(tsPayload, timestamps);

            int start = LowerBound(timestamps, from);
            int end = UpperBound(timestamps, toInclusive);

            if (start >= end)
            {
                if ((d.ValueEncoding & BlockEncoding.Int64Rle) != 0)
                    Int64RleCodec.DecodeRangeInto(valPayload, count, 0, 0, Span<DataPoint>.Empty);
                return [];
            }

            int rangeCount = end - start;
            var result = new DataPoint[rangeCount];

            // 将目标时间戳复制到 DataPoint
            for (int i = 0; i < rangeCount; i++)
                result[i] = new DataPoint(timestamps[start + i], default);

            // 解码对应范围的值
            ReadValuesRange(d.FieldType, d.ValueEncoding, valPayload, count, start, rangeCount, result);
            return result;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(rented);
        }
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────────────

    private static void ReadTimestamps(BlockEncoding tsEncoding, ReadOnlySpan<byte> tsPayload, int count, DataPoint[] result)
    {
        if ((tsEncoding & BlockEncoding.DeltaTimestamp) != 0)
        {
            TimestampCodec.ReadDeltaOfDelta(tsPayload, result.AsSpan(0, count));
            return;
        }

        for (int i = 0; i < count; i++)
        {
            long ts = BinaryPrimitives.ReadInt64LittleEndian(tsPayload.Slice(i * 8, 8));
            result[i] = new DataPoint(ts, default);
        }
    }

    private static DataPoint[] DecodeRawTimestampRange(
        in BlockDescriptor d,
        ReadOnlySpan<byte> tsPayload,
        ReadOnlySpan<byte> valPayload,
        int count,
        long from,
        long toInclusive)
    {
        int start = LowerBoundRaw(tsPayload, count, from);
        int end = UpperBoundRaw(tsPayload, count, toInclusive);

        if (start >= end)
        {
            if ((d.ValueEncoding & BlockEncoding.Int64Rle) != 0)
                Int64RleCodec.DecodeRangeInto(valPayload, count, 0, 0, Span<DataPoint>.Empty);
            return [];
        }

        int rangeCount = end - start;
        var result = new DataPoint[rangeCount];
        for (int i = 0; i < rangeCount; i++)
        {
            long ts = ReadRawTimestamp(tsPayload, start + i);
            result[i] = new DataPoint(ts, default);
        }

        ReadValuesRange(d.FieldType, d.ValueEncoding, valPayload, count, start, rangeCount, result);
        return result;
    }

    private static void ReadValues(FieldType fieldType, BlockEncoding valEncoding, ReadOnlySpan<byte> valPayload, int count, DataPoint[] result)
    {
        if ((valEncoding & BlockEncoding.Int64Rle) != 0)
        {
            Int64RleCodec.DecodeRangeInto(valPayload, count, 0, count, result.AsSpan(0, count));
            return;
        }
        if ((valEncoding & BlockEncoding.DeltaValue) != 0)
        {
            ValuePayloadCodecV2.DecodeInto(fieldType, valPayload, count, result.AsSpan(0, count));
            return;
        }

        switch (fieldType)
        {
            case FieldType.Float64:
                for (int i = 0; i < count; i++)
                {
                    double v = BinaryPrimitives.ReadDoubleLittleEndian(valPayload.Slice(i * 8, 8));
                    result[i] = new DataPoint(result[i].Timestamp, FieldValue.FromDouble(v));
                }
                break;

            case FieldType.Int64:
                for (int i = 0; i < count; i++)
                {
                    long v = BinaryPrimitives.ReadInt64LittleEndian(valPayload.Slice(i * 8, 8));
                    result[i] = new DataPoint(result[i].Timestamp, FieldValue.FromLong(v));
                }
                break;

            case FieldType.Boolean:
                for (int i = 0; i < count; i++)
                {
                    bool v = valPayload[i] != 0;
                    result[i] = new DataPoint(result[i].Timestamp, FieldValue.FromBool(v));
                }
                break;

            case FieldType.String:
                ReadStringValues(valPayload, count, result, startIndex: 0, resultOffset: 0);
                break;

            case FieldType.GeoPoint:
                ReadGeoPointValues(valPayload, result, startIndex: 0, rangeCount: count, resultOffset: 0);
                break;

            case FieldType.Vector:
                ReadVectorValues(valPayload, count, result, startIndex: 0, rangeCount: count, resultOffset: 0);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, null);
        }
    }

    private static void ReadValuesRange(
        FieldType fieldType,
        BlockEncoding valEncoding,
        ReadOnlySpan<byte> valPayload,
        int totalCount,
        int start,
        int rangeCount,
        DataPoint[] result)
    {
        if ((valEncoding & BlockEncoding.Int64Rle) != 0)
        {
            Int64RleCodec.DecodeRangeInto(valPayload, totalCount, start, rangeCount, result.AsSpan(0, rangeCount));
            return;
        }
        if ((valEncoding & BlockEncoding.DeltaValue) != 0)
        {
            ValuePayloadCodecV2.DecodeRangeInto(
                fieldType,
                valPayload,
                totalCount,
                start,
                rangeCount,
                result.AsSpan(0, rangeCount));
            return;
        }

        switch (fieldType)
        {
            case FieldType.Float64:
                for (int i = 0; i < rangeCount; i++)
                {
                    double v = BinaryPrimitives.ReadDoubleLittleEndian(valPayload.Slice((start + i) * 8, 8));
                    result[i] = new DataPoint(result[i].Timestamp, FieldValue.FromDouble(v));
                }
                break;

            case FieldType.Int64:
                for (int i = 0; i < rangeCount; i++)
                {
                    long v = BinaryPrimitives.ReadInt64LittleEndian(valPayload.Slice((start + i) * 8, 8));
                    result[i] = new DataPoint(result[i].Timestamp, FieldValue.FromLong(v));
                }
                break;

            case FieldType.Boolean:
                for (int i = 0; i < rangeCount; i++)
                {
                    bool v = valPayload[start + i] != 0;
                    result[i] = new DataPoint(result[i].Timestamp, FieldValue.FromBool(v));
                }
                break;

            case FieldType.Vector:
                ReadVectorValues(valPayload, totalCount, result, startIndex: start, rangeCount: rangeCount, resultOffset: 0);
                break;

            case FieldType.GeoPoint:
                ReadGeoPointValues(valPayload, result, startIndex: start, rangeCount: rangeCount, resultOffset: 0);
                break;

            case FieldType.String:
                ReadStringValues(valPayload, totalCount, result, startIndex: start, resultOffset: 0);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(fieldType), fieldType, null);
        }
    }

    private static void ReadStringValues(
        ReadOnlySpan<byte> valPayload,
        int totalCount,
        DataPoint[] result,
        int startIndex,
        int resultOffset)
    {
        // 需要跳过前 startIndex 个字符串条目
        int pos = 0;
        int skipped = 0;
        int written = 0;
        int targetCount = result.Length - resultOffset;

        while (skipped + written < totalCount && written < targetCount)
        {
            int byteLen = BinaryPrimitives.ReadInt32LittleEndian(valPayload.Slice(pos, 4));
            pos += 4;

            if (skipped < startIndex)
            {
                pos += byteLen;
                skipped++;
            }
            else
            {
                string s = Encoding.UTF8.GetString(valPayload.Slice(pos, byteLen));
                pos += byteLen;
                result[resultOffset + written] = new DataPoint(
                    result[resultOffset + written].Timestamp,
                    FieldValue.FromString(s));
                written++;
            }
        }
    }

    private static void ReadVectorValues(
        ReadOnlySpan<byte> valPayload,
        int totalCount,
        DataPoint[] result,
        int startIndex,
        int rangeCount,
        int resultOffset)
    {
        if (rangeCount == 0 || totalCount == 0)
            return;

        // VectorRaw 编码下 totalCount 个点占满 valPayload，每点 dim×4 字节。
        int totalBytes = valPayload.Length;
        if (totalBytes % totalCount != 0)
            throw new InvalidDataException(
                $"Vector value payload 长度 {totalBytes} 不是 totalCount({totalCount}) 的整数倍。");
        int bytesPerPoint = totalBytes / totalCount;
        if (bytesPerPoint == 0 || bytesPerPoint % sizeof(float) != 0)
            throw new InvalidDataException(
                $"Vector value payload 每点字节数 {bytesPerPoint} 不是 4 的正整数倍。");
        int dim = bytesPerPoint / sizeof(float);

        for (int i = 0; i < rangeCount; i++)
        {
            int srcOffset = (startIndex + i) * bytesPerPoint;
            ReadOnlySpan<byte> bytes = valPayload.Slice(srcOffset, bytesPerPoint);
            // 拷贝出独立的 float[]，避免外部直接持有底层 valPayload 字节。
            float[] arr = new float[dim];
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).CopyTo(arr);
            result[resultOffset + i] = new DataPoint(
                result[resultOffset + i].Timestamp,
                FieldValue.FromVector(arr));
        }
    }

    private static void ReadGeoPointValues(
        ReadOnlySpan<byte> valPayload,
        DataPoint[] result,
        int startIndex,
        int rangeCount,
        int resultOffset)
    {
        const int BytesPerPoint = 16;
        for (int i = 0; i < rangeCount; i++)
        {
            int srcOffset = (startIndex + i) * BytesPerPoint;
            double lat = BinaryPrimitives.ReadDoubleLittleEndian(valPayload.Slice(srcOffset, 8));
            double lon = BinaryPrimitives.ReadDoubleLittleEndian(valPayload.Slice(srcOffset + 8, 8));
            result[resultOffset + i] = new DataPoint(
                result[resultOffset + i].Timestamp,
                FieldValue.FromGeoPoint(lat, lon));
        }
    }

    /// <summary>二分查找：第一个 timestamps[i] >= value 的位置。</summary>
    private static int LowerBound(ReadOnlySpan<long> timestamps, long value)
    {
        int lo = 0, hi = timestamps.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (timestamps[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    /// <summary>二分查找：第一个 timestamps[i] > value 的位置（即上界 exclusive end）。</summary>
    private static int UpperBound(ReadOnlySpan<long> timestamps, long value)
    {
        int lo = 0, hi = timestamps.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (timestamps[mid] <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int LowerBoundRaw(ReadOnlySpan<byte> tsPayload, int count, long value)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ReadRawTimestamp(tsPayload, mid) < value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static int UpperBoundRaw(ReadOnlySpan<byte> tsPayload, int count, long value)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (ReadRawTimestamp(tsPayload, mid) <= value)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static long ReadRawTimestamp(ReadOnlySpan<byte> tsPayload, int index)
        => BinaryPrimitives.ReadInt64LittleEndian(tsPayload.Slice(index * 8, 8));
}
