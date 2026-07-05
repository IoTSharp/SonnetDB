using System.Buffers;
using System.Globalization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Ingest;
using SonnetDB.Model;
using SonnetDB.Protocol;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M28 P5b #237 列式批量写编码基准：列式二进制帧 vs JSON points vs InfluxDB Line Protocol。
/// 覆盖「编码 payload」与「解析 payload → Point 流」两侧（不含引擎写入——引擎侧三条路径
/// 汇合于同一 <see cref="BulkIngestor"/>/<c>WriteMany</c>，见 <see cref="BulkIngestBenchmark"/>）。
/// GlobalSetup 打印 bytes-on-wire 对照。
/// </summary>
[Config(typeof(ColumnarIngestBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("ColumnarIngest")]
public class ColumnarIngestBenchmark
{
    /// <summary>每批行数（每行 = host tag + value/temp 两个 Float64 字段）。</summary>
    [Params(10_000, 100_000)]
    public int Rows { get; set; }

    private long[] _timestamps = [];
    private double[] _values = [];
    private double[] _temps = [];
    private TsdbColumnarBlock[] _blocks = [];

    private byte[] _frameBytes = [];
    private byte[] _jsonBytes = [];
    private byte[] _lpBytes = [];
    private ArrayBufferWriter<byte> _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _timestamps = new long[Rows];
        _values = new double[Rows];
        _temps = new double[Rows];
        long baseTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (int i = 0; i < Rows; i++)
        {
            _timestamps[i] = baseTs + i * 1000L;
            _values[i] = Math.Round(rng.NextDouble() * 100.0, 4);
            _temps[i] = Math.Round(rng.NextDouble() * 40.0, 4);
        }

        _blocks =
        [
            new TsdbColumnarBlock(
                new Dictionary<string, string> { ["host"] = "server001" },
                _timestamps,
                [
                    TsdbColumnarColumn.Float64("value", _values),
                    TsdbColumnarColumn.Float64("temp", _temps),
                ]),
        ];
        _writer = new ArrayBufferWriter<byte>(Rows * 24 + 4096);

        // 预编码三种 payload 供解析基准 + bytes-on-wire 对照
        var frameWriter = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(frameWriter, 1, "benchdb", "sensor_data", BulkFlushMode.None, _blocks);
        _frameBytes = frameWriter.WrittenMemory.ToArray();

        var json = new StringBuilder(Rows * 96);
        json.Append("{\"m\":\"sensor_data\",\"points\":[");
        for (int i = 0; i < Rows; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"t\":{_timestamps[i]},\"tags\":{{\"host\":\"server001\"}},\"fields\":{{\"value\":{_values[i]:F4},\"temp\":{_temps[i]:F4}}}}}");
        }
        json.Append("]}");
        _jsonBytes = Encoding.UTF8.GetBytes(json.ToString());

        var lp = new StringBuilder(Rows * 64);
        for (int i = 0; i < Rows; i++)
            lp.Append(CultureInfo.InvariantCulture,
                $"sensor_data,host=server001 value={_values[i]:F4},temp={_temps[i]:F4} {_timestamps[i]}\n");
        _lpBytes = Encoding.UTF8.GetBytes(lp.ToString());

        Console.WriteLine(
            $"[bytes-on-wire] rows={Rows}  frame={_frameBytes.Length}B  json={_jsonBytes.Length}B ({(double)_jsonBytes.Length / _frameBytes.Length:F2}x)  lp={_lpBytes.Length}B ({(double)_lpBytes.Length / _frameBytes.Length:F2}x)");
    }

    // ────────────────────────────── 编码 ──────────────────────────────

    [Benchmark(Baseline = true, Description = "JSON encode")]
    public byte[] Json_Encode()
    {
        var json = new StringBuilder(Rows * 96);
        json.Append("{\"m\":\"sensor_data\",\"points\":[");
        for (int i = 0; i < Rows; i++)
        {
            if (i > 0) json.Append(',');
            json.Append(CultureInfo.InvariantCulture,
                $"{{\"t\":{_timestamps[i]},\"tags\":{{\"host\":\"server001\"}},\"fields\":{{\"value\":{_values[i]:F4},\"temp\":{_temps[i]:F4}}}}}");
        }
        json.Append("]}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    [Benchmark(Description = "LineProtocol encode")]
    public byte[] Lp_Encode()
    {
        var lp = new StringBuilder(Rows * 64);
        for (int i = 0; i < Rows; i++)
            lp.Append(CultureInfo.InvariantCulture,
                $"sensor_data,host=server001 value={_values[i]:F4},temp={_temps[i]:F4} {_timestamps[i]}\n");
        return Encoding.UTF8.GetBytes(lp.ToString());
    }

    [Benchmark(Description = "Columnar frame encode")]
    public int Frame_Encode()
    {
        _writer.ResetWrittenCount();
        TsdbFrameCodec.EncodeWriteColumnarRequest(_writer, 1, "benchdb", "sensor_data", BulkFlushMode.None, _blocks);
        return _writer.WrittenCount;
    }

    // ────────────────────────────── 解析 → Point 流 ──────────────────────────────

    [Benchmark(Description = "JSON parse to points")]
    public int Json_Parse()
    {
        using var reader = new JsonPointsReader(new ReadOnlyMemory<byte>(_jsonBytes));
        return DrainPoints(reader);
    }

    [Benchmark(Description = "LineProtocol parse to points")]
    public int Lp_Parse()
    {
        // LP 端点先 UTF-8 → char[] 再解析，此处一并计入（与 BulkIngestEndpointHandler 同路径）
        char[] chars = new char[Encoding.UTF8.GetCharCount(_lpBytes)];
        Encoding.UTF8.GetChars(_lpBytes, chars);
        var reader = new LineProtocolReader(chars.AsMemory(), measurementOverride: "sensor_data");
        return DrainPoints(reader);
    }

    [Benchmark(Description = "Columnar frame parse to points")]
    public int Frame_Parse()
    {
        var buffer = new ReadOnlySequence<byte>(_frameBytes);
        FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload);
        TsdbWriteColumnarFrameRequest request = TsdbFrameCodec.DecodeWriteColumnarRequest(payload.First);
        var reader = new TsdbColumnarPointReader(request);
        return DrainPoints(reader);
    }

    private static int DrainPoints(IPointReader reader)
    {
        int count = 0;
        while (reader.TryRead(out Point point))
        {
            count++;
            _ = point.Timestamp;
        }
        return count;
    }

    private sealed class ColumnarIngestBenchmarkConfig : ManualConfig
    {
        public ColumnarIngestBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithId("ColumnarIngestShortRun"));
        }
    }
}
