using System.Buffers;
using SonnetDB.Model;
using SonnetDB.Protocol;
using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="SqlFrameCodec"/> SQL 流式列式结果集编解码测试（#238）：
/// 请求（含参数）往返、meta/rows/end 帧往返、列类型推断（稠密/null 位图/variant/全 null）、
/// 大 long 精度保持、块切分与解码防御。
/// </summary>
public sealed class SqlFrameCodecTests
{
    // ────────────────────────────── query 请求 ──────────────────────────────

    [Fact]
    public void QueryRequest_RoundTrip_NoParameters()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 42, "demo", "SELECT * FROM cpu");

        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.Equal((byte)FrameService.Sql, header.Service);
        Assert.Equal((byte)SqlFrameOp.Query, header.Op);
        Assert.Equal(42u, header.StreamId);
        Assert.False(header.IsResponse);

        SqlQueryFrameRequest request = SqlFrameCodec.DecodeQueryRequest(payload);
        Assert.Equal("demo", request.Db);
        Assert.Equal("SELECT * FROM cpu", request.Sql);
        Assert.Null(request.Parameters);
    }

    [Fact]
    public void QueryRequest_RoundTrip_AllParameterKinds()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 1, "db1", "SELECT * FROM t WHERE a = @i AND b = @f AND c = @b AND d = @s AND e = @n",
            new Dictionary<string, object?>
            {
                ["i"] = long.MaxValue,
                ["f"] = 2.5,
                ["b"] = true,
                ["s"] = "你好 world",
                ["n"] = null,
            });

        SqlQueryFrameRequest request = SqlFrameCodec.DecodeQueryRequest(ParseSingleFrame(writer, out _));
        SqlParameters parameters = Assert.IsType<SqlParameters>(request.Parameters);
        Assert.True(parameters.TryResolve(-1, "i", out object? i));
        Assert.Equal(long.MaxValue, Assert.IsType<long>(i));
        Assert.True(parameters.TryResolve(-1, "f", out object? f));
        Assert.Equal(2.5, Assert.IsType<double>(f));
        Assert.True(parameters.TryResolve(-1, "b", out object? b));
        Assert.Equal(true, b);
        Assert.True(parameters.TryResolve(-1, "s", out object? s));
        Assert.Equal("你好 world", s);
        Assert.True(parameters.TryResolve(-1, "n", out object? n));
        Assert.Null(n);
    }

    [Fact]
    public void QueryRequest_EmptySql_Throws()
    {
        // 编码端已拒绝空 sql，这里验证解码防御：payload = varstr("d") + varstr("") + varuint(0)
        byte[] payload = [1, (byte)'d', 0, 0];
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRequest(payload));
    }

    [Fact]
    public void QueryRequest_TrailingBytes_Throws()
    {
        // payload = varstr("d") + varstr("x") + varuint(0) + 多余 1 字节
        byte[] payload = [1, (byte)'d', 1, (byte)'x', 0, 0xFF];
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRequest(payload));
    }

    // ────────────────────────────── meta 帧 ──────────────────────────────

    [Fact]
    public void MetaFrame_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 9, ["time", "host", "value"]);

        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.False(header.IsError);
        Assert.Equal(9u, header.StreamId);
        Assert.Equal(SqlQueryChunkKind.Meta, SqlFrameCodec.PeekChunkKind(payload));

        string[] columns = SqlFrameCodec.DecodeQueryMetaFrame(payload);
        Assert.Equal(["time", "host", "value"], columns);
    }

    [Fact]
    public void MetaFrame_EmptyColumns_RoundTrip()
    {
        // 空投影结果集（如空表 SELECT）也要能表达
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, []);
        string[] columns = SqlFrameCodec.DecodeQueryMetaFrame(ParseSingleFrame(writer, out _));
        Assert.Empty(columns);
    }

    // ────────────────────────────── rows 帧：类型推断 ──────────────────────────────

    [Fact]
    public void RowsFrame_RoundTrip_DenseTypedColumns()
    {
        var rows = MakeRows(
            [1000L, "a", 1.5, true],
            [2000L, "b", 2.5, false],
            [3000L, "c", 3.5, true]);

        object?[][] decoded = RoundTripRows(rows, 4);
        Assert.Equal(3, decoded.Length);
        Assert.Equal(1000L, decoded[0][0]);
        Assert.Equal("b", decoded[1][1]);
        Assert.Equal(3.5, decoded[2][2]);
        Assert.Equal(true, decoded[2][3]);
        Assert.Equal(false, decoded[1][3]);
    }

    [Fact]
    public void RowsFrame_RoundTrip_NullBitmap()
    {
        // 9 行制造跨字节位图；value 列仅奇数行有值
        var rows = new List<IReadOnlyList<object?>>();
        for (int i = 0; i < 9; i++)
            rows.Add(new object?[] { (long)i, i % 2 == 1 ? i * 1.5 : null });

        object?[][] decoded = RoundTripRows(rows, 2);
        Assert.Equal(9, decoded.Length);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal((long)i, decoded[i][0]);
            if (i % 2 == 1)
                Assert.Equal(i * 1.5, decoded[i][1]);
            else
                Assert.Null(decoded[i][1]);
        }
    }

    [Fact]
    public void RowsFrame_RoundTrip_AllNullColumn()
    {
        var rows = MakeRows(
            [1L, null],
            [2L, null]);

        object?[][] decoded = RoundTripRows(rows, 2);
        Assert.Null(decoded[0][1]);
        Assert.Null(decoded[1][1]);
    }

    [Fact]
    public void RowsFrame_RoundTrip_VariantColumn_PreservesBigLongPrecision()
    {
        // 整型与浮点混列不得合并为 double：big long 必须原样往返（#219 Q15 精度语义）
        long bigLong = long.MaxValue - 1;
        var rows = MakeRows(
            [bigLong],
            [2.5],
            ["mixed"],
            [(object?)null]);

        object?[][] decoded = RoundTripRows(rows, 1);
        Assert.Equal(bigLong, Assert.IsType<long>(decoded[0][0]));
        Assert.Equal(2.5, decoded[1][0]);
        Assert.Equal("mixed", decoded[2][0]);
        Assert.Null(decoded[3][0]);
    }

    [Fact]
    public void RowsFrame_RoundTrip_ExtendedValueTypes()
    {
        var utc = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
        var rows = MakeRows(
            [new byte[] { 1, 2, 3 }, utc, new float[] { 1f, 2f, 3f }, new GeoPoint(31.2, 121.5)],
            [Array.Empty<byte>(), new DateTimeOffset(utc).AddHours(1), new float[] { 4f, 5f, 6f }, new GeoPoint(-45.0, 170.0)]);

        object?[][] decoded = RoundTripRows(rows, 4);
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded[0][0]);
        Assert.Equal(Array.Empty<byte>(), decoded[1][0]);
        Assert.Equal(utc, Assert.IsType<DateTime>(decoded[0][1]));
        Assert.Equal(DateTimeKind.Utc, ((DateTime)decoded[0][1]!).Kind);
        Assert.Equal(utc.AddHours(1), decoded[1][1]);
        Assert.Equal(new float[] { 1f, 2f, 3f }, decoded[0][2]);
        Assert.Equal(new GeoPoint(31.2, 121.5), decoded[0][3]);
        Assert.Equal(new GeoPoint(-45.0, 170.0), decoded[1][3]);
    }

    [Fact]
    public void RowsFrame_IntFamilyNormalizesToInt64()
    {
        var rows = MakeRows(
            [(int)7],
            [(short)-3],
            [(byte)255]);

        object?[][] decoded = RoundTripRows(rows, 1);
        Assert.Equal(7L, Assert.IsType<long>(decoded[0][0]));
        Assert.Equal(-3L, decoded[1][0]);
        Assert.Equal(255L, decoded[2][0]);
    }

    [Fact]
    public void RowsFrame_SubRange_EncodesOnlyRequestedRows()
    {
        var rows = MakeRows([1L], [2L], [3L], [4L], [5L]);

        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, start: 1, count: 3, columnCount: 1);
        object?[][] decoded = SqlFrameCodec.DecodeQueryRowsFrame(ParseSingleFrame(writer, out _));
        Assert.Equal(3, decoded.Length);
        Assert.Equal(2L, decoded[0][0]);
        Assert.Equal(4L, decoded[2][0]);
    }

    // ────────────────────────────── end 帧 ──────────────────────────────

    [Fact]
    public void EndFrame_RoundTrip()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryEndFrame(writer, 5, rowCount: 12345, elapsedMilliseconds: 6.75);

        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.Equal(SqlQueryChunkKind.End, SqlFrameCodec.PeekChunkKind(payload));
        (long rowCount, double elapsed) = SqlFrameCodec.DecodeQueryEndFrame(payload);
        Assert.Equal(12345, rowCount);
        Assert.Equal(6.75, elapsed);
    }

    // ────────────────────────────── 块切分 ──────────────────────────────

    [Fact]
    public void SelectChunkRowCount_RespectsTargetBytesAndMaxRows()
    {
        // 每行约 1KB 字符串 → 目标 4KB 应切在 ~4 行
        var rows = new List<IReadOnlyList<object?>>();
        string big = new('x', 1024);
        for (int i = 0; i < 100; i++)
            rows.Add(new object?[] { big });

        int chunk = SqlFrameCodec.SelectChunkRowCount(rows, 0, targetChunkBytes: 4096, maxRows: 1000);
        Assert.InRange(chunk, 3, 5);

        int capped = SqlFrameCodec.SelectChunkRowCount(rows, 0, targetChunkBytes: int.MaxValue, maxRows: 10);
        Assert.Equal(10, capped);

        // 单行超大也至少返回 1
        int atLeastOne = SqlFrameCodec.SelectChunkRowCount(rows, 99, targetChunkBytes: 1, maxRows: 10);
        Assert.Equal(1, atLeastOne);
    }

    // ────────────────────────────── 解码防御 ──────────────────────────────

    [Fact]
    public void DecodeRows_BadChunkKind_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, ["a"]);
        var payload = ParseSingleFrame(writer, out _);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(payload));
    }

    [Fact]
    public void DecodeRows_CellCountBomb_Throws()
    {
        // rowCount=65536 × columnCount=4096 = 2.6 亿单元格 > MaxChunkCells，须在分配前拒绝
        byte[] payload = new byte[16];
        payload[0] = (byte)SqlQueryChunkKind.Rows;
        int pos = 1;
        pos += WriteVarUInt(payload.AsSpan(pos), SqlFrameCodec.MaxChunkRows);
        pos += WriteVarUInt(payload.AsSpan(pos), SqlFrameCodec.MaxColumnCount);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryRowsFrame(payload.AsSpan(0, pos)));
    }

    [Fact]
    public void DecodeRows_TruncatedValues_Throws()
    {
        var rows = MakeRows([1L], [2L]);
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, 0, 2, 1);
        byte[] frame = writer.WrittenMemory.ToArray();
        // 砍掉最后 4 字节值数据
        Assert.ThrowsAny<Exception>(() =>
            SqlFrameCodec.DecodeQueryRowsFrame(frame.AsSpan(FrameHeader.Size, frame.Length - FrameHeader.Size - 4)));
    }

    [Fact]
    public void DecodeMeta_OversizedColumnName_Throws()
    {
        var writer = new ArrayBufferWriter<byte>();
        string longName = new('c', SqlFrameCodec.MaxNameBytes + 1);
        // 编码端不设列名上限（列名来自引擎），解码端防御
        SqlFrameCodec.EncodeQueryMetaFrame(writer, 1, [longName]);
        var payload = ParseSingleFrame(writer, out _);
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.DecodeQueryMetaFrame(payload));
    }

    [Fact]
    public void PeekChunkKind_Invalid_Throws()
    {
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.PeekChunkKind([]));
        Assert.Throws<FrameFormatException>(() => SqlFrameCodec.PeekChunkKind([99]));
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static List<IReadOnlyList<object?>> MakeRows(params object?[][] rows)
        => [.. rows];

    private static object?[][] RoundTripRows(IReadOnlyList<IReadOnlyList<object?>> rows, int columnCount)
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRowsFrame(writer, 1, rows, 0, rows.Count, columnCount);
        var payload = ParseSingleFrame(writer, out FrameHeader header);
        Assert.True(header.IsResponse);
        Assert.Equal(SqlQueryChunkKind.Rows, SqlFrameCodec.PeekChunkKind(payload));
        return SqlFrameCodec.DecodeQueryRowsFrame(payload);
    }

    private static byte[] ParseSingleFrame(ArrayBufferWriter<byte> writer, out FrameHeader header)
    {
        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory);
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payload));
        Assert.Equal(0, buffer.Length);
        return payload.ToArray();
    }

    private static int WriteVarUInt(Span<byte> destination, uint value)
    {
        int count = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            destination[count++] = b;
        } while (value != 0);
        return count;
    }
}
