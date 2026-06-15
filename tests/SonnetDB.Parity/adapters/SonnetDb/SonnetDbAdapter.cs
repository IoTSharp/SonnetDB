using System.Globalization;
using SonnetDB.Data;
using SonnetDB.Parity.Adapters;
using System.Data;
using System.Data.Common;

namespace SonnetDB.Parity.Adapters.SonnetDb;

/// <summary>
/// SonnetDB 后端适配器：以**嵌入式**模式打开一个临时目录数据库（无需 docker），
/// 把 <see cref="IRelationalOps"/> 翻译成 SonnetDB SQL 方言。
/// </summary>
/// <remarks>
/// 连接字符串为 <c>Data Source={tempDir}</c>，<see cref="DisposeAsync"/> 关闭连接并尽力删除临时目录。
/// </remarks>
public sealed class SonnetDbAdapter : IDataPlane, IRelationalOps, ITimeSeriesOps
{
    private readonly string _root;
    private readonly SndbConnection _connection;

    /// <summary>创建适配器，在系统临时目录下开辟独立子目录并打开嵌入式连接。</summary>
    public SonnetDbAdapter()
    {
        _root = Path.Combine(Path.GetTempPath(), "sonnetdb-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _connection = new SndbConnection($"Data Source={_root}");
        _connection.Open();
    }

    /// <inheritdoc />
    public string BackendName => "sonnetdb";

    /// <inheritdoc />
    public Capability Capabilities =>
        Capability.Relational |
        Capability.SqlSubquery |
        Capability.SqlForeignKey |
        Capability.SqlGroupBy |
        Capability.SqlInformationSchema |
        Capability.SqlUpdateCount |
        Capability.SqlAlterTable |
        Capability.SqlReadCommitted |
        Capability.TimeSeries |
        Capability.TimeSeriesRemoteWrite |
        Capability.TimeSeriesGroupByTime |
        Capability.TimeSeriesDerivative |
        Capability.TimeSeriesRateIrate |
        Capability.TimeSeriesHoltWinters |
        Capability.TimeSeriesQuantile |
        Capability.TimeSeriesDistinctCount;

    /// <inheritdoc />
    public IRelationalOps Relational => this;

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => this;

    /// <inheritdoc />
    public RelationalDialect Dialect => RelationalDialect.SonnetDb;

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RelationalSqlResult> QueryAsync(string sql, CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await RelationalResultMaterializer.ReadAsync(reader, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IRelationalSession> OpenSessionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return Task.FromResult<IRelationalSession>(new SonnetDbRelationalSession(connection));
    }

    /// <inheritdoc />
    public async Task EnsureDeviceTableAsync(CancellationToken ct)
    {
        await DropDeviceTableAsync(ct).ConfigureAwait(false);
        await ExecuteAsync("CREATE TABLE devices (id INT, name STRING, PRIMARY KEY (id))", ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task InsertDevicesAsync(IReadOnlyList<RelationalRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (var row in rows)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO devices (id, name) VALUES (@id, @name)";
            AddParameter(cmd, "@id", row.Id);
            AddParameter(cmd, "@name", row.Name);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelationalRow>> SelectDevicesOrderByIdAsync(CancellationToken ct)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM devices ORDER BY id";
        var rows = new List<RelationalRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(new RelationalRow(reader.GetInt64(0), reader.GetString(1)));
        return rows;
    }

    /// <inheritdoc />
    public Task DropDeviceTableAsync(CancellationToken ct)
        => ExecuteAsync("DROP TABLE IF EXISTS devices", ct);

    /// <inheritdoc />
    public async Task IngestAsync(IReadOnlyList<TsdbPoint> points, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(points);
        foreach (var group in points.GroupBy(static p => p.Measurement, StringComparer.Ordinal))
        {
            await ExecuteAsync($"CREATE MEASUREMENT IF NOT EXISTS {group.Key} (device TAG, region TAG, value FIELD FLOAT)", ct)
                .ConfigureAwait(false);

            const int BatchSize = 2_000;
            var batch = new List<string>(BatchSize);
            foreach (var point in group)
            {
                batch.Add(string.Create(CultureInfo.InvariantCulture,
                    $"({point.TimestampMs}, '{EscapeSql(point.Device)}', '{EscapeSql(point.Region)}', {point.Value:G17})"));
                if (batch.Count == BatchSize)
                {
                    await InsertTimeSeriesBatchAsync(group.Key, batch, ct).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
                await InsertTimeSeriesBatchAsync(group.Key, batch, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<RelationalSqlResult> CountAsync(string measurement, CancellationToken ct)
        => QueryAsync($"SELECT count(value) FROM {measurement}", ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> GroupByTimeAverageAsync(string measurement, TimeSpan window, CancellationToken ct)
        => QueryAsync(
            $"SELECT time, avg(value) FROM {measurement} GROUP BY time({(long)window.TotalMilliseconds}ms)",
            ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> DerivativeAsync(string measurement, CancellationToken ct)
        => QueryAsync($"SELECT time, derivative(value, 1s) FROM {measurement}", ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> RateIrateAsync(string measurement, CancellationToken ct)
        => QueryAsync($"SELECT time, rate(value, 1s), irate(value, 1s) FROM {measurement}", ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> HoltWintersForecastAsync(string measurement, int horizon, CancellationToken ct)
        => QueryAsync($"SELECT time, value FROM forecast({measurement}, value, {horizon}, 'holt_winters', 10)", ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> PercentileP95Async(string measurement, CancellationToken ct)
        => QueryAsync($"SELECT percentile(value, 95) FROM {measurement}", ct);

    /// <inheritdoc />
    public Task<RelationalSqlResult> DistinctDeviceCountAsync(string measurement, CancellationToken ct)
        => QueryAsync($"SELECT distinct_count(value) FROM {measurement}", ct);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await _connection.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort close */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static void AddParameter(SndbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private Task InsertTimeSeriesBatchAsync(string measurement, IReadOnlyList<string> values, CancellationToken ct)
        => ExecuteAsync($"INSERT INTO {measurement} (time, device, region, value) VALUES {string.Join(", ", values)}", ct);

    private static string EscapeSql(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class SonnetDbRelationalSession : IRelationalSession
    {
        private readonly SndbConnection _connection;
        private SndbTransaction? _transaction;

        public SonnetDbRelationalSession(SndbConnection connection)
        {
            _connection = connection;
        }

        public async Task<int> ExecuteAsync(string sql, CancellationToken ct)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = _transaction;
            cmd.CommandText = sql;
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<RelationalSqlResult> QueryAsync(string sql, CancellationToken ct)
        {
            await using var cmd = _connection.CreateCommand();
            cmd.Transaction = _transaction;
            cmd.CommandText = sql;
            await using DbDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await RelationalResultMaterializer.ReadAsync(reader, ct).ConfigureAwait(false);
        }

        public Task<IRelationalTransaction> BeginTransactionAsync(IsolationLevel isolationLevel, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _transaction = (SndbTransaction)_connection.BeginTransaction(isolationLevel);
            return Task.FromResult<IRelationalTransaction>(new SonnetDbRelationalTransaction(this, _transaction));
        }

        public async ValueTask DisposeAsync()
        {
            try { await (_transaction?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false); }
            finally { await _connection.DisposeAsync().ConfigureAwait(false); }
        }

        public void ClearTransaction(SndbTransaction transaction)
        {
            if (ReferenceEquals(_transaction, transaction))
                _transaction = null;
        }
    }

    private sealed class SonnetDbRelationalTransaction : IRelationalTransaction
    {
        private readonly SonnetDbRelationalSession _session;
        private readonly SndbTransaction _transaction;

        public SonnetDbRelationalTransaction(SonnetDbRelationalSession session, SndbTransaction transaction)
        {
            _session = session;
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _transaction.Commit();
            _session.ClearTransaction(_transaction);
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _transaction.Rollback();
            _session.ClearTransaction(_transaction);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}
