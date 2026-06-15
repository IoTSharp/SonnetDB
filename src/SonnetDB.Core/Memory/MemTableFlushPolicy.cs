namespace SonnetDB.Memory;

/// <summary>
/// MemTable 触发 Flush 的阈值策略，基于字节数、数据点数与时间间隔三种条件。
/// </summary>
public sealed record MemTableFlushPolicy
{
    /// <summary>触发 Flush 的字节数上限，默认 16 MB。</summary>
    public long MaxBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>
    /// MemTable 硬上限字节数；达到该值时写入路径会同步等待 Flush 完成以施加背压。
    /// 为 null 时使用 <see cref="MaxBytes"/> 的 4 倍；小于等于 0 表示禁用硬上限。
    /// </summary>
    public long? HardCapBytes { get; init; }

    /// <summary>触发 Flush 的数据点数上限，默认 100 万点。</summary>
    public long MaxPoints { get; init; } = 1_000_000;

    /// <summary>触发 Flush 的最大存活时间，默认 5 分钟。</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>默认策略实例（16 MB / 100 万点 / 5 分钟）。</summary>
    public static MemTableFlushPolicy Default { get; } = new();

    /// <summary>
    /// 解析实际硬上限字节数；默认按 <see cref="MaxBytes"/> 的 4 倍计算并做溢出保护。
    /// </summary>
    /// <returns>实际硬上限字节数；返回小于等于 0 表示禁用。</returns>
    public long ResolveHardCapBytes()
    {
        if (HardCapBytes.HasValue)
            return HardCapBytes.Value;

        if (MaxBytes <= 0)
            return 0;

        return MaxBytes > long.MaxValue / 4
            ? long.MaxValue
            : MaxBytes * 4;
    }
}
