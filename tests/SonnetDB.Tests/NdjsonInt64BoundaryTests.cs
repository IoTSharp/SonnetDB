using System.Numerics;
using System.Text.Json;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>GH-Issue #198：NDJSON 编码拒绝无法用 Int64 表示的整数。</summary>
public sealed class NdjsonInt64BoundaryTests
{
    [Theory]
    [InlineData(9223372036854775808UL)]
    [InlineData(ulong.MaxValue)]
    public void WriteValue_UnsignedBeyondInt64_RejectsBeforeOutput(ulong value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        var error = Assert.Throws<InvalidDataException>(() => NdjsonRowWriter.WriteValue(writer, value));
        Assert.Contains("Int64", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, writer.BytesPending);
        Assert.Equal(0, stream.Length);
    }

    [Fact]
    public void WriteValue_BigInteger_RejectsBeforeOutput()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        Assert.Throws<InvalidDataException>(() => NdjsonRowWriter.WriteValue(writer, BigInteger.One));
        Assert.Equal(0, writer.BytesPending);
        Assert.Equal(0, stream.Length);
    }
}
