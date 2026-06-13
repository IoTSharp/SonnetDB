namespace SonnetDB.Kv;

internal sealed class KvValueEntry
{
    public KvValueEntry(byte[] value, long version, DateTimeOffset? expiresAtUtc = null)
    {
        Value = value;
        Version = version;
        ExpiresAtUtc = expiresAtUtc;
    }

    public byte[] Value { get; }

    public long Version { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public bool IsExpired(DateTimeOffset utcNow) =>
        ExpiresAtUtc.HasValue && ExpiresAtUtc.Value <= utcNow;
}
