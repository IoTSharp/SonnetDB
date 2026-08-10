using SonnetDB.Graphs;
using SonnetDB.Kv;
using SonnetDB.Storage.Codecs;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class SortableScalarCodecTests
{
    [Fact]
    public void EncodeGraph_AllKinds_RoundTrips()
    {
        GraphPropertyValue[] values =
        [
            GraphPropertyValue.Null,
            GraphPropertyValue.FromInt64(long.MinValue),
            GraphPropertyValue.FromFloat64(double.NegativeInfinity),
            GraphPropertyValue.FromFloat64(BitConverter.Int64BitsToDouble(unchecked((long)0x7FF8_0000_0000_0042UL))),
            GraphPropertyValue.FromBoolean(false),
            GraphPropertyValue.FromString("a\0中文"),
            GraphPropertyValue.FromDateTime(DateTimeOffset.UnixEpoch.AddMilliseconds(-1)),
            GraphPropertyValue.FromBlob([0, 1, 0, 255]),
            GraphPropertyValue.FromJson("[0,\"x\"]"),
        ];

        foreach (GraphPropertyValue expected in values)
        {
            byte[] encoded = SortableScalarCodec.EncodeGraph(expected);
            GraphPropertyValue actual = SortableScalarCodec.DecodeGraph(encoded, out int consumed);

            Assert.Equal(encoded.Length, consumed);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void EncodeGraph_Int64_ByteOrderMatchesNumericOrder()
    {
        long[] values = [long.MinValue, -100, -1, 0, 1, 100, long.MaxValue];
        byte[][] encoded = values.Select(value =>
            SortableScalarCodec.EncodeGraph(GraphPropertyValue.FromInt64(value))).ToArray();

        AssertStrictlyIncreasing(encoded);
    }

    [Fact]
    public void EncodeGraph_Float64_ByteOrderMatchesNumericOrderIncludingSignedZero()
    {
        double[] values =
        [
            double.NegativeInfinity,
            -100,
            -double.Epsilon,
            -0.0,
            0.0,
            double.Epsilon,
            100,
            double.PositiveInfinity,
        ];
        byte[][] encoded = values.Select(value =>
            SortableScalarCodec.EncodeGraph(GraphPropertyValue.FromFloat64(value))).ToArray();

        AssertStrictlyIncreasing(encoded);
    }

    [Fact]
    public void EncodeGraph_StringAndBlob_UseLexicographicEscaping()
    {
        string[] strings = ["", "\0", "\0a", "a", "aa", "b"];
        byte[][] encodedStrings = strings.Select(value =>
            SortableScalarCodec.EncodeGraph(GraphPropertyValue.FromString(value))).ToArray();
        AssertStrictlyIncreasing(encodedStrings);

        byte[][] blobs = [[], [0], [0, 1], [1], [1, 0], [2]];
        byte[][] encodedBlobs = blobs.Select(value =>
            SortableScalarCodec.EncodeGraph(GraphPropertyValue.FromBlob(value))).ToArray();
        AssertStrictlyIncreasing(encodedBlobs);
    }

    [Fact]
    public void DecodeGraph_CorruptOrTruncatedValue_Throws()
    {
        Assert.Throws<InvalidDataException>(() => SortableScalarCodec.DecodeGraph([], out _));
        Assert.Throws<InvalidDataException>(() => SortableScalarCodec.DecodeGraph([0x7F], out _));
        Assert.Throws<InvalidDataException>(() => SortableScalarCodec.DecodeGraph([(byte)GraphPropertyKind.Int64, 0], out _));
        Assert.Throws<InvalidDataException>(() => SortableScalarCodec.DecodeGraph([(byte)GraphPropertyKind.String, 0], out _));
        Assert.Throws<InvalidDataException>(() => SortableScalarCodec.DecodeGraph([(byte)GraphPropertyKind.Boolean, 2], out _));
    }

    private static void AssertStrictlyIncreasing(IReadOnlyList<byte[]> values)
    {
        for (int i = 1; i < values.Count; i++)
        {
            Assert.True(
                KvKeyComparer.Instance.Compare(values[i - 1], values[i]) < 0,
                $"{Convert.ToHexString(values[i - 1])} must sort before {Convert.ToHexString(values[i])}");
        }
    }
}
