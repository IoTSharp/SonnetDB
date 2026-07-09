namespace SonnetDB.Query;

/// <summary>
/// 单桶的多聚合结果：一次块扫描同时得到 count/sum/min/max，供 <c>SELECT count(v), sum(v), min(v), max(v)</c>
/// 这类共享字段的多聚合查询免去逐聚合各扫一遍。<see cref="Project"/> 把本桶投影为等价的单聚合
/// <see cref="AggregateBucket"/>，其值与逐聚合单独执行时按位一致。
/// </summary>
/// <param name="BucketStart">桶起点时间戳（毫秒，<see cref="TimeBucket.Floor"/> 对齐后的值）。</param>
/// <param name="BucketEndExclusive">桶终点时间戳（毫秒，不含）。</param>
/// <param name="Count">桶内数据点数量。</param>
/// <param name="Sum">桶内数值之和。</param>
/// <param name="Min">桶内最小值（空桶时为 <see cref="double.PositiveInfinity"/>）。</param>
/// <param name="Max">桶内最大值（空桶时为 <see cref="double.NegativeInfinity"/>）。</param>
public readonly record struct MultiAggregateBucket(
    long BucketStart,
    long BucketEndExclusive,
    long Count,
    double Sum,
    double Min,
    double Max)
{
    /// <summary>
    /// 把本多聚合桶投影为指定聚合函数的单聚合值。仅支持一次扫描即可得出的数值聚合
    /// （<see cref="Aggregator.Count"/> / <see cref="Aggregator.Sum"/> / <see cref="Aggregator.Min"/>
    /// / <see cref="Aggregator.Max"/> / <see cref="Aggregator.Avg"/>）；
    /// <see cref="Aggregator.First"/> / <see cref="Aggregator.Last"/> 依赖有序首末值，不在此路径。
    /// </summary>
    public double ProjectValue(Aggregator aggregator) => aggregator switch
    {
        Aggregator.Count => (double)Count,
        Aggregator.Sum => Sum,
        Aggregator.Min => Min,
        Aggregator.Max => Max,
        Aggregator.Avg => Count == 0 ? 0.0 : Sum / Count,
        _ => throw new ArgumentOutOfRangeException(
            nameof(aggregator), aggregator,
            "多聚合单次扫描仅支持 count/sum/min/max/avg 投影。"),
    };

    /// <summary>
    /// 投影为等价的 <see cref="AggregateBucket"/>；其 <see cref="AggregateBucket.Value"/> 与该聚合
    /// 单独经 <c>Execute(AggregateQuery)</c> 得到的值按位一致，可直接并入既有桶合并逻辑。
    /// </summary>
    public AggregateBucket Project(Aggregator aggregator)
        => new(BucketStart, BucketEndExclusive, Count, ProjectValue(aggregator));
}
