using System.Buffers;
using SonnetDB.Ingest;
using SonnetDB.Model;
using SonnetDB.Protocol;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Protocol;

/// <summary>
/// <see cref="TsdbColumnarBlockBuilder"/> 行式点集 → 列式块聚合测试（#261）。
/// 聚合结果经 <see cref="TsdbFrameCodec"/> 编码 + <see cref="TsdbColumnarPointReader"/> 列转行往返，
/// 验证与原始点集语义等价（同一 tag 组内保序）。
/// </summary>
public sealed class TsdbColumnarBlockBuilderTests
{
    [Fact]
    public void Build_EmptyPoints_ReturnsEmpty()
    {
        Assert.Empty(TsdbColumnarBlockBuilder.Build([]));
    }

    [Fact]
    public void Build_SingleTagGroup_OneDenseBlock()
    {
        var points = new List<Point>
        {
            Point.Create("cpu", 1000, Tags(("host", "a")), Fields(("value", FieldValue.FromDouble(1.5)))),
            Point.Create("cpu", 2000, Tags(("host", "a")), Fields(("value", FieldValue.FromDouble(2.5)))),
            Point.Create("cpu", 3000, Tags(("host", "a")), Fields(("value", FieldValue.FromDouble(3.5)))),
        };

        IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
        Assert.Single(blocks);
        Assert.Equal(3, blocks[0].Timestamps.Length);
        Assert.Single(blocks[0].Columns);
        // 稠密：presence 为空
        Assert.True(blocks[0].Columns[0].Presence.IsEmpty);

        AssertRoundTrip("cpu", points, blocks);
    }

    [Fact]
    public void Build_MultipleTagGroups_SeparateBlocks()
    {
        var points = new List<Point>
        {
            Point.Create("cpu", 1000, Tags(("host", "a")), Fields(("v", FieldValue.FromLong(1)))),
            Point.Create("cpu", 1500, Tags(("host", "b")), Fields(("v", FieldValue.FromLong(2)))),
            Point.Create("cpu", 2000, Tags(("host", "a")), Fields(("v", FieldValue.FromLong(3)))),
        };

        IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
        Assert.Equal(2, blocks.Count);
        // host=a 有 2 行、host=b 有 1 行（顺序按首次出现）
        Assert.Equal(2, blocks[0].Timestamps.Length);
        Assert.Equal(1, blocks[1].Timestamps.Length);

        AssertRoundTrip("cpu", points, blocks);
    }

    [Fact]
    public void Build_TagOrderInsensitive_SameBlock()
    {
        // 同一组 tag 不同书写顺序应归入同一块
        var points = new List<Point>
        {
            Point.Create("m", 1, Tags(("a", "1"), ("b", "2")), Fields(("v", FieldValue.FromLong(10)))),
            Point.Create("m", 2, Tags(("b", "2"), ("a", "1")), Fields(("v", FieldValue.FromLong(20)))),
        };

        IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
        Assert.Single(blocks);
        Assert.Equal(2, blocks[0].Timestamps.Length);
    }

    [Fact]
    public void Build_SparseField_PresenceBitmap()
    {
        // 第 2 行缺 temp 字段 → temp 列稀疏
        var points = new List<Point>
        {
            Point.Create("s", 1000, Tags(("host", "x")), Fields(("value", FieldValue.FromLong(1)), ("temp", FieldValue.FromDouble(0.5)))),
            Point.Create("s", 2000, Tags(("host", "x")), Fields(("value", FieldValue.FromLong(2)))),
            Point.Create("s", 3000, Tags(("host", "x")), Fields(("value", FieldValue.FromLong(3)), ("temp", FieldValue.FromDouble(4.5)))),
        };

        IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
        Assert.Single(blocks);
        TsdbColumnarBlock block = blocks[0];
        Assert.Equal(3, block.Timestamps.Length);

        TsdbColumnarColumn valueCol = block.Columns.Single(c => c.Name == "value");
        TsdbColumnarColumn tempCol = block.Columns.Single(c => c.Name == "temp");
        Assert.True(valueCol.Presence.IsEmpty); // 每行都有 value → 稠密
        Assert.False(tempCol.Presence.IsEmpty);  // temp 缺一行 → 稀疏
        Assert.Equal(new[] { true, false, true }, tempCol.Presence.ToArray());

        AssertRoundTrip("s", points, blocks);
    }

    [Fact]
    public void Build_AllFieldTypes_RoundTrip()
    {
        var points = new List<Point>
        {
            Point.Create("all", 10, null, Fields(
                ("f", FieldValue.FromDouble(1.25)),
                ("i", FieldValue.FromLong(42)),
                ("b", FieldValue.FromBool(true)),
                ("s", FieldValue.FromString("hello")),
                ("vec", FieldValue.FromVector(new float[] { 1f, 2f, 3f })),
                ("geo", FieldValue.FromGeoPoint(30.5, 120.25)))),
            Point.Create("all", 20, null, Fields(
                ("f", FieldValue.FromDouble(2.5)),
                ("i", FieldValue.FromLong(-7)),
                ("b", FieldValue.FromBool(false)),
                ("s", FieldValue.FromString("世界")),
                ("vec", FieldValue.FromVector(new float[] { 4f, 5f, 6f })),
                ("geo", FieldValue.FromGeoPoint(-45.0, -100.0)))),
        };

        IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
        AssertRoundTrip("all", points, blocks);
    }

