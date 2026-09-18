using System.Collections.Generic;
using System.Threading;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;

namespace SonnetDB.Query;

/// <summary>
/// 时序查询的类型化构建器。
/// <para>
/// 构建器只负责把调用者的意图转换成现有的 <see cref="PointQuery"/> 与
/// <see cref="AggregateQuery"/>，实际扫描始终由 <see cref="QueryEngine"/> 完成。
/// </para>
/// <para>实例可复用，但不是线程安全的；每次调用配置方法都会修改当前实例。</para>
/// </summary>
public sealed class TimeSeriesQueryBuilder
{
    private readonly QueryEngine _engine;
    private readonly SeriesCatalog? _catalog;
    private readonly ulong _seriesId;
    private readonly string _fieldName;
    private TimeRange _range = TimeRange.All;
    private QueryDirection _direction = QueryDirection.Ascending;
    private int? _limit;
    private Aggregator? _aggregator;
    private long _bucketSizeMs;
    private bool _gapFill;
    private double _gapFillValue;

    /// <summary>
    /// 创建时序查询构建器。
    /// </summary>
    /// <param name="engine">查询执行器。</param>
    /// <param name="seriesId">目标序列标识符。</param>
    /// <param name="fieldName">目标字段名称。</param>
    public TimeSeriesQueryBuilder(QueryEngine engine, ulong seriesId, string fieldName)
        : this(engine, catalog: null, seriesId, fieldName)
    {
    }

    internal TimeSeriesQueryBuilder(
        QueryEngine engine,
        SeriesCatalog? catalog,
        ulong seriesId,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(fieldName);
        if (fieldName.Length == 0)
            throw new ArgumentException("字段名称不能为空。", nameof(fieldName));

        _engine = engine;
        _catalog = catalog;
        _seriesId = seriesId;
        _fieldName = fieldName;
    }

    /// <summary>设置闭区间时间范围。</summary>
    /// <param name="range">时间范围。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Range(TimeRange range)
    {
        _range = range;
        return this;
    }

    /// <summary>设置从指定时间戳开始的时间范围。</summary>
    /// <param name="fromInclusive">起始时间戳（含）。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder From(long fromInclusive)
    {
        _range = TimeRange.From(fromInclusive);
        return this;
    }

    /// <summary>设置截至指定时间戳的时间范围。</summary>
    /// <param name="toInclusive">终止时间戳（含）。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Until(long toInclusive)
    {
        _range = TimeRange.Until(toInclusive);
        return this;
    }

    /// <summary>设置有限的闭区间时间范围。</summary>
    /// <param name="fromInclusive">起始时间戳（含）。</param>
    /// <param name="toInclusive">终止时间戳（含）。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Between(long fromInclusive, long toInclusive)
    {
        _range = new TimeRange(fromInclusive, toInclusive);
        return this;
    }

    /// <summary>按时间戳升序返回点或桶。</summary>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Ascending()
    {
        _direction = QueryDirection.Ascending;
        return this;
    }

    /// <summary>按时间戳降序返回原始点。</summary>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Descending()
    {
        _direction = QueryDirection.Descending;
        return this;
    }

