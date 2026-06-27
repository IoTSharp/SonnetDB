namespace SonnetDB.Kv;

internal sealed class KvValueEntry
{
    public KvValueEntry(byte[] value, long version, DateTimeOffset? expiresAtUtc = null, bool isDeleted = false)
    {
        Value = value;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
        IsDeleted = isDeleted;
    }

    public byte[] Value { get; }

    public long Version { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool IsDeleted { get; }

    public bool IsExpired(DateTimeOffset utcNow) =>
        !IsDeleted && ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= utcNow;

    public static KvValueEntry Deleted(long version) => new([], version, isDeleted: true);
}
