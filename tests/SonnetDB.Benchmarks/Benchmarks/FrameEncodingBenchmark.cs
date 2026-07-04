using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Protocol;
using SonnetMQ;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M28 P5b #235 编码基准：二进制帧 codec vs System.Text.Json+Base64（既有 REST DTO 形状）。
/// 覆盖 MQ publish 请求编/解与 pull 100 条响应编/解，[Params] 对齐 #230 的 payload 尺寸。
/// GlobalSetup 打印 bytes-on-wire 对照（帧 ≈ payload + ~30B 开销 vs JSON ≈ 4/3·payload + 字段名）。
/// </summary>
[Config(typeof(FrameEncodingBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("FrameEncoding")]
public class FrameEncodingBenchmark
{
    private const int PullMessageCount = 100;

    private byte[] _payload = [];
    private Dictionary<string, string> _headers = null!;
    private SonnetMqMessage[] _pullMessages = [];

    private byte[] _framePublishBytes = [];
    private byte[] _jsonPublishBytes = [];
    private byte[] _framePullBytes = [];
    private byte[] _jsonPullBytes = [];

    private ArrayBufferWriter<byte> _writer = null!;

    [Params(64, 1024, 16384)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[PayloadBytes];
        var random = new Random(42);
        random.NextBytes(_payload);
        _headers = new Dictionary<string, string> { ["source"] = "bench", ["trace-id"] = "0123456789abcdef" };
        _writer = new ArrayBufferWriter<byte>(64 * 1024 + PayloadBytes * 2);

        var timestamp = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        _pullMessages = new SonnetMqMessage[PullMessageCount];
        for (int i = 0; i < PullMessageCount; i++)
            _pullMessages[i] = new SonnetMqMessage("bench-topic", i, timestamp.AddMilliseconds(i), _headers, _payload);

        // 预编码一份用于 decode 基准 + bytes-on-wire 对照
        var frameWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(frameWriter, 1, "benchdb", "bench-topic", _headers, _payload);
        _framePublishBytes = frameWriter.WrittenMemory.ToArray();
        _jsonPublishBytes = JsonSerializer.SerializeToUtf8Bytes(
            new JsonMqPublishRequest(_payload, _headers), FrameBenchJsonContext.Default.JsonMqPublishRequest);

        frameWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullResponse(frameWriter, 1, _pullMessages);
        _framePullBytes = frameWriter.WrittenMemory.ToArray();
        _jsonPullBytes = JsonSerializer.SerializeToUtf8Bytes(
            BuildJsonPullResponse(), FrameBenchJsonContext.Default.JsonMqPullResponse);

        Console.WriteLine(
            $"[bytes-on-wire] payload={PayloadBytes}B  publish: frame={_framePublishBytes.Length}B json={_jsonPublishBytes.Length}B ({(double)_jsonPublishBytes.Length / _framePublishBytes.Length:F2}x)  " +
            $"pull({PullMessageCount}msgs): frame={_framePullBytes.Length}B json={_jsonPullBytes.Length}B ({(double)_jsonPullBytes.Length / _framePullBytes.Length:F2}x)");
    }

    // ────────────────────────────── publish 编码 ──────────────────────────────

    [Benchmark(Baseline = true, Description = "JSON+Base64 encode publish")]
    public byte[] Json_EncodePublish()
        => JsonSerializer.SerializeToUtf8Bytes(
            new JsonMqPublishRequest(_payload, _headers), FrameBenchJsonContext.Default.JsonMqPublishRequest);

    [Benchmark(Description = "Frame encode publish")]
    public int Frame_EncodePublish()
    {
        _writer.ResetWrittenCount();
        MqFrameCodec.EncodePublishRequest(_writer, 1, "benchdb", "bench-topic", _headers, _payload);
        return _writer.WrittenCount;
    }

    // ────────────────────────────── publish 解码 ──────────────────────────────

    [Benchmark(Description = "JSON+Base64 decode publish")]
    public int Json_DecodePublish()
    {
        JsonMqPublishRequest request = JsonSerializer.Deserialize(
            _jsonPublishBytes, FrameBenchJsonContext.Default.JsonMqPublishRequest)!;
        return request.Payload.Length;
    }

    [Benchmark(Description = "Frame decode publish")]
    public int Frame_DecodePublish()
    {
        var buffer = new ReadOnlySequence<byte>(_framePublishBytes);
        FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload);
        MqPublishFrameRequest request = MqFrameCodec.DecodePublishRequest(payload.First);
        return request.Payload.Length;
    }

    // ────────────────────────────── pull 100 条响应编码 ──────────────────────────────

    [Benchmark(Description = "JSON+Base64 encode pull(100)")]
    public byte[] Json_EncodePullResponse()
        => JsonSerializer.SerializeToUtf8Bytes(BuildJsonPullResponse(), FrameBenchJsonContext.Default.JsonMqPullResponse);

    [Benchmark(Description = "Frame encode pull(100)")]
    public int Frame_EncodePullResponse()
    {
        _writer.ResetWrittenCount();
        MqFrameCodec.EncodePullResponse(_writer, 1, _pullMessages);
        return _writer.WrittenCount;
    }

    // ────────────────────────────── pull 100 条响应解码 ──────────────────────────────

    [Benchmark(Description = "JSON+Base64 decode pull(100)")]
    public int Json_DecodePullResponse()
    {
        JsonMqPullResponse response = JsonSerializer.Deserialize(
            _jsonPullBytes, FrameBenchJsonContext.Default.JsonMqPullResponse)!;
        return response.Messages.Count;
    }

    [Benchmark(Description = "Frame decode pull(100)")]
    public int Frame_DecodePullResponse()
    {
        var buffer = new ReadOnlySequence<byte>(_framePullBytes);
        FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload);
        SonnetMqMessage[] messages = MqFrameCodec.DecodePullResponse(payload.First, "bench-topic");
        return messages.Length;
    }

    private JsonMqPullResponse BuildJsonPullResponse()
    {
        var messages = new JsonMqMessageResponse[PullMessageCount];
        for (int i = 0; i < PullMessageCount; i++)
        {
            SonnetMqMessage message = _pullMessages[i];
            messages[i] = new JsonMqMessageResponse(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload);
        }

        return new JsonMqPullResponse(messages);
    }

    private sealed class FrameEncodingBenchmarkConfig : ManualConfig
    {
        public FrameEncodingBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithId("FrameEncodingShortRun"));
        }
    }
}

// Server 的 MqPublishRequest / MqPullResponse DTO 是 internal，基准内定义同形镜像 record，
// byte[] 由 System.Text.Json 按 Base64 字符串序列化——与 REST 路径的线上形状一致。
public sealed record JsonMqPublishRequest(byte[] Payload, IReadOnlyDictionary<string, string>? Headers = null);

public sealed record JsonMqMessageResponse(
    string Topic,
    long Offset,
    DateTimeOffset TimestampUtc,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Payload);

public sealed record JsonMqPullResponse(IReadOnlyList<JsonMqMessageResponse> Messages);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(JsonMqPublishRequest))]
[JsonSerializable(typeof(JsonMqPullResponse))]
internal sealed partial class FrameBenchJsonContext : JsonSerializerContext;
