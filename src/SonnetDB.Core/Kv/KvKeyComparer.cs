namespace SonnetDB.Kv;

internal sealed class KvKeyComparer :
    IEqualityComparer<byte[]>,
    IComparer<byte[]>,
    IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static KvKeyComparer Instance { get; } = new();

    private KvKeyComparer()
    {
    }

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return GetHashCode(obj.AsSpan());
    }

    /// <summary>比较调用方的临时字节视图与字典持有的稳定键，不创建中间数组。</summary>
    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return alternate.SequenceEqual(other);
    }

    /// <summary>为临时字节视图计算与 byte[] 键完全一致的 FNV-1a 哈希。</summary>
    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        unchecked
        {
            int hash = (int)2166136261;
            for (int i = 0; i < alternate.Length; i++)
                hash = (hash ^ alternate[i]) * 16777619;
            return hash;
        }
    }

    /// <summary>需要把 alternate key 插入字典时创建独立数组；只读查找不会调用本方法。</summary>
    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

    public int Compare(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        int min = Math.Min(x.Length, y.Length);
        for (int i = 0; i < min; i++)
        {
            int c = x[i].CompareTo(y[i]);
            if (c != 0)
                return c;
        }

        return x.Length.CompareTo(y.Length);
    }
}
