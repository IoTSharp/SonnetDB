namespace SonnetDB.Kv;

internal enum KvWalRecordKind : byte
{
    Put = 1,
    Delete = 2,
}

internal sealed class KvWalRecord
{
    public KvWalRecord(KvWalRecordKind kind, long sequence, byte[] key, byte[]? value)
    {
        Kind = kind;
        Sequence = sequence;
        Key = key;
        Value = value;
    }

    public KvWalRecordKind Kind { get; }

    public long Sequence { get; }

    public byte[] Key { get; }

    public byte[]? Value { get; }
}
