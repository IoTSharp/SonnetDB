using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Format;

namespace SonnetDB.Core.Tests.Vector.Format;

public sealed class HnswNodeHeaderTests
{
    [Fact]
    public void SizeOf_MatchesExpectedLayout()
    {
        // 4 (NodeId) + 1 (Level) + 1 (Tombstone) + 2 (Reserved0) + 16*2 (NeighborCounts) = 40
        Assert.Equal(40, Unsafe.SizeOf<HnswNodeHeader>());
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var header = new HnswNodeHeader
        {
            NodeId = 0xDEADBEEFu,
            Level = 7,
            Tombstone = 1,
            Reserved0 = 0,
        };
        for (int i = 0; i < 16; i++)
        {
            header.NeighborCounts[i] = (ushort)(i * 3 + 1);
        }

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<HnswNodeHeader>()];
        MemoryMarshal.Write(bytes, in header);
        var read = MemoryMarshal.Read<HnswNodeHeader>(bytes);

        Assert.Equal(header.NodeId, read.NodeId);
        Assert.Equal(header.Level, read.Level);
        Assert.Equal(header.Tombstone, read.Tombstone);
        Assert.Equal(header.Reserved0, read.Reserved0);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(header.NeighborCounts[i], read.NeighborCounts[i]);
        }
    }
}
