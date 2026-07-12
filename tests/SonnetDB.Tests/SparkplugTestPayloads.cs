using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.Tests;

/// <summary>
/// 测试专用的最小 Sparkplug B protobuf encoder，不引入 Protobuf 运行时依赖。
/// </summary>
internal static class SparkplugTestPayloads
{
    public static byte[] Payload(ulong timestamp, params Metric[] metrics)
        => PayloadCore(timestamp, sequence: null, metrics);

    public static byte[] PayloadWithSequence(ulong timestamp, byte sequence, params Metric[] metrics)
        => PayloadCore(timestamp, sequence, metrics);

    private static byte[] PayloadCore(ulong timestamp, byte? sequence, params Metric[] metrics)
    {
        using var stream = new MemoryStream();
        WriteTag(stream, 1, 0);
        WriteVarint(stream, timestamp);
        foreach (var metric in metrics)
        {
            byte[] encoded = EncodeMetric(metric);
            WriteTag(stream, 2, 2);
            WriteVarint(stream, (ulong)encoded.Length);
            stream.Write(encoded);
        }
        if (sequence.HasValue)
        {
            WriteTag(stream, 3, 0);
            WriteVarint(stream, sequence.Value);
        }
        return stream.ToArray();
    }

    public static Metric Int32(string? name, ulong? alias, int value, ulong? timestamp = null)
        => new(name, alias, timestamp, 3, unchecked((uint)value), null, null, null, null, null, null, false);

    public static Metric Float(string? name, ulong? alias, float value, ulong? timestamp = null)
        => new(name, alias, timestamp, 9, null, null, value, null, null, null, null, false);

    public static Metric Double(string? name, ulong? alias, double value, ulong? timestamp = null)
        => new(name, alias, timestamp, 10, null, null, null, value, null, null, null, false);

    public static Metric Boolean(string? name, ulong? alias, bool value, ulong? timestamp = null)
        => new(name, alias, timestamp, 11, null, null, null, null, value, null, null, false);

    public static Metric String(string? name, ulong? alias, string value, ulong? timestamp = null)
        => new(name, alias, timestamp, 12, null, null, null, null, null, value, null, false);

    public static Metric UInt64(string? name, ulong? alias, ulong value, ulong? timestamp = null)
        => new(name, alias, timestamp, 8, null, value, null, null, null, null, null, false);

    public static Metric Bytes(string? name, ulong? alias, byte[] value, ulong? timestamp = null)
        => new(name, alias, timestamp, 17, null, null, null, null, null, null, value, false);

    public static Metric Null(string? name, ulong? alias, uint dataType = 12)
        => new(name, alias, null, dataType, null, null, null, null, null, null, null, true);

    private static byte[] EncodeMetric(Metric metric)
    {
        using var stream = new MemoryStream();
        if (metric.Name is not null)
            WriteString(stream, 1, metric.Name);
        if (metric.Alias.HasValue)
        {
            WriteTag(stream, 2, 0);
            WriteVarint(stream, metric.Alias.Value);
        }
        if (metric.Timestamp.HasValue)
        {
            WriteTag(stream, 3, 0);
            WriteVarint(stream, metric.Timestamp.Value);
        }

        WriteTag(stream, 4, 0);
        WriteVarint(stream, metric.DataType);
        if (metric.IsNull)
        {
            WriteTag(stream, 7, 0);
            WriteVarint(stream, 1);
        }
        if (metric.IntValue.HasValue)
        {
            WriteTag(stream, 10, 0);
            WriteVarint(stream, metric.IntValue.Value);
        }
        if (metric.LongValue.HasValue)
        {
            WriteTag(stream, 11, 0);
            WriteVarint(stream, metric.LongValue.Value);
        }
        if (metric.FloatValue.HasValue)
        {
            WriteTag(stream, 12, 5);
            Span<byte> bytes = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, unchecked((uint)BitConverter.SingleToInt32Bits(metric.FloatValue.Value)));
            stream.Write(bytes);
        }
        if (metric.DoubleValue.HasValue)
        {
            WriteTag(stream, 13, 1);
            Span<byte> bytes = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, unchecked((ulong)BitConverter.DoubleToInt64Bits(metric.DoubleValue.Value)));
            stream.Write(bytes);
        }
        if (metric.BooleanValue.HasValue)
        {
            WriteTag(stream, 14, 0);
            WriteVarint(stream, metric.BooleanValue.Value ? 1UL : 0UL);
        }
        if (metric.StringValue is not null)
            WriteString(stream, 15, metric.StringValue);
        if (metric.BytesValue is not null)
        {
            WriteTag(stream, 16, 2);
            WriteVarint(stream, (ulong)metric.BytesValue.Length);
            stream.Write(metric.BytesValue);
        }

        return stream.ToArray();
    }

    private static void WriteString(Stream stream, int fieldNumber, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteTag(stream, fieldNumber, 2);
        WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteTag(Stream stream, int fieldNumber, int wireType)
        => WriteVarint(stream, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    internal sealed record Metric(
        string? Name,
        ulong? Alias,
        ulong? Timestamp,
        uint DataType,
        uint? IntValue,
        ulong? LongValue,
        float? FloatValue,
        double? DoubleValue,
        bool? BooleanValue,
        string? StringValue,
        byte[]? BytesValue,
        bool IsNull);
}
