using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using SonnetDB.Graphs;

namespace SonnetDB.Storage.Codecs;

/// <summary>
/// 共享标量编码原语。Graph V1 使用真正保持自然序的编码；Table V1 的旧字节布局通过
/// 独立的 legacy 方法保留，避免改变已经发布的表主键和索引。
/// </summary>
internal static class SortableScalarCodec
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private const ulong SignBit = 0x8000_0000_0000_0000UL;

    /// <summary>计算 Graph V1 标量编码所需字节数。</summary>
    public static int GetGraphSize(GraphPropertyValue value)
    {
        return value.Kind switch
        {
            GraphPropertyKind.Null => 1,
            GraphPropertyKind.Int64 or GraphPropertyKind.Float64 or GraphPropertyKind.DateTime => 9,
            GraphPropertyKind.Boolean => 2,
            GraphPropertyKind.String or GraphPropertyKind.Json =>
                GetTextGraphSize(value.ReferenceText),
            GraphPropertyKind.Blob => 1 + GetEscapedSize(value.BlobSpan),
            _ => throw new InvalidDataException($"未知 Graph V1 标量类型 {value.Kind}。"),
        };
    }

    /// <summary>将 Graph V1 标量编码为确定性、可按字节排序的表示。</summary>
    /// <param name="value">待编码的图属性值。</param>
    /// <returns>拥有的编码副本。</returns>
    public static byte[] EncodeGraph(GraphPropertyValue value)
    {
        byte[] result = new byte[GetGraphSize(value)];
        int written = WriteGraph(result, value);
        if (written != result.Length)
            throw new InvalidDataException("Graph V1 标量编码长度不一致。");
        return result;
    }

    /// <summary>把 Graph V1 标量写入目标缓冲区。</summary>
    /// <param name="destination">容量足够的目标缓冲区。</param>
    /// <param name="value">待编码的图属性值。</param>
    /// <returns>写入字节数。</returns>
    public static int WriteGraph(Span<byte> destination, GraphPropertyValue value)
    {
        int required = GetGraphSize(value);
        if (destination.Length < required)
            throw new ArgumentException("目标缓冲区不足。", nameof(destination));

        int offset = 0;
        destination[offset++] = (byte)value.Kind;
        switch (value.Kind)
        {
            case GraphPropertyKind.Null:
                return offset;
            case GraphPropertyKind.Int64:
                BinaryPrimitives.WriteUInt64BigEndian(
                    destination[offset..], ToSortableInt64(value.NumericBits));
                return offset + sizeof(long);
            case GraphPropertyKind.Float64:
                BinaryPrimitives.WriteUInt64BigEndian(
                    destination[offset..], ToSortableDouble(value.NumericBits));
                return offset + sizeof(long);
            case GraphPropertyKind.DateTime:
                BinaryPrimitives.WriteUInt64BigEndian(
                    destination[offset..], ToSortableInt64(value.NumericBits));
                return offset + sizeof(long);
            case GraphPropertyKind.Boolean:
                destination[offset] = value.NumericBits == 0 ? (byte)0 : (byte)1;
                return offset + 1;
            case GraphPropertyKind.String:
            case GraphPropertyKind.Json:
                return offset + WriteEscaped(destination[offset..], StrictUtf8.GetBytes(value.ReferenceText));
            case GraphPropertyKind.Blob:
                return offset + WriteEscaped(destination[offset..], value.BlobSpan);
            default:
                throw new InvalidDataException($"未知 Graph V1 标量类型 {value.Kind}。");
        }
    }

    /// <summary>
    /// 从 Graph V1 编码读取一个标量。输入必须从标量起始位置开始，允许后面跟随复合键内容。
    /// </summary>
    /// <param name="source">编码源。</param>
    /// <param name="bytesConsumed">成功时输出消耗的字节数。</param>
    /// <returns>解码后的属性值。</returns>
    public static GraphPropertyValue DecodeGraph(ReadOnlySpan<byte> source, out int bytesConsumed)
    {
        if (source.IsEmpty)
            throw new InvalidDataException("Graph V1 标量编码为空。");

        GraphPropertyKind kind = (GraphPropertyKind)source[0];
        int offset = 1;
        switch (kind)
        {
            case GraphPropertyKind.Null:
                bytesConsumed = offset;
                return GraphPropertyValue.Null;
            case GraphPropertyKind.Int64:
                EnsureRemaining(source, offset, sizeof(long));
                long integer = unchecked((long)FromSortableInt64(BinaryPrimitives.ReadUInt64BigEndian(source[offset..])));
                bytesConsumed = offset + sizeof(long);
                return GraphPropertyValue.FromInt64(integer);
            case GraphPropertyKind.Float64:
                EnsureRemaining(source, offset, sizeof(long));
                ulong bits = FromSortableDouble(BinaryPrimitives.ReadUInt64BigEndian(source[offset..]));
                bytesConsumed = offset + sizeof(long);
                return GraphPropertyValue.FromFloat64(BitConverter.UInt64BitsToDouble(bits));
            case GraphPropertyKind.DateTime:
                EnsureRemaining(source, offset, sizeof(long));
                long milliseconds = unchecked((long)FromSortableInt64(BinaryPrimitives.ReadUInt64BigEndian(source[offset..])));
                bytesConsumed = offset + sizeof(long);
                try
                {
                    return GraphPropertyValue.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new InvalidDataException("Graph V1 DateTime 超出合法范围。", exception);
                }
            case GraphPropertyKind.Boolean:
                EnsureRemaining(source, offset, 1);
                if (source[offset] > 1)
                    throw new InvalidDataException("Graph V1 Boolean 编码无效。");
                bytesConsumed = offset + 1;
                return GraphPropertyValue.FromBoolean(source[offset] == 1);
            case GraphPropertyKind.String:
            case GraphPropertyKind.Json:
            {
                byte[] payload = ReadEscaped(source, ref offset);
                string text;
                try
                {
                    text = StrictUtf8.GetString(payload);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("Graph V1 字符串不是合法 UTF-8。", exception);
                }

                bytesConsumed = offset;
                if (kind == GraphPropertyKind.String)
                    return GraphPropertyValue.FromString(text);

                try
                {
                    return GraphPropertyValue.FromJson(text);
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException("Graph V1 Json 标量语法无效。", exception);
                }
            }
            case GraphPropertyKind.Blob:
            {
                byte[] payload = ReadEscaped(source, ref offset);
                bytesConsumed = offset;
                return GraphPropertyValue.FromBlob(payload);
            }
            default:
                throw new InvalidDataException($"Graph V1 标量类型标签 {(byte)kind} 未知。");
        }
    }

    /// <summary>写入 Table V1 原有的有符号整数大端布局。</summary>
    public static void WriteTableLegacyInt64(Span<byte> destination, long value)
        => BinaryPrimitives.WriteInt64BigEndian(destination, value);

    /// <summary>写入 Table V1 原有的浮点位模式大端布局。</summary>
    public static void WriteTableLegacyDouble(Span<byte> destination, double value)
        => BinaryPrimitives.WriteInt64BigEndian(destination, BitConverter.DoubleToInt64Bits(value));

    /// <summary>写入 Table V1 原有的 UTC 毫秒大端布局。</summary>
    public static void WriteTableLegacyDateTime(Span<byte> destination, long milliseconds)
        => BinaryPrimitives.WriteInt64BigEndian(destination, milliseconds);

    /// <summary>写入 Table V1 原有的 32 位长度前缀。</summary>
    public static void WriteTableLegacyLengthPrefixed(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination[..sizeof(int)], value.Length);
        value.CopyTo(destination[sizeof(int)..]);
    }

    private static ulong ToSortableInt64(long value)
        => unchecked((ulong)value) ^ SignBit;

    private static ulong FromSortableInt64(ulong value)
        => value ^ SignBit;

    private static ulong ToSortableDouble(long bitsAsSigned)
    {
        ulong bits = unchecked((ulong)bitsAsSigned);
        return (bits & SignBit) != 0 ? ~bits : bits ^ SignBit;
    }

    private static ulong FromSortableDouble(ulong sortable)
    {
        ulong bits = (sortable & SignBit) != 0 ? sortable ^ SignBit : ~sortable;
        return bits;
    }

    private static int GetEscapedSize(ReadOnlySpan<byte> payload)
    {
        int zeroCount = 0;
        foreach (byte value in payload)
        {
            if (value == 0)
                zeroCount++;
        }

        return checked(payload.Length + zeroCount + 2);
    }

    private static int GetTextGraphSize(string value)
    {
        int zeroCount = 0;
        foreach (char character in value)
        {
            if (character == '\0')
                zeroCount++;
        }
        return checked(1 + StrictUtf8.GetByteCount(value) + zeroCount + 2);
    }

    private static int WriteEscaped(Span<byte> destination, ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        foreach (byte value in payload)
        {
            if (value == 0)
            {
                destination[offset++] = 0;
                destination[offset++] = 0xFF;
            }
            else
            {
                destination[offset++] = value;
            }
        }

        destination[offset++] = 0;
        destination[offset++] = 0;
        return offset;
    }

    private static byte[] ReadEscaped(ReadOnlySpan<byte> source, ref int offset)
    {
        int start = offset;
        int cursor = offset;
        int decodedLength = 0;
        while (cursor < source.Length)
        {
            byte value = source[cursor++];
            if (value != 0)
            {
                decodedLength++;
                continue;
            }

            if (cursor >= source.Length)
                throw new InvalidDataException("Graph V1 转义标量缺少终止符。");
            byte escape = source[cursor++];
            if (escape == 0)
            {
                byte[] payload = decodedLength == 0 ? [] : new byte[decodedLength];
                int read = start;
                int written = 0;
                while (read < cursor - 2)
                {
                    byte decoded = source[read++];
                    if (decoded == 0)
                    {
                        read++;
                        decoded = 0;
                    }
                    payload[written++] = decoded;
                }
                offset = cursor;
                return payload;
            }
            if (escape != 0xFF)
                throw new InvalidDataException("Graph V1 转义标量包含未知转义字节。");
            decodedLength++;
        }

        throw new InvalidDataException("Graph V1 转义标量缺少终止符。");
    }

    private static void EnsureRemaining(ReadOnlySpan<byte> source, int offset, int count)
    {
        if (source.Length - offset < count)
            throw new InvalidDataException("Graph V1 标量编码被截断。");
    }
}
