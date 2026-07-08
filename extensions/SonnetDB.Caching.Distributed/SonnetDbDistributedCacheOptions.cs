namespace SonnetDB.Caching.Distributed;

/// <summary>
/// SonnetDB IDistributedCache Provider 选项。
/// </summary>
public sealed class SonnetDbDistributedCacheOptions
{
    /// <summary>SonnetDB.Data 连接字符串；可指向嵌入式目录或 SonnetDB Server 远程地址。</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>KV keyspace 名称。</summary>
    public string Keyspace { get; set; } = "cache";

    /// <summary>逻辑命名空间名称。</summary>
    public string Namespace { get; set; } = "default";

    /// <summary>后台过期清理间隔；小于等于零表示不启动清理循环。</summary>
    public TimeSpan ExpirationScanInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>每轮最多清理的过期 key 数量。</summary>
    public int ExpirationScanBatchSize { get; set; } = 1024;
}
