using System.Buffers;
using SonnetDB.Protocol;
using SonnetDB.Query;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="VectorFrameCodec"/> KNN 检索帧编解码测试（#239）：
/// search 请求（f32 二进制向量 / tag 过滤 / 时间窗 / metric）往返、
/// 响应帧与 sql 块布局互通（<see cref="SqlFrameCodec"/> 解码器直接解析）、解码防御。
/// </summary>
public sealed class VectorFrameCodecTests
{
    // ────────────────────────────── search 请求 ──────────────────────────────

    [Fact]
    public void SearchRequest_RoundTrip_Minimal()
    {
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 42, "demo", "docs", "embedding", [1f, 0f, -0.5f], 10);

        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Vector, header.Service);
        Assert.Equal((byte)VectorFrameOp.Search, header.Op);
        Assert.Equal(42u, header.StreamId);
        Assert.False(header.IsResponse);

        VectorSearchFrameRequest request = VectorFrameCodec.DecodeSearchRequest(payload);
        Assert.Equal("demo", request.Db);
        Assert.Equal("docs", request.Measurement);
        Assert.Equal("embedding", request.Column);
        Assert.Equal(10, request.K);
        Assert.Equal(KnnMetric.Cosine, request.Metric);
        Assert.Null(request.TagFilter);
        Assert.Equal(TimeRange.All, request.TimeRange);
        Assert.Equal(new float[] { 1f, 0f, -0.5f }, request.QueryVector);
    }

    [Fact]
    public void SearchRequest_RoundTrip_AllOptions()
    {
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 7, "db1", "m1", "vec",
            [0.25f, float.MaxValue, float.Epsilon, -1f], 3, KnnMetric.InnerProduct,
            new Dictionary<string, string> { ["region"] = "华东", ["host"] = "" },
            new TimeRange(1000, 9000));

        VectorSearchFrameRequest request = VectorFrameCodec.DecodeSearchRequest(ParseSingleFrame(writer, out _));
        Assert.Equal(KnnMetric.InnerProduct, request.Metric);
        Assert.NotNull(request.TagFilter);
        Assert.Equal(2, request.TagFilter!.Count);
        Assert.Equal("华东", request.TagFilter["region"]);
        Assert.Equal("", request.TagFilter["host"]);
        Assert.Equal(new TimeRange(1000, 9000), request.TimeRange);
        Assert.Equal(new float[] { 0.25f, float.MaxValue, float.Epsilon, -1f }, request.QueryVector);
    }

    [Fact]
    public void SearchRequest_L2Metric_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 1, "d", "m", "v", [1f], 1, KnnMetric.L2);
        VectorSearchFrameRequest request = VectorFrameCodec.DecodeSearchRequest(ParseSingleFrame(writer, out _));
        Assert.Equal(KnnMetric.L2, request.Metric);
        Assert.Equal(1, request.K);
    }

    [Fact]
    public void SearchRequest_DecodedVector_IsOwned()
    {
        // 解码结果不得依赖输入缓冲：改写源缓冲后向量值不变
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 1, "d", "m", "v", [1f, 2f], 1);
        byte[] body = writer.WrittenMemory.ToArray();
        var buffer = new ReadOnlySequence<byte>(body);
        FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload);
        VectorSearchFrameRequest request = VectorFrameCodec.DecodeSearchRequest(payload.First.Span);
        Array.Clear(body);
        Assert.Equal(new float[] { 1f, 2f }, request.QueryVector);
    }

    // ────────────────────────────── 请求编码防御 ──────────────────────────────

    [Fact]
    public void SearchRequest_EncodeEmptyVector_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() =>
            VectorFrameCodec.EncodeSearchRequest(writer, 1, "d", "m", "v", ReadOnlySpan<float>.Empty, 1));
    }

    [Fact]
    public void SearchRequest_EncodeKZero_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VectorFrameCodec.EncodeSearchRequest(writer, 1, "d", "m", "v", [1f], 0));
    }

    // ────────────────────────────── 请求解码防御 ──────────────────────────────

    [Fact]
    public void SearchRequest_DecodeBadMetric_Throws()
    {
        byte[] payload = BuildRawSearchPayload(metric: 9);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(payload));
    }

    [Fact]
    public void SearchRequest_DecodeZeroK_Throws()
    {
        byte[] payload = BuildRawSearchPayload(k: 0);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(payload));
    }

    [Fact]
    public void SearchRequest_DecodeZeroDim_Throws()
    {
        byte[] payload = BuildRawSearchPayload(dim: 0);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(payload));
    }

    [Fact]
    public void SearchRequest_DecodeDimBomb_ThrowsBeforeAllocation()
    {
        // 声明超大维度但帧体没有对应字节：必须在分配前拒绝
        byte[] payload = BuildRawSearchPayload(dim: 100_000_000, vectorBytes: 4);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(payload));
    }

    [Fact]
    public void SearchRequest_DecodeInvertedTimeRange_Throws()
    {
        byte[] payload = BuildRawSearchPayload(from: 100, to: 50);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(payload));
    }

    [Fact]
    public void SearchRequest_DecodeDuplicateTagKey_Throws()
    {
        // 手工构造重复 tag key：db m v k metric tagCount=2 (a=1, a=2) range vector
        var raw = new RawPayloadBuilder();
        raw.VarStr("d"); raw.VarStr("m"); raw.VarStr("v");
        raw.VarU32(1); raw.Byte(0);
        raw.VarU32(2);
        raw.VarStr("a"); raw.VarStr("1");
        raw.VarStr("a"); raw.VarStr("2");
        raw.I64(long.MinValue); raw.I64(long.MaxValue);
        raw.VarU32(1); raw.F32(1f);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(raw.ToArray()));
    }

    [Fact]
    public void SearchRequest_DecodeTrailingBytes_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchRequest(writer, 1, "d", "m", "v", [1f], 1);
        byte[] payload = ParseSingleFrame(writer, out _).ToArray();
        byte[] withTrailing = [.. payload, 0xFF];
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(withTrailing));
    }

    [Fact]
    public void SearchRequest_DecodeEmptyMeasurement_Throws()
    {
        var raw = new RawPayloadBuilder();
        raw.VarStr("d"); raw.VarStr(""); raw.VarStr("v");
        raw.VarU32(1); raw.Byte(0); raw.VarU32(0);
        raw.I64(long.MinValue); raw.I64(long.MaxValue);
        raw.VarU32(1); raw.F32(1f);
        Assert.Throws<FrameFormatException>(() => VectorFrameCodec.DecodeSearchRequest(raw.ToArray()));
    }

    // ────────────────────────────── 响应帧：sql 块布局互通 ──────────────────────────────

    [Fact]
    public void ResponseFrames_DecodableBySqlFrameCodec()
    {
        string[] columns = ["time", "distance", "source", "embedding"];
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { 1000L, 0.0, "a", new float[] { 1f, 0f, 0f } },
            new object?[] { 2000L, 0.29, "b", new float[] { 1f, 1f, 0f } },
        };

        var writer = new ArrayBufferWriter<byte>();
        VectorFrameCodec.EncodeSearchMetaFrame(writer, 5, columns);
        VectorFrameCodec.EncodeSearchRowsFrame(writer, 5, rows, 0, rows.Count, columns.Length);
        VectorFrameCodec.EncodeSearchEndFrame(writer, 5, rows.Count, 1.25);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
        var frames = new List<(FrameHeader Header, byte[] Payload)>();
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader h, out ReadOnlySequence<byte> p))
            frames.Add((h, p.ToArray()));

        Assert.Equal(3, frames.Count);
        Assert.All(frames, f =>
        {
            Assert.Equal((byte)FrameService.Vector, f.Header.Service);
            Assert.Equal((byte)VectorFrameOp.Search, f.Header.Op);
            Assert.Equal(5u, f.Header.StreamId);
            Assert.True(f.Header.IsResponse);
            Assert.False(f.Header.IsError);
        });

        // 块布局与 sql 完全一致：SqlFrameCodec 解码器直接解析
        Assert.Equal(SqlQueryChunkKind.Meta, SqlFrameCodec.PeekChunkKind(frames[0].Payload));
        Assert.Equal(columns, SqlFrameCodec.DecodeQueryMetaFrame(frames[0].Payload));

        Assert.Equal(SqlQueryChunkKind.Rows, SqlFrameCodec.PeekChunkKind(frames[1].Payload));
        object?[][] decoded = SqlFrameCodec.DecodeQueryRowsFrame(frames[1].Payload);
        Assert.Equal(2, decoded.Length);
        Assert.Equal(1000L, decoded[0][0]);
        Assert.Equal(0.29, decoded[1][1]);
        Assert.Equal("a", decoded[0][2]);
        Assert.Equal(new float[] { 1f, 1f, 0f }, decoded[1][3]);

        Assert.Equal(SqlQueryChunkKind.End, SqlFrameCodec.PeekChunkKind(frames[2].Payload));
        (long rowCount, double elapsed) = SqlFrameCodec.DecodeQueryEndFrame(frames[2].Payload);
        Assert.Equal(2, rowCount);
        Assert.Equal(1.25, elapsed);
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static ReadOnlySpan<byte> ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }

    /// <summary>手工构造 search 请求帧体（绕过编码端校验，测解码防御）。</summary>
    private static byte[] BuildRawSearchPayload(
        uint k = 1, byte metric = 0, long from = long.MinValue, long to = long.MaxValue,
        uint dim = 1, int vectorBytes = -1)
    {
        var raw = new RawPayloadBuilder();
        raw.VarStr("d"); raw.VarStr("m"); raw.VarStr("v");
        raw.VarU32(k); raw.Byte(metric);
        raw.VarU32(0);
        raw.I64(from); raw.I64(to);
        raw.VarU32(dim);
        int bytes = vectorBytes >= 0 ? vectorBytes : (int)(4 * dim);
        for (int i = 0; i < bytes; i++)
            raw.Byte(0);
        return raw.ToArray();
    }

    /// <summary>裸帧体字节构造器（LEB128 varuint / varstr / i64 LE / f32 LE）。</summary>
    private sealed class RawPayloadBuilder
    {
        private readonly List<byte> _bytes = [];

        public void Byte(byte value) => _bytes.Add(value);

        public void VarU32(uint value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }
            _bytes.Add((byte)value);
        }

        public void VarStr(string value)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
            VarU32((uint)utf8.Length);
            _bytes.AddRange(utf8);
        }

        public void I64(long value)
        {
            Span<byte> buffer = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        public void F32(float value)
        {
            Span<byte> buffer = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
            _bytes.AddRange(buffer.ToArray());
        }

        public byte[] ToArray() => [.. _bytes];
    }
}
