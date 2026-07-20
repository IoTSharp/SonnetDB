namespace SonnetDB.Kv;

internal sealed record KvBatchMutation(
    byte[] Key,
    byte[]? Value,
    DateTimeOffset? ExpiresAtUtc = null)
{
    public static KvBatchMutation Put(byte[] key, byte[] value, DateTimeOffset? expiresAtUtc = null)
        => new(key, value, expiresAtUtc);

    public static KvBatchMutation Delete(byte[] key)
        => new(key, Value: null);
}
