using System.Runtime.CompilerServices;
using SonnetDB.Vector.Format;

namespace SonnetDB.Core.Tests.Vector.Format;

public sealed class IvfListHeaderTests
{
    [Fact]
    public void Size_Is28Bytes()
    {
        Assert.Equal(28, Unsafe.SizeOf<IvfListHeader>());
        Assert.Equal(28, IvfListHeader.Size);
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var header = new IvfListHeader
        {
            ListId = 0xDEADBEEF,
            VectorCount = 1234,
            DataOffset = 0x0102030405060708L,
            Dimensions = 384,
            ListKind = (byte)IvfListKind.Pq,
            CodeBytes = 8,
            Reserved0 = 0,
            Reserved1 = 0,
        };

        Span<byte> buf = stackalloc byte[IvfListHeader.Size];
        header.WriteTo(buf);
        var read = IvfListHeader.ReadFrom(buf);

        Assert.Equal(header.ListId, read.ListId);
        Assert.Equal(header.VectorCount, read.VectorCount);
        Assert.Equal(header.DataOffset, read.DataOffset);
        Assert.Equal(header.Dimensions, read.Dimensions);
        Assert.Equal(header.ListKind, read.ListKind);
        Assert.Equal(header.CodeBytes, read.CodeBytes);
    }

    [Fact]
    public void WriteTo_TooSmallBuffer_Throws()
    {
        var header = default(IvfListHeader);
        var buf = new byte[IvfListHeader.Size - 1];
        Assert.Throws<ArgumentException>(() => header.WriteTo(buf));
    }
}
