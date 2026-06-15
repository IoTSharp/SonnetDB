namespace SonnetDB.Parity.Adapters;

/// <summary>
/// 分析支柱的语义操作集合。场景描述固定数据集与分析意图；
/// 适配器负责翻译成 SonnetDB SQL 或 ClickHouse SQL。
/// </summary>
public interface IAnalyticalOps
{
    /// <summary>写入分析样本，并确保同名数据集的旧数据不影响本次 run。</summary>
    /// <param name="rows">待写入的分析行。</param>
    /// <param name="ct">取消令牌。</param>
    Task IngestAsync(IReadOnlyList<AnalyticalRow> rows, CancellationToken ct);

    /// <summary>按固定时间窗口计算平均值。</summary>
    /// <param name="dataset">数据集 / measurement / table 名称。</param>
    /// <param name="window">窗口宽度。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>bucket 起始毫秒 + 平均值。</returns>
    Task<RelationalSqlResult> GroupByTimeAverageAsync(string dataset, TimeSpan window, CancellationToken ct);

    /// <summary>计算 7 点移动平均窗口样本。</summary>
    /// <param name="dataset">数据集 / measurement / table 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>time + moving average。</returns>
    Task<RelationalSqlResult> WindowAverage7DayAsync(string dataset, CancellationToken ct);

    /// <summary>按设备聚合并返回 Top-N。</summary>
    /// <param name="dataset">数据集 / measurement / table 名称。</param>
    /// <param name="topN">返回设备数量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>device + total。</returns>
    Task<RelationalSqlResult> TopNPerDeviceAsync(string dataset, int topN, CancellationToken ct);

    /// <summary>返回列式压缩率指标。性能指标只进入报告，不作为红绿门槛。</summary>
    /// <param name="dataset">数据集 / measurement / table 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>单行压缩率。</returns>
    Task<RelationalSqlResult> CompressionRatioAsync(string dataset, CancellationToken ct);

    /// <summary>计算 p50 / p95 / p99 分位数。</summary>
    /// <param name="dataset">数据集 / measurement / table 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>单行 p50 / p95 / p99。</returns>
    Task<RelationalSqlResult> PercentilesAsync(string dataset, CancellationToken ct);
}

/// <summary>
/// 不支持分析能力的空操作对象。
/// </summary>
public sealed class UnsupportedAnalyticalOps : IAnalyticalOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedAnalyticalOps Instance { get; } = new();

    private UnsupportedAnalyticalOps() { }

    /// <inheritdoc />
    public Task IngestAsync(IReadOnlyList<AnalyticalRow> rows, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<RelationalSqlResult> GroupByTimeAverageAsync(string dataset, TimeSpan window, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> WindowAverage7DayAsync(string dataset, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> TopNPerDeviceAsync(string dataset, int topN, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> CompressionRatioAsync(string dataset, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> PercentilesAsync(string dataset, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持分析操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持分析操作。");
}

/// <summary>
/// Parity 分析场景使用的规范化行。
/// </summary>
/// <param name="Dataset">数据集 / measurement / table 名称。</param>
/// <param name="TimestampMs">Unix 毫秒时间戳。</param>
/// <param name="Device">设备标签。</param>
/// <param name="Region">区域标签。</param>
/// <param name="Value">数值字段。</param>
public sealed record AnalyticalRow(string Dataset, long TimestampMs, string Device, string Region, double Value);
