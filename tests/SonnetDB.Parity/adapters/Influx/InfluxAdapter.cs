using System.Globalization;
using System.Net.Sockets;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Core.Flux.Domain;
using InfluxDB.Client.Writes;

namespace SonnetDB.Parity.Adapters.Influx;

/// <summary>
/// InfluxDB 2.x 时序适配器。通过官方 <c>InfluxDB.Client</c> 写入 line protocol 语义数据，
/// 并用 Flux 查询聚合 / derivative / quantile 等结果。
/// </summary>
public sealed class InfluxAdapter : IDataPlane, ITimeSeriesOps
{
    private readonly InfluxDBClient _client;
    private readonly string _org;
    private readonly string _bucket;
    private long _minTimestamp = long.MaxValue;
    private long _maxTimestamp = long.MinValue;

    /// <summary>使用 <c>PARITY_INFLUX_*</c> 环境变量创建 InfluxDB 连接。</summary>
    public InfluxAdapter()
    {
        _org = Env("PARITY_INFLUX_ORG", "sndb");
        _bucket = Env("PARITY_INFLUX_BUCKET", "parity");
        _client = new InfluxDBClient(Env("PARITY_INFLUX_URL", "http://127.0.0.1:28086"), Env("PARITY_INFLUX_TOKEN", "parity-token"));
    }

    /// <inheritdoc />
    public string BackendName => "influxdb";

