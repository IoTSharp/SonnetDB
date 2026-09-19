using System.Collections.ObjectModel;
using SonnetDB.Ingest;
using SonnetDB.Model;

namespace SonnetDB.Data.TimeSeries;

/// <summary>类型化时序写入选项。</summary>
public sealed record SndbTimeSeriesWriteOptions
{
    /// <summary>单次传输的最大点数。</summary>
    public int BatchSize { get; init; } = 512;

    /// <summary>
    /// 单次 WriteBatchAsync 允许接收的最大点数，默认 8192，范围为 1..65536。
    /// 超限批次在入队前整体拒绝；更大的输入应由调用方拆成多次调用。
    /// </summary>
    public int MaxBatchPoints { get; init; } = 8192;

    /// <summary>写入队列允许等待的批次数；达到上限时写入方异步等待。</summary>
    public int MaxPendingBatches { get; init; } = 4;

    /// <summary>
    /// 传输失败时最多重试次数，默认不自动重试。
    /// HTTP POST 可能在响应丢失前已经落盘；调用方只有在批次具备幂等语义时才应显式开启重试。
    /// </summary>
    public int MaxRetries { get; init; } = 0;

    /// <summary>第一次重试前的等待时间，后续按尝试次数线性退避。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>写入点时间戳的精度。引擎最终统一保存为 Unix 毫秒。</summary>
    public TimePrecision Precision { get; init; } = TimePrecision.Milliseconds;

    /// <summary>每个批次完成后的刷新策略。</summary>
    public BulkFlushMode FlushMode { get; init; } = BulkFlushMode.None;

    internal void Validate()
    {
        if (BatchSize is < 1 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(BatchSize), "BatchSize 必须位于 1..8192。");
        if (MaxBatchPoints is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(MaxBatchPoints), "MaxBatchPoints 必须位于 1..65536。");
        if (MaxPendingBatches is < 1 or > 1024)
            throw new ArgumentOutOfRangeException(nameof(MaxPendingBatches), "MaxPendingBatches 必须位于 1..1024。");
        if (MaxRetries is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries), "MaxRetries 必须位于 0..8。");
        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(RetryDelay), "RetryDelay 必须位于 0..1 分钟。");
        if (Precision is < TimePrecision.Nanoseconds or > TimePrecision.Seconds)
            throw new ArgumentOutOfRangeException(nameof(Precision));
        if (FlushMode is < BulkFlushMode.None or > BulkFlushMode.Sync)
            throw new ArgumentOutOfRangeException(nameof(FlushMode));
    }
}

/// <summary>单点写入结果。</summary>
public sealed record SndbTimeSeriesWriteItemResult(
    int Index,
    bool Succeeded,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    /// <summary>创建成功结果。</summary>
    public static SndbTimeSeriesWriteItemResult Success(int index) => new(index, true);

    /// <summary>创建失败结果。</summary>
    public static SndbTimeSeriesWriteItemResult Failure(int index, string code, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(index, false, code, message);
    }
}

/// <summary>一批时序点的逐项写入结果。</summary>
public sealed class SndbTimeSeriesWriteResult
{
    /// <summary>创建批次结果。</summary>
    public SndbTimeSeriesWriteResult(IReadOnlyList<SndbTimeSeriesWriteItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = new ReadOnlyCollection<SndbTimeSeriesWriteItemResult>(items.ToArray());
        SucceededCount = Items.Count(static item => item.Succeeded);
        FailedCount = Items.Count - SucceededCount;
    }

    /// <summary>逐项结果，顺序与输入顺序一致。</summary>
    public IReadOnlyList<SndbTimeSeriesWriteItemResult> Items { get; }

    /// <summary>成功写入点数。</summary>
    public int SucceededCount { get; }

    /// <summary>失败点数。</summary>
    public int FailedCount { get; }

    /// <summary>所有点都成功时返回 true。</summary>
    public bool IsSuccess => FailedCount == 0;
}

/// <summary>一个待写入的类型化时序点。</summary>
public sealed class SndbTimeSeriesPoint
{
    private SndbTimeSeriesPoint(
        string measurement,
        long timestamp,
        IReadOnlyDictionary<string, string> tags,
        IReadOnlyDictionary<string, FieldValue> fields)
    {
        Measurement = measurement;
        Timestamp = timestamp;
        Tags = tags;
        Fields = fields;
    }

    /// <summary>创建点 builder。</summary>
    public static SndbTimeSeriesPointBuilder Create(string measurement)
        => new(measurement);

