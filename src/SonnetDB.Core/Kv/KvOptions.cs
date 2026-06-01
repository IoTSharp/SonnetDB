namespace SonnetDB.Kv;

/// <summary>
/// 内置 KV Keyspace 的存储选项。
/// </summary>
public sealed record KvOptions
{
    /// <summary>KV WAL 写缓冲区大小（字节），默认 64 KB。</summary>
    public int WalBufferSize { get; init; } = 64 * 1024;

    /// <summary>
    /// 是否在每次 <c>Put</c> / <c>Delete</c> 后强制 fsync KV WAL。
    /// 默认开启，优先保证小对象和内部元数据写入的崩溃安全性。
    /// </summary>
    public bool SyncWalOnEveryWrite { get; init; } = true;

    /// <summary>单个 key 的最大字节数，默认 64 KB。</summary>
    public int MaxKeyBytes { get; init; } = 64 * 1024;

    /// <summary>单个 value 的最大字节数，默认 16 MB。</summary>
    public int MaxValueBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>单次前缀扫描的默认最大返回行数。</summary>
    public int DefaultScanLimit { get; init; } = 1024;

    /// <summary>默认 KV 选项实例。</summary>
    public static KvOptions Default { get; } = new();
}
