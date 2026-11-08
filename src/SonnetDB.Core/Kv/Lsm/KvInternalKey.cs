using System.Buffers.Binary;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// KV-LSM 内部条目类型，编码在 internal key 尾部 trailer 的最低字节。
/// </summary>
internal enum KvEntryKind : byte
{
    /// <summary>仅用于构造查找键，不能写入文件。</summary>
    SeekMarker = 0,

    /// <summary>写入或覆盖值。</summary>
    Put = 1,

    /// <summary>点删除墓碑。</summary>
    Delete = 2,

    // 3 = RangeDelete，K5 交付，K1 读取时按未知类型拒绝。
}

/// <summary>
/// KV-LSM internal key：<c>UserKey | Trailer(8B, 大端)</c>，
/// 其中 <c>Trailer = ((MaxSequence - sequence) &lt;&lt; 8) | kind</c>。
/// </summary>
/// <remarks>
/// 排序规则见 <see cref="Compare"/>：先按 user key 无符号字节序升序，再按 trailer 升序，
/// 即同一 user key 的多个版本按 sequence 降序排列，读者遇到的第一个可见版本就是最新版本。
/// </remarks>
internal static class KvInternalKey
{
    /// <summary>trailer 字节数。</summary>
    internal const int TrailerSize = 8;

    /// <summary>可编码的最大 sequence（56 位）。</summary>
    internal const long MaxSequence = (1L << 56) - 1;

    /// <summary>编码一个 internal key。</summary>
    /// <exception cref="ArgumentOutOfRangeException">sequence 超出 [0, <see cref="MaxSequence"/>]。</exception>
    /// <exception cref="ArgumentException">kind 不是可持久化类型。</exception>
    internal static byte[] Encode(ReadOnlySpan<byte> userKey, long sequence, KvEntryKind kind)
    {
        if (kind is not (KvEntryKind.Put or KvEntryKind.Delete))
            throw new ArgumentException($"KV-LSM entry kind {kind} cannot be persisted.", nameof(kind));
        return EncodeCore(userKey, sequence, kind);
    }

    /// <summary>
    /// 构造查找键：在 internal key 序中位于 <paramref name="userKey"/> 所有 sequence ≤
    /// <paramref name="snapshotSequence"/> 的版本之前、所有更新版本之后。
    /// </summary>
    internal static byte[] EncodeSeekKey(ReadOnlySpan<byte> userKey, long snapshotSequence)
        => EncodeCore(userKey, snapshotSequence, KvEntryKind.SeekMarker);

    /// <summary>
    /// 构造 <paramref name="userKey"/> 在 internal key 序中的最大可能 key（位于其所有版本之后、
    /// 下一个 user key 之前），用于“最后一个 ≤ userKey”的反向定位。
    /// </summary>
    internal static byte[] EncodeLastKey(ReadOnlySpan<byte> userKey)
    {
        byte[] result = new byte[userKey.Length + TrailerSize];
        userKey.CopyTo(result);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(userKey.Length), ulong.MaxValue);
        return result;
    }

    private static byte[] EncodeCore(ReadOnlySpan<byte> userKey, long sequence, KvEntryKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sequence, MaxSequence);
        byte[] result = new byte[userKey.Length + TrailerSize];
        userKey.CopyTo(result);
        ulong trailer = ((ulong)(MaxSequence - sequence) << 8) | (byte)kind;
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(userKey.Length), trailer);
        return result;
    }

    /// <summary>返回 internal key 中的 user key 部分。</summary>
    internal static ReadOnlySpan<byte> UserKey(ReadOnlySpan<byte> internalKey)
        => internalKey[..^TrailerSize];

    /// <summary>返回 internal key 中的 sequence。</summary>
    internal static long Sequence(ReadOnlySpan<byte> internalKey)
        => MaxSequence - (long)(BinaryPrimitives.ReadUInt64BigEndian(internalKey[^TrailerSize..]) >> 8);

    /// <summary>返回 internal key 中的条目类型。</summary>
    internal static KvEntryKind Kind(ReadOnlySpan<byte> internalKey)
        => (KvEntryKind)internalKey[^1];

    /// <summary>校验 internal key 长度与持久化类型。</summary>
    /// <exception cref="InvalidDataException">长度不足 trailer 或类型未知。</exception>
    internal static void Validate(ReadOnlySpan<byte> internalKey)
    {
        if (internalKey.Length < TrailerSize)
            throw new InvalidDataException("KV-LSM internal key is shorter than its trailer.");
        KvEntryKind kind = Kind(internalKey);
        if (kind is not (KvEntryKind.Put or KvEntryKind.Delete))
            throw new InvalidDataException($"KV-LSM internal key has unsupported kind {(byte)kind}.");
    }

    /// <summary>按 internal key 序比较两个 key。</summary>
    internal static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int userCompare = UserKey(left).SequenceCompareTo(UserKey(right));
        if (userCompare != 0)
            return userCompare;
        return left[^TrailerSize..].SequenceCompareTo(right[^TrailerSize..]);
    }
}
