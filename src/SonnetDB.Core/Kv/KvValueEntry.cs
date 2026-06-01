namespace SonnetDB.Kv;

internal sealed class KvValueEntry
{
    public KvValueEntry(byte[] value, long version)
    {
        Value = value;
        Version = version;
    }

    public byte[] Value { get; }

    public long Version { get; }
}