    [Fact]
    public void Build_ConflictingFieldType_Throws()
    {
        // 同一序列内同名字段先 double 后 long → 冲突
        var points = new List<Point>
        {
            Point.Create("m", 1, Tags(("h", "a")), Fields(("v", FieldValue.FromDouble(1.0)))),
            Point.Create("m", 2, Tags(("h", "a")), Fields(("v", FieldValue.FromLong(2)))),
        };

        Assert.Throws<BulkIngestException>(() => TsdbColumnarBlockBuilder.Build(points));
    }

    [Fact]
    public void Build_ConflictingVectorDim_Throws()
    {
        var points = new List<Point>
        {
            Point.Create("m", 1, Tags(("h", "a")), Fields(("v", FieldValue.FromVector(new float[] { 1f, 2f })))),
            Point.Create("m", 2, Tags(("h", "a")), Fields(("v", FieldValue.FromVector(new float[] { 1f, 2f, 3f })))),
        };

        Assert.Throws<BulkIngestException>(() => TsdbColumnarBlockBuilder.Build(points));
    }

    [Fact]
    public void Build_DifferentTagGroupsSameField_NoConflict()
    {
        // 不同 tag 组各自独立列，即便同名字段类型不同也互不影响
        var points = new List<Point>
        {
            Point.Create("m", 1, Tags(("h", "a")), Fields(("v", FieldValue.FromDouble(1.0)))),
            Point.Create("m", 2, Tags(("h", "b")), Fields(("v", FieldValue.FromLong(2)))),
        };

        IReadOnlyList<TsdbColumnarBlock> blocks = TsdbColumnarBlockBuilder.Build(points);
        Assert.Equal(2, blocks.Count);
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private static void AssertRoundTrip(string measurement, IReadOnlyList<Point> original, IReadOnlyList<TsdbColumnarBlock> blocks)
    {
        var writer = new ArrayBufferWriter<byte>();
        TsdbFrameCodec.EncodeWriteColumnarRequest(writer, 1, "db", measurement, BulkFlushMode.None, blocks);

        var buffer = new ReadOnlySequence<byte>(writer.WrittenMemory.ToArray());
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out _, out ReadOnlySequence<byte> payload));
        TsdbWriteColumnarFrameRequest request = TsdbFrameCodec.DecodeWriteColumnarRequest(payload.ToArray());

        var reader = new TsdbColumnarPointReader(request);
        var decoded = new List<Point>();
        while (reader.TryRead(out Point p))
            decoded.Add(p);

        Assert.Equal(original.Count, decoded.Count);

        // 列转行按块顺序输出，同一 tag 组内保序但块间可能重排；按 (tag签名, timestamp) 匹配。
        var expected = original
            .OrderBy(TagSignature, StringComparer.Ordinal).ThenBy(p => p.Timestamp)
            .ToList();
        var actual = decoded
            .OrderBy(TagSignature, StringComparer.Ordinal).ThenBy(p => p.Timestamp)
            .ToList();

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(measurement, actual[i].Measurement);
            Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
            Assert.Equal(TagSignature(expected[i]), TagSignature(actual[i]));
            AssertFieldsEqual(expected[i].Fields, actual[i].Fields);
        }
    }

    private static void AssertFieldsEqual(IReadOnlyDictionary<string, FieldValue> expected, IReadOnlyDictionary<string, FieldValue> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (KeyValuePair<string, FieldValue> kv in expected)
        {
            Assert.True(actual.TryGetValue(kv.Key, out FieldValue got), $"缺字段 {kv.Key}");
            Assert.Equal(kv.Value.Type, got.Type);
            switch (kv.Value.Type)
            {
                case FieldType.Float64: Assert.Equal(kv.Value.AsDouble(), got.AsDouble()); break;
                case FieldType.Int64: Assert.Equal(kv.Value.AsLong(), got.AsLong()); break;
                case FieldType.Boolean: Assert.Equal(kv.Value.AsBool(), got.AsBool()); break;
                case FieldType.String: Assert.Equal(kv.Value.AsString(), got.AsString()); break;
                case FieldType.Vector: Assert.True(kv.Value.AsVector().Span.SequenceEqual(got.AsVector().Span)); break;
                case FieldType.GeoPoint: Assert.Equal(kv.Value.AsGeoPoint(), got.AsGeoPoint()); break;
            }
        }
    }

    private static string TagSignature(Point p)
        => string.Join('|', p.Tags.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key}={t.Value}"));

    private static Dictionary<string, string> Tags(params (string Key, string Value)[] tags)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in tags)
            dict[key] = value;
        return dict;
    }

    private static Dictionary<string, FieldValue> Fields(params (string Key, FieldValue Value)[] fields)
    {
        var dict = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        foreach ((string key, FieldValue value) in fields)
            dict[key] = value;
        return dict;
    }
}
