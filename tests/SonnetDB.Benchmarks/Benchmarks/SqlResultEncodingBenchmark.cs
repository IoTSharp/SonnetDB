using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Protocol;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M28 P5b #238 SQL 结果集编码基准：列式二进制 rows 帧 vs REST NDJSON（Utf8JsonWriter 行数组，
/// 与 <c>SqlEndpointHandler.WriteSelectAsync</c>/<c>NdjsonRowWriter</c> 同形状）。
/// 两侧编码同一份 <c>SelectExecutionResult</c> 形状的行集合（time i64 + host string + value f64 + cnt i64），
/// 覆盖编码 CPU/分配与 bytes-on-wire；帧侧按 <see cref="SqlFrameCodec.SelectChunkRowCount"/> 分块
/// （对齐服务端流式路径），解码侧对照 rows 帧列式解码 vs NDJSON 逐行 JsonDocument 解析。
/// GlobalSetup 打印 bytes-on-wire 对照。
/// </summary>
[Config(typeof(SqlResultEncodingBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("SqlResultEncoding")]
public class SqlResultEncodingBenchmark
{
    /// <summary>结果集行数（每行 = time i64 + host string + value f64 + cnt i64 四列）。</summary>
    [Params(10_000, 100_000)]
    public int Rows { get; set; }

    private static readonly string[] _columns = ["time", "host", "value", "cnt"];

    private List<IReadOnlyList<object?>> _rows = null!;
    private byte[] _frameBytes = [];
    private byte[] _ndjsonBytes = [];
    private ArrayBufferWriter<byte> _writer = null!;
    private static readonly byte[] _newline = "\n"u8.ToArray();
    private static readonly JsonWriterOptions _jsonOptions = new() { Indented = false, SkipValidation = false };

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        long baseTs = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        _rows = new List<IReadOnlyList<object?>>(Rows);
        for (int i = 0; i < Rows; i++)
        {
            _rows.Add(new object?[]
            {
                baseTs + i * 1000L,
                "server001",
                Math.Round(rng.NextDouble() * 100.0, 4),
                (long)rng.Next(0, 1000),
            });
        }

        _writer = new ArrayBufferWriter<byte>(Rows * 48 + 64 * 1024);

        _frameBytes = EncodeFrame();
        _ndjsonBytes = EncodeNdjson();

        Console.WriteLine(
            $"[bytes-on-wire] rows={Rows}  frame={_frameBytes.Length}B  ndjson={_ndjsonBytes.Length}B ({(double)_ndjsonBytes.Length / _frameBytes.Length:F2}x)");
    }

    // ────────────────────────────── 编码 ──────────────────────────────

    [Benchmark(Baseline = true, Description = "NDJSON encode result set")]
    public byte[] Ndjson_Encode() => EncodeNdjson();

    [Benchmark(Description = "Columnar frame encode result set")]
    public int Frame_Encode()
    {
        _writer.ResetWrittenCount();
        SqlFrameCodec.EncodeQueryMetaFrame(_writer, 1, _columns);
        int position = 0;
        while (position < _rows.Count)
        {
            int chunk = SqlFrameCodec.SelectChunkRowCount(_rows, position);
            SqlFrameCodec.EncodeQueryRowsFrame(_writer, 1, _rows, position, chunk, _columns.Length);
            position += chunk;
        }
        SqlFrameCodec.EncodeQueryEndFrame(_writer, 1, _rows.Count, 1.0);
        return _writer.WrittenCount;
    }

    // ────────────────────────────── 解码 ──────────────────────────────

    [Benchmark(Description = "NDJSON parse result set")]
    public long Ndjson_Parse()
    {
        long checksum = 0;
        int offset = 0;
        var span = _ndjsonBytes.AsSpan();
        while (offset < span.Length)
        {
            int newline = span[offset..].IndexOf((byte)'\n');
            int lineEnd = newline < 0 ? span.Length : offset + newline;
            var line = _ndjsonBytes.AsMemory(offset, lineEnd - offset);
            offset = lineEnd + 1;
            if (line.IsEmpty)
                continue;

            using var doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                checksum += root[0].GetInt64();
        }
        return checksum;
    }

    [Benchmark(Description = "Columnar frame parse result set")]
    public long Frame_Parse()
    {
        long checksum = 0;
        var buffer = new ReadOnlySequence<byte>(_frameBytes);
        while (FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload))
        {
            ReadOnlySpan<byte> span = payload.First.Span;
            if (SqlFrameCodec.PeekChunkKind(span) != SqlQueryChunkKind.Rows)
                continue;
            object?[][] rows = SqlFrameCodec.DecodeQueryRowsFrame(span);
            for (int i = 0; i < rows.Length; i++)
                checksum += (long)rows[i][0]!;
        }
        return checksum;
    }

    // ────────────────────────────── 内部 ──────────────────────────────

    private byte[] EncodeFrame()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, _columns);
        int position = 0;
        while (position < _rows.Count)
        {
            int chunk = SqlFrameCodec.SelectChunkRowCount(_rows, position);
            SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, _rows, position, chunk, _columns.Length);
            position += chunk;
        }
        SqlFrameCodec.EncodeQueryEndFrame(writer, 1, _rows.Count, 1.0);
        return writer.WrittenMemory.ToArray();
    }

    private byte[] EncodeNdjson()
    {
        // 与 SqlEndpointHandler.WriteSelectAsync 同构：meta 行 + 每行一个 JSON 数组 + end 行
        var body = new ArrayBufferWriter<byte>(Rows * 64 + 4096);
        using (var metaWriter = new Utf8JsonWriter(body, _jsonOptions))
        {
            metaWriter.WriteStartObject();
            metaWriter.WriteString("type", "meta");
            metaWriter.WriteStartArray("columns");
            for (int i = 0; i < _columns.Length; i++)
                metaWriter.WriteStringValue(_columns[i]);
            metaWriter.WriteEndArray();
            metaWriter.WriteEndObject();
        }
        body.Write(_newline);

        for (int r = 0; r < _rows.Count; r++)
        {
            using (var rowWriter = new Utf8JsonWriter(body, _jsonOptions))
            {
                IReadOnlyList<object?> row = _rows[r];
                rowWriter.WriteStartArray();
                for (int c = 0; c < row.Count; c++)
                {
                    switch (row[c])
                    {
                        case long i64: rowWriter.WriteNumberValue(i64); break;
                        case double f64: rowWriter.WriteNumberValue(f64); break;
                        case string s: rowWriter.WriteStringValue(s); break;
                        case null: rowWriter.WriteNullValue(); break;
                        default: rowWriter.WriteStringValue(row[c]!.ToString()); break;
                    }
                }
                rowWriter.WriteEndArray();
            }
            body.Write(_newline);
        }

        using (var endWriter = new Utf8JsonWriter(body, _jsonOptions))
        {
            endWriter.WriteStartObject();
            endWriter.WriteString("type", "end");
            endWriter.WriteNumber("rowCount", _rows.Count);
            endWriter.WriteNumber("recordsAffected", -1);
            endWriter.WriteNumber("elapsedMilliseconds", 1.0);
            endWriter.WriteEndObject();
        }
        body.Write(_newline);
        return body.WrittenMemory.ToArray();
    }

    private sealed class SqlResultEncodingBenchmarkConfig : ManualConfig
    {
        public SqlResultEncodingBenchmarkConfig()
        {
            AddJob(Job.ShortRun.WithId("SqlResultEncodingShortRun"));
        }
    }
}
