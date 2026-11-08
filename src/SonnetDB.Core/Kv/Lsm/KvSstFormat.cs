using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using SonnetDB.Buffers;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// SST 文件常量与块 trailer 编解码。
/// </summary>
/// <remarks>
/// 每个块写为 <c>payload | compressionType(1B) | CRC32(4B, LE)</c>，CRC 覆盖 payload 与压缩类型字节，
/// 算法与现有 KV 文件一致（<see cref="Crc32"/>，IEEE）。v1 只允许 <see cref="CompressionNone"/>。
/// </remarks>
internal static class KvSstFormat
{
    /// <summary>文件扩展名。</summary>
    internal const string FileExtension = ".SDBKVSST";

    /// <summary>当前格式版本。</summary>
    internal const int CurrentVersion = 1;

    /// <summary>块 trailer 字节数。</summary>
    internal const int BlockTrailerSize = 5;

    /// <summary>不压缩。</summary>
    internal const byte CompressionNone = 0;

    /// <summary>footer 魔数 <c>"SDBKVSST"</c>。</summary>
    internal static ReadOnlySpan<byte> Magic => "SDBKVSST"u8;

    /// <summary>计算块 trailer 中的 CRC。</summary>
    internal static uint ComputeBlockCrc(ReadOnlySpan<byte> payload, byte compressionType)
    {
        var crc = new Crc32();
        crc.Append(payload);
        crc.Append([compressionType]);
        return crc.GetCurrentHashAsUInt32();
    }
}

/// <summary>块在文件中的位置；<see cref="Length"/> 为 payload 长度，不含 trailer。</summary>
internal readonly record struct KvSstBlockHandle(long Offset, int Length)
{
    /// <summary>编码后最大字节数。</summary>
    internal const int MaxEncodedLength = KvLsmVarint.MaxLength * 2;

    /// <summary>编码为 <c>varint offset | varint length</c>，返回写入字节数。</summary>
    internal int Encode(Span<byte> destination)
    {
        int written = KvLsmVarint.Write(destination, (ulong)Offset);
        written += KvLsmVarint.Write(destination[written..], (ulong)Length);
        return written;
    }

    /// <summary>从 <paramref name="source"/> 解码一个块位置。</summary>
    /// <exception cref="InvalidDataException">编码损坏或存在多余字节。</exception>
    internal static KvSstBlockHandle Decode(ReadOnlySpan<byte> source)
    {
        int position = 0;
        long offset = KvLsmVarint.ReadInt64(source, ref position);
        int length = KvLsmVarint.ReadInt32(source, ref position);
        if (position != source.Length)
            throw new InvalidDataException("KV-LSM block handle has trailing bytes.");
        return new KvSstBlockHandle(offset, length);
    }
}

