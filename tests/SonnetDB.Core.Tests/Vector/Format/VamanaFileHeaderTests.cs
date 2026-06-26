using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SonnetDB.Vector.Format;

namespace SonnetDB.Core.Tests.Vector.Format;

public sealed class VamanaFileHeaderTests
{
    [Fact]
    public void SizeOf_MatchesExpectedLayout()
    {
        // 8 (Magic) + 4 (Version) + 4 (MaxDegree) + 4 (Alpha) + 4 (EntryPointId)
        // + 4 (NodeCount) + 4 (Dimensions) + 1 (MetricKind) + 1 (InlineVectors) + 14 (Reserved) = 48
        Assert.Equal(48, Unsafe.SizeOf<VamanaFileHeader>());
    }

    [Fact]
    public void RoundTrip_PreservesAllFields()
    {
        var header = new VamanaFileHeader
        {
            Version = VamanaFileHeaderConstants.CurrentVersion,
            MaxDegree = 64,
            Alpha = 1.2f,
            EntryPointId = 12345,
            NodeCount = 1_000_000,
            Dimensions = 768,
            MetricKind = 1,
            InlineVectors = 1,
        };
        VamanaFileHeaderConstants.MagicAscii.CopyTo(header.Magic);

        Span<byte> bytes = stackalloc byte[Unsafe.SizeOf<VamanaFileHeader>()];
        MemoryMarshal.Write(bytes, in header);
        var read = MemoryMarshal.Read<VamanaFileHeader>(bytes);

        Assert.Equal(header.Version, read.Version);
        Assert.Equal(header.MaxDegree, read.MaxDegree);
        Assert.Equal(header.Alpha, read.Alpha);
        Assert.Equal(header.EntryPointId, read.EntryPointId);
        Assert.Equal(header.NodeCount, read.NodeCount);
        Assert.Equal(header.Dimensions, read.Dimensions);
        Assert.Equal(header.MetricKind, read.MetricKind);
        Assert.Equal(header.InlineVectors, read.InlineVectors);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(header.Magic[i], read.Magic[i]);
        }
    }

    [Fact]
    public void Magic_IsDvanAscii()
    {
        ReadOnlySpan<byte> magic = VamanaFileHeaderConstants.MagicAscii;
        Assert.Equal(4, magic.Length);
        Assert.Equal((byte)'D', magic[0]);
        Assert.Equal((byte)'V', magic[1]);
        Assert.Equal((byte)'A', magic[2]);
        Assert.Equal((byte)'N', magic[3]);
    }
}
