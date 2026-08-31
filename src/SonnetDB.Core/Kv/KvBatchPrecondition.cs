namespace SonnetDB.Kv;

/// <summary>KV 原子批次在 WAL 追加前、写锁内校验的条件类别。</summary>
internal enum KvBatchPreconditionKind
{
    KeyVersionEquals,
    KeyExists,
    PrefixEmpty,
}

/// <summary>KV 原子批次的内部乐观条件。</summary>
internal sealed record KvBatchPrecondition(
    KvBatchPreconditionKind Kind,
    byte[] Operand,
    long ExpectedVersion)
{
    /// <summary>要求 key 不存在（版本 0）或当前版本等于指定值。</summary>
    internal static KvBatchPrecondition KeyVersion(byte[] key, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedVersion);
        return new KvBatchPrecondition(
            KvBatchPreconditionKind.KeyVersionEquals,
            key,
            expectedVersion);
    }

    /// <summary>要求 key 当前存在且未过期。</summary>
    internal static KvBatchPrecondition Exists(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new KvBatchPrecondition(KvBatchPreconditionKind.KeyExists, key, 0);
    }

    /// <summary>要求指定前缀下没有可见 key。</summary>
    internal static KvBatchPrecondition PrefixEmpty(byte[] prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return new KvBatchPrecondition(KvBatchPreconditionKind.PrefixEmpty, prefix, 0);
    }
}

/// <summary>条件批次的内部提交结果。</summary>
internal readonly record struct KvConditionalBatchResult(
    bool Applied,
    long Sequence,
    int FailedPreconditionIndex)
{
    /// <summary>创建成功结果。</summary>
    internal static KvConditionalBatchResult Success(long sequence)
        => new(true, sequence, -1);

    /// <summary>创建条件不成立结果。</summary>
    internal static KvConditionalBatchResult Conflict(long sequence, int failedPreconditionIndex)
        => new(false, sequence, failedPreconditionIndex);
}