/// <summary>
/// SST 文件尾部固定 64 字节结构（little-endian）。
/// </summary>
/// <remarks>
/// <see cref="FooterCrc"/> 覆盖前 60 字节。range tombstone 块位置在 v1 中必须为 0，
/// 由 K5 启用（届时升级 <see cref="FormatVersion"/>）。
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct KvSstFooter
{
    /// <summary>footer 字节数。</summary>
    internal const int Size = 64;

    /// <summary>CRC 覆盖的字节数。</summary>
    internal const int CrcCoveredLength = 60;

    /// <summary>魔数 <c>"SDBKVSST"</c>。</summary>
    public InlineBytes8 Magic;

    /// <summary>格式版本。</summary>
    public int FormatVersion;

    /// <summary>布隆过滤器块 payload 长度。</summary>
    public int FilterLength;

    /// <summary>布隆过滤器块偏移。</summary>
    public long FilterOffset;

    /// <summary>索引块偏移。</summary>
    public long IndexOffset;

    /// <summary>索引块 payload 长度。</summary>
    public int IndexLength;

    /// <summary>属性块 payload 长度。</summary>
    public int PropertiesLength;

    /// <summary>属性块偏移。</summary>
    public long PropertiesOffset;

    /// <summary>range tombstone 块偏移（v1 必须为 0）。</summary>
    public long RangeDelOffset;

    /// <summary>range tombstone 块 payload 长度（v1 必须为 0）。</summary>
    public int RangeDelLength;

    /// <summary>前 60 字节的 CRC32。</summary>
    public uint FooterCrc;

    /// <summary>按 little-endian 写入 <paramref name="destination"/> 并计算 CRC。</summary>
    internal void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException("Footer destination is too small.", nameof(destination));
        Magic.AsReadOnlySpan().CopyTo(destination[..8]);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], FilterLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], FilterOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], IndexOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..], IndexLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[36..], PropertiesLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[40..], PropertiesOffset);
        BinaryPrimitives.WriteInt64LittleEndian(destination[48..], RangeDelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[56..], RangeDelLength);
        FooterCrc = Crc32.HashToUInt32(destination[..CrcCoveredLength]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[60..], FooterCrc);
    }

    /// <summary>从 little-endian 字节解析 footer 并校验魔数、CRC 与版本。</summary>
    /// <exception cref="InvalidDataException">魔数、CRC、版本或保留字段无效。</exception>
    internal static KvSstFooter Read(ReadOnlySpan<byte> source)
    {
        if (source.Length != Size)
            throw new InvalidDataException("KV-LSM SST footer has an invalid length.");
        if (!source[..8].SequenceEqual(KvSstFormat.Magic))
            throw new InvalidDataException("KV-LSM SST footer magic is invalid.");
        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(source[60..]);
        if (crc != Crc32.HashToUInt32(source[..CrcCoveredLength]))
            throw new InvalidDataException("KV-LSM SST footer CRC mismatch.");

        KvSstFooter footer = default;
        source[..8].CopyTo(footer.Magic.AsSpan());
        footer.FormatVersion = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
        footer.FilterLength = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
        footer.FilterOffset = BinaryPrimitives.ReadInt64LittleEndian(source[16..]);
        footer.IndexOffset = BinaryPrimitives.ReadInt64LittleEndian(source[24..]);
        footer.IndexLength = BinaryPrimitives.ReadInt32LittleEndian(source[32..]);
        footer.PropertiesLength = BinaryPrimitives.ReadInt32LittleEndian(source[36..]);
        footer.PropertiesOffset = BinaryPrimitives.ReadInt64LittleEndian(source[40..]);
        footer.RangeDelOffset = BinaryPrimitives.ReadInt64LittleEndian(source[48..]);
        footer.RangeDelLength = BinaryPrimitives.ReadInt32LittleEndian(source[56..]);
        footer.FooterCrc = crc;

        if (footer.FormatVersion != KvSstFormat.CurrentVersion)
            throw new InvalidDataException($"KV-LSM SST format version {footer.FormatVersion} is not supported.");
        if (footer.RangeDelOffset != 0 || footer.RangeDelLength != 0)
            throw new InvalidDataException("KV-LSM SST v1 cannot contain a range tombstone block.");
        return footer;
    }
}