    /// <summary>限制原始点结果数量。</summary>
    /// <param name="limit">最大点数，必须大于零。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Limit(int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        _limit = limit;
        return this;
    }

    /// <summary>配置聚合模式；零桶大小表示整个范围一个桶。</summary>
    /// <param name="aggregator">聚合函数。</param>
    /// <param name="bucketSizeMs">桶宽（毫秒）；零表示全局聚合。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Aggregate(Aggregator aggregator, long bucketSizeMs = 0)
    {
        if (aggregator is Aggregator.None)
            throw new ArgumentOutOfRangeException(nameof(aggregator), "聚合函数不能为 None。 ");
        if (bucketSizeMs < 0)
            throw new ArgumentOutOfRangeException(nameof(bucketSizeMs), "桶宽不能为负数。 ");

        _aggregator = aggregator;
        _bucketSizeMs = bucketSizeMs;
        return this;
    }

    /// <summary>配置固定窗口聚合。窗口结果仍由现有聚合执行路径流式产生。</summary>
    /// <param name="everyMs">窗口宽（毫秒，必须大于零）。</param>
    /// <param name="aggregator">窗口内聚合函数。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder Window(long everyMs, Aggregator aggregator)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(everyMs);
        return Aggregate(aggregator, everyMs);
    }

    /// <summary>
    /// 为窗口聚合补充无数据桶。补桶要求时间范围有明确起止点，以保证结果有界。
    /// </summary>
    /// <param name="value">空桶的聚合值；桶计数始终为零。</param>
    /// <returns>当前构建器。</returns>
    public TimeSeriesQueryBuilder GapFill(double value = 0)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new ArgumentOutOfRangeException(nameof(value), "补桶值必须是有限数。 ");

        _gapFill = true;
        _gapFillValue = value;
        return this;
    }

    /// <summary>构建当前配置对应的原始点查询。</summary>
    /// <returns>现有查询引擎使用的点查询参数。</returns>
    public PointQuery BuildPointQuery()
        => new(_seriesId, _fieldName, _range, _limit) { Direction = _direction };

    /// <summary>构建当前配置对应的聚合查询。</summary>
    /// <returns>现有查询引擎使用的聚合查询参数。</returns>
    /// <exception cref="InvalidOperationException">尚未配置聚合函数时抛出。</exception>
    public AggregateQuery BuildAggregateQuery()
        => new(_seriesId, _fieldName, _range, RequireAggregator(), _bucketSizeMs);

    /// <summary>
    /// 返回当前构建器的静态诊断信息，不读取业务数据。
    /// </summary>
    /// <returns>可用于管理工具展示的查询诊断快照。</returns>
    public TimeSeriesQueryDiagnostics Diagnose()
    {
        var issues = new List<TimeSeriesQueryDiagnostic>();
        bool isAggregate = _aggregator.HasValue;
        if (_catalog is not null && _catalog.TryGet(_seriesId) is null)
        {
            issues.Add(new TimeSeriesQueryDiagnostic(
                "TSQ001",
                TimeSeriesQueryDiagnosticSeverity.Error,
                "目标序列不存在。",
                "先写入或选择有效的 series id。"));
        }

        if (_gapFill && (!isAggregate || _bucketSizeMs <= 0))
        {
            issues.Add(new TimeSeriesQueryDiagnostic(
                "TSQ002",
                TimeSeriesQueryDiagnosticSeverity.Error,
                "GapFill 只能用于固定窗口聚合。",
                "先调用 Window(everyMs, aggregator)，再调用 GapFill。"));
        }

        if (_gapFill && (_range.FromInclusive == long.MinValue || _range.ToInclusive == long.MaxValue))
        {
            issues.Add(new TimeSeriesQueryDiagnostic(
                "TSQ003",
                TimeSeriesQueryDiagnosticSeverity.Error,
                "GapFill 需要有界时间范围。",
                "使用 Between(fromInclusive, toInclusive) 限定补桶数量。"));
        }

        if (!isAggregate && _limit is null)
        {
            issues.Add(new TimeSeriesQueryDiagnostic(
                "TSQ004",
                TimeSeriesQueryDiagnosticSeverity.Info,
                "原始点查询未设置 limit。",
                "大范围浏览建议设置 Limit 以保持客户端内存有界。"));
        }

        return new TimeSeriesQueryDiagnostics(
            _seriesId,
            _fieldName,
            _range,
            _direction,
            _limit,
            _aggregator,
            _bucketSizeMs,
            _gapFill,
            issues);
    }

    /// <summary>流式读取原始点；枚举周期内由现有查询引擎保持一致快照。</summary>
    /// <returns>按配置方向排列的数据点序列。</returns>
    public IEnumerable<DataPoint> ReadPoints()
        => _engine.Execute(BuildPointQuery());

    /// <summary>按取消令牌流式读取原始点。</summary>
    /// <param name="cancellationToken">取消令牌；每次拉取前检查。</param>
    /// <returns>按配置方向排列的数据点序列。</returns>
    public IEnumerable<DataPoint> ReadPoints(CancellationToken cancellationToken)
        => WithCancellation(_engine.Execute(BuildPointQuery()), cancellationToken);

    /// <summary>流式读取聚合桶；不启用补桶时直接透传现有聚合执行器。</summary>
    /// <returns>按桶起点升序排列的聚合结果。</returns>
    /// <exception cref="InvalidOperationException">尚未配置聚合函数时抛出。</exception>
    public IEnumerable<AggregateBucket> ReadAggregates()
    {
        var query = BuildAggregateQuery();
        return _gapFill ? ReadGapFilled(query) : _engine.Execute(query);
    }

    /// <summary>按取消令牌流式读取聚合桶。</summary>
    /// <param name="cancellationToken">取消令牌；每次拉取前检查。</param>
    /// <returns>按桶起点升序排列的聚合结果。</returns>
    public IEnumerable<AggregateBucket> ReadAggregates(CancellationToken cancellationToken)
    {
        var query = BuildAggregateQuery();
        return _gapFill
            ? ReadGapFilled(query, cancellationToken)
            : WithCancellation(_engine.Execute(query), cancellationToken);
    }

    private Aggregator RequireAggregator()
        => _aggregator ?? throw new InvalidOperationException("尚未配置聚合函数，请调用 Aggregate 或 Window。 ");

    private IEnumerable<AggregateBucket> ReadGapFilled(AggregateQuery query)
        => ReadGapFilled(query, CancellationToken.None);

    private IEnumerable<AggregateBucket> ReadGapFilled(
        AggregateQuery query,
        CancellationToken cancellationToken)
    {
        if (query.BucketSizeMs <= 0)
            throw new InvalidOperationException("GapFill 只能用于固定窗口聚合。 ");
        if (_range.FromInclusive == long.MinValue || _range.ToInclusive == long.MaxValue)
            throw new InvalidOperationException("GapFill 需要有界时间范围。 ");

        using var observed = _engine.Execute(query).GetEnumerator();
        bool hasObserved = observed.MoveNext();
        long bucket = TimeBucket.Floor(_range.FromInclusive, query.BucketSizeMs);
        while (bucket <= _range.ToInclusive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hasObserved && observed.Current.BucketStart == bucket)
            {
                yield return observed.Current;
                hasObserved = observed.MoveNext();
            }
            else
            {
                yield return new AggregateBucket(
                    bucket,
                    AddBucketWidth(bucket, query.BucketSizeMs),
                    0,
                    _gapFillValue);
            }

            long next = AddBucketWidth(bucket, query.BucketSizeMs);
            if (next <= bucket)
                break;
            bucket = next;
        }
    }

    private static IEnumerable<T> WithCancellation<T>(
        IEnumerable<T> source,
        CancellationToken cancellationToken)
    {
        using var enumerator = source.GetEnumerator();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enumerator.MoveNext())
                yield break;
            yield return enumerator.Current;
        }
    }

    private static long AddBucketWidth(long bucketStart, long bucketSizeMs)
    {
        if (bucketSizeMs > long.MaxValue - bucketStart)
            return long.MaxValue;
        return bucketStart + bucketSizeMs;
    }
}

