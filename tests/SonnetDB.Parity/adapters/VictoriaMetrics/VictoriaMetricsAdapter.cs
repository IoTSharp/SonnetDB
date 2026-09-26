using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Snappier;

namespace SonnetDB.Parity.Adapters.VictoriaMetrics;

/// <summary>
/// VictoriaMetrics 单节点适配器：写入走 Prometheus remote_write，
/// 查询走 Prometheus HTTP API / PromQL。
/// </summary>
public sealed class VictoriaMetricsAdapter : IDataPlane, ITimeSeriesOps
{
    private readonly HttpClient _client;
    private readonly TimeSpan _visibilityTimeout;
    private long _minTimestamp = long.MaxValue;
    private long _maxTimestamp = long.MinValue;

    /// <summary>使用 <c>PARITY_VM_URL</c> 环境变量创建连接。</summary>
    public VictoriaMetricsAdapter()
        : this(new HttpClient { BaseAddress = new Uri(Env("PARITY_VM_URL", "http://127.0.0.1:28428")) }, TimeSpan.FromSeconds(15))
    {
    }

    internal VictoriaMetricsAdapter(HttpClient client, TimeSpan visibilityTimeout)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(visibilityTimeout, TimeSpan.Zero);
        _client = client;
        _visibilityTimeout = visibilityTimeout;
    }

    /// <inheritdoc />
    public string BackendName => "victoriametrics";

    /// <inheritdoc />
    public Capability Capabilities =>
        Capability.TimeSeries |
        Capability.TimeSeriesRemoteWrite |
        Capability.TimeSeriesGroupByTime |
        Capability.TimeSeriesDerivative |
        Capability.TimeSeriesRateIrate |
        Capability.TimeSeriesQuantile |
        Capability.TimeSeriesDistinctCount;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("VictoriaMetrics 适配器不支持关系型操作。");

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

    /// <summary>探测 VictoriaMetrics 是否可达。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>可达返回 true。</returns>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(Env("PARITY_VM_URL", "http://127.0.0.1:28428")) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync("/health", timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
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

        var encoded = PrometheusRemoteWriteEncoder.Encode(points);
        var compressed = Snappy.CompressToArray(encoded);
        using var content = new ByteArrayContent(compressed);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        content.Headers.ContentEncoding.Add("snappy");

        using var response = await _client.PostAsync("/api/v1/write", content, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NoContent && !response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"VictoriaMetrics remote_write failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
        }

        await WaitUntilQueryableAsync(points, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> CountAsync(string measurement, CancellationToken ct)
    {
        var value = await QueryScalarAsync(
            $"sum(count_over_time({measurement}[{FullRangeDuration}]))",
            _maxTimestamp,
            ct).ConfigureAwait(false);
        return Result(["count"], [Row(value)]);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> GroupByTimeAverageAsync(string measurement, TimeSpan window, CancellationToken ct)
    {
        var rows = await QueryRangeAsync(
            $"avg(avg_over_time({measurement}[{FormatPromDuration(window)}]))",
            _minTimestamp + Math.Max(0L, (long)window.TotalMilliseconds - 1_000L),
            _maxTimestamp,
            window,
            ct).ConfigureAwait(false);
        return Result(["time", "avg"], rows);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> DerivativeAsync(string measurement, CancellationToken ct)
    {
        var rows = await QueryRangeAsync(
            $"deriv({measurement}[2s])",
            _minTimestamp + 1_000,
            _maxTimestamp,
            TimeSpan.FromSeconds(1),
            ct).ConfigureAwait(false);
        return Result(["time", "derivative"], PrependInitialNull(rows));
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> RateIrateAsync(string measurement, CancellationToken ct)
    {
        var rate = await QueryRangeAsync(
            $"rate({measurement}[2s])",
            _minTimestamp + 1_000,
            _maxTimestamp,
            TimeSpan.FromSeconds(1),
            ct).ConfigureAwait(false);
        var irate = await QueryRangeAsync(
            $"irate({measurement}[2s])",
            _minTimestamp + 1_000,
            _maxTimestamp,
            TimeSpan.FromSeconds(1),
            ct).ConfigureAwait(false);
        rate = PrependInitialNull(rate);
        irate = PrependInitialNull(irate);

        var rows = new List<RelationalSqlRow>(Math.Min(rate.Count, irate.Count));
        for (var i = 0; i < Math.Min(rate.Count, irate.Count); i++)
            rows.Add(Row(rate[i].Values[0], rate[i].Values[1], irate[i].Values[1]));
        return Result(["time", "rate", "irate"], rows);
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> HoltWintersForecastAsync(string measurement, int horizon, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new RelationalSqlResult([], [], -1));
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> PercentileP95Async(string measurement, CancellationToken ct)
    {
        var value = await QueryScalarAsync(
            $"quantile_over_time(0.95, {measurement}[{FullRangeDuration}])",
            _maxTimestamp,
            ct).ConfigureAwait(false);
        return Result(["p95"], [Row(value)]);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> DistinctDeviceCountAsync(string measurement, CancellationToken ct)
    {
        var value = await QueryScalarAsync(
            $"count(count_over_time({measurement}[{FullRangeDuration}]))",
            _maxTimestamp,
            ct).ConfigureAwait(false);
        return Result(["distinct_count"], [Row(value)]);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<double> QueryScalarAsync(string query, long timestampMs, CancellationToken ct)
    {
        using var doc = await QueryAsync(
            "/api/v1/query?query=" + Uri.EscapeDataString(query) + "&time=" + FormatUnixSeconds(timestampMs) + "&nocache=1",
            ct).ConfigureAwait(false);
        var result = doc.RootElement.GetProperty("data").GetProperty("result");
        if (result.GetArrayLength() == 0)
            return 0d;
        return ReadPromValue(result[0].GetProperty("value"));
    }

    private async Task<IReadOnlyList<RelationalSqlRow>> QueryRangeAsync(
        string query,
        long startMs,
        long endMs,
        TimeSpan step,
        CancellationToken ct)
    {
        var url = "/api/v1/query_range?query=" + Uri.EscapeDataString(query)
                  + "&start=" + FormatUnixSeconds(startMs)
                  + "&end=" + FormatUnixSeconds(endMs)
                  + "&step=" + Math.Max(1, (long)step.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                  + "&nocache=1";
        using var doc = await QueryAsync(url, ct).ConfigureAwait(false);
        var result = doc.RootElement.GetProperty("data").GetProperty("result");
        if (result.GetArrayLength() == 0)
            return [];

        var rows = new List<RelationalSqlRow>();
        foreach (var value in result[0].GetProperty("values").EnumerateArray())
        {
            var timestampMs = (long)Math.Round(value[0].GetDouble() * 1000d);
            rows.Add(Row(timestampMs, ReadPromValue(value)));
        }

        return rows;
    }

    private async Task<JsonDocument> QueryAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            using var reader = new StreamReader(stream);
            throw new InvalidOperationException($"VictoriaMetrics query failed: {(int)response.StatusCode} {await reader.ReadToEndAsync(ct).ConfigureAwait(false)}");
        }

        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static double ReadPromValue(JsonElement valueArray)
        => double.Parse(valueArray[1].GetString() ?? "0", CultureInfo.InvariantCulture);

    private static RelationalSqlResult Result(IReadOnlyList<string> columns, IReadOnlyList<RelationalSqlRow> rows)
        => new(columns, rows, -1);

    private static RelationalSqlRow Row(params object?[] values) => new(values);

    private string FullRangeDuration
        => FormatPromDuration(TimeSpan.FromMilliseconds(Math.Max(1_000L, _maxTimestamp - _minTimestamp + 1_000L)));

    private IReadOnlyList<RelationalSqlRow> PrependInitialNull(IReadOnlyList<RelationalSqlRow> rows)
    {
        if (rows.Count == 0 || Convert.ToInt64(rows[0].Values[0], CultureInfo.InvariantCulture) <= _minTimestamp)
            return rows;

        var normalized = new List<RelationalSqlRow>(rows.Count + 1)
        {
            Row(_minTimestamp, null),
        };
        normalized.AddRange(rows);
        return normalized;
    }

    private async Task WaitUntilQueryableAsync(IReadOnlyList<TsdbPoint> points, CancellationToken ct)
    {
        var expected = points.GroupBy(static point => point.Measurement, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var observed = expected.Keys.ToDictionary(static measurement => measurement, static _ => 0d, StringComparer.Ordinal);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_visibilityTimeout);
        try
        {
            while (true)
            {
                bool complete = true;
                foreach (var (measurement, count) in expected)
                {
                    // remote_write 的确认与首条样本可见都不保证整批已可查询。
                    // 必须等到每个 measurement 的完整精确数量，额外点仍直接失败。
                    double visible = await QueryScalarAsync(
                        $"sum(count_over_time({measurement}[{FullRangeDuration}]))",
                        _maxTimestamp,
                        timeout.Token).ConfigureAwait(false);
                    observed[measurement] = visible;
                    if (visible > count)
                        throw new InvalidOperationException($"VictoriaMetrics measurement '{measurement}' has {visible.ToString(CultureInfo.InvariantCulture)} visible points; expected exactly {count}.");
                    complete &= visible == count;
                }

                if (complete)
                    return;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            string counts = string.Join("; ", expected.Select(pair =>
                $"'{pair.Key}': expected {pair.Value}, observed {observed[pair.Key].ToString(CultureInfo.InvariantCulture)}"));
            throw new TimeoutException($"VictoriaMetrics complete batch was not queryable within {_visibilityTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds: {counts}.", ex);
        }
    }

    private static string FormatUnixSeconds(long timestampMs)
        => (timestampMs / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatPromDuration(TimeSpan value)
        => Math.Max(1, (long)value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
