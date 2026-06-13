namespace SonnetDB.Kv;

internal enum KvWalRecordKind : byte
{
    Put = 1,
    Delete = 2,
}

internal sealed class KvWalRecord
{
    public KvWalRecord(
        KvWalRecordKind kind,
        long sequence,
        byte[] key,
        byte[]? value,
        DateTimeOffset? expiresAtUtc = null)
    {
        Kind = kind;
        Sequence = sequence;
        Key = key;
        Value = value;
        ExpiresAtUtc = expiresAtUtc;
    }

    public KvWalRecordKind Kind { get; }

    public long Sequence { get; }

    public byte[] Key { get; }

    public byte[]? Value { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }
}
