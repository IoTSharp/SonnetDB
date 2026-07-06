using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Protocol;
using SonnetDB.Query;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M28 P5b #239 向量检索编码基准：f32 二进制帧 vs JSON 数字数组（REST DTO 形状）。
/// 请求侧对比 <see cref="VectorFrameCodec.EncodeSearchRequest"/>（`MemoryMarshal` 整段 f32）
/// vs JSON 序列化 `float[]`（每分量数字文本）；响应侧对比 KNN 结果集（time/distance/source/embedding
/// 四列 × K 行，向量列按 <see cref="SqlValueKind.Vector"/> f32 编码）的列式 rows 帧 vs JSON 行数组。
/// [Params] 覆盖典型 embedding 维度。GlobalSetup 打印 bytes-on-wire 对照。
/// </summary>
[Config(typeof(VectorSearchEncodingBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("VectorSearchEncoding")]
public class VectorSearchEncodingBenchmark
{
    private const int TopK = 100;

    /// <summary>查询/结果向量维度（128=小型、768=BERT 族、1536=OpenAI ada-002 形状）。</summary>
    [Params(128, 768, 1536)]
    public int Dim { get; set; }

    private static readonly string[] _columns = ["time", "distance", "source", "embedding"];

    private float[] _queryVector = [];
    private List<IReadOnlyList<object?>> _resultRows = null!;

    private byte[] _frameRequestBytes = [];
    private byte[] _jsonRequestBytes = [];
    private byte[] _frameResultBytes = [];
    private byte[] _jsonResultBytes = [];

    private ArrayBufferWriter<byte> _writer = null!;

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _queryVector = MakeVector(rng, Dim);

        long baseTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _resultRows = new List<IReadOnlyList<object?>>(TopK);
        for (int i = 0; i < TopK; i++)
        {
            _resultRows.Add(new object?[]
            {
                baseTs + i * 1000L,
                Math.Round(rng.NextDouble() * 2.0, 6),
                "doc-source",
                MakeVector(rng, Dim),
            });
        }

        _writer = new ArrayBufferWriter<byte>(TopK * Dim * 4 + 1024 * 1024);

        // 预编码用于解码基准 + bytes-on-wire 对照
        var frameWriter = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(frameWriter, 1, "benchdb", "docs", "embedding", _queryVector, TopK);
        _frameRequestBytes = frameWriter.WrittenMemory.ToArray();
        _jsonRequestBytes = JsonSerializer.SerializeToUtf8Bytes(
            BuildJsonRequest(), VectorBenchJsonContext.Default.JsonVectorSearchRequest);

        _frameResultBytes = EncodeFrameResult();
        _jsonResultBytes = JsonSerializer.SerializeToUtf8Bytes(
            BuildJsonResult(), VectorBenchJsonContext.Default.JsonVectorSearchResult);

        Console.WriteLine(
            $"[bytes-on-wire] dim={Dim}  request: frame={_frameRequestBytes.Length}B json={_jsonRequestBytes.Length}B ({(double)_jsonRequestBytes.Length / _frameRequestBytes.Length:F2}x)  " +
            $"result({TopK}hits): frame={_frameResultBytes.Length}B json={_jsonResultBytes.Length}B ({(double)_jsonResultBytes.Length / _frameResultBytes.Length:F2}x)");
    }

    private static float[] MakeVector(Random rng, int dim)
    {
        var vector = new float[dim];
        for (int i = 0; i < dim; i++)
            vector[i] = (float)(rng.NextDouble() * 2.0 - 1.0);
        return vector;
    }

    // ────────────────────────────── search 请求编码 ──────────────────────────────

    [Benchmark(Baseline = true, Description = "JSON number-array encode search request")]
    public byte[] Json_EncodeRequest()
        => JsonSerializer.SerializeToUtf8Bytes(BuildJsonRequest(), VectorBenchJsonContext.Default.JsonVectorSearchRequest);

    [Benchmark(Description = "Frame f32 encode search request")]
    public int Frame_EncodeRequest()
    {
        _writer.ResetWrittenCount();
        VectorFrameCodec.EncodeSearchRequest(_writer, 1, "benchdb", "docs", "embedding", _queryVector, TopK);
        return _writer.WrittenCount;
    }

    // ────────────────────────────── search 请求解码 ──────────────────────────────

    [Benchmark(Description = "JSON number-array decode search request")]
    public int Json_DecodeRequest()
    {
        JsonVectorSearchRequest request = JsonSerializer.Deserialize(
            _jsonRequestBytes, VectorBenchJsonContext.Default.JsonVectorSearchRequest)!;
        return request.Query.Length;
    }

    [Benchmark(Description = "Frame f32 decode search request")]
    public int Frame_DecodeRequest()
    {
        var buffer = new ReadOnlySequence<byte>(_frameRequestBytes);
        FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload);
        VectorSearchFrameRequest request = VectorFrameCodec.DecodeSearchRequest(payload.First.Span);
        return request.QueryVector.Length;
    }

    // ────────────────────────────── KNN 结果集编码 ──────────────────────────────

    [Benchmark(Description = "JSON encode KNN result(100)")]
    public byte[] Json_EncodeResult()
        => JsonSerializer.SerializeToUtf8Bytes(BuildJsonResult(), VectorBenchJsonContext.Default.JsonVectorSearchResult);

    [Benchmark(Description = "Frame columnar encode KNN result(100)")]
    public int Frame_EncodeResult()
    {
        _writer.ResetWrittenCount();
        VectorFrameCodec.EncodeSearchMetaFrame(_writer, 1, _columns);
        int position = 0;
        while (position < _resultRows.Count)
        {
            int chunk = SqlFrameCodec.SelectChunkRowCount(_resultRows, position);
            VectorFrameCodec.EncodeSearchRowsFrame(_writer, 1, _resultRows, position, chunk, _columns.Length);
            position += chunk;
        }
        VectorFrameCodec.EncodeSearchEndFrame(_writer, 1, _resultRows.Count, 1.0);
        return _writer.WrittenCount;
    }

    // ────────────────────────────── KNN 结果集解码 ──────────────────────────────

    [Benchmark(Description = "JSON decode KNN result(100)")]
    public double Json_DecodeResult()
    {
        JsonVectorSearchResult result = JsonSerializer.Deserialize(
            _jsonResultBytes, VectorBenchJsonContext.Default.JsonVectorSearchResult)!;
        double checksum = 0;
        for (int i = 0; i < result.Hits.Count; i++)
            checksum += result.Hits[i].Embedding[0];
        return checksum;
    }

    [Benchmark(Description = "Frame columnar decode KNN result(100)")]
    public double Frame_DecodeResult()
    {
        double checksum = 0;
        var buffer = new ReadOnlySequence<byte>(_frameResultBytes);
        while (FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload))
        {
            ReadOnlySpan<byte> span = payload.First.Span;
            if (SqlFrameCodec.PeekChunkKind(span) != SqlQueryChunkKind.Rows)
                continue;
            object?[][] rows = SqlFrameCodec.DecodeQueryRowsFrame(span);
            for (int i = 0; i < rows.Length; i++)
                checksum += ((float[])rows[i][3]!)[0];
        }
        return checksum;
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    private byte[] EncodeFrameResult()
    {
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchMetaFrame(writer, 1, _columns);
        int position = 0;
        while (position < _resultRows.Count)
        {
            int chunk = SqlFrameCodec.SelectChunkRowCount(_resultRows, position);
            VectorFrameCodec.EncodeSearchRowsFrame(writer, 1, _resultRows, position, chunk, _columns.Length);
            position += chunk;
        }
        VectorFrameCodec.EncodeSearchEndFrame(writer, 1, _resultRows.Count, 1.0);
        return writer.WrittenMemory.ToArray();
    }

    private JsonVectorSearchRequest BuildJsonRequest()
        => new("docs", "embedding", _queryVector, TopK);

    private JsonVectorSearchResult BuildJsonResult()
    {
        var hits = new JsonVectorSearchHit[_resultRows.Count];
        for (int i = 0; i < _resultRows.Count; i++)
        {
            IReadOnlyList<object?> row = _resultRows[i];
            hits[i] = new JsonVectorSearchHit((long)row[0]!, (double)row[1]!, (string)row[2]!, (float[])row[3]!);
        }
        return new JsonVectorSearchResult(hits);
    }

    private sealed class VectorSearchEncodingBenchmarkConfig : ManualConfig
    {
        public VectorSearchEncodingBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithId("VectorSearchEncodingShortRun"));
        }
    }
}

// Server 的 VectorSearchPreviewRequest DTO 是 internal，基准内定义同形镜像 record（查询向量
// float[] 由 System.Text.Json 按数字数组序列化——与 REST 路径的线上形状一致）；结果集镜像
// 「命中行携带向量」形状（REST 侧 NdjsonRowWriter 无 float[] case，向量列实际会降级 ToString，
// JSON 数字数组是它语义正确时的下界对照）。
public sealed record JsonVectorSearchRequest(string Measurement, string Column, float[] Query, int TopK);

public sealed record JsonVectorSearchHit(long Time, double Distance, string Source, float[] Embedding);

public sealed record JsonVectorSearchResult(IReadOnlyList<JsonVectorSearchHit> Hits);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonVectorSearchRequest))]
[JsonSerializable(typeof(JsonVectorSearchResult))]
internal sealed partial class VectorBenchJsonContext : JsonSerializerContext;
