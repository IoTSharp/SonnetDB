using System.Data.Common;
using System.Globalization;
using ClickHouse.Client.ADO;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Adapters.ClickHouse;

/// <summary>
/// ClickHouse 单节点分析适配器。使用 <c>ClickHouse.Client</c> ADO.NET API 连接
/// <c>docker-compose.parity.yml</c> 中的 ClickHouse 服务。
/// </summary>
public sealed class ClickHouseAdapter : IDataPlane, IAnalyticalOps
{
    private readonly ClickHouseConnection _connection;
    private readonly Dictionary<string, long> _logicalBytes = new(StringComparer.Ordinal);

    /// <summary>使用 <c>PARITY_CLICKHOUSE_*</c> 环境变量创建 ClickHouse 连接。</summary>
    public ClickHouseAdapter()
    {
        _connection = new ClickHouseConnection(BuildConnectionString());
    }

    /// <inheritdoc />
    public string BackendName => "clickhouse";

    /// <inheritdoc />
    public Capability Capabilities =>
        Capability.Analytics |
        Capability.AnalyticsGroupByTime |
        Capability.SqlWindowFunction |
        Capability.AnalyticsTopN |
        Capability.AnalyticsCompressionRatio |
        Capability.AccuracyPercentile;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("ClickHouse 适配器不支持关系型 parity 操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => UnsupportedTimeSeriesOps.Instance;

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
    public IAnalyticalOps Analytics => this;

    /// <summary>探测 ClickHouse 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            await using var connection = new ClickHouseConnection(BuildConnectionString());
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task IngestAsync(IReadOnlyList<AnalyticalRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return;

        await EnsureOpenAsync(ct).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(static row => row.Dataset, StringComparer.Ordinal))
        {
            string table = QuoteIdentifier(group.Key);
            await ExecuteAsync($"DROP TABLE IF EXISTS {table}", ct).ConfigureAwait(false);
            await ExecuteAsync($"""
                CREATE TABLE {table}
                (
                    time_ms Int64,
                    device String,
                    region String,
                    value Float64
                )
                ENGINE = MergeTree
                ORDER BY (time_ms, device)
                """, ct).ConfigureAwait(false);

            const int BatchSize = 2_000;
            var batch = new List<string>(BatchSize);
            long logicalBytes = 0;
            foreach (var row in group)
            {
                batch.Add(string.Create(CultureInfo.InvariantCulture,
                    $"({row.TimestampMs}, '{EscapeSql(row.Device)}', '{EscapeSql(row.Region)}', {row.Value:G17})"));
                logicalBytes += EstimateLogicalBytes(row);
                if (batch.Count == BatchSize)
                {
                    await InsertBatchAsync(table, batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                await InsertBatchAsync(table, batch, ct).ConfigureAwait(false);
            _logicalBytes[group.Key] = logicalBytes;
        }
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> GroupByTimeAverageAsync(string dataset, TimeSpan window, CancellationToken ct)
    {
        long windowMs = (long)window.TotalMilliseconds;
        return QueryAsync($"""
            SELECT avg(value) AS avg_value
            FROM {QuoteIdentifier(dataset)}
            GROUP BY intDiv(time_ms, {windowMs})
            ORDER BY intDiv(time_ms, {windowMs})
            """, ct);
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> WindowAverage7DayAsync(string dataset, CancellationToken ct)
        => QueryAsync($"""
            SELECT time_ms, if(row_number() OVER (ORDER BY time_ms) < 7, NULL, avg(value) OVER (ORDER BY time_ms ROWS BETWEEN 6 PRECEDING AND CURRENT ROW)) AS avg_7day
            FROM {QuoteIdentifier(dataset)}
            ORDER BY time_ms
            """, ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> TopNPerDeviceAsync(string dataset, int topN, CancellationToken ct)
        => QueryAsync($"""
            SELECT device, sum(value) AS total
            FROM {QuoteIdentifier(dataset)}
            GROUP BY device
            ORDER BY total DESC, device ASC
            LIMIT {topN}
            """, ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> CompressionRatioAsync(string dataset, CancellationToken ct)
    {
        long logical = _logicalBytes.TryGetValue(dataset, out var bytes) ? bytes : 0L;
        return QueryAsync($"""
            SELECT {logical}.0 / greatest(sum(data_compressed_bytes), 1) AS compression_ratio
            FROM system.parts
            WHERE active = 1 AND database = currentDatabase() AND table = '{EscapeSql(dataset)}'
            """, ct);
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> PercentilesAsync(string dataset, CancellationToken ct)
        => QueryAsync($"""
            SELECT
                quantileExact(0.50)(value) AS p50,
                quantileExact(0.95)(value) AS p95,
                quantileExact(0.99)(value) AS p99
            FROM {QuoteIdentifier(dataset)}
            """, ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort close */ }
    }

    private async Task EnsureOpenAsync(CancellationToken ct)
    {
        if (_connection.State != System.Data.ConnectionState.Open)
            await _connection.OpenAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct)
    {
        await EnsureOpenAsync(ct).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<RelationalSqlResult> QueryAsync(string sql, CancellationToken ct)
    {
        await EnsureOpenAsync(ct).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await RelationalResultMaterializer.ReadAsync(reader, ct).ConfigureAwait(false);
    }

    private Task InsertBatchAsync(string table, IReadOnlyList<string> values, CancellationToken ct)
        => ExecuteAsync($"INSERT INTO {table} (time_ms, device, region, value) VALUES {string.Join(", ", values)}", ct);

    private static long EstimateLogicalBytes(AnalyticalRow row)
        => sizeof(long) + sizeof(double) + row.Device.Length * 2L + row.Region.Length * 2L;

    private static string BuildConnectionString()
    {
        var host = Env("PARITY_CLICKHOUSE_HOST", "127.0.0.1");
        var port = Env("PARITY_CLICKHOUSE_HTTP_PORT", "28123");
        var user = Env("PARITY_CLICKHOUSE_USER", "default");
        var password = Env("PARITY_CLICKHOUSE_PASSWORD", string.Empty);
        var database = Env("PARITY_CLICKHOUSE_DATABASE", "default");
        return $"Host={host};Port={port};Username={user};Password={password};Database={database};Protocol=http;Timeout=3";
    }

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string QuoteIdentifier(string identifier)
        => "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    private static string EscapeSql(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
}
