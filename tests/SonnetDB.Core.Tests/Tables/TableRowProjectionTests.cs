using System.Buffers.Binary;
using System.Diagnostics;
using SonnetDB.Tables;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证恢复投影只省略大字段实例化，不能弱化既有行格式和值域校验。</summary>
public sealed class TableRowProjectionTests
{
    /// <summary>全类型完整投影和逐列投影都应与原解码器一致，未选列始终为空。</summary>
    [Fact]
    public void DecodeProjection_AllTypesAndNullableColumns_MatchFullDecode()
    {
        var schema = TableSchema.Create("projection_types",
            [
                ("id", TableColumnType.Int64, false),
                ("integer", TableColumnType.Int64, true),
                ("floating", TableColumnType.Float64, true),
                ("boolean", TableColumnType.Boolean, true),
                ("text", TableColumnType.String, true),
                ("datetime", TableColumnType.DateTime, true),
                ("blob", TableColumnType.Blob, true),
                ("json", TableColumnType.Json, true),
                ("decimal", TableColumnType.Decimal, true),
                ("time", TableColumnType.Time, true),
                ("absent", TableColumnType.String, true)
            ], ["id"]);
        object?[] source = [7L, long.MinValue, -1.25d, true, "中文与🏁",
            DateTimeOffset.UnixEpoch.AddMilliseconds(1234), new byte[] { 0, 1, 255 },
            "{\"plate\":\"新B12345\"}", -12345.6789m, new TimeOnly(14, 30, 12).Add(TimeSpan.FromTicks(123)), null];
        var payload = TableRowCodec.Encode(schema, source);
        var expected = TableRowCodec.Decode(schema, payload);
        var completeMask = Enumerable.Repeat(true, schema.Columns.Count).ToArray();
        var actual = TableRowCodec.DecodeProjection(schema, payload, completeMask);
        Assert.Equal(expected.Length, actual.Length);
        var watch = Stopwatch.StartNew();
        // 固定十一种列选择，在五秒预算内校验整个 schema 长度和每个未选列。
        for (var selected = 0; selected < schema.Columns.Count; selected++)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("投影类型回归超时。");
            AssertValueEqual(expected[selected], actual[selected]);
            var mask = new bool[schema.Columns.Count];
            mask[selected] = true;
            var projected = TableRowCodec.DecodeProjection(schema, payload, mask);
            Assert.Equal(expected.Length, projected.Length);
            for (var column = 0; column < projected.Length; column++)
            {
                if (column == selected) AssertValueEqual(expected[column], projected[column]);
                else Assert.Null(projected[column]);
            }
        }
    }

    /// <summary>未选的多兆文本、JSON 和二进制字段不得产生与正文大小成比例的分配。</summary>
    [Fact]
    public void DecodeProjection_UnselectedLargeValues_AvoidPayloadSizedAllocations()
    {
        var schema = TableSchema.Create("projection_large",
            [("id", TableColumnType.Int64, false), ("text", TableColumnType.String, true),
             ("json", TableColumnType.Json, true), ("blob", TableColumnType.Blob, true)], ["id"]);
        var payload = TableRowCodec.Encode(schema,
            [42L, new string('x', 2 * 1024 * 1024), "\"" + new string('y', 2 * 1024 * 1024) + "\"", new byte[2 * 1024 * 1024]]);
        bool[] mask = [true, false, false, false];
        _ = TableRowCodec.DecodeProjection(schema, payload, mask);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var projected = TableRowCodec.DecodeProjection(schema, payload, mask);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(42L, projected[0]);
        Assert.Null(projected[1]);
        Assert.Null(projected[2]);
        Assert.Null(projected[3]);
        Assert.True(allocated < 256 * 1024, $"跳过六兆正文却分配了 {allocated} 字节。");
    }

    /// <summary>列掩码必须严格匹配 schema，过短和过长均须拒绝。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void DecodeProjection_InvalidMaskLength_IsRejected(int maskLength)
    {
        var schema = CreateSingleColumnSchema(TableColumnType.String);
        var payload = TableRowCodec.Encode(schema, ["abc"]);
        var error = Assert.Throws<ArgumentException>(() => TableRowCodec.DecodeProjection(schema, payload, new bool[maskLength]));
        Assert.Equal("materializedColumns", error.ParamName);
    }

    /// <summary>跳过变长列仍须拒绝负长度、截断正文和多余尾字节，保持原错误类型与消息。</summary>
    [Theory]
    [InlineData(TableColumnType.String)]
    [InlineData(TableColumnType.Json)]
    [InlineData(TableColumnType.Blob)]
    public void DecodeProjection_UnselectedVariableFields_RejectMalformedLayouts(TableColumnType type)
    {
        var schema = CreateSingleColumnSchema(type);
        AssertSameDecodeFailure(schema, [1, 255, 255, 255, 255]);
        AssertSameDecodeFailure(schema, [1, 4, 0, 0, 0, 127]);
        AssertSameDecodeFailure(schema, [1, 0, 0, 0, 0, 127]);
    }

    /// <summary>未选列也不能忽略非法 NULL 标志、缺失标志或空值后多余正文。</summary>
    [Fact]
    public void DecodeProjection_UnselectedFields_RejectMalformedNullMarkers()
    {
        var schema = CreateSingleColumnSchema(TableColumnType.String);
        AssertSameDecodeFailure(schema, [2]);
        AssertSameDecodeFailure(schema, []);
        AssertSameDecodeFailure(schema, [0, 1]);
    }

    /// <summary>未选定长列仍执行原解码器的 decimal、TimeOnly 和 Unix 时间值域校验。</summary>
    [Theory]
    [InlineData(TableColumnType.Decimal)]
    [InlineData(TableColumnType.Time)]
    [InlineData(TableColumnType.DateTime)]
    public void DecodeProjection_UnselectedFixedFields_RejectInvalidValues(TableColumnType type)
    {
        var schema = CreateSingleColumnSchema(type);
        var payload = new byte[type == TableColumnType.Decimal ? 17 : 9];
        payload[0] = 1;
        if (type == TableColumnType.Decimal)
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(13), 1);
        else
            BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(1), long.MaxValue);
        AssertSameDecodeFailure(schema, payload);
        AssertSameDecodeFailure(schema, payload[..^1]);
    }

    /// <summary>构造一个可空单列 schema，避免主键约束干扰行布局测试。</summary>
    private static TableSchema CreateSingleColumnSchema(TableColumnType type)
        => TableSchema.Create("projection_single", [("value", type, true)], []);

    /// <summary>以完整解码的实际异常为基准，分别验证跳过列和选中列时错误合同一致。</summary>
    private static void AssertSameDecodeFailure(TableSchema schema, byte[] payload)
    {
        var expected = Record.Exception(() => TableRowCodec.Decode(schema, payload));
        Assert.NotNull(expected);
        var skipped = Record.Exception(() => TableRowCodec.DecodeProjection(schema, payload, new[] { false }));
        var selected = Record.Exception(() => TableRowCodec.DecodeProjection(schema, payload, new[] { true }));
        Assert.NotNull(skipped);
        Assert.NotNull(selected);
        Assert.Equal(expected.GetType(), skipped.GetType());
        Assert.Equal(expected.Message, skipped.Message);
        Assert.Equal(expected.GetType(), selected.GetType());
        Assert.Equal(expected.Message, selected.Message);
    }

    /// <summary>二进制字段按内容比较，其余列沿用标量值相等判断。</summary>
    private static void AssertValueEqual(object? expected, object? actual)
    {
        if (expected is byte[] bytes) Assert.Equal(bytes, Assert.IsType<byte[]>(actual));
        else Assert.Equal(expected, actual);
    }
}