/// <summary>
/// SST 属性块内容。
/// </summary>
/// <param name="EntryCount">条目总数（含墓碑）。</param>
/// <param name="DeletionCount">点删除墓碑数。</param>
/// <param name="DataBlockCount">数据块数。</param>
/// <param name="MinSequence">最小 sequence。</param>
/// <param name="MaxSequence">最大 sequence。</param>
/// <param name="MinExpiresAtUtcTicks">最早到期时间（UTC ticks），没有带 TTL 的条目时为 0。</param>
/// <param name="Generation">写入时所属的 keyspace generation。</param>
/// <param name="CreatedUtcTicks">文件创建时间（UTC ticks）。</param>
/// <param name="RawKeyBytes">全部 internal key 的未压缩字节数。</param>
/// <param name="RawValueBytes">全部 value 载荷字节数。</param>
/// <param name="SmallestKey">最小 internal key。</param>
/// <param name="LargestKey">最大 internal key。</param>
internal sealed record KvSstProperties(
    long EntryCount,
    long DeletionCount,
    int DataBlockCount,
    long MinSequence,
    long MaxSequence,
    long MinExpiresAtUtcTicks,
    long Generation,
    long CreatedUtcTicks,
    long RawKeyBytes,
    long RawValueBytes,
    byte[] SmallestKey,
    byte[] LargestKey)
{
    private const int Version = 1;
    private const int FixedSize = 4 + (8 * 9) + 4;

    /// <summary>编码为属性块载荷（little-endian 定长字段 + 长度前缀 key）。</summary>
    internal byte[] Encode()
    {
        byte[] result = new byte[FixedSize + 8 + SmallestKey.Length + LargestKey.Length];
        Span<byte> span = result;
        BinaryPrimitives.WriteInt32LittleEndian(span, Version);
        BinaryPrimitives.WriteInt64LittleEndian(span[4..], EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[12..], DeletionCount);
        BinaryPrimitives.WriteInt64LittleEndian(span[20..], MinSequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[28..], MaxSequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[36..], MinExpiresAtUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(span[44..], Generation);
        BinaryPrimitives.WriteInt64LittleEndian(span[52..], CreatedUtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(span[60..], RawKeyBytes);
        BinaryPrimitives.WriteInt64LittleEndian(span[68..], RawValueBytes);
        BinaryPrimitives.WriteInt32LittleEndian(span[76..], DataBlockCount);
        int position = FixedSize;
        BinaryPrimitives.WriteInt32LittleEndian(span[position..], SmallestKey.Length);
        SmallestKey.CopyTo(span[(position + 4)..]);
        position += 4 + SmallestKey.Length;
        BinaryPrimitives.WriteInt32LittleEndian(span[position..], LargestKey.Length);
        LargestKey.CopyTo(span[(position + 4)..]);
        return result;
    }

    /// <summary>解析属性块载荷并校验字段一致性。</summary>
    /// <exception cref="InvalidDataException">版本、长度或字段取值无效。</exception>
    internal static KvSstProperties Decode(ReadOnlySpan<byte> source)
    {
        if (source.Length < FixedSize + 8)
            throw new InvalidDataException("KV-LSM SST properties block is truncated.");
        if (BinaryPrimitives.ReadInt32LittleEndian(source) != Version)
            throw new InvalidDataException("KV-LSM SST properties version is not supported.");

        int position = FixedSize;
        byte[] smallest = ReadKey(source, ref position);
        byte[] largest = ReadKey(source, ref position);
        if (position != source.Length)
            throw new InvalidDataException("KV-LSM SST properties block has trailing bytes.");

        var properties = new KvSstProperties(
            EntryCount: BinaryPrimitives.ReadInt64LittleEndian(source[4..]),
            DeletionCount: BinaryPrimitives.ReadInt64LittleEndian(source[12..]),
            DataBlockCount: BinaryPrimitives.ReadInt32LittleEndian(source[76..]),
            MinSequence: BinaryPrimitives.ReadInt64LittleEndian(source[20..]),
            MaxSequence: BinaryPrimitives.ReadInt64LittleEndian(source[28..]),
            MinExpiresAtUtcTicks: BinaryPrimitives.ReadInt64LittleEndian(source[36..]),
            Generation: BinaryPrimitives.ReadInt64LittleEndian(source[44..]),
            CreatedUtcTicks: BinaryPrimitives.ReadInt64LittleEndian(source[52..]),
            RawKeyBytes: BinaryPrimitives.ReadInt64LittleEndian(source[60..]),
            RawValueBytes: BinaryPrimitives.ReadInt64LittleEndian(source[68..]),
            SmallestKey: smallest,
            LargestKey: largest);

        if (properties.EntryCount <= 0
            || properties.DeletionCount < 0
            || properties.DeletionCount > properties.EntryCount
            || properties.DataBlockCount <= 0
            || properties.MinSequence < 0
            || properties.MaxSequence < properties.MinSequence
            || properties.MaxSequence > KvInternalKey.MaxSequence
            || properties.MinExpiresAtUtcTicks < 0
            || properties.Generation < 0
            || properties.RawKeyBytes < 0
            || properties.RawValueBytes < 0)
        {
            throw new InvalidDataException("KV-LSM SST properties contain invalid values.");
        }

        KvInternalKey.Validate(smallest);
        KvInternalKey.Validate(largest);
        if (KvInternalKey.Compare(smallest, largest) > 0)
            throw new InvalidDataException("KV-LSM SST properties key range is inverted.");
        return properties;
    }

    private static byte[] ReadKey(ReadOnlySpan<byte> source, ref int position)
    {
        if (position > source.Length - 4)
            throw new InvalidDataException("KV-LSM SST properties key length is truncated.");
        int length = BinaryPrimitives.ReadInt32LittleEndian(source[position..]);
        position += 4;
        if (length < 0 || length > source.Length - position)
            throw new InvalidDataException("KV-LSM SST properties key is truncated.");
        byte[] key = source.Slice(position, length).ToArray();
        position += length;
        return key;
    }
}
