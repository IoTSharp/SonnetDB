using System.IO.Hashing;

namespace SonnetDB.Kv.Lsm;

/// <summary>
/// SST 全文件 user key 布隆过滤器。
/// </summary>
/// <remarks>
/// 布局：<c>bitArray | probeCount(1B)</c>。哈希为 XxHash64，低/高 32 位做双重哈希
/// <c>h1 + i * h2</c>。位数组至少 64 位，按字节对齐。
/// </remarks>
internal sealed class KvBloomFilter
{
    /// <summary>默认每个 key 占用的位数（约 1% 假阳性）。</summary>
    internal const int DefaultBitsPerKey = 10;

    /// <summary>允许的最大探测次数。</summary>
    internal const int MaxProbes = 30;

    private readonly byte[] _filter;
    private readonly long _bitCount;
    private readonly int _probes;

    private KvBloomFilter(byte[] filter, long bitCount, int probes)
    {
        _filter = filter;
        _bitCount = bitCount;
        _probes = probes;
    }

    /// <summary>按 user key 哈希值构建过滤器载荷。</summary>
    /// <param name="hashes">各个不同 user key 的 <see cref="Hash"/> 值。</param>
    /// <param name="bitsPerKey">每个 key 的位数，1～64。</param>
    internal static byte[] Build(IReadOnlyList<ulong> hashes, int bitsPerKey = DefaultBitsPerKey)
    {
        ArgumentNullException.ThrowIfNull(hashes);
        ArgumentOutOfRangeException.ThrowIfLessThan(bitsPerKey, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitsPerKey, 64);

        int probes = Math.Clamp((int)Math.Round(bitsPerKey * 0.69), 1, MaxProbes);
        long bits = Math.Max(64L, (long)hashes.Count * bitsPerKey);
        long bytes = (bits + 7) / 8;
        if (bytes > Array.MaxLength - 1)
            throw new InvalidOperationException("KV-LSM bloom filter exceeds the maximum array length.");
        bits = bytes * 8;

        byte[] filter = new byte[bytes + 1];
        foreach (ulong hash in hashes)
        {
            uint h1 = (uint)hash;
            uint h2 = (uint)(hash >> 32);
            for (int i = 0; i < probes; i++)
            {
                long bit = (long)((h1 + ((ulong)(uint)i * h2)) % (ulong)bits);
                filter[bit >> 3] |= (byte)(1 << (int)(bit & 7));
            }
        }
        filter[^1] = (byte)probes;
        return filter;
    }

    /// <summary>解析过滤器载荷。</summary>
    /// <exception cref="InvalidDataException">载荷长度或探测次数无效。</exception>
    internal static KvBloomFilter Parse(byte[] filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Parse(filter, filter.Length);
    }

    /// <summary>以 <paramref name="filter"/> 的前 <paramref name="length"/> 字节解析过滤器（其后可附带块 trailer），不复制位数组。</summary>
    /// <exception cref="InvalidDataException">过滤器截断或探测次数无效。</exception>
    internal static KvBloomFilter Parse(byte[] filter, int length)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, filter.Length);
        if (length < 9)
            throw new InvalidDataException("KV-LSM bloom filter is truncated.");
        int probes = filter[length - 1];
        if (probes is < 1 or > MaxProbes)
            throw new InvalidDataException("KV-LSM bloom filter probe count is invalid.");
        return new KvBloomFilter(filter, (long)(length - 1) * 8, probes);
    }

    /// <summary>计算 user key 的过滤器哈希。</summary>
    internal static ulong Hash(ReadOnlySpan<byte> userKey) => XxHash64.HashToUInt64(userKey);

    /// <summary>返回 key 可能存在；false 表示一定不存在。</summary>
    internal bool MayContain(ReadOnlySpan<byte> userKey)
    {
        ulong hash = Hash(userKey);
        uint h1 = (uint)hash;
        uint h2 = (uint)(hash >> 32);
        for (int i = 0; i < _probes; i++)
        {
            long bit = (long)((h1 + ((ulong)(uint)i * h2)) % (ulong)_bitCount);
            if ((_filter[bit >> 3] & (1 << (int)(bit & 7))) == 0)
                return false;
        }
        return true;
    }
}