    /// <inheritdoc />
    public Capability Capabilities =>
        Capability.TimeSeries |
        Capability.TimeSeriesRemoteWrite |
        Capability.TimeSeriesGroupByTime |
        Capability.TimeSeriesDerivative |
        Capability.TimeSeriesRateIrate |
        Capability.TimeSeriesHoltWinters |
        Capability.TimeSeriesQuantile |
        Capability.TimeSeriesDistinctCount;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("InfluxDB 适配器不支持关系型操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => this;

    /// <inheritdoc />
    public IKvOps Kv => UnsupportedKvOps.Instance;

    /// <inheritdoc />
    public IObjectOps Objects => UnsupportedObjectOps.Instance;

    /// <inheritdoc />
    public IVectorOps Vector => UnsupportedVectorOps.Instance;

    /// <inheritdoc />
    public IMqOps Mq => UnsupportedMqOps.Instance;

    /// <inheritdoc />
    public IFullTextOps FullText => UnsupportedFullTextOps.Instance;

    /// <inheritdoc />
    public IAnalyticalOps Analytics => UnsupportedAnalyticalOps.Instance;

    /// <summary>探测 InfluxDB 是否可达。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可达返回 true。</returns>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var client = new InfluxDBClient(
                Env("PARITY_INFLUX_URL", "http://127.0.0.1:28086"),
                Env("PARITY_INFLUX_TOKEN", "parity-token"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await client.PingAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task IngestAsync(IReadOnlyList<TsdbPoint> points, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            return;

        _minTimestamp = points.Min(static p => p.TimestampMs);
        _maxTimestamp = points.Max(static p => p.TimestampMs);

        await EnsureBucketAsync(ct).ConfigureAwait(false);
        await DeleteRangeAsync(_minTimestamp, _maxTimestamp + 1, ct).ConfigureAwait(false);

        const int BatchSize = 5_000;
        var write = _client.GetWriteApiAsync();
        for (var offset = 0; offset < points.Count; offset += BatchSize)
        {
            var batch = points.Skip(offset).Take(BatchSize).Select(static p =>
                PointData.Measurement(p.Measurement)
                    .Tag("device", p.Device)
                    .Tag("region", p.Region)
                    .Field("value", p.Value)
                    .Timestamp(p.TimestampMs, WritePrecision.Ms)).ToArray();
            await write.WritePointsAsync(batch, _bucket, _org, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> CountAsync(string measurement, CancellationToken ct)
        => QueryScalarAsync($$"""
            from(bucket: "{{_bucket}}")
              |> range(start: {{StartFlux}}, stop: {{StopFlux}})
              |> filter(fn: (r) => r._measurement == "{{measurement}}" and r._field == "value")
              |> group(columns: [])
              |> count()
            """, "count", ct);

    /// <inheritdoc />
    public async Task<RelationalSqlResult> GroupByTimeAverageAsync(string measurement, TimeSpan window, CancellationToken ct)
    {
        var every = FormatFluxDuration(window);
        var tables = await QueryAsync($$"""
            from(bucket: "{{_bucket}}")
              |> range(start: {{StartFlux}}, stop: {{StopFlux}})
              |> filter(fn: (r) => r._measurement == "{{measurement}}" and r._field == "value")
              |> group(columns: [])
              |> aggregateWindow(every: {{every}}, fn: mean, createEmpty: false)
              |> keep(columns: ["_time", "_value"])
              |> sort(columns: ["_time"])
            """, ct).ConfigureAwait(false);

        var rows = tables.SelectMany(static t => t.Records)
            .Select(r => Row(TimeMs(r), Number(r.GetValue())))
            .ToArray();
        return Result(["time", "avg"], rows);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> DerivativeAsync(string measurement, CancellationToken ct)
    {
        var tables = await QueryAsync($$"""
            from(bucket: "{{_bucket}}")
              |> range(start: {{StartFlux}}, stop: {{StopFlux}})
              |> filter(fn: (r) => r._measurement == "{{measurement}}" and r._field == "value")
              |> group(columns: [])
              |> sort(columns: ["_time"])
              |> derivative(unit: 1s, nonNegative: false)
              |> keep(columns: ["_time", "_value"])
            """, ct).ConfigureAwait(false);

        var rows = tables.SelectMany(static t => t.Records)
            .Select(r => Row(TimeMs(r), Number(r.GetValue())))
            .ToArray();
        return Result(["time", "derivative"], rows);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> RateIrateAsync(string measurement, CancellationToken ct)
    {
        var derivative = await DerivativeAsync(measurement, ct).ConfigureAwait(false);
        var rows = derivative.Rows
            .Select(static r => Row(r.Values[0], r.Values[1], r.Values[1]))
            .ToArray();
        return Result(["time", "rate", "irate"], rows);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> HoltWintersForecastAsync(string measurement, int horizon, CancellationToken ct)
    {
        var tables = await QueryAsync($$"""
            from(bucket: "{{_bucket}}")
              |> range(start: {{StartFlux}}, stop: {{StopFlux}})
              |> filter(fn: (r) => r._measurement == "{{measurement}}" and r._field == "value")
              |> group(columns: [])
              |> sort(columns: ["_time"])
              |> holtWinters(n: {{horizon}}, seasonality: 0, interval: 1s)
              |> keep(columns: ["_time", "_value"])
            """, ct).ConfigureAwait(false);

        var rows = tables.SelectMany(static t => t.Records)
            .Take(horizon)
            .Select((r, i) => Row((long)i, Number(r.GetValue())))
            .ToArray();
        return Result(["step", "forecast"], rows);
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> PercentileP95Async(string measurement, CancellationToken ct)
        => QueryScalarAsync($$"""
            from(bucket: "{{_bucket}}")
              |> range(start: {{StartFlux}}, stop: {{StopFlux}})
              |> filter(fn: (r) => r._measurement == "{{measurement}}" and r._field == "value")
              |> group(columns: [])
              |> quantile(q: 0.95, method: "estimate_tdigest")
            """, "p95", ct);

    /// <inheritdoc />
    public async Task<RelationalSqlResult> DistinctDeviceCountAsync(string measurement, CancellationToken ct)
    {
        var tables = await QueryAsync($$"""
            from(bucket: "{{_bucket}}")
              |> range(start: {{StartFlux}}, stop: {{StopFlux}})
              |> filter(fn: (r) => r._measurement == "{{measurement}}" and r._field == "value")
              |> keep(columns: ["device"])
              |> group(columns: ["device"])
              |> distinct(column: "device")
              |> group(columns: [])
              |> count(column: "device")
            """, ct).ConfigureAwait(false);

        var value = tables.SelectMany(static t => t.Records).Select(static r => Number(r.GetValueByKey("device"))).SingleOrDefault();
        return Result(["distinct_count"], [Row(value)]);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        _client.Dispose();
    }

    private string StartFlux => ToFluxTime(_minTimestamp);

    private string StopFlux => ToFluxTime(_maxTimestamp + 1);

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        var buckets = _client.GetBucketsApi();
        var bucket = await buckets.FindBucketByNameAsync(_bucket, ct).ConfigureAwait(false);
        if (bucket is not null)
            return;

        var orgs = await _client.GetOrganizationsApi().FindOrganizationsAsync(org: _org, cancellationToken: ct).ConfigureAwait(false);
        var org = orgs.FirstOrDefault() ?? throw new InvalidOperationException($"InfluxDB org '{_org}' not found.");
        await buckets.CreateBucketAsync(_bucket, org.Id, cancellationToken: ct).ConfigureAwait(false);
    }

    private Task DeleteRangeAsync(long startMs, long stopMs, CancellationToken ct)
        => _client.GetDeleteApi().Delete(
            DateTimeOffset.FromUnixTimeMilliseconds(startMs).UtcDateTime,
            DateTimeOffset.FromUnixTimeMilliseconds(stopMs).UtcDateTime,
            string.Empty,
            _bucket,
            _org,
            ct);

    private async Task<RelationalSqlResult> QueryScalarAsync(string flux, string column, CancellationToken ct)
    {
        var tables = await QueryAsync(flux, ct).ConfigureAwait(false);
        var record = tables.SelectMany(static t => t.Records).FirstOrDefault();
        object? value = record is null ? null : Number(record.GetValue());
        return Result([column], [Row(value)]);
    }

    private Task<List<FluxTable>> QueryAsync(string flux, CancellationToken ct)
        => _client.GetQueryApi().QueryAsync(flux, _org, ct);

    private static RelationalSqlResult Result(IReadOnlyList<string> columns, IReadOnlyList<RelationalSqlRow> rows)
        => new(columns, rows, -1);

    private static RelationalSqlRow Row(params object?[] values) => new(values);

    private static long TimeMs(FluxRecord record)
    {
        var time = record.GetTimeInDateTime()
            ?? throw new InvalidOperationException("InfluxDB record does not contain _time.");
        return new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
    }

    private static double Number(object? value)
        => Convert.ToDouble(value, CultureInfo.InvariantCulture);

    private static string ToFluxTime(long timestampMs)
        => DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private static string FormatFluxDuration(TimeSpan window)
        => ((long)window.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "ms";

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
