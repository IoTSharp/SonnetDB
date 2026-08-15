namespace SonnetDB.Kv;

/// <summary>
/// KV 稳定读快照的双向范围游标选项。
/// </summary>
public sealed record KvRangeScanOptions
{
    /// <summary>默认单页 key/value payload 字节预算（32 MiB）。</summary>
    public const int DefaultMaxPageBytes = 32 * 1024 * 1024;

    /// <summary>必须匹配的 key 前缀；为空时不限制前缀。</summary>
    public ReadOnlyMemory<byte> Prefix { get; init; }

    /// <summary>包含的起始 key；为空时不设置下界。</summary>
    public ReadOnlyMemory<byte> StartInclusive { get; init; }

    /// <summary>不包含的结束 key；为空时不设置上界。</summary>
    public ReadOnlyMemory<byte> EndExclusive { get; init; }

    /// <summary>
    /// 初始 continuation key；为空时从扫描方向起点开始，否则严格从该 key 之后继续。
    /// 升序读取更大的 key，降序读取更小的 key。
    /// </summary>
    public ReadOnlyMemory<byte> AfterKey { get; init; }

    /// <summary>是否按 key 字节序降序读取；默认 false 保持升序合同。</summary>
    public bool Descending { get; init; }

    /// <summary>每页最多返回的条目数，默认 256，必须为正数。</summary>
    public int PageSize { get; init; } = 256;

    /// <summary>
    /// 每页独立复制的 key/value payload 最大字节数，默认 32 MiB，必须为正数。
    /// 单条记录本身超过该预算时读取会失败，调用方必须显式提高预算。
    /// </summary>
    public int MaxPageBytes { get; init; } = DefaultMaxPageBytes;
}
