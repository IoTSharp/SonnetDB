using System.Buffers.Binary;
using SonnetDB.Model;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

public sealed class Int64RleCodecTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Write_LongConstantRun_PreservesFullInt64Range(long value)
    {
        var points = Enumerable.Range(0, 4096).Select(i => new DataPoint(i, FieldValue.FromLong(value))).ToArray();
        var payload = new byte[Int64RleCodec.Measure(points)];
        Int64RleCodec.Write(points, payload);
        Assert.Equal(10, payload.Length);
        var decoded = new DataPoint[points.Length];
        Int64RleCodec.DecodeRangeInto(payload, points.Length, 0, points.Length, decoded);
        Assert.All(decoded, point => Assert.Equal(value, point.Value.AsLong()));
    }

    [Fact]
    public void DecodeRangeInto_CrossesRuns_PreservesValuesAndDestinationTimestamps()
    {
        long[] values = [long.MinValue, 0, long.MaxValue];
        var points = Enumerable.Range(0, 768).Select(i => new DataPoint(i, FieldValue.FromLong(values[i / 256]))).ToArray();
        var payload = new byte[Int64RleCodec.Measure(points)];
        Int64RleCodec.Write(points, payload);
        var decoded = Enumerable.Range(253, 263).Select(i => new DataPoint(i, default)).ToArray();
        Int64RleCodec.DecodeRangeInto(payload, points.Length, 253, decoded.Length, decoded);
        Assert.Equal(points[253..516], decoded);
    }

    public static IEnumerable<object[]> MalformedRuns()
    {
        yield return [Array.Empty<byte>(), 1];
        yield return [new byte[7], 1];
        yield return [new byte[8], 1];
        yield return [WithLength(0), 1];
        yield return [WithLength(2), 1];
        yield return [WithLength(1), 2];
        yield return [WithLength(0x80), 1];
        yield return [WithLength(0x81, 0), 1];
        yield return [WithLength(0xff, 0xff, 0xff, 0xff, 0x10), 1];
        yield return [WithLength(0x80, 0x80, 0x80, 0x80, 0x80, 1), 1];
        yield return [WithLength(1, 0), 1];
    }

    [Theory]
    [MemberData(nameof(MalformedRuns))]
    public void DecodeRangeInto_MalformedRun_RejectsInsteadOfPartialSuccess(byte[] payload, int totalCount)
    {
        Assert.Throws<InvalidDataException>(() =>
            Int64RleCodec.DecodeRangeInto(payload, totalCount, 0, 1, new DataPoint[1]));
    }

    [Fact]
    public void DecodeRangeInto_CorruptionAfterRequestedRange_StillRejects()
    {
        byte[] payload = [.. WithLength(1), .. WithLength(0)];
        Assert.Throws<InvalidDataException>(() =>
            Int64RleCodec.DecodeRangeInto(payload, 2, 0, 1, new DataPoint[1]));
    }

    [Fact]
    public void DecodeRangeInto_EmptyBlock_RequiresEmptyPayload()
    {
        Int64RleCodec.DecodeRangeInto([], 0, 0, 0, []);
        Assert.Throws<InvalidDataException>(() => Int64RleCodec.DecodeRangeInto(new byte[1], 0, 0, 0, []));
    }

    private static byte[] WithLength(params byte[] length)
    {
        var payload = new byte[8 + length.Length];
        BinaryPrimitives.WriteInt64LittleEndian(payload, long.MaxValue);
        length.CopyTo(payload, 8);
        return payload;
    }
}
