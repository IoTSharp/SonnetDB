using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using SonnetDB.Model;
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

    [Theory]
    [InlineData("embedded")]
    [InlineData("remote")]
    public void AdoGetSchema_EmbeddedAndRemote_ReturnsTablesColumnsAndIndexes(string mode)
    {
        using var connection = OpenAdoSchemaMatrixConnection(mode);
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = "CREATE TABLE schema_devices (id INT, name STRING NULL, enabled BOOL, PRIMARY KEY (id))";
            Assert.Equal(0, ddl.ExecuteNonQuery());
            ddl.CommandText = "CREATE UNIQUE INDEX ux_schema_devices_name ON schema_devices (name)";
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
            ["id", "name", "enabled"],
            columns.Rows.Cast<DataRow>().Select(static row => (string)row["COLUMN_NAME"]).ToArray());
        Assert.Contains(
            columns.Rows.Cast<DataRow>(),
            row => string.Equals((string)row["COLUMN_NAME"], "id", StringComparison.Ordinal)
                && (bool)row["IS_PRIMARY_KEY"]
                && !(bool)row["IS_NULLABLE"]
                && string.Equals((string)row["DATA_TYPE"], "INT", StringComparison.Ordinal));

        var indexes = connection.GetSchema("Indexes", [null, null, "schema_devices", "ux_schema_devices_name"]);
        var index = Assert.Single(indexes.Rows.Cast<DataRow>());
        Assert.Equal("ux_schema_devices_name", index["INDEX_NAME"]);
        Assert.True((bool)index["IS_UNIQUE"]);
        Assert.Equal("name", index["COLUMN_NAME"]);
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
            Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
            cmd.CommandText = "INSERT INTO tx_devices (id, name) VALUES (2, 'fan')";
            Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
            await tx.CommitAsync();
        }

        Assert.Equal(new long[] { 1L, 2L }, await ReadIdsAsync(c, "tx_devices"));
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
            Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
            cmd.CommandText = "INSERT INTO tx_b (id, name) VALUES (1, 'b')";
            Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
        }

        await tx.CommitAsync();

        Assert.Equal(new long[] { 1L }, await ReadIdsAsync(c, "tx_a"));
        Assert.Equal(new long[] { 1L }, await ReadIdsAsync(c, "tx_b"));
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

    private static async Task<long[]> ReadIdsAsync(SndbConnection connection, string tableName)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT id FROM {tableName} ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetInt64(0));
        return [.. ids];
    }
}
