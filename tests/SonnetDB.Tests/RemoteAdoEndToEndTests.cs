using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using SonnetDB.Model;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 端到端测试：启动真实 Kestrel + 用 <see cref="SndbConnection"/> 远程模式作为客户端调用。
/// 验证 PR #33：远程客户端 + 嵌入式客户端共享同一套 ADO.NET API，仅 ConnectionString scheme 不同。
/// </summary>
public sealed class RemoteAdoEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private const string _adminToken = "remote-admin";
    private const string _readOnlyToken = "remote-ro";
    private const string _dbName = "remote_e2e";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-remote-ado-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readOnlyToken] = ServerRoles.ReadOnly,
            },
        };
        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        // 创建数据库
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{_dbName}\"}}", System.Text.Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private string RemoteConnString(string token = _adminToken)
        => $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{_dbName};Token={token};Timeout=30";

    private SndbConnection OpenRemote(string token = _adminToken)
    {
        var c = new SndbConnection(RemoteConnString(token));
        c.Open();
        return c;
    }

    private SndbConnection OpenAdoSchemaMatrixConnection(string mode)
    {
        if (string.Equals(mode, "remote", StringComparison.Ordinal))
            return OpenRemote();

        var path = Path.Combine(
            _dataRoot ?? Path.GetTempPath(),
            "embedded-ado-schema-matrix-" + Guid.NewGuid().ToString("N"));
        var connection = new SndbConnection($"Data Source={path}");
        connection.Open();
        return connection;
    }

    [Fact]
    public void ConnectionString_Scheme_DispatchesToRemote()
    {
        using var c = OpenRemote();
        Assert.Equal(SndbProviderMode.Remote, c.ProviderMode);
        Assert.Equal(ConnectionState.Open, c.State);
        Assert.Null(c.UnderlyingTsdb); // 远程模式没有本地 Tsdb
        Assert.Equal(_dbName, c.Database);
    }

    [Fact]
    public void EmbeddedConnectionString_StaysEmbedded()
    {
        // 与远程同一连接字符串体系，仅 scheme 不同 → 自动走嵌入式实现
        var path = Path.Combine(Path.GetTempPath(), "sndb-emb-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var c = new SndbConnection($"Data Source={path}");
            c.Open();
            Assert.Equal(SndbProviderMode.Embedded, c.ProviderMode);
            Assert.NotNull(c.UnderlyingTsdb);
        }
        finally
        {
            try { Directory.Delete(path, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Remote_CreateInsertSelect_RoundTrip()
    {
        using var c = OpenRemote();
        using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)";
            ddl.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1.5), (2000, 'a', 2.5)";
            Assert.Equal(2, ins.ExecuteNonQuery());
        }
        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT time, host, value FROM cpu";
        using var r = sel.ExecuteReader();

        Assert.Equal(3, r.FieldCount);
        Assert.Equal("time", r.GetName(0));
        Assert.True(r.HasRows);
        Assert.True(r.Read());
        Assert.Equal(1000L, r.GetInt64(0));
        Assert.Equal("a", r.GetString(1));
        Assert.Equal(1.5, r.GetDouble(2));
        Assert.True(r.Read());
        Assert.Equal(2000L, r.GetInt64(0));
        Assert.False(r.Read());
        Assert.Equal(-1, r.RecordsAffected);
    }

    [Fact]
    public async Task Remote_CreateMeasurementIfNotExists_ConcurrentRequestsAreIdempotent()
    {
        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            await using var connection = new SndbConnection(RemoteConnString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE MEASUREMENT IF NOT EXISTS remote_device_status (device TAG, online FIELD BOOL)";
            return await command.ExecuteNonQueryAsync();
        });

        var affected = await Task.WhenAll(tasks);

        Assert.All(affected, count => Assert.Equal(0, count));
        await using var verifyConnection = new SndbConnection(RemoteConnString());
        await verifyConnection.OpenAsync();
        await using var verify = verifyConnection.CreateCommand();
        verify.CommandText = "DESCRIBE MEASUREMENT remote_device_status";
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void AdoGetSchema_EmbeddedAndRemote_ReturnsTablesColumnsAndIndexes(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE schema_sites (id INT, PRIMARY KEY (id))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
            ddl.CommandText = "CREATE TABLE schema_devices (id INT AUTO_INCREMENT, site_id INT, name STRING NULL, enabled BOOL DEFAULT TRUE, version INT ROWVERSION, PRIMARY KEY (id), CONSTRAINT fk_schema_devices_sites FOREIGN KEY (site_id) REFERENCES schema_sites (id) ON DELETE SET NULL)";
            Assert.Equal(0, ddl.ExecuteNonQuery());
            ddl.CommandText = "CREATE UNIQUE INDEX ux_schema_devices_name ON schema_devices (name)";
            Assert.Equal(0, ddl.ExecuteNonQuery());
            ddl.CommandText = "CREATE VIEW schema_devices_view AS SELECT id, name FROM schema_devices";
            Assert.Equal(0, ddl.ExecuteNonQuery());
            ddl.CommandText = "CREATE DOCUMENT COLLECTION schema_docs";
            Assert.Equal(0, ddl.ExecuteNonQuery());
        }

        var collections = connection.GetSchema();
        Assert.Contains(
            collections.Rows.Cast<DataRow>(),
            row => string.Equals((string)row["CollectionName"], "Tables", StringComparison.Ordinal));

        var tables = connection.GetSchema("Tables", [null, null, "schema_devices", null]);
        var table = Assert.Single(tables.Rows.Cast<DataRow>());
        Assert.Equal(connection.Database, table["TABLE_CATALOG"]);
        Assert.Equal("schema_devices", table["TABLE_NAME"]);
        Assert.Equal("BASE TABLE", table["TABLE_TYPE"]);

        var columns = connection.GetSchema("Columns", [null, null, "schema_devices", null]);
        Assert.Equal(
            ["id", "site_id", "name", "enabled", "version"],
            columns.Rows.Cast<DataRow>().Select(static row => (string)row["COLUMN_NAME"]).ToArray());
        Assert.Contains(
            columns.Rows.Cast<DataRow>(),
            row => string.Equals((string)row["COLUMN_NAME"], "id", StringComparison.Ordinal)
                && (bool)row["IS_PRIMARY_KEY"]
                && (bool)row["IS_AUTO_INCREMENT"]
                && !(bool)row["IS_NULLABLE"]
                && string.Equals((string)row["DATA_TYPE"], "INT", StringComparison.Ordinal));
        Assert.Contains(
            columns.Rows.Cast<DataRow>(),
            row => string.Equals((string)row["COLUMN_NAME"], "name", StringComparison.Ordinal)
                && !(bool)row["IS_AUTO_INCREMENT"]);
        Assert.Contains(
            columns.Rows.Cast<DataRow>(),
            row => string.Equals((string)row["COLUMN_NAME"], "enabled", StringComparison.Ordinal)
                && string.Equals((string)row["COLUMN_DEFAULT"], "TRUE", StringComparison.Ordinal));
        Assert.Contains(
            columns.Rows.Cast<DataRow>(),
            row => string.Equals((string)row["COLUMN_NAME"], "version", StringComparison.Ordinal)
                && (bool)row["IS_ROW_VERSION"]);

        var indexes = connection.GetSchema("Indexes", [null, null, "schema_devices", "ux_schema_devices_name"]);
        var index = Assert.Single(indexes.Rows.Cast<DataRow>());
        Assert.Equal("ux_schema_devices_name", index["INDEX_NAME"]);
        Assert.True((bool)index["IS_UNIQUE"]);
        Assert.Equal("name", index["COLUMN_NAME"]);

        var views = connection.GetSchema("Views", [null, null, "schema_devices_view", null]);
        var view = Assert.Single(views.Rows.Cast<DataRow>());
        Assert.Equal("schema_devices_view", view["TABLE_NAME"]);
        Assert.Equal("VIEW", view["TABLE_TYPE"]);

        var foreignKeys = connection.GetSchema("ForeignKeys", [null, null, "schema_devices", "fk_schema_devices_sites"]);
        var foreignKey = Assert.Single(foreignKeys.Rows.Cast<DataRow>());
        Assert.Equal("site_id", foreignKey["COLUMN_NAME"]);
        Assert.Equal("schema_sites", foreignKey["PRINCIPAL_TABLE_NAME"]);

        var documents = connection.GetSchema("DocumentCollections", [null, null, "schema_docs", null]);
        var document = Assert.Single(documents.Rows.Cast<DataRow>());
        Assert.Equal("schema_docs", document["TABLE_NAME"]);
        Assert.Equal("DOCUMENT COLLECTION", document["TABLE_TYPE"]);
    }

    [Fact]
    public void Remote_Parameters_AreInlinedAndEscaped()
    {
        using var c = OpenRemote();
        using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT m1 (host TAG, v FIELD FLOAT)";
            ddl.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO m1 (time, host, v) VALUES (@t, @h, @v)";
            ins.Parameters.AddWithValue("@t", 5000L);
            // 含单引号的字符串：应被安全转义，不会引发服务端 SQL 错误
            ins.Parameters.AddWithValue("@h", "o'reilly");
            ins.Parameters.AddWithValue("@v", 7.25);
            Assert.Equal(1, ins.ExecuteNonQuery());
        }
        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT host, v FROM m1 WHERE host = @h";
        sel.Parameters.AddWithValue("@h", "o'reilly");
        using var r = sel.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("o'reilly", r.GetString(0));
        Assert.Equal(7.25, r.GetDouble(1));
        Assert.False(r.Read());
    }

    /// <summary>验证远程 ADO 可执行清理任务使用的参数化同表 IN 子查询，并严格遵守排序和批量上限。</summary>
    [Fact]
    public async Task Remote_DeleteInSubqueryWithDateTimeParameter_DeletesBoundedBatch()
    {
        await using var connection = new SndbConnection(RemoteConnString());
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE remote_cleanup_acquisitions (
                    guid STRING,
                    uploadTime DATETIME,
                    captureTime DATETIME,
                    PRIMARY KEY (guid)
                )
                """;
            Assert.Equal(0, await setup.ExecuteNonQueryAsync());
        }

        var start = new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 5; i++)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO remote_cleanup_acquisitions (guid, uploadTime, captureTime)
                VALUES (@guid, @uploadTime, @captureTime)
                """;
            insert.Parameters.AddWithValue("@guid", $"g{i:D2}");
            insert.Parameters.AddWithValue("@uploadTime", i < 4 ? start : start.AddDays(2));
            insert.Parameters.AddWithValue("@captureTime", start.AddMinutes(i));
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.CommandText = """
                DELETE FROM remote_cleanup_acquisitions
                WHERE guid IN (
                    SELECT guid
                    FROM remote_cleanup_acquisitions
                    WHERE uploadTime < @cutoff
                    ORDER BY captureTime
                    LIMIT 2
                )
                """;
            delete.Parameters.AddWithValue("@cutoff", start.AddDays(1));
            Assert.Equal(2, await delete.ExecuteNonQueryAsync());
        }

        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT guid FROM remote_cleanup_acquisitions ORDER BY guid";
        await using var reader = await verify.ExecuteReaderAsync();
        var remaining = new List<string>();
        while (await reader.ReadAsync())
            remaining.Add(reader.GetString(0));
        Assert.Equal(["g02", "g03", "g04"], remaining);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void DateTimeParameters_EmbeddedAndRemote_CompareAgainstDateTimeColumns(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE temporal_events (id INT, occurred_at DATETIME, PRIMARY KEY (id))";
            ddl.ExecuteNonQuery();
        }

        var first = new DateTime(2026, 7, 10, 15, 48, 56, DateTimeKind.Utc);
        var second = first.AddSeconds(5);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO temporal_events (id, occurred_at) VALUES (1, @first), (2, @second)";
            insert.Parameters.AddWithValue("@first", first);
            insert.Parameters.AddWithValue("@second", second);
            Assert.Equal(2, insert.ExecuteNonQuery());
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT id FROM temporal_events WHERE occurred_at >= @from AND occurred_at < @to ORDER BY id";
        select.Parameters.AddWithValue("@from", first);
        select.Parameters.AddWithValue("@to", second);
        using var reader = select.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Remote_GeoPointColumn_ReturnsGeoPointStruct()
    {
        using var c = OpenRemote();
        using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)";
            ddl.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO vehicle (time, device, position) VALUES (1000, 'car-1', POINT(39.9042, 116.4074))";
            Assert.Equal(1, ins.ExecuteNonQuery());
        }

        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT position FROM vehicle";
        using var r = sel.ExecuteReader();

        Assert.True(r.Read());
        Assert.Equal(typeof(GeoPoint), r.GetFieldType(0));
        var point = Assert.IsType<GeoPoint>(r.GetValue(0));
        Assert.Equal(39.9042, point.Lat, 6);
        Assert.Equal(116.4074, point.Lon, 6);
        Assert.False(r.Read());
    }

    [Fact]
    public void Remote_ExecuteScalar_ReturnsCount()
    {
        using var c = OpenRemote();
        using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT m2 (host TAG, v FIELD INT)";
            ddl.ExecuteNonQuery();
        }
        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO m2 (time, host, v) VALUES (1, 'a', 1), (2, 'a', 2), (3, 'a', 3)";
            ins.ExecuteNonQuery();
        }
        using var cnt = c.CreateCommand();
        cnt.CommandText = "SELECT count(*) FROM m2";
        var v = cnt.ExecuteScalar();
        Assert.Equal(3L, v);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void ExecuteScalar_InsertReturning_EmbeddedAndRemote_ReturnsGeneratedId(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE returning_scalar (id INT AUTO_INCREMENT, name STRING, PRIMARY KEY (id))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO returning_scalar (name) VALUES ('pump') RETURNING id";
            Assert.Equal(1L, Assert.IsType<long>(insert.ExecuteScalar()));

            insert.CommandText = "INSERT INTO returning_scalar (name) VALUES ('fan') RETURNING id";
            Assert.Equal(1, insert.ExecuteNonQuery());
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT count(*) FROM returning_scalar";
        Assert.Equal(2L, Assert.IsType<long>(select.ExecuteScalar()));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void ExecuteReader_InsertReturning_EmbeddedAndRemote_ReturnsRowsAndRecordsAffected(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE returning_reader (id INT AUTO_INCREMENT, name STRING, PRIMARY KEY (id))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO returning_reader (name) VALUES ('pump'), ('fan') RETURNING id, name";
        using var reader = insert.ExecuteReader();

        Assert.Equal(2, reader.FieldCount);
        Assert.Equal("id", reader.GetName(0));
        Assert.Equal("name", reader.GetName(1));
        Assert.True(reader.Read());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("pump", reader.GetString(1));
        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal("fan", reader.GetString(1));
        Assert.False(reader.Read());
        Assert.Equal(2, reader.RecordsAffected);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void ExecuteReader_UpdateDeleteReturning_EmbeddedAndRemote_ReturnsRowsAndRecordsAffected(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE dml_returning (id INT, value INT, PRIMARY KEY (id))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
        }

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "INSERT INTO dml_returning (id, value) VALUES (1, 10), (2, 20)";
            Assert.Equal(2, seed.ExecuteNonQuery());
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE dml_returning SET value = value + 1 WHERE id >= 1 RETURNING id, value";
            using var reader = update.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(11L, reader.GetInt64(1));
            Assert.True(reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(21L, reader.GetInt64(1));
            Assert.False(reader.Read());
            Assert.Equal(2, reader.RecordsAffected);
        }

        using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM dml_returning WHERE id = 1 RETURNING id, value";
        using var deleted = delete.ExecuteReader();
        Assert.True(deleted.Read());
        Assert.Equal(1L, deleted.GetInt64(0));
        Assert.Equal(11L, deleted.GetInt64(1));
        Assert.False(deleted.Read());
        Assert.Equal(1, deleted.RecordsAffected);
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void ExecuteReader_InsertOnConflictReturning_EmbeddedAndRemote_SkipsConflicts(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE conflict_returning (id INT, value INT, PRIMARY KEY (id))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
        }

        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "INSERT INTO conflict_returning (id, value) VALUES (1, 10)";
            Assert.Equal(1, seed.ExecuteNonQuery());
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO conflict_returning (id, value) VALUES (1, 99), (2, 20) ON CONFLICT (id) DO NOTHING RETURNING id, value";
        using var reader = insert.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(20L, reader.GetInt64(1));
        Assert.False(reader.Read());
        Assert.Equal(1, reader.RecordsAffected);
    }

    [Fact]
    public async Task Remote_Transaction_CommitsViaSqlBatch()
    {
        await using var c = new SndbConnection(RemoteConnString());
        await c.OpenAsync();

        await using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE tx_devices (id INT, name STRING, PRIMARY KEY (id))";
            Assert.Equal(0, await ddl.ExecuteNonQueryAsync());
        }

        await using (var tx = Assert.IsType<SndbTransaction>(await c.BeginTransactionAsync()))
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO tx_devices (id, name) VALUES (1, 'pump')";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            cmd.CommandText = "INSERT INTO tx_devices (id, name) VALUES (2, 'fan')";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            await tx.CommitAsync();
        }

        Assert.Equal(new long[] { 1L, 2L }, await ReadIdsAsync(c, "tx_devices"));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void UpdateJoin_MultiTableWithTrigger_EmbeddedAndRemoteReturnSameFinalImage(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE join_jobs (tenant INT, id INT, source_id INT, value INT, rv INT ROWVERSION, PRIMARY KEY (tenant, id))";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE join_sources (tenant INT, id INT, value INT, PRIMARY KEY (tenant, id))";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE join_factors (tenant INT, id INT, delta INT, PRIMARY KEY (tenant, id))";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TABLE join_audit (tenant INT, id INT, value INT, rv INT, PRIMARY KEY (tenant, id))";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO join_jobs (tenant, id, source_id, value) VALUES (1, 1, 10, 0), (2, 1, 10, 0)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO join_sources (tenant, id, value) VALUES (1, 10, 20), (2, 10, 50)";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO join_factors (tenant, id, delta) VALUES (1, 10, 3), (2, 10, 7)";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TRIGGER join_normalize BEFORE UPDATE ON join_jobs FOR EACH ROW LANGUAGE SQL AS BEGIN SET NEW.value = NEW.value + 1; END";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE TRIGGER join_track AFTER UPDATE ON join_jobs FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO join_audit (tenant, id, value, rv) VALUES (NEW.tenant, NEW.id, NEW.value, NEW.rv); END";
        command.ExecuteNonQuery();

        command.CommandText = """
            UPDATE join_jobs AS j
            JOIN join_sources AS s ON j.tenant = s.tenant AND j.source_id = s.id
            JOIN join_factors AS f ON s.tenant = f.tenant AND s.id = f.id
            SET value = s.value + f.delta
            WHERE j.tenant = @tenant
            RETURNING tenant, id, value, rv
            """;
        command.Parameters.AddWithValue("@tenant", 1L);
        using (var reader = command.ExecuteReader())
        {
            Assert.Equal(["tenant", "id", "value", "rv"], Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            Assert.True(reader.Read());
            Assert.Equal([1L, 1L, 24L, 2L], Enumerable.Range(0, reader.FieldCount).Select(reader.GetInt64));
            Assert.False(reader.Read());
            Assert.Equal(1, reader.RecordsAffected);
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT value, rv FROM join_audit WHERE tenant = 1 AND id = 1";
        using (var audit = command.ExecuteReader())
        {
            Assert.True(audit.Read());
            Assert.Equal(24L, audit.GetInt64(0));
            Assert.Equal(2L, audit.GetInt64(1));
            Assert.False(audit.Read());
        }
        command.CommandText = "SELECT value, rv FROM join_jobs WHERE tenant = 2 AND id = 1";
        using var untouched = command.ExecuteReader();
        Assert.True(untouched.Read());
        Assert.Equal(0L, untouched.GetInt64(0));
        Assert.Equal(1L, untouched.GetInt64(1));
        Assert.False(untouched.Read());
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public async Task UpdateJoin_AsyncTransactions_RollBackThenCommitAcrossModes(string mode)
    {
        await using var connection = OpenAdoSchemaMatrixConnection(mode);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE join_tx_targets (id INT, source_id INT, value INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE TABLE join_tx_sources (id INT, value INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO join_tx_targets (id, source_id, value) VALUES (1, 10, 0), (2, 20, 0)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO join_tx_sources (id, value) VALUES (10, 31), (20, 42)";
        await command.ExecuteNonQueryAsync();

        const string updateSql = "UPDATE join_tx_targets AS t SET value = s.value FROM join_tx_sources AS s WHERE t.source_id = s.id AND t.id = @id RETURNING id, value";
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            command.Transaction = transaction;
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("@id", 1L);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(31L, reader.GetInt64(1));
                Assert.False(await reader.ReadAsync());
                Assert.Equal(1, reader.RecordsAffected);
            }
            await transaction.RollbackAsync();
            command.Transaction = null;
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT value FROM join_tx_targets WHERE id = 1";
        Assert.Equal(0L, Assert.IsType<long>(await command.ExecuteScalarAsync()));

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            command.Transaction = transaction;
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("@id", 2L);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(42L, reader.GetInt64(1));
                Assert.False(await reader.ReadAsync());
                Assert.Equal(1, reader.RecordsAffected);
            }
            await transaction.CommitAsync();
            command.Transaction = null;
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT id, value FROM join_tx_targets ORDER BY id";
        await using var persisted = await command.ExecuteReaderAsync();
        Assert.True(await persisted.ReadAsync());
        Assert.Equal(0L, persisted.GetInt64(1));
        Assert.True(await persisted.ReadAsync());
        Assert.Equal(42L, persisted.GetInt64(1));
        Assert.False(await persisted.ReadAsync());
    }

    [Fact]
    public async Task UpdateJoin_RemoteUniqueAndForeignKeyFailures_LeaveAllTargetsUnchanged()
    {
        await using var connection = new SndbConnection(RemoteConnString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE join_parents (id INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE TABLE join_checked (id INT, code INT, parent_id INT, PRIMARY KEY (id), FOREIGN KEY (parent_id) REFERENCES join_parents (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE UNIQUE INDEX ux_join_checked_code ON join_checked (code)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE TABLE join_changes (id INT, code INT, parent_id INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO join_parents (id) VALUES (1)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO join_checked (id, code, parent_id) VALUES (1, 10, 1), (2, 20, 1)";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO join_changes (id, code, parent_id) VALUES (1, 30, 1), (2, 30, 1)";
        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE join_checked AS t JOIN join_changes AS s ON t.id = s.id SET code = s.code WHERE t.id >= 1 RETURNING id, code";
        var unique = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteReaderAsync());
        Assert.Equal(TableConstraintException.UniqueViolation, unique.Error);
        await AssertOriginalRowsAsync();

        command.CommandText = "UPDATE join_changes SET code = 40, parent_id = 999 WHERE id = 2";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "UPDATE join_checked AS t JOIN join_changes AS s ON t.id = s.id SET code = s.code, parent_id = s.parent_id WHERE t.id >= 1 RETURNING id, code";
        var foreignKey = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteReaderAsync());
        Assert.Equal(TableConstraintException.ForeignKeyViolation, foreignKey.Error);
        await AssertOriginalRowsAsync();

        async Task AssertOriginalRowsAsync()
        {
            command.CommandText = "SELECT id, code, parent_id FROM join_checked ORDER BY id";
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal([1L, 10L, 1L], Enumerable.Range(0, reader.FieldCount).Select(reader.GetInt64));
            Assert.True(await reader.ReadAsync());
            Assert.Equal([2L, 20L, 1L], Enumerable.Range(0, reader.FieldCount).Select(reader.GetInt64));
            Assert.False(await reader.ReadAsync());
        }
    }

    [Fact]
    public async Task RemoteTransaction_InsertOnConflictDoUpdateReturning_IsRejectedUntilParity()
    {
        await using var c = new SndbConnection(RemoteConnString());
        await c.OpenAsync();

        await using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE tx_conflict_update (id INT, value INT, PRIMARY KEY (id))";
            await ddl.ExecuteNonQueryAsync();
            ddl.CommandText = "INSERT INTO tx_conflict_update (id, value) VALUES (1, 10)";
            await ddl.ExecuteNonQueryAsync();
        }

        await using var transaction = Assert.IsType<SndbTransaction>(await c.BeginTransactionAsync());
        await using var command = c.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO tx_conflict_update (id, value) VALUES (1, 20) ON CONFLICT (id) DO UPDATE SET value = excluded.value RETURNING id, value";
        await Assert.ThrowsAsync<NotSupportedException>(() => command.ExecuteReaderAsync());
        await transaction.RollbackAsync();

        await using var select = c.CreateCommand();
        select.CommandText = "SELECT value FROM tx_conflict_update WHERE id = 1";
        Assert.Equal(10L, Assert.IsType<long>(await select.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Remote_Transaction_CrossTableCommit_CommitsBothTables()
    {
        await using var c = new SndbConnection(RemoteConnString());
        await c.OpenAsync();

        await using (var ddl = c.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE tx_a (id INT, name STRING, PRIMARY KEY (id))";
            Assert.Equal(0, await ddl.ExecuteNonQueryAsync());
            ddl.CommandText = "CREATE TABLE tx_b (id INT, name STRING, PRIMARY KEY (id))";
            Assert.Equal(0, await ddl.ExecuteNonQueryAsync());
        }

        await using var tx = Assert.IsType<SndbTransaction>(await c.BeginTransactionAsync());
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO tx_a (id, name) VALUES (1, 'a')";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
            cmd.CommandText = "INSERT INTO tx_b (id, name) VALUES (1, 'b')";
            Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
        }

        await tx.CommitAsync();

        Assert.Equal(new long[] { 1L }, await ReadIdsAsync(c, "tx_a"));
        Assert.Equal(new long[] { 1L }, await ReadIdsAsync(c, "tx_b"));
    }

    [Fact]
    public async Task Remote_Transaction_WithSerializableReads_ReturnsTransactionViewAndCommits()
    {
        await using var connection = new SndbConnection(RemoteConnString());
        await connection.OpenAsync();

        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE tx_serializable (id INT, name STRING, PRIMARY KEY (id))";
            await setup.ExecuteNonQueryAsync();
            setup.CommandText = "INSERT INTO tx_serializable (id, name) VALUES (1, 'pump')";
            await setup.ExecuteNonQueryAsync();
        }

        await using var transaction = Assert.IsType<SndbTransaction>(
            await connection.BeginTransactionAsync(IsolationLevel.Serializable));
        Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);

        Assert.Equal(new long[] { 1L }, await ReadIdsAsync(connection, "tx_serializable", transaction));

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO tx_serializable (id, name) VALUES (2, 'fan')";
            await insert.ExecuteNonQueryAsync();
        }

        Assert.Equal(new long[] { 1L, 2L }, await ReadIdsAsync(connection, "tx_serializable", transaction));
        await transaction.CommitAsync();

        Assert.Equal(new long[] { 1L, 2L }, await ReadIdsAsync(connection, "tx_serializable"));
    }

    [Fact]
    public async Task RemoteTransaction_InsertReturningWithConcurrentInsert_CommitsReservedId()
    {
        await using var transactionConnection = new SndbConnection(RemoteConnString());
        await transactionConnection.OpenAsync();
        await using var concurrentConnection = new SndbConnection(RemoteConnString());
        await concurrentConnection.OpenAsync();

        await using (var ddl = transactionConnection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE tx_returning (id INT AUTO_INCREMENT, name STRING, PRIMARY KEY (id))";
            Assert.Equal(0, await ddl.ExecuteNonQueryAsync());
        }

        await using var transaction = Assert.IsType<SndbTransaction>(
            await transactionConnection.BeginTransactionAsync());
        long reservedId;
        await using (var insert = transactionConnection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO tx_returning (name) VALUES ('reserved') RETURNING id";
            reservedId = Assert.IsType<long>(await insert.ExecuteScalarAsync());
        }

        long concurrentId;
        await using (var insert = concurrentConnection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO tx_returning (name) VALUES ('concurrent') RETURNING id";
            concurrentId = Assert.IsType<long>(await insert.ExecuteScalarAsync());
        }

        Assert.NotEqual(reservedId, concurrentId);
        await transaction.CommitAsync();

        await using var select = transactionConnection.CreateCommand();
        select.CommandText = "SELECT id, name FROM tx_returning ORDER BY id";
        await using var reader = await select.ExecuteReaderAsync();
        var rows = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(1), reader.GetInt64(0));

        Assert.Equal(2, rows.Count);
        Assert.Equal(reservedId, rows["reserved"]);
        Assert.Equal(concurrentId, rows["concurrent"]);
    }

    [Fact]
    public void Remote_ReadOnlyToken_InsertForbidden()
    {
        // 先用 admin 建表
        using (var admin = OpenRemote(_adminToken))
        using (var ddl = admin.CreateCommand())
        {
            ddl.CommandText = "CREATE MEASUREMENT m3 (host TAG, v FIELD FLOAT)";
            ddl.ExecuteNonQuery();
        }

        using var c = OpenRemote(_readOnlyToken);
        using var ins = c.CreateCommand();
        ins.CommandText = "INSERT INTO m3 (time, host, v) VALUES (1, 'a', 1)";
        var ex = Assert.Throws<SndbServerException>(() => ins.ExecuteNonQuery());
        Assert.Equal("forbidden", ex.Error);
    }

    [Fact]
    public void Remote_BadSql_ThrowsTsdbServerException()
    {
        using var c = OpenRemote();
        using var bad = c.CreateCommand();
        bad.CommandText = "SELECT FROM nope_table";
        var ex = Assert.Throws<SndbServerException>(() => bad.ExecuteNonQuery());
        Assert.Equal("sql_error", ex.Error);
    }

    [Fact]
    public void Remote_MissingToken_Unauthorized()
    {
        var cs = $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/{_dbName}";
        using var c = new SndbConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM whatever";
        var ex = Assert.Throws<SndbServerException>(() => cmd.ExecuteNonQuery());
        Assert.Equal("unauthorized", ex.Error);
    }

    [Fact]
    public void Remote_UnknownDatabase_NotFound()
    {
        var cs = $"Data Source=sonnetdb+http://{new Uri(_baseUrl).Authority}/no_such_db;Token={_adminToken}";
        using var c = new SndbConnection(cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM x";
        var ex = Assert.Throws<SndbServerException>(() => cmd.ExecuteNonQuery());
        Assert.Equal("db_not_found", ex.Error);
    }

    private static async Task<long[]> ReadIdsAsync(
        SndbConnection connection,
        string tableName,
        SndbTransaction? transaction = null)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT id FROM {tableName} ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetInt64(0));
        return [.. ids];
    }
}
