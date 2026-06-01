namespace SonnetDB.Kv;

internal sealed class KvKeyComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
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

        unchecked
        {
            int hash = (int)2166136261;
            for (int i = 0; i < obj.Length; i++)
                hash = (hash ^ obj[i]) * 16777619;
            return hash;
        }
    }

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
