using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

public sealed class KvLsmEncodingTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(16_383UL)]
    [InlineData(16_384UL)]
    [InlineData((ulong)long.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void Varint_RoundTripsBoundaryValues(ulong value)
    {
        byte[] buffer = new byte[KvLsmVarint.MaxLength];
        int written = KvLsmVarint.Write(buffer, value);
        Assert.Equal(KvLsmVarint.Measure(value), written);

        int position = 0;
        Assert.Equal(value, KvLsmVarint.Read(buffer.AsSpan(0, written), ref position));
        Assert.Equal(written, position);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0x80, 0x00 })]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 })]
    [InlineData(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 })]
    public void Varint_RejectsTruncatedOverlongAndNonMinimalEncodings(byte[] encoded)
    {
        Assert.Throws<InvalidDataException>(() =>
        {
            int position = 0;
            KvLsmVarint.Read(encoded, ref position);
        });
    }

    [Fact]
    public void Varint_ReadInt32RejectsValuesAboveInt32()
    {
        byte[] buffer = new byte[KvLsmVarint.MaxLength];
        int written = KvLsmVarint.Write(buffer, (ulong)int.MaxValue + 1);
        Assert.Throws<InvalidDataException>(() =>
        {
            int position = 0;
            KvLsmVarint.ReadInt32(buffer.AsSpan(0, written), ref position);
        });
    }

    [Fact]
    public void InternalKey_OrdersByUserKeyThenSequenceDescending()
    {
        byte[][] keys =
        [
            KvInternalKey.Encode("ab"u8, 1, KvEntryKind.Put),
            KvInternalKey.Encode("a"u8, 1, KvEntryKind.Put),
            KvInternalKey.Encode("a"u8, 9, KvEntryKind.Delete),
            KvInternalKey.Encode(""u8, 0, KvEntryKind.Put),
            KvInternalKey.Encode("a"u8, KvInternalKey.MaxSequence, KvEntryKind.Put),
            KvInternalKey.Encode([0xFF], 3, KvEntryKind.Put),
        ];

        Array.Sort(keys, static (left, right) => KvInternalKey.Compare(left, right));

        Assert.Equal(
            ["/0", "a/" + KvInternalKey.MaxSequence, "a/9", "a/1", "ab/1", "ÿ/3"],
            keys.Select(Describe).ToArray());
    }

    [Fact]
    public void InternalKey_DecodesSequenceKindAndUserKey()
    {
        byte[] key = KvInternalKey.Encode("device:7"u8, 123_456_789, KvEntryKind.Delete);

        Assert.Equal("device:7", Encoding.UTF8.GetString(KvInternalKey.UserKey(key)));
        Assert.Equal(123_456_789, KvInternalKey.Sequence(key));
        Assert.Equal(KvEntryKind.Delete, KvInternalKey.Kind(key));
    }

    [Fact]
    public void InternalKey_SeekKeySitsBetweenNewerAndVisibleVersions()
    {
        byte[] newer = KvInternalKey.Encode("k"u8, 11, KvEntryKind.Put);
        byte[] visible = KvInternalKey.Encode("k"u8, 10, KvEntryKind.Put);
        byte[] seek = KvInternalKey.EncodeSeekKey("k"u8, 10);

        Assert.True(KvInternalKey.Compare(newer, seek) < 0);
        Assert.True(KvInternalKey.Compare(seek, visible) < 0);
    }

    [Fact]
    public void InternalKey_RejectsInvalidSequenceKindAndLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KvInternalKey.Encode("k"u8, -1, KvEntryKind.Put));
        Assert.Throws<ArgumentOutOfRangeException>(() => KvInternalKey.Encode("k"u8, KvInternalKey.MaxSequence + 1, KvEntryKind.Put));
        Assert.Throws<ArgumentException>(() => KvInternalKey.Encode("k"u8, 1, KvEntryKind.SeekMarker));
        Assert.Throws<InvalidDataException>(() => KvInternalKey.Validate(new byte[7]));
        Assert.Throws<InvalidDataException>(() => KvInternalKey.Validate(KvInternalKey.EncodeSeekKey("k"u8, 1)));
    }

    [Fact]
    public void Footer_IsSixtyFourByteUnmanagedLittleEndianStruct()
    {
        Assert.Equal(KvSstFooter.Size, Unsafe.SizeOf<KvSstFooter>());
        Assert.Equal(KvSstFooter.Size, Marshal.SizeOf<KvSstFooter>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<KvSstFooter>());

        KvSstFooter footer = CreateFooter();
        byte[] bytes = new byte[KvSstFooter.Size];
        footer.WriteTo(bytes);

        KvSstFooter decoded = KvSstFooter.Read(bytes);
        Assert.Equal(footer.FilterOffset, decoded.FilterOffset);
        Assert.Equal(footer.FilterLength, decoded.FilterLength);
        Assert.Equal(footer.IndexOffset, decoded.IndexOffset);
        Assert.Equal(footer.IndexLength, decoded.IndexLength);
        Assert.Equal(footer.PropertiesOffset, decoded.PropertiesOffset);
        Assert.Equal(footer.PropertiesLength, decoded.PropertiesLength);
        Assert.Equal(footer.FooterCrc, decoded.FooterCrc);

        if (BitConverter.IsLittleEndian)
        {
            // 与 MemoryMarshal 的内存布局一致，证明字段顺序、Pack=1 与 little-endian 合同。
            KvSstFooter viaMarshal = MemoryMarshal.Read<KvSstFooter>(bytes);
            Assert.Equal(bytes, MemoryMarshal.AsBytes(new ReadOnlySpan<KvSstFooter>(ref viaMarshal)).ToArray());
            Assert.Equal(footer.PropertiesOffset, viaMarshal.PropertiesOffset);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(59)]
    [InlineData(63)]
    public void Footer_RejectsAnySingleByteCorruption(int offset)
    {
        byte[] bytes = new byte[KvSstFooter.Size];
        CreateFooter().WriteTo(bytes);
        bytes[offset] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => KvSstFooter.Read(bytes));
    }

    [Fact]
    public void Footer_RejectsUnsupportedVersionAndRangeDeleteBlockEvenWithValidCrc()
    {
        KvSstFooter future = CreateFooter();
        future.FormatVersion = KvSstFormat.CurrentVersion + 1;
        byte[] futureBytes = new byte[KvSstFooter.Size];
        future.WriteTo(futureBytes);
        Assert.Throws<InvalidDataException>(() => KvSstFooter.Read(futureBytes));

        KvSstFooter rangeDelete = CreateFooter();
        rangeDelete.RangeDelOffset = 100;
        rangeDelete.RangeDelLength = 10;
        byte[] rangeDeleteBytes = new byte[KvSstFooter.Size];
        rangeDelete.WriteTo(rangeDeleteBytes);
        Assert.Throws<InvalidDataException>(() => KvSstFooter.Read(rangeDeleteBytes));
    }

    [Fact]
    public void BloomFilter_HasNoFalseNegativesAndBoundedFalsePositives()
    {
        const int count = 10_000;
        ulong[] hashes = Enumerable.Range(0, count)
            .Select(static i => KvBloomFilter.Hash(Encoding.UTF8.GetBytes("present:" + i)))
            .ToArray();
        KvBloomFilter filter = KvBloomFilter.Parse(KvBloomFilter.Build(hashes));

        for (int i = 0; i < count; i++)
            Assert.True(filter.MayContain(Encoding.UTF8.GetBytes("present:" + i)));

        int falsePositives = 0;
        for (int i = 0; i < count; i++)
        {
            if (filter.MayContain(Encoding.UTF8.GetBytes("absent:" + i)))
                falsePositives++;
        }

        Assert.True(falsePositives < count * 3 / 100, $"false positives: {falsePositives}");
    }

    [Fact]
    public void BloomFilter_EmptyKeySetRejectsEverythingAndInvalidPayloadIsRejected()
    {
        KvBloomFilter empty = KvBloomFilter.Parse(KvBloomFilter.Build([]));
        Assert.False(empty.MayContain("anything"u8));

        Assert.Throws<InvalidDataException>(() => KvBloomFilter.Parse(new byte[8]));
        byte[] badProbes = new byte[9];
        badProbes[^1] = KvBloomFilter.MaxProbes + 1;
        Assert.Throws<InvalidDataException>(() => KvBloomFilter.Parse(badProbes));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void Block_RoundTripsAndSeeksAcrossRestartPoints(int restartInterval)
    {
        var builder = new KvSstBlockBuilder(restartInterval);
        var keys = Enumerable.Range(0, 100)
            .Select(static i => KvInternalKey.Encode(Encoding.UTF8.GetBytes($"key:{i * 2:D4}"), 1, KvEntryKind.Put))
            .ToArray();
        foreach (byte[] key in keys)
            builder.Add(key, key.AsSpan(0, 4));

        var block = new KvSstBlock(builder.Finish().ToArray());
        KvSstBlockIterator iterator = block.CreateIterator();

        int index = 0;
        for (iterator.SeekToFirst(); iterator.Valid; iterator.MoveNext())
        {
            Assert.Equal(keys[index], iterator.Key.ToArray());
            Assert.Equal(keys[index].AsSpan(0, 4).ToArray(), iterator.Value.ToArray());
            index++;
        }
        Assert.Equal(keys.Length, index);

        iterator.Seek(KvInternalKey.Encode("key:0051"u8, 1, KvEntryKind.Put));
        Assert.Equal(keys[26], iterator.Key.ToArray());
        iterator.Seek(keys[50]);
        Assert.Equal(keys[50], iterator.Key.ToArray());
        iterator.Seek(KvInternalKey.Encode("a"u8, 1, KvEntryKind.Put));
        Assert.Equal(keys[0], iterator.Key.ToArray());
        iterator.Seek(KvInternalKey.Encode("z"u8, 1, KvEntryKind.Put));
        Assert.False(iterator.Valid);
    }

    [Fact]
    public void Block_RejectsCorruptRestartArray()
    {
        var builder = new KvSstBlockBuilder(2);
        builder.Add(KvInternalKey.Encode("a"u8, 1, KvEntryKind.Put), [1]);
        builder.Add(KvInternalKey.Encode("b"u8, 1, KvEntryKind.Put), [2]);
        builder.Add(KvInternalKey.Encode("c"u8, 1, KvEntryKind.Put), [3]);
        byte[] payload = builder.Finish().ToArray();

        byte[] zeroCount = (byte[])payload.Clone();
        zeroCount.AsSpan(zeroCount.Length - 4).Clear();
        Assert.Throws<InvalidDataException>(() => new KvSstBlock(zeroCount));

        byte[] hugeCount = (byte[])payload.Clone();
        hugeCount[^1] = 0x7F;
        Assert.Throws<InvalidDataException>(() => new KvSstBlock(hugeCount));

        byte[] badRestart = (byte[])payload.Clone();
        badRestart[payload.Length - 8] = 0xFF;
        Assert.Throws<InvalidDataException>(() => new KvSstBlock(badRestart));

        Assert.Throws<InvalidDataException>(() => new KvSstBlock(new byte[4]));
    }

    private static KvSstFooter CreateFooter()
    {
        KvSstFooter footer = default;
        KvSstFormat.Magic.CopyTo(MemoryMarshal.AsBytes(new Span<KvSstFooter>(ref footer))[..8]);
        footer.FormatVersion = KvSstFormat.CurrentVersion;
        footer.FilterOffset = 4096;
        footer.FilterLength = 129;
        footer.IndexOffset = 4230;
        footer.IndexLength = 77;
        footer.PropertiesOffset = 4312;
        footer.PropertiesLength = 120;
        return footer;
    }

    private static string Describe(byte[] internalKey)
        => Encoding.Latin1.GetString(KvInternalKey.UserKey(internalKey)) + "/" + KvInternalKey.Sequence(internalKey);
}
