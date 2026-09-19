using System.Text.Json.Serialization;
using SonnetDB.Exceptions;
using SonnetDB.Storage.Format;

namespace SonnetDB.Query;

/// <summary>时序预检的工作预算和建模告警阈值；达到预算时停止数据检查。</summary>
public sealed record TimeSeriesPreflightOptions
{
    /// <summary>最多检查的可见原始点数，范围 1～4096，默认 256。与查询 Limit 独立。</summary>
    public int MaxSamplePoints { get; init; } = 256;

    /// <summary>排序、合并和解码前允许涉及的源数据点上限，范围 1～65536，默认 8192。</summary>
    public int MaxSourcePoints { get; init; } = 8192;

    /// <summary>段、内存表、块索引项或墓碑的各自数量上限，范围 1～4096，默认 256。</summary>
    public int MaxSources { get; init; } = 256;

    /// <summary>候选段块字节数与内存桶估算字节数之和上限，范围 1～67108864，默认 8388608。</summary>
    public long MaxSourceBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>最多返回的 measurement tag 基数项，范围 1～256，默认 32。</summary>
    public int MaxTagKeys { get; init; } = 32;

    /// <summary>measurement 序列数达到该值时告警；默认 100000，不代表引擎容量上限。</summary>
    public int SeriesWarningThreshold { get; init; } = 100_000;

    /// <summary>任一已检查 tag 的不同值数量达到该值时告警；默认 10000。</summary>
    public int TagValueWarningThreshold { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSamplePoints, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSamplePoints, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourcePoints, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSourcePoints, 65536);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSources, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSources, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSourceBytes, 64L * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTagKeys, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTagKeys, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SeriesWarningThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TagValueWarningThreshold);
    }
}

/// <summary>从真实 tag 倒排索引读取的不同值计数。</summary>
/// <param name="TagKey">Tag 名称。</param>
/// <param name="DistinctValues">已登记的不同值数量；不等同于保留期内活跃值数量。</param>
public sealed record TimeSeriesTagCardinality(string TagKey, int DistinctValues);

/// <summary>只读时序预检结果；目录计数与数据快照分别读取，不承诺跨组件事务一致性。</summary>
public sealed record TimeSeriesPreflightReport
{
    /// <summary>查询配置和既有静态诊断。</summary>
    public required TimeSeriesQueryDiagnostics Query { get; init; }

    /// <summary>从真实序列目录解析的 measurement；缺少数据库上下文或序列时为 null。</summary>
    public string? Measurement { get; init; }

    /// <summary>目录中是否存在 measurement schema（可来自显式创建或写入推导）；无数据库上下文时为 null。</summary>
    public bool? HasSchema { get; init; }

    /// <summary>Schema 声明的目标字段类型；未声明或名称不是 Field 时为 null。</summary>
    public FieldType? DeclaredFieldType { get; init; }

    /// <summary>真实目录登记的 measurement 序列数；无法读取时为 null。</summary>
    public int? MeasurementSeriesCount { get; init; }

    /// <summary>有限数量的真实 tag 不同值计数。</summary>
    public IReadOnlyList<TimeSeriesTagCardinality> Tags { get; init; } = [];

    /// <summary>是否还有因 MaxTagKeys 未检查的 tag；为 true 时不能推断其他 tag 基数。</summary>
    public bool TagsTruncated { get; init; }

    /// <summary>实际数据库是否启用 retention；没有数据库上下文时为 null。</summary>
    public bool? RetentionEnabled { get; init; }

    /// <summary>实际配置换算后的 TTL，单位与时间戳一致；禁用、未知或配置无效时为 null。</summary>
    public long? RetentionTtl { get; init; }

    /// <summary>实际 NowFn 本次返回的时间戳；禁用或未知时为 null。</summary>
    public long? RetentionNow { get; init; }

    /// <summary>时间戳严格小于该值时过期；禁用、未知或算术溢出时为 null。</summary>
    public long? RetentionCutoff { get; init; }

    /// <summary>数据检查是否执行完成；预算拒绝或缺少序列时为 false。</summary>
    public bool DataChecked { get; init; }

    /// <summary>是否检查了当前快照内整个目标序列/字段/范围；不代表整个 measurement。</summary>
    public bool SampleComplete { get; init; }

    /// <summary>实际检查的可见点数。</summary>
    public int SampledPoints { get; init; }

    /// <summary>样本中观察到的字段类型；不会从空结果推断字段不存在。</summary>
    public IReadOnlyList<FieldType> ObservedFieldTypes { get; init; } = [];

    /// <summary>样本中 Float64 为 NaN 或正负无穷的点数；不把乱序或同时间戳合法覆盖视为坏点。</summary>
    public int NonFinitePoints { get; init; }

    /// <summary>样本中与目录 schema 不兼容的点数；允许 Float64 schema 读取提升前的 Int64 历史点。</summary>
    public int SchemaMismatchPoints { get; init; }

    /// <summary>样本中尚可见但已经超过实际 TTL 的点数；不等同于已删除数量。</summary>
    public int ExpiredPoints { get; init; }

    /// <summary>额外的建模、数据质量和未检查原因；静态诊断见 Query。</summary>
    public IReadOnlyList<TimeSeriesQueryDiagnostic> Issues { get; init; } = [];

    /// <summary>是否存在静态错误或确定的字段/查询类型错误；没有错误不意味着全量数据健康。</summary>
    public bool HasErrors => Query.HasErrors || Issues.Any(static issue => issue.Severity == TimeSeriesQueryDiagnosticSeverity.Error);
}

/// <summary>时序诊断的公开 Native AOT JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TimeSeriesPreflightOptions))]
[JsonSerializable(typeof(TimeSeriesPreflightReport))]
[JsonSerializable(typeof(TimeSeriesQueryDiagnostics))]
public sealed partial class TimeSeriesQueryJsonContext : JsonSerializerContext;

internal sealed class TimeSeriesReadBudget(TimeSeriesPreflightOptions options, CancellationToken cancellationToken)
{
    private int _points;
    private long _bytes;

    internal int MaxSources => options.MaxSources;
    internal CancellationToken CancellationToken => cancellationToken;

    internal void CheckSources(long count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count > options.MaxSources)
            throw new TimeSeriesPreflightBudgetException("数据源或索引项数量超过 MaxSources。");
    }

    internal void Charge(int points, long bytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (points > options.MaxSourcePoints - _points || bytes > options.MaxSourceBytes - _bytes)
            throw new TimeSeriesPreflightBudgetException("排序、合并或解码所需的源数据超过 MaxSourcePoints / MaxSourceBytes。");
        _points += points;
        _bytes += bytes;
    }
}