/// <summary>时序查询诊断严重级别。</summary>
public enum TimeSeriesQueryDiagnosticSeverity : byte
{
    /// <summary>提示信息。</summary>
    Info,

    /// <summary>会阻止当前配置执行。</summary>
    Error,
}

/// <summary>一条时序查询诊断信息。</summary>
/// <param name="Code">稳定诊断代码。</param>
/// <param name="Severity">严重级别。</param>
/// <param name="Message">面向用户的说明。</param>
/// <param name="Hint">可执行的修复建议。</param>
public sealed record TimeSeriesQueryDiagnostic(
    string Code,
    TimeSeriesQueryDiagnosticSeverity Severity,
    string Message,
    string Hint);

/// <summary>时序查询构建器的静态诊断快照。</summary>
/// <param name="SeriesId">目标序列标识符。</param>
/// <param name="FieldName">目标字段名称。</param>
/// <param name="Range">查询时间范围。</param>
/// <param name="Direction">点查询方向。</param>
/// <param name="Limit">原始点数量上限。</param>
/// <param name="Aggregator">聚合函数；原始点查询为 null。</param>
/// <param name="BucketSizeMs">聚合桶宽。</param>
/// <param name="GapFill">是否补充空桶。</param>
/// <param name="Issues">诊断信息列表。</param>
public sealed record TimeSeriesQueryDiagnostics(
    ulong SeriesId,
    string FieldName,
    TimeRange Range,
    QueryDirection Direction,
    int? Limit,
    Aggregator? Aggregator,
    long BucketSizeMs,
    bool GapFill,
    IReadOnlyList<TimeSeriesQueryDiagnostic> Issues)
{
    /// <summary>是否存在错误级别诊断。</summary>
    public bool HasErrors
        => Issues.Any(static issue => issue.Severity is TimeSeriesQueryDiagnosticSeverity.Error);
}

/// <summary>把现有查询引擎接入时序类型化构建器的扩展。</summary>
public static class TimeSeriesQueryExtensions
{
    /// <summary>为指定序列和字段创建时序查询构建器。</summary>
    /// <param name="engine">查询执行器。</param>
    /// <param name="seriesId">目标序列标识符。</param>
    /// <param name="fieldName">目标字段名称。</param>
    /// <returns>新的时序查询构建器。</returns>
    public static TimeSeriesQueryBuilder ForSeries(
        this QueryEngine engine,
        ulong seriesId,
        string fieldName)
        => new(engine, seriesId, fieldName);

    /// <summary>为指定数据库中的序列创建时序查询构建器，并启用序列存在性诊断。</summary>
    /// <param name="database">嵌入式数据库实例。</param>
    /// <param name="seriesId">目标序列标识符。</param>
    /// <param name="fieldName">目标字段名称。</param>
    /// <returns>新的时序查询构建器。</returns>
    public static TimeSeriesQueryBuilder ForSeries(
        this Tsdb database,
        ulong seriesId,
        string fieldName)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new TimeSeriesQueryBuilder(database.Query, database.Catalog, seriesId, fieldName);
    }
}
