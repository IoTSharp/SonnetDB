namespace SonnetDB.Kv;

internal enum KvWalRecordKind : byte
{
    Put = 1,
    Delete = 2,
    ClearGeneration = 3,
    DeleteBatch = 4,
    DeleteBatchCommit = 5,
    MutationBatch = 6,

    /// <summary>含范围删除的原子批次（仅 KV-LSM 引擎写入）。</summary>
    MutationBatchWithRanges = 7,
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