    /// <summary>从现有 tag/field 集合直接创建并校验一个点。</summary>
    public static SndbTimeSeriesPoint Create(
        string measurement,
        long timestamp,
        IReadOnlyDictionary<string, string>? tags,
        IReadOnlyDictionary<string, FieldValue> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return FromBuilder(
            measurement,
            timestamp,
            tags is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : tags.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal),
            fields.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal));
    }

    /// <summary>从 field 集合直接创建不带 tag 的点。</summary>
    public static SndbTimeSeriesPoint Create(
        string measurement,
        long timestamp,
        IReadOnlyDictionary<string, FieldValue> fields)
        => Create(measurement, timestamp, tags: null, fields);

    /// <summary>Measurement 名称。</summary>
    public string Measurement { get; }

    /// <summary>按写入选项精度解释的 Unix 时间戳。</summary>
    public long Timestamp { get; }

    /// <summary>Tag 集合。</summary>
    public IReadOnlyDictionary<string, string> Tags { get; }

    /// <summary>Field 集合。</summary>
    public IReadOnlyDictionary<string, FieldValue> Fields { get; }

    internal static SndbTimeSeriesPoint FromBuilder(
        string measurement,
        long timestamp,
        Dictionary<string, string> tags,
        Dictionary<string, FieldValue> fields)
    {
        Point point = Point.Create(measurement, timestamp, tags, fields);
        return new SndbTimeSeriesPoint(
            point.Measurement,
            point.Timestamp,
            new ReadOnlyDictionary<string, string>(point.Tags.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)),
            new ReadOnlyDictionary<string, FieldValue>(point.Fields.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)));
    }

    internal Point ToCorePoint(TimePrecision precision)
        => Point.Create(Measurement, NormalizeTimestamp(Timestamp, precision), Tags, Fields);

    internal static long NormalizeTimestamp(long timestamp, TimePrecision precision)
    {
        if (timestamp < 0)
            throw new ArgumentOutOfRangeException(nameof(timestamp), "timestamp 必须大于等于 0。");
        return precision switch
        {
            TimePrecision.Nanoseconds => timestamp / 1_000_000L,
            TimePrecision.Microseconds => timestamp / 1_000L,
            TimePrecision.Milliseconds => timestamp,
            TimePrecision.Seconds => checked(timestamp * 1_000L),
            _ => throw new ArgumentOutOfRangeException(nameof(precision)),
        };
    }
}

/// <summary>构造一个时序点的 fluent builder。</summary>
public sealed class SndbTimeSeriesPointBuilder
{
    private readonly string _measurement;
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FieldValue> _fields = new(StringComparer.Ordinal);
    private long _timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal SndbTimeSeriesPointBuilder(string measurement)
    {
        if (string.IsNullOrWhiteSpace(measurement))
            throw new ArgumentException("measurement 不能为空。", nameof(measurement));
        _measurement = measurement;
    }

    /// <summary>设置原始 Unix 时间戳；单位由 writer 的 Precision 决定。</summary>
    public SndbTimeSeriesPointBuilder Timestamp(long timestamp)
    {
        _timestamp = timestamp;
        return this;
    }

    /// <summary>以 Unix 毫秒设置时间戳。</summary>
    public SndbTimeSeriesPointBuilder Timestamp(DateTimeOffset timestamp)
    {
        _timestamp = timestamp.ToUnixTimeMilliseconds();
        return this;
    }

    /// <summary>添加或替换 tag。</summary>
    public SndbTimeSeriesPointBuilder Tag(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        _tags[key] = value;
        return this;
    }

    /// <summary>添加或替换 tag；<see cref="Tag(string, string)"/> 的语义别名。</summary>
    public SndbTimeSeriesPointBuilder AddTag(string key, string value) => Tag(key, value);

    /// <summary>添加或替换 double field。</summary>
    public SndbTimeSeriesPointBuilder Field(string key, double value) => SetField(key, FieldValue.FromDouble(value));

    /// <summary>添加或替换 long field。</summary>
    public SndbTimeSeriesPointBuilder Field(string key, long value) => SetField(key, FieldValue.FromLong(value));

    /// <summary>添加或替换 int field。</summary>
    public SndbTimeSeriesPointBuilder Field(string key, int value) => SetField(key, FieldValue.FromLong(value));

    /// <summary>添加或替换 bool field。</summary>
    public SndbTimeSeriesPointBuilder Field(string key, bool value) => SetField(key, FieldValue.FromBool(value));

    /// <summary>添加或替换 string field。</summary>
    public SndbTimeSeriesPointBuilder Field(string key, string value) => SetField(key, FieldValue.FromString(value));

    /// <summary>添加或替换任意核心 field value。</summary>
    public SndbTimeSeriesPointBuilder Field(string key, FieldValue value) => SetField(key, value);

    /// <summary>添加或替换任意核心 field value；<see cref="Field(string, FieldValue)"/> 的语义别名。</summary>
    public SndbTimeSeriesPointBuilder AddField(string key, FieldValue value) => Field(key, value);

    /// <summary>添加或替换向量 field。</summary>
    public SndbTimeSeriesPointBuilder FieldVector(string key, float[] value) => SetField(key, FieldValue.FromVector(value));

    /// <summary>添加或替换地理点 field。</summary>
    public SndbTimeSeriesPointBuilder FieldGeoPoint(string key, double latitude, double longitude)
        => SetField(key, FieldValue.FromGeoPoint(latitude, longitude));

    /// <summary>完成点构造并执行核心参数校验。</summary>
    public SndbTimeSeriesPoint Build()
        => SndbTimeSeriesPoint.FromBuilder(_measurement, _timestamp, _tags, _fields);

    private SndbTimeSeriesPointBuilder SetField(string key, FieldValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        _fields[key] = value;
        return this;
    }
}
