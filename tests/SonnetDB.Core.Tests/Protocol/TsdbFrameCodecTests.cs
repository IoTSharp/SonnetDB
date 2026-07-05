using System.Buffers;
using SonnetDB.Ingest;
using SonnetDB.Model;
using SonnetDB.Protocol;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="TsdbFrameCodec"/> 列式批量写编解码 + <see cref="TsdbColumnarPointReader"/> 列转行测试（#237）。
/// </summary>
public sealed class TsdbFrameCodecTests
{
    // ────────────────────────────── 基本往返 ──────────────────────────────

    [Fact]
    public void WriteColumnar_RoundTrip_DenseSingleBlock()
    {
        long[] timestamps = [1000, 2000, 3000];
        double[] values = [1.5, 2.5, 3.5];
        var block = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "a" },
            timestamps,
            [TsdbColumnarColumn.Float64("value", values)]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 7, "demo", "cpu", BulkFlushMode.None, [block]);

        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Tsdb, header.Service);
        Assert.Equal((byte)TsdbFrameOp.WriteColumnar, header.Op);
        Assert.Equal(7u, header.StreamId);
        Assert.False(header.IsResponse);

        TsdbWriteColumnarFrameRequest request = TsdbFrameCodec.DecodeWriteColumnarRequest(payload);
        Assert.Equal("demo", request.Db);
        Assert.Equal("cpu", request.Measurement);
        Assert.Equal(BulkFlushMode.None, request.FlushMode);
        Assert.Equal(1, request.BlockCount);

        List<Point> points = Drain(request);
        Assert.Equal(3, points.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal("cpu", points[i].Measurement);
            Assert.Equal(timestamps[i], points[i].Timestamp);
            Assert.Equal("a", points[i].Tags["host"]);
            Assert.Equal(values[i], points[i].Fields["value"].AsDouble());
        }
    }

    [Fact]
    public void WriteColumnar_RoundTrip_AllFieldTypes()
    {
        long[] timestamps = [10, 20];
        var block = new TsdbColumnarBlock(
            null,
            timestamps,
            [
                TsdbColumnarColumn.Float64("f", new double[] { 1.25, -2.5 }),
                TsdbColumnarColumn.Int64("i", new long[] { long.MaxValue, -42 }),
                TsdbColumnarColumn.Boolean("b", new bool[] { true, false }),
                TsdbColumnarColumn.String("s", ["你好", ""]),
                TsdbColumnarColumn.Vector("v", 3, new float[] { 1f, 2f, 3f, 4f, 5f, 6f }),
                TsdbColumnarColumn.GeoPoint("g", new GeoPoint[] { new(31.2, 121.5), new(-45.0, 170.0) }),
            ]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "db1", "m1", BulkFlushMode.Sync, [block]);
        TsdbWriteColumnarFrameRequest request = TsdbFrameCodec.DecodeWriteColumnarRequest(ParseSingleFrame(writer, out _));
        Assert.Equal(BulkFlushMode.Sync, request.FlushMode);

        List<Point> points = Drain(request);
        Assert.Equal(2, points.Count);

        Assert.Empty(points[0].Tags);
        Assert.Equal(1.25, points[0].Fields["f"].AsDouble());
        Assert.Equal(long.MaxValue, points[0].Fields["i"].AsLong());
        Assert.True(points[0].Fields["b"].AsBool());
        Assert.Equal("你好", points[0].Fields["s"].AsString());
        Assert.Equal(new float[] { 1f, 2f, 3f }, points[0].Fields["v"].AsVector().ToArray());
        Assert.Equal(31.2, points[0].Fields["g"].AsGeoPoint().Lat);

        Assert.Equal(-2.5, points[1].Fields["f"].AsDouble());
        Assert.Equal(-42, points[1].Fields["i"].AsLong());
        Assert.False(points[1].Fields["b"].AsBool());
        Assert.Equal("", points[1].Fields["s"].AsString());
        Assert.Equal(new float[] { 4f, 5f, 6f }, points[1].Fields["v"].AsVector().ToArray());
        Assert.Equal(170.0, points[1].Fields["g"].AsGeoPoint().Lon);
    }

    // ────────────────────────────── 稀疏列（presence 位图）──────────────────────────────

    [Fact]
    public void WriteColumnar_SparseColumns_PresenceBitmapSkipsRows()
    {
        long[] timestamps = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        // value 每行都有；temp 只在偶数行（0-based 奇数索引）
        bool[] tempPresence = new bool[10];
        var tempValues = new List<double>();
        for (int i = 0; i < 10; i++)
        {
            if (i % 2 == 1)
            {
                tempPresence[i] = true;
                tempValues.Add(i * 1.5);
            }
        }

        var block = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "sparse" },
            timestamps,
            [
                TsdbColumnarColumn.Int64("value", Enumerable.Range(0, 10).Select(i => (long)i).ToArray()),
                TsdbColumnarColumn.Float64("temp", tempValues.ToArray(), tempPresence),
            ]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 3, "demo", "cpu", BulkFlushMode.None, [block]);
        List<Point> points = Drain(TsdbFrameCodec.DecodeWriteColumnarRequest(ParseSingleFrame(writer, out _)));

        Assert.Equal(10, points.Count);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(i, points[i].Fields["value"].AsLong());
            if (i % 2 == 1)
                Assert.Equal(i * 1.5, points[i].Fields["temp"].AsDouble());
            else
                Assert.False(points[i].Fields.ContainsKey("temp"));
        }
    }

    [Fact]
    public void WriteColumnar_AllAbsentRow_ThrowsBulkIngestException()
    {
        // 单列 presence 全 false 的行 = 该行无任何字段 → 行级 BulkIngestException（可被 Skip 策略吞掉）
        long[] timestamps = [1, 2];
        bool[] presence = [true, false];
        var block = new TsdbColumnarBlock(
            null, timestamps,
            [TsdbColumnarColumn.Float64("v", new double[] { 1.0 }, presence)]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "d", "m", BulkFlushMode.None, [block]);
        var reader = new TsdbColumnarPointReader(TsdbFrameCodec.DecodeWriteColumnarRequest(ParseSingleFrame(writer, out _)));

        Assert.True(reader.TryRead(out Point first));
        Assert.Equal(1.0, first.Fields["v"].AsDouble());
        Assert.Throws<BulkIngestException>(() => reader.TryRead(out _));
        // 行游标已推进，跳过后到达末尾
        Assert.False(reader.TryRead(out _));
    }

    // ────────────────────────────── 多块 ──────────────────────────────

    [Fact]
    public void WriteColumnar_MultipleBlocks_DifferentTagSets()
    {
        var blockA = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "a", ["dc"] = "east" },
            new long[] { 100 },
            [TsdbColumnarColumn.Float64("value", new double[] { 1.0 })]);
        var blockB = new TsdbColumnarBlock(
            new Dictionary<string, string> { ["host"] = "b" },
            new long[] { 200, 300 },
            [TsdbColumnarColumn.Float64("value", new double[] { 2.0, 3.0 })]);

        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 2, "demo", "cpu", BulkFlushMode.Async, [blockA, blockB]);
        TsdbWriteColumnarFrameRequest request = TsdbFrameCodec.DecodeWriteColumnarRequest(ParseSingleFrame(writer, out _));
        Assert.Equal(2, request.BlockCount);

        List<Point> points = Drain(request);
        Assert.Equal(3, points.Count);
        Assert.Equal("east", points[0].Tags["dc"]);
        Assert.Equal(100, points[0].Timestamp);
        Assert.Equal("b", points[1].Tags["host"]);
        Assert.False(points[1].Tags.ContainsKey("dc"));
        Assert.Equal(3.0, points[2].Fields["value"].AsDouble());
    }

    // ────────────────────────────── 响应帧 ──────────────────────────────

    [Fact]
    public void WriteColumnarResponse_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarResponse(writer, 9, 123456);

        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.False(header.IsError);
        Assert.Equal(9u, header.StreamId);
        Assert.Equal(123456, TsdbFrameCodec.DecodeWriteColumnarResponse(payload.Span));
    }

    // ────────────────────────────── 编码侧防御 ──────────────────────────────

    [Fact]
    public void Encode_ValueCountMismatch_Throws()
    {
        var block = new TsdbColumnarBlock(
            null, new long[] { 1, 2 },
            [TsdbColumnarColumn.Float64("v", new double[] { 1.0 })]); // 2 行但只有 1 个值且无 presence

        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() =>
            TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "d", "m", BulkFlushMode.None, [block]));
    }

    [Fact]
    public void Encode_EmptyBlocks_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() =>
            TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "d", "m", BulkFlushMode.None, []));
    }

    // ────────────────────────────── 解码侧防御 ──────────────────────────────

    [Fact]
    public void Decode_InvalidFlushMode_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        EncodeValidSingleRowFrame(writer);
        byte[] bytes = writer.WrittenMemory.ToArray();
        // 帧体布局：varstr db("d"=2B) + varstr m("m"=2B) + flushMode(1B)
        int flushModeOffset = FrameHeader.Size + 2 + 2;
        bytes[flushModeOffset] = 9;
        Assert.Throws<FrameFormatException>(() =>
            TsdbFrameCodec.DecodeWriteColumnarRequest(ExtractPayload(bytes)));
    }

    [Fact]
    public void Decode_RowCountExceedsRemaining_Throws()
    {
        // 手工构造：块声明 1000 行但时间戳区不足
        var writer = new ArrayBufferWriter<byte>();
        EncodeValidSingleRowFrame(writer);
        byte[] bytes = writer.WrittenMemory.ToArray();
        // 定位 rowCount：db(2) + m(2) + flush(1) + blockCount(1) + tagCount(1) → rowCount varuint 在此
        int rowCountOffset = FrameHeader.Size + 2 + 2 + 1 + 1 + 1;
        Assert.Equal(1, bytes[rowCountOffset]); // 原 1 行
        bytes[rowCountOffset] = 0x7F;           // 127 行，远超剩余 8 字节时间戳区
        var reader = new TsdbColumnarPointReader(TsdbFrameCodec.DecodeWriteColumnarRequest(ExtractPayload(bytes)));
        Assert.Throws<FrameFormatException>(() => reader.TryRead(out _));
    }

    [Fact]
    public void Decode_ReservedFieldName_ThrowsFrameFormat()
    {
        // 编码时字段名合法，改字节注入保留字符 ','
        var writer = new ArrayBufferWriter<byte>();
        EncodeValidSingleRowFrame(writer, fieldName: "x");
        byte[] bytes = writer.WrittenMemory.ToArray();
        int index = bytes.AsSpan(FrameHeader.Size).IndexOf((byte)'x');
        Assert.True(index >= 0);
        bytes[FrameHeader.Size + index] = (byte)',';

        var reader = new TsdbColumnarPointReader(TsdbFrameCodec.DecodeWriteColumnarRequest(ExtractPayload(bytes)));
        Assert.Throws<FrameFormatException>(() => reader.TryRead(out _));
    }

    [Fact]
    public void Decode_NegativeTimestamp_ThrowsBulkIngest()
    {
        var block = new TsdbColumnarBlock(
            null, new long[] { -5 },
            [TsdbColumnarColumn.Float64("v", new double[] { 1.0 })]);
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "d", "m", BulkFlushMode.None, [block]);
        var reader = new TsdbColumnarPointReader(TsdbFrameCodec.DecodeWriteColumnarRequest(ParseSingleFrame(writer, out _)));
        Assert.Throws<BulkIngestException>(() => reader.TryRead(out _));
    }

    [Fact]
    public void Decode_TrailingGarbageAfterBlocks_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        EncodeValidSingleRowFrame(writer);
        byte[] frame = writer.WrittenMemory.ToArray();
        // 在帧体末尾追加 3 字节垃圾并修 payloadLength
        byte[] bytes = new byte[frame.Length + 3];
        frame.CopyTo(bytes, 0);
        uint newLength = (uint)(bytes.Length - FrameHeader.Size);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, newLength);

        var reader = new TsdbColumnarPointReader(TsdbFrameCodec.DecodeWriteColumnarRequest(ExtractPayload(bytes)));
        Assert.True(reader.TryRead(out _));
        Assert.Throws<FrameFormatException>(() => reader.TryRead(out _));
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static void EncodeValidSingleRowFrame(ArrayBufferWriter<byte> writer, string fieldName = "v")
    {
        var block = new TsdbColumnarBlock(
            null, new long[] { 1 },
            [TsdbColumnarColumn.Float64(fieldName, new double[] { 1.0 })]);
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "d", "m", BulkFlushMode.None, [block]);
    }

    private static List<Point> Drain(in TsdbWriteColumnarFrameRequest request)
    {
        var reader = new TsdbColumnarPointReader(request);
        var points = new List<Point>();
        while (reader.TryRead(out Point point))
            points.Add(point);
        return points;
    }

    private static ReadOnlyMemory<byte> ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
        => ExtractPayload(writer.WrittenMemory.ToArray(), out header);

    private static ReadOnlyMemory<byte> ExtractPayload(byte[] bytes)
        => ExtractPayload(bytes, out _);

    private static ReadOnlyMemory<byte> ExtractPayload(byte[] bytes, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }
}
