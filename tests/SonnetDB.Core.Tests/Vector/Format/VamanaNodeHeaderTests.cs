using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Format;

namespace SonnetDB.Core.Tests.Vector.Format;

public sealed class VamanaNodeHeaderTests
{
    [Fact]
    public void SizeOf_MatchesExpectedLayout()
    {
        // 4 (NodeId) + 2 (NeighborCount) + 1 (Tombstone) + 1 (Reserved0) = 8
        Assert.Equal(8, Unsafe.SizeOf<VamanaNodeHeader>());
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var header = new VamanaNodeHeader
        {
            NodeId = 0xCAFEBABEu,
            NeighborCount = 42,
            Tombstone = 1,
            Reserved0 = 0,
        };

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<VamanaNodeHeader>()];
        MemoryMarshal.Write(bytes, in header);
        var read = MemoryMarshal.Read<VamanaNodeHeader>(bytes);

        Assert.Equal(header.NodeId, read.NodeId);
        Assert.Equal(header.NeighborCount, read.NeighborCount);
        Assert.Equal(header.Tombstone, read.Tombstone);
        Assert.Equal(header.Reserved0, read.Reserved0);
    }
}
