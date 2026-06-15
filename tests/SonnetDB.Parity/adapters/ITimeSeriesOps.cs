namespace SonnetDB.Parity.Adapters;

/// <summary>
/// 时序支柱的语义操作集合。场景只描述固定数据集和算法意图；
/// 适配器负责翻译为 SonnetDB SQL、InfluxDB Flux 或 PromQL。
/// </summary>
public interface ITimeSeriesOps
{
    /// <summary>写入 <paramref name="points"/>，并确保同名 measurement / metric 的旧数据不影响本次 run。</summary>
    /// <param name="points">待写入的数据点。</param>
    /// <param name="ct">取消令牌。</param>
    Task IngestAsync(IReadOnlyList<TsdbPoint> points, CancellationToken ct);

    /// <summary>读取总点数，用于 ingest 场景确认落库完整性。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>规范化结果集。</returns>
    Task<RelationalSqlResult> CountAsync(string measurement, CancellationToken ct);

    /// <summary>按固定时间窗口计算平均值。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="window">窗口宽度。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>bucket 起始毫秒 + 平均值。</returns>
    Task<RelationalSqlResult> GroupByTimeAverageAsync(string measurement, TimeSpan window, CancellationToken ct);

    /// <summary>计算相邻点 derivative。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>time + derivative 值。</returns>
    Task<RelationalSqlResult> DerivativeAsync(string measurement, CancellationToken ct);

    /// <summary>计算 rate 与 irate 的一致性样本。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>time + rate + irate。</returns>
    Task<RelationalSqlResult> RateIrateAsync(string measurement, CancellationToken ct);

    /// <summary>计算 Holt-Winters 预测召回样本。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="horizon">预测点数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>step + forecast。</returns>
    Task<RelationalSqlResult> HoltWintersForecastAsync(string measurement, int horizon, CancellationToken ct);

    /// <summary>计算 p95 分位数。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>单行 p95 值。</returns>
    Task<RelationalSqlResult> PercentileP95Async(string measurement, CancellationToken ct);

    /// <summary>计算唯一 device 数。</summary>
    /// <param name="measurement">measurement / metric 名称。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>单行 distinct count 值。</returns>
    Task<RelationalSqlResult> DistinctDeviceCountAsync(string measurement, CancellationToken ct);
}

/// <summary>
/// 不支持时序能力的空操作对象。
/// </summary>
public sealed class UnsupportedTimeSeriesOps : ITimeSeriesOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedTimeSeriesOps Instance { get; } = new();

    private UnsupportedTimeSeriesOps() { }

    /// <inheritdoc />
    public Task IngestAsync(IReadOnlyList<TsdbPoint> points, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<RelationalSqlResult> CountAsync(string measurement, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> GroupByTimeAverageAsync(string measurement, TimeSpan window, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> DerivativeAsync(string measurement, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> RateIrateAsync(string measurement, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> HoltWintersForecastAsync(string measurement, int horizon, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> PercentileP95Async(string measurement, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    /// <inheritdoc />
    public Task<RelationalSqlResult> DistinctDeviceCountAsync(string measurement, CancellationToken ct) => Unsupported<RelationalSqlResult>();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持时序操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持时序操作。");
}

/// <summary>
/// Parity TSDB 场景使用的规范化数据点。
/// </summary>
/// <param name="Measurement">measurement / metric 名称。</param>
/// <param name="TimestampMs">Unix 毫秒时间戳。</param>
/// <param name="Device">设备标签。</param>
/// <param name="Region">区域标签。</param>
/// <param name="Value">数值字段。</param>
public sealed record TsdbPoint(string Measurement, long TimestampMs, string Device, string Region, double Value);
