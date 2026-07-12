using System.Buffers.Binary;
using System.Text;
using BenchmarkDotNet.Attributes;
using SonnetDB.Mqtt;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M30 #268 Sparkplug B protobuf 解码基准。只覆盖 payload 解码与强类型 metric 到 Point 的映射，
/// 不把 MQTT 网络传输或持久化 I/O 混入 codec 热路径。
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory("ProtocolIngest")]
public class SparkplugDecodeBenchmark
{
    private byte[] _payload = [];
    private SparkplugTopicRoute _route;
    private SparkplugAliasStore _aliases = null!;

    /// <summary>单个 Sparkplug DATA payload 内的标量 metric 数量。</summary>
    [Params(10, 100)]
    public int MetricCount { get; set; }

    /// <summary>构造固定 DATA payload，避免把基准数据编码计入解码耗时。</summary>
    [GlobalSetup]
    public void Setup()
    {
        if (!SparkplugTopicParser.TryParse(
                "spBv1.0/factory/NDATA/edge-01",
                out _route,
                out string error))
        {
            throw new InvalidOperationException(error);
        }

        _aliases = new SparkplugAliasStore();
        _payload = BuildPayload(MetricCount);
    }

    /// <summary>解码完整 payload 并物化其中所有标量 Point。</summary>
    /// <returns>成功映射的 Point 数量。</returns>
    [Benchmark]
    public int DecodeAndMapPoints()
    {
        var reader = new SparkplugPayloadReader(_payload, _route, _aliases);
        int count = 0;
        while (reader.TryRead(out _))
            count++;

        if (count != MetricCount)
            throw new InvalidOperationException($"期望解码 {MetricCount} 个 metric，实际 {count}。");
        return count;
    }

    private static byte[] BuildPayload(int metricCount)
    {
        var payload = new List<byte>(metricCount * 32);
        WriteTag(payload, 1, 0);
        WriteVarint(payload, 1_700_000_000_000);
        for (int i = 0; i < metricCount; i++)
        {
            byte[] metric = BuildDoubleMetric(i);
            WriteTag(payload, 2, 2);
            WriteVarint(payload, (ulong)metric.Length);
            payload.AddRange(metric);
        }
        WriteTag(payload, 3, 0);
        WriteVarint(payload, 7);
        return payload.ToArray();
    }

    private static byte[] BuildDoubleMetric(int index)
    {
        var metric = new List<byte>(40);
        WriteTag(metric, 1, 2);
        WriteString(metric, "metric_" + index);
        WriteTag(metric, 3, 0);
        WriteVarint(metric, (ulong)(1_700_000_000_000 + index));
        WriteTag(metric, 4, 0);
        WriteVarint(metric, 10);
        WriteTag(metric, 13, 1);
        Span<byte> bits = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(
            bits,
            unchecked((ulong)BitConverter.DoubleToInt64Bits(index + 0.5d)));
        for (int i = 0; i < bits.Length; i++)
            metric.Add(bits[i]);
        return metric.ToArray();
    }

    private static void WriteString(List<byte> buffer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(buffer, (ulong)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteTag(List<byte> buffer, int fieldNumber, int wireType)
        => WriteVarint(buffer, (ulong)((fieldNumber << 3) | wireType));

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buffer.Add((byte)value);
    }
}
