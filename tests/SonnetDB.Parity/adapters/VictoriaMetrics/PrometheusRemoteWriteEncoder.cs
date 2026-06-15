using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.Parity.Adapters.VictoriaMetrics;

/// <summary>
/// 极简 Prometheus remote_write protobuf 编码器，仅覆盖 PR #129 场景需要的
/// <c>WriteRequest / TimeSeries / Label / Sample</c> 字段。
/// </summary>
public static class PrometheusRemoteWriteEncoder
{
    /// <summary>构造 remote_write <c>WriteRequest</c>。</summary>
    /// <param name="points">待编码的数据点。</param>
    /// <returns>protobuf 字节。</returns>
    public static byte[] Encode(IReadOnlyList<TsdbPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        using var ms = new MemoryStream();
        foreach (var series in points.GroupBy(static p => (p.Measurement, p.Device, p.Region)))
        {
            var labels = new (string, string)[]
            {
                ("__name__", series.Key.Measurement),
                ("device", series.Key.Device),
                ("region", series.Key.Region),
            };
            var samples = series.Select(static p => (p.Value, p.TimestampMs)).ToArray();
            var ts = TimeSeries(labels, samples);
            WriteTag(ms, fieldNo: 1, wireType: 2);
            WriteVarint(ms, (ulong)ts.Length);
            ms.Write(ts);
        }

        return ms.ToArray();
    }

    private static byte[] TimeSeries((string Name, string Value)[] labels, (double Value, long TimestampMs)[] samples)
    {
        using var ms = new MemoryStream();
        foreach (var (name, value) in labels)
        {
            var label = Label(name, value);
            WriteTag(ms, fieldNo: 1, wireType: 2);
            WriteVarint(ms, (ulong)label.Length);
            ms.Write(label);
        }

        foreach (var (value, timestampMs) in samples)
        {
            var sample = Sample(value, timestampMs);
            WriteTag(ms, fieldNo: 2, wireType: 2);
            WriteVarint(ms, (ulong)sample.Length);
            ms.Write(sample);
        }

        return ms.ToArray();
    }

    private static byte[] Label(string name, string value)
    {
        using var ms = new MemoryStream();
        var nameBytes = Encoding.UTF8.GetBytes(name);
        WriteTag(ms, fieldNo: 1, wireType: 2);
        WriteVarint(ms, (ulong)nameBytes.Length);
        ms.Write(nameBytes);

        var valueBytes = Encoding.UTF8.GetBytes(value);
        WriteTag(ms, fieldNo: 2, wireType: 2);
        WriteVarint(ms, (ulong)valueBytes.Length);
        ms.Write(valueBytes);
        return ms.ToArray();
    }

    private static byte[] Sample(double value, long timestampMs)
    {
        using var ms = new MemoryStream();
        WriteTag(ms, fieldNo: 1, wireType: 1);
        Span<byte> valueBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(valueBytes, BitConverter.DoubleToInt64Bits(value));
        ms.Write(valueBytes);

        WriteTag(ms, fieldNo: 2, wireType: 0);
        WriteVarint(ms, (ulong)timestampMs);
        return ms.ToArray();
    }

    private static void WriteTag(Stream stream, int fieldNo, int wireType)
        => WriteVarint(stream, (ulong)((fieldNo << 3) | wireType));

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }
}
