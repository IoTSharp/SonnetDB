using System.Globalization;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.Parity.Adapters;
using System.Data;
using System.Data.Common;
using System.Text;
using SonnetDB.ObjectStorage;
using SonnetMQ;

namespace SonnetDB.Parity.Adapters.SonnetDb;

/// <summary>
/// SonnetDB 后端适配器：以**嵌入式**模式打开一个临时目录数据库（无需 docker），
/// 把 <see cref="IRelationalOps"/> 翻译成 SonnetDB SQL 方言。
/// </summary>
/// <remarks>
/// 连接字符串为 <c>Data Source={tempDir}</c>，<see cref="DisposeAsync"/> 关闭连接并尽力删除临时目录。
/// </remarks>
public sealed class SonnetDbAdapter : IDataPlane, IRelationalOps, ITimeSeriesOps, IKvOps, IObjectOps, IVectorOps, IMqOps
{
    private readonly string _root;
    private readonly SndbConnection _connection;
    private SonnetMqStore _mq;

    /// <summary>创建适配器，在系统临时目录下开辟独立子目录并打开嵌入式连接。</summary>
    public SonnetDbAdapter()
    {
        _root = Path.Combine(Path.GetTempPath(), "sonnetdb-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _connection = new SndbConnection($"Data Source={_root}");
        _connection.Open();
        _mq = OpenMqStore();
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
        Capability.TimeSeriesDistinctCount |
        Capability.Kv |
        Capability.KvIncr |
        Capability.KvCas |
        Capability.KvRangeScan |
        Capability.Object |
        Capability.ObjectMultipart |
        Capability.Mq |
        Capability.MqConsumerGroup |
        Capability.MqReplayFromOffset |
        Capability.Vector |
        Capability.HnswFiltered;

    /// <inheritdoc />
    public IRelationalOps Relational => this;

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => this;

    /// <inheritdoc />
    public IKvOps Kv => this;

    /// <inheritdoc />
    public IObjectOps Objects => this;

    /// <inheritdoc />
    public IVectorOps Vector => this;

    /// <inheritdoc />
    public IMqOps Mq => this;

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
    public Task ResetAsync(string scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Database.Keyspaces.Open("parity_kv").DeletePrefix(scope + ":");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetAsync(string scope, string key, byte[] value, DateTimeOffset? expiresAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Database.Keyspaces.Open("parity_kv").Put(QualifyKv(scope, key), value, expiresAtUtc);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<KvRecord?> GetAsync(string scope, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = Database.Keyspaces.Open("parity_kv").GetEntry(QualifyKv(scope, key));
        return Task.FromResult(entry is null
            ? null
            : new KvRecord(key, entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KvRecord>> ScanPrefixAsync(string scope, string prefix, int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var rows = Database.Keyspaces.Open("parity_kv")
            .ScanPrefix(QualifyKv(scope, prefix), limit)
            .Select(entry => new KvRecord(UnqualifyKv(scope, entry), entry.Value.ToArray(), entry.Version, entry.ExpiresAtUtc))
            .ToArray();
        return Task.FromResult<IReadOnlyList<KvRecord>>(rows);
    }

    /// <inheritdoc />
    public Task<long> IncrementAsync(string scope, string key, long delta, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Database.Keyspaces.Open("parity_kv").Increment(QualifyKv(scope, key), delta);
        return Task.FromResult(result.Value);
    }

    /// <inheritdoc />
    public Task<long> DecrementAsync(string scope, string key, long delta, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Database.Keyspaces.Open("parity_kv").Decrement(QualifyKv(scope, key), delta);
        return Task.FromResult(result.Value);
    }

    /// <inheritdoc />
    public Task<KvCasOutcome> CompareAndSetAsync(
        string scope,
        string key,
        long expectedVersion,
        byte[] value,
        DateTimeOffset? expiresAtUtc,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = Database.Keyspaces.Open("parity_kv")
            .CompareAndSet(QualifyKv(scope, key), expectedVersion, value, expiresAtUtc);
        return Task.FromResult(new KvCasOutcome(result.Succeeded, result.CurrentVersion, result.NewVersion));
    }

    /// <inheritdoc />
    public Task<bool> ExpireAsync(string scope, string key, DateTimeOffset expiresAtUtc, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Database.Keyspaces.Open("parity_kv")
            .ExpireAt(QualifyKv(scope, key), expiresAtUtc));
    }

    /// <inheritdoc />
    public Task<bool> PersistAsync(string scope, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Database.Keyspaces.Open("parity_kv")
            .Persist(QualifyKv(scope, key)));
    }

    /// <inheritdoc />
    public Task<long> TtlMillisecondsAsync(string scope, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var ttl = Database.Keyspaces.Open("parity_kv")
            .GetTimeToLive(QualifyKv(scope, key));
        return Task.FromResult(ttl.Milliseconds);
    }

    /// <inheritdoc />
    public Task ResetBucketAsync(string bucket, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var store = new SndbObjectStore(Database);
        if (store.GetBucket(bucket) is null)
        {
            store.CreateBucket(bucket, SndbBucketPurpose.General);
            return Task.CompletedTask;
        }

        while (true)
        {
            var page = store.ListObjects(bucket, maxKeys: 1000);
            if (page.Objects.Count == 0)
                break;

            foreach (var item in page.Objects)
                store.DeleteObject(bucket, item.Key);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<ObjectPutResult> PutAsync(string bucket, string key, Stream content, string contentType, CancellationToken ct)
    {
        var result = await new SndbObjectStore(Database).PutObjectAsync(bucket, key, content, contentType, cancellationToken: ct).ConfigureAwait(false);
        return new ObjectPutResult(result.Key, result.SizeBytes, result.ETag);
    }

    /// <inheritdoc />
    public async Task<ObjectReadResult?> GetAsync(string bucket, string key, ObjectRange? range, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var read = new SndbObjectStore(Database).OpenRead(
            bucket,
            key,
            range.HasValue ? new SndbObjectRange(range.Value.Offset, range.Value.Length) : null);
        if (read is null)
            return null;

        await using (read.Content)
        {
            using var output = new MemoryStream();
            await read.Content.CopyToAsync(output, ct).ConfigureAwait(false);
            return new ObjectReadResult(output.ToArray(), read.Info.ContentType, read.Length);
        }
    }

    /// <inheritdoc />
    public Task<ObjectListPage> ListAsync(string bucket, string prefix, int maxKeys, string? continuationToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = new SndbObjectStore(Database).ListObjects(bucket, prefix, maxKeys, continuationToken);
        return Task.FromResult(new ObjectListPage(
            page.Objects.Select(static item => new ObjectListItem(item.Key, item.SizeBytes)).ToArray(),
            page.IsTruncated,
            page.NextContinuationToken));
    }

    /// <inheritdoc />
    public async Task<ObjectPutResult> CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken ct)
    {
        var result = await new SndbObjectStore(Database).CopyObjectAsync(bucket, sourceKey, bucket, destinationKey, cancellationToken: ct).ConfigureAwait(false);
        return new ObjectPutResult(result.Key, result.SizeBytes, result.ETag);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string bucket, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        new SndbObjectStore(Database).DeleteObject(bucket, key);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ObjectDeleteResult>> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new SndbObjectStore(Database).DeleteObjects(bucket, keys);
        return Task.FromResult<IReadOnlyList<ObjectDeleteResult>>(result.Deleted
            .Select(static item => new ObjectDeleteResult(item.Key, item.DeleteMarker, item.ErrorCode))
            .ToArray());
    }

    /// <inheritdoc />
    public Task<string> InitiateMultipartAsync(string bucket, string key, string contentType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var upload = new SndbObjectStore(Database).InitiateMultipartUpload(bucket, key, contentType);
        return Task.FromResult(upload.UploadId);
    }

    /// <inheritdoc />
    public Task UploadPartAsync(string bucket, string key, string uploadId, int partNumber, Stream content, CancellationToken ct)
        => new SndbObjectStore(Database).UploadPartAsync(uploadId, partNumber, content, ct);

    /// <inheritdoc />
    public async Task<ObjectPutResult> CompleteMultipartAsync(string bucket, string key, string uploadId, IReadOnlyList<int> partNumbers, CancellationToken ct)
    {
        var result = await new SndbObjectStore(Database).CompleteMultipartUploadAsync(uploadId, partNumbers, ct).ConfigureAwait(false);
        return new ObjectPutResult(result.Key, result.SizeBytes, result.ETag);
    }

    /// <inheritdoc />
    public Task<string> CreatePresignedGetUrlAsync(string bucket, string key, TimeSpan expiresAfter, CancellationToken ct)
        => throw new NotSupportedException("嵌入式 SonnetDB parity adapter 没有 HTTP 预签名 URL。");

    /// <inheritdoc />
    public async Task ResetCollectionAsync(string collection, int dimension, CancellationToken ct)
    {
        if (Database.Measurements.TryGet(collection) is not null)
            return;

        await ExecuteAsync($"CREATE MEASUREMENT {collection} (category TAG, embedding FIELD VECTOR({dimension}) WITH INDEX hnsw(m=8, ef=32))", ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
            return;

        const int BatchSize = 500;
        var batch = new List<string>(BatchSize);
        foreach (var record in records)
        {
            string vector = string.Join(", ", record.Vector.Select(static v => v.ToString("G9", CultureInfo.InvariantCulture)));
            batch.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"({(long)record.Id}, '{EscapeSql(record.Category)}', [{vector}])"));
            if (batch.Count == BatchSize)
            {
                await InsertVectorBatchAsync(collection, batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await InsertVectorBatchAsync(collection, batch, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection,
        float[] query,
        int topK,
        string? categoryFilter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        string vector = string.Join(", ", query.Select(static v => v.ToString("G9", CultureInfo.InvariantCulture)));
        string sql = $"SELECT * FROM knn({collection}, embedding, [{vector}], {topK}, 'cosine')";
        if (!string.IsNullOrWhiteSpace(categoryFilter))
            sql += $" WHERE category = '{EscapeSql(categoryFilter)}'";

        var result = await QueryAsync(sql, ct).ConfigureAwait(false);
        return result.Rows
            .Select(row => new VectorHit(
                Convert.ToUInt64(row.Values[0], CultureInfo.InvariantCulture),
                Convert.ToDouble(row.Values[1], CultureInfo.InvariantCulture),
                Convert.ToString(row.Values[2], CultureInfo.InvariantCulture)))
            .ToArray();
    }

    /// <inheritdoc />
    public Task ResetTopicAsync(string topic, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _mq.Dispose();
        string path = MqPath;
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        _mq = OpenMqStore();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<long> PublishAsync(string topic, byte[] payload, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_mq.Publish(topic, payload, new SonnetMqPublishOptions(headers)));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> PublishManyAsync(string topic, IReadOnlyList<MqPublishRecord> records, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = records.Select(static r => new SonnetMqPublishEntry(r.Payload, r.Headers)).ToArray();
        return Task.FromResult(_mq.PublishMany(topic, entries));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MqMessageRecord>> PullAsync(string topic, string consumerGroup, int maxCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MqMessageRecord>>(_mq.Pull(topic, consumerGroup, maxCount).Select(ToMqMessage).ToArray());
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MqMessageRecord>> ReplayAsync(string topic, long offset, int maxCount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<MqMessageRecord>>(_mq.Pull(topic, offset, maxCount).Select(ToMqMessage).ToArray());
    }

    /// <inheritdoc />
    public Task<long> AckAsync(string topic, string consumerGroup, long offset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_mq.Ack(topic, consumerGroup, offset));
    }

    /// <inheritdoc />
    public Task RestartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _mq.Dispose();
        _mq = OpenMqStore();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { _mq.Dispose(); } catch { /* best-effort close */ }
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

    private Tsdb Database =>
        _connection.UnderlyingTsdb ?? throw new InvalidOperationException("SonnetDB parity adapter must use embedded mode.");

    private string MqPath => Path.Combine(_root, "mq");

    private SonnetMqStore OpenMqStore()
        => SonnetMqStore.Open(new SonnetMqOptions
        {
            Path = MqPath,
            RetentionInterval = TimeSpan.Zero,
        });

    private static MqMessageRecord ToMqMessage(SonnetMqMessage message)
        => new(message.Topic, message.Offset, message.TimestampUtc, message.Headers, message.Payload);

    private Task InsertTimeSeriesBatchAsync(string measurement, IReadOnlyList<string> values, CancellationToken ct)
        => ExecuteAsync($"INSERT INTO {measurement} (time, device, region, value) VALUES {string.Join(", ", values)}", ct);

    private Task InsertVectorBatchAsync(string collection, IReadOnlyList<string> values, CancellationToken ct)
        => ExecuteAsync($"INSERT INTO {collection} (time, category, embedding) VALUES {string.Join(", ", values)}", ct);

    private static string EscapeSql(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static string QualifyKv(string scope, string key) => scope + ":" + key;

    private static string UnqualifyKv(string scope, KvEntry entry)
    {
        string key = Encoding.UTF8.GetString(entry.Key.Span);
        string prefix = scope + ":";
        return key.StartsWith(prefix, StringComparison.Ordinal) ? key[prefix.Length..] : key;
    }

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
