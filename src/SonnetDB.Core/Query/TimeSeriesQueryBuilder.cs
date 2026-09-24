using System.Collections.Generic;
using System.Threading;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Engine.Retention;
using SonnetDB.Exceptions;
using SonnetDB.Model;
using SonnetDB.Storage.Format;

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
    private readonly MeasurementCatalog? _measurements;
    private readonly RetentionPolicy? _retention;
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
        string fieldName,
        MeasurementCatalog? measurements = null,
        RetentionPolicy? retention = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(fieldName);
        if (fieldName.Length == 0)
            throw new ArgumentException("字段名称不能为空。", nameof(fieldName));

        _engine = engine;
        _catalog = catalog;
        _measurements = measurements;
        _retention = retention;
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

    /// <summary>
    /// 只读检查真实 schema、目录基数、retention 配置和目标范围内的有限原始点样本。
    /// 使用现有查询引擎的合并、墓碑和快照语义；不执行聚合或补桶，不修改数据。
    /// 源工作量超过预算时明确返回未检查，空样本或截断样本不能证明字段不存在或全量健康。
    /// </summary>
    /// <param name="options">检查预算与告警阈值；省略时使用有限默认值。</param>
    /// <param name="cancellationToken">在访问数据前和有界工作单元之间检查的取消令牌。</param>
    /// <returns>包含真实证据计数及检查范围的报告。</returns>
    public TimeSeriesPreflightReport Preflight(
        TimeSeriesPreflightOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new TimeSeriesPreflightOptions();
        options.Validate();
        var issues = new List<TimeSeriesQueryDiagnostic>();
        var report = new TimeSeriesPreflightReport { Query = Diagnose() };
        var entry = _catalog?.TryGet(_seriesId);
        MeasurementColumn? field = null;
        if (_catalog is null || _measurements is null || _retention is null)
        {
            AddIssue("TSP001", "缺少数据库上下文，schema、基数和 retention 未检查。", "通过 database.ForSeries 创建构建器以读取实际数据库配置。");
        }
        else if (entry is not null)
        {
            var schema = _measurements.TryGet(entry.Measurement);
            field = schema?.TryGetColumn(_fieldName);
            report = report with
            {
                Measurement = entry.Measurement,
                HasSchema = schema is not null,
                DeclaredFieldType = field?.Role == MeasurementColumnRole.Field ? field.DataType : null,
            };
            if (schema is null)
                AddIssue("TSP002", "Measurement 目录没有 schema，样本类型不能替代完整 schema。", "检查 measurement schema 的创建或写入推导状态。");
            else if (field?.Role != MeasurementColumnRole.Field)
                AddIssue("TSP003", "目标名称不是 schema 中的 Field。", "选择已声明的 Field，Tag 不能作为原始字段查询。", TimeSeriesQueryDiagnosticSeverity.Error);
            else if (RequiresNumericValue() && !IsNumeric(field.DataType))
                AddIssue("TSP004", "当前聚合要求数值字段，schema 声明的类型不兼容。", "使用 Count 或原始点读取，或选择数值字段。", TimeSeriesQueryDiagnosticSeverity.Error);

            var cardinality = _catalog.ReadCardinality(entry.Measurement, options.MaxTagKeys, cancellationToken);
            report = report with
            {
                MeasurementSeriesCount = cardinality.SeriesCount,
                Tags = cardinality.Tags,
                TagsTruncated = cardinality.Truncated,
            };
            if (cardinality.SeriesCount >= options.SeriesWarningThreshold)
                AddIssue("TSP005", "Measurement 已登记序列数达到调用方告警阈值。", "检查 tag 是否含请求 ID、时间戳等高基数字段；阈值不代表容量验收结果。", TimeSeriesQueryDiagnosticSeverity.Warning);
            if (cardinality.Tags.Any(tag => tag.DistinctValues >= options.TagValueWarningThreshold))
                AddIssue("TSP006", "已检查 tag 的不同值数量达到调用方告警阈值。", "结合 Tags 的真实计数评估 tag/field 建模。", TimeSeriesQueryDiagnosticSeverity.Warning);
            if (cardinality.Truncated)
                AddIssue("TSP007", "Tag 数超过 MaxTagKeys，部分 tag 基数未检查。", "在允许范围内提高 MaxTagKeys 或单独检查建模。");
        }

        if (_retention is not null)
        {
            report = report with { RetentionEnabled = _retention.Enabled };
            if (_retention.Enabled)
            {
                long ttl = _retention.TtlInTimestampUnits ?? (long)_retention.Ttl.TotalMilliseconds;
                cancellationToken.ThrowIfCancellationRequested();
                long now = _retention.NowFn();
                cancellationToken.ThrowIfCancellationRequested();
                report = report with { RetentionNow = now };
                if (ttl <= 0 || now < long.MinValue + Math.Max(ttl, 0))
                    AddIssue("TSP008", "实际 retention TTL 无效或截止时间计算溢出，无法检查过期范围。", "修正实际 TsdbOptions.Retention 的 TTL 与时间戳单位。", TimeSeriesQueryDiagnosticSeverity.Warning);
                else
                {
                    long cutoff = now - ttl;
                    report = report with { RetentionTtl = ttl, RetentionCutoff = cutoff };
                    if (_range.FromInclusive < cutoff)
                        AddIssue("TSP009", "查询范围包含实际 retention 截止时间之前的数据。", "过期数据可能尚未清理或已经不可见；不要把空结果解释成从未写入。", TimeSeriesQueryDiagnosticSeverity.Warning);
                }
            }
        }

        if (_catalog is not null && entry is null)
            return report with { Issues = issues.AsReadOnly() };

        int sampled = 0, nonFinite = 0, mismatches = 0, expired = 0;
        var observed = new HashSet<FieldType>();
        bool complete = false, checkedData = false;
        try
        {
            // 多拉取一个点仅用于确定是否截断，不纳入样本诊断计数。
            var query = new PointQuery(_seriesId, _fieldName, _range, options.MaxSamplePoints + 1) { Direction = _direction };
            using var points = _engine.ExecuteDiagnosticSample(query, new TimeSeriesReadBudget(options, cancellationToken)).GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!points.MoveNext())
                {
                    complete = true;
                    break;
                }
                if (sampled == options.MaxSamplePoints)
                    break;
                var point = points.Current;
                sampled++;
                observed.Add(point.Value.Type);
                if (point.Value.Type == FieldType.Float64 && !double.IsFinite(point.Value.AsDouble()))
                    nonFinite++;
                if (field?.Role == MeasurementColumnRole.Field && field.DataType != point.Value.Type
                    && !(field.DataType == FieldType.Float64 && point.Value.Type == FieldType.Int64))
                    mismatches++;
                if (report.RetentionCutoff is long cutoff && point.Timestamp < cutoff)
                    expired++;
            }
            checkedData = true;
        }
        catch (TimeSeriesPreflightBudgetException exception)
        {
            AddIssue("TSP010", exception.Message + " 数据质量未检查。", "缩小范围，或在硬上限内增加预检预算；使用专门离线验收检查大数据集。");
        }

        if (checkedData && sampled == 0)
            AddIssue("TSP011", "该快照的目标范围没有可见点，未获得字段类型或质量样本。", "检查范围、字段名、写入与墓碑；空结果不能证明字段不存在。");
        if (checkedData && !complete)
            AddIssue("TSP012", "达到 MaxSamplePoints，结论只覆盖按查询方向取得的前缀样本。", "其他点未经质量检查，不能据此报告全量健康。");
        if (nonFinite > 0)
            AddIssue("TSP013", "样本中存在 NaN 或无穷值。", "检查上游传感器数值与写入校验；预检不会修改坏点。", TimeSeriesQueryDiagnosticSeverity.Warning);
        if (mismatches > 0)
            AddIssue("TSP014", "样本字段类型与目录 schema 不兼容。", "核对历史数据与 schema 变更。", TimeSeriesQueryDiagnosticSeverity.Error);
        if (RequiresNumericValue() && observed.Any(static type => !IsNumeric(type))
            && !issues.Any(static issue => issue.Code == "TSP004"))
            AddIssue("TSP004", "实际样本类型不支持当前数值聚合。", "使用 Count 或原始点读取，或选择数值字段。", TimeSeriesQueryDiagnosticSeverity.Error);
        if (expired > 0)
            AddIssue("TSP015", "样本中存在超过实际 TTL 但仍可见的点。", "结合 retention worker 状态检查异步清理；预检不会执行清理。", TimeSeriesQueryDiagnosticSeverity.Warning);
        cancellationToken.ThrowIfCancellationRequested();
        return report with
        {
            DataChecked = checkedData,
            SampleComplete = complete,
            SampledPoints = sampled,
            ObservedFieldTypes = observed.Order().ToArray(),
            NonFinitePoints = nonFinite,
            SchemaMismatchPoints = mismatches,
            ExpiredPoints = expired,
            Issues = issues.AsReadOnly(),
        };

        void AddIssue(string code, string message, string hint, TimeSeriesQueryDiagnosticSeverity severity = TimeSeriesQueryDiagnosticSeverity.Info)
            => issues.Add(new TimeSeriesQueryDiagnostic(code, severity, message, hint));
    }

    private bool RequiresNumericValue() => _aggregator.HasValue && _aggregator != Aggregator.Count;

    private static bool IsNumeric(FieldType type) => type is FieldType.Float64 or FieldType.Int64 or FieldType.Boolean;

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

    /// <summary>已观察到的建模或数据质量风险，不阻止合法查询。</summary>
    Warning,
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
        return new TimeSeriesQueryBuilder(database.Query, database.Catalog, seriesId, fieldName,
            database.Measurements, database.TimeSeriesRetentionPolicy);
    }
}
