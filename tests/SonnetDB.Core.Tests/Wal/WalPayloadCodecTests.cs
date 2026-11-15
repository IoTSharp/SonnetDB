using SonnetDB.IO;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Wal;

/// <summary>
/// <see cref="WalPayloadCodec"/> 单元测试。
/// </summary>
public sealed class WalPayloadCodecTests
{
    // ── WritePoint round-trip ────────────────────────────────────────────────

    [Theory]
    [InlineData(1.234)]
    [InlineData(-9999.0)]
    public void WritePoint_Float64_RoundTrip(double numericValue)
    {
        var value = FieldValue.FromDouble(numericValue);
        RoundTripWritePoint(0xABCDUL, 1000L, "temperature", value);
    }

    [Theory]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void WritePoint_Int64_RoundTrip(long numericValue)
    {
        var value = FieldValue.FromLong(numericValue);
        RoundTripWritePoint(0x1UL, 2000L, "count", value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WritePoint_Boolean_RoundTrip(bool boolValue)
    {
        var value = FieldValue.FromBool(boolValue);
        RoundTripWritePoint(0x2UL, 3000L, "active", value);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("中文字段值")]
    [InlineData("")]
    public void WritePoint_String_RoundTrip(string strValue)
    {
        var value = FieldValue.FromString(strValue);
        RoundTripWritePoint(0x3UL, 4000L, "label", value);
    }

    [Theory]
    [InlineData("温度")]
    [InlineData("field_name_with_underscores")]
    public void WritePoint_ChineseFieldName_RoundTrip(string fieldName)
    {
        var value = FieldValue.FromDouble(1.0);
        RoundTripWritePoint(0x4UL, 5000L, fieldName, value);
    }

    private static void RoundTripWritePoint(ulong seriesId, long pointTs, string fieldName, FieldValue value)
    {
        int size = WalPayloadCodec.MeasureWritePoint(fieldName, value);
        byte[] buf = new byte[size];
        var writer = new SpanWriter(buf.AsSpan());
        WalPayloadCodec.WriteWritePointPayload(ref writer, seriesId, pointTs, fieldName, value);
        Assert.Equal(size, writer.Position);

        var reader = new SpanReader(buf.AsSpan());
        var record = WalPayloadCodec.ReadWritePointPayload(reader, lsn: 1L, timestampUtcTicks: 0L);

        Assert.Equal(seriesId, record.SeriesId);
        Assert.Equal(pointTs, record.PointTimestamp);
        Assert.Equal(fieldName, record.FieldName);
        Assert.Equal(value, record.Value);
    }

    // ── CreateSeries round-trip ─────────────────────────────────────────────

    [Fact]
    public void CreateSeries_EmptyTags_RoundTrip()
    {
        var tags = new Dictionary<string, string>();
        RoundTripCreateSeries(0xBEEFUL, "cpu", tags);
    }

    [Fact]
    public void CreateSeries_MultipleTags_RoundTrip()
    {
        var tags = new Dictionary<string, string>
        {
            ["host"] = "server1",
            ["region"] = "us-east",
            ["env"] = "prod",
        };
        RoundTripCreateSeries(0xCAFEUL, "network", tags);
    }

    [Fact]
    public void CreateSeries_ChineseTagValues_RoundTrip()
    {
        var tags = new Dictionary<string, string>
        {
            ["位置"] = "上海",
            ["类型"] = "温度传感器",
        };
        RoundTripCreateSeries(0x1234UL, "sensor", tags);
    }

    private static void RoundTripCreateSeries(ulong seriesId, string measurement, IReadOnlyDictionary<string, string> tags)
    {
        int size = WalPayloadCodec.MeasureCreateSeries(measurement, tags);
        byte[] buf = new byte[size];
        var writer = new SpanWriter(buf.AsSpan());
        WalPayloadCodec.WriteCreateSeriesPayload(ref writer, seriesId, measurement, tags);
        Assert.Equal(size, writer.Position);

        var reader = new SpanReader(buf.AsSpan());
        var record = WalPayloadCodec.ReadCreateSeriesPayload(reader, lsn: 1L, timestampUtcTicks: 0L);

        Assert.Equal(seriesId, record.SeriesId);
        Assert.Equal(measurement, record.Measurement);
        Assert.Equal(tags.Count, record.Tags.Count);
        foreach (var kv in tags)
        {
            Assert.True(record.Tags.ContainsKey(kv.Key));
            Assert.Equal(kv.Value, record.Tags[kv.Key]);
        }
    }

    // ── Checkpoint round-trip ───────────────────────────────────────────────

    [Fact]
    public void Checkpoint_RoundTrip()
    {
        byte[] buf = new byte[8];
        var writer = new SpanWriter(buf.AsSpan());
        WalPayloadCodec.WriteCheckpointPayload(ref writer, checkpointLsn: 999L);
        Assert.Equal(8, writer.Position);

        var reader = new SpanReader(buf.AsSpan());
        var record = WalPayloadCodec.ReadCheckpointPayload(reader, lsn: 1L, timestampUtcTicks: 0L);
        Assert.Equal(999L, record.CheckpointLsn);
    }

    [Fact]
    public void Checkpoint_NegativeLsn_IsRejectedAsCorruptPayload()
    {
        byte[] payload = new byte[8];
        var writer = new SpanWriter(payload);
        writer.WriteInt64(-1);

        Assert.Throws<InvalidDataException>(() =>
            WalPayloadCodec.ReadCheckpointPayload(new SpanReader(payload), lsn: 1L, timestampUtcTicks: 0L));
    }

    // ── Vector round-trip（PR #58 a） ────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(384)]
    public void WritePoint_Vector_RoundTrip(int dim)
    {
        var arr = new float[dim];
        for (int i = 0; i < dim; i++)
            arr[i] = (float)Math.Sin(i * 0.1);
        var value = FieldValue.FromVector(arr);

        // 期望 valueBytes = 4(dim) + dim*4 字节，外加固定开销由 MeasureWritePoint 计算。
        int size = WalPayloadCodec.MeasureWritePoint("embedding", value);
        Assert.Equal(8 + 8 + 1 + 3 + 4 + "embedding".Length + 4 + 4 + dim * 4, size);

        byte[] buf = new byte[size];
        var writer = new SpanWriter(buf.AsSpan());
        WalPayloadCodec.WriteWritePointPayload(ref writer, seriesId: 0x58UL, pointTimestamp: 1234L, "embedding", value);
        Assert.Equal(size, writer.Position);

        var reader = new SpanReader(buf.AsSpan());
        var record = WalPayloadCodec.ReadWritePointPayload(reader, lsn: 1L, timestampUtcTicks: 0L);
        Assert.Equal(0x58UL, record.SeriesId);
        Assert.Equal(1234L, record.PointTimestamp);
        Assert.Equal("embedding", record.FieldName);
        Assert.Equal(FieldType.Vector, record.Value.Type);
        Assert.Equal(dim, record.Value.VectorDimension);
        Assert.True(record.Value.AsVector().Span.SequenceEqual(arr));
    }
}
