using System.Text;
using SonnetDB.Vector.IO;
using Xunit;

namespace SonnetDB.Core.Tests.Vector.IO;

/// <summary>向量文件 IEEE CRC32 的持久格式与输入边界差分测试。</summary>
public sealed class Crc32Tests
{
    private static readonly int[] Lengths = [0, 1, 7, 8, 15, 16, 63, 64, 4_096, 1_048_576];
    private static readonly uint[] LegacyTable = BuildLegacyTable();

    /// <summary>验证标准 IEEE CRC32 向量，防止误换成 CRC32C 多项式。</summary>
    [Fact]
    public void Compute_StandardGoldenVectors_MatchesIeeeCrc32()
    {
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0x414FA339u, Crc32.Compute(Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog")));
    }

    /// <summary>固定随机语料覆盖非对齐 offset、小输入、块边界和一兆字节 payload。</summary>
    [Fact]
    public void Compute_RandomOffsetsAndLengths_MatchesLegacyFormat()
    {
        var random = new Random(0x43524333);
        foreach (int length in Lengths)
        {
            var payload = new byte[checked(length + 15)];
            random.NextBytes(payload);
            for (int offset = 0; offset <= 15; offset++)
            {
                ReadOnlySpan<byte> input = payload.AsSpan(offset, length);
                Assert.Equal(ComputeLegacy(input), Crc32.Compute(input));
            }
        }
    }

    /// <summary>构造冻结旧实现使用的 IEEE 802.3 查找表。</summary>
    private static uint[] BuildLegacyTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint current = index;
            for (int bit = 0; bit < 8; bit++)
                current = (current & 1) != 0 ? 0xEDB88320u ^ (current >> 1) : current >> 1;
            table[index] = current;
        }

        return table;
    }

    /// <summary>按旧版逐字节算法计算 CRC32，作为持久格式差分基准。</summary>
    private static uint ComputeLegacy(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
            crc = LegacyTable[(crc ^ value) & byte.MaxValue] ^ (crc >> 8);
        return crc ^ uint.MaxValue;
    }
}
