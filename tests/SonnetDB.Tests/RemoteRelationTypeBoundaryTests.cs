using System.Collections.Concurrent;
using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using SonnetDB.Model;
using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// GH-Issue #193：关系表类型边界、ADO 模型能力元数据与 measurement 专用类型入口。
/// </summary>
public sealed class RemoteRelationTypeBoundaryTests : IAsyncLifetime
{
    private const string AdminToken = "relation-boundary-admin";
    private const string DatabaseName = "relation_boundary";
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-relation-boundary-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _frameUrl = string.Empty;

    /// <summary>启动有界的真实 Kestrel HTTP/1.1 与 HTTP/2 测试入口。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        try
        {
            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server"),
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
            }, extraArgs:
            [
                "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
                "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
            ]);
            _app.Use(async (context, next) =>
            {
                _requests.Enqueue(new ObservedRequest(context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
                await next(context);
            });
            await _app.StartAsync(_deadline.Token);
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            Assert.Equal(2, addresses.Addresses.Count);
            // 仅探测已知的两个 listener；每次请求至多 3 秒，总期限 60 秒。
            foreach (string address in addresses.Addresses)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                try
                {
                    using var response = await probe.SendAsync(request, _deadline.Token);
                    if (response.IsSuccessStatusCode) _restUrl = address;
                    else _frameUrl = address;
                }
                catch (HttpRequestException)
                {
                    _frameUrl = address;
                }
            }
            Assert.NotEmpty(_restUrl);
            Assert.NotEmpty(_frameUrl);
            using var http = new HttpClient { BaseAddress = new Uri(_restUrl), Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            using var created = await http.PostAsync("/v1/db",
                new StringContent("{\"name\":\"" + DatabaseName + "\"}", Encoding.UTF8, "application/json"), _deadline.Token);
            created.EnsureSuccessStatusCode();
            _requests.Clear();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>关闭本测试宿主并只清理本测试拥有的临时目录。</summary>
    public async Task DisposeAsync()
    {
        try
        {
            if (_app is not null)
            {
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _app.StopAsync(stopDeadline.Token); }
                finally { await _app.DisposeAsync(); _app = null; }
            }
        }
        finally
        {
            _deadline.Dispose();
            var root = Path.GetFullPath(_root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(root).StartsWith("sndb-relation-boundary-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理不属于本测试的目录。");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>CREATE 与 ALTER 对专用类型确定性拒绝，失败不残留目录或改变现有数据。</summary>
    [Theory]
    [InlineData("embedded", "VECTOR(3)", "VECTOR")]
    [InlineData("rest", "VECTOR(3)", "VECTOR")]
    [InlineData("frame-http2", "VECTOR(3)", "VECTOR")]
    [InlineData("embedded", "GEOPOINT", "GEOPOINT")]
    [InlineData("rest", "GEOPOINT", "GEOPOINT")]
    [InlineData("frame-http2", "GEOPOINT", "GEOPOINT")]
    public async Task RelationDdl_MeasurementOnlyType_RejectsWithoutPartialSchema(string mode, string declaration, string typeName)
    {
        using var connection = Open(mode);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = $"CREATE TABLE boundary_rows (id INT, payload {declaration}, PRIMARY KEY (id))";
        string firstError = await AssertRejectedAsync(command, mode, typeName);
        Assert.Equal(firstError, await AssertRejectedAsync(command, mode, typeName));
        Assert.Empty(connection.GetSchema("Tables", [null, null, "boundary_rows", null]).Rows.Cast<DataRow>());
        Assert.Empty(connection.GetSchema("Columns", [null, null, "boundary_rows", null]).Rows.Cast<DataRow>());
        AssertControlRequests(mode, expectSchema: true);

        Assert.Equal(0, await ExecuteAsync(connection, "CREATE TABLE boundary_rows (id INT, payload STRING, PRIMARY KEY (id))"));
        Assert.Equal(1, await ExecuteAsync(connection, "INSERT INTO boundary_rows (id, payload) VALUES (1, 'preserved')"));
        command.CommandText = $"ALTER TABLE boundary_rows ADD COLUMN other {declaration}";
        await AssertRejectedAsync(command, mode, typeName);
        command.CommandText = $"ALTER TABLE boundary_rows ALTER COLUMN payload TYPE {declaration}";
        await AssertRejectedAsync(command, mode, typeName);
        var columns = connection.GetSchema("Columns", [null, null, "boundary_rows", null]);
        Assert.Equal(["INT", "STRING"], columns.Rows.Cast<DataRow>().Select(row => (string)row["DATA_TYPE"]).ToArray());
        Assert.Equal(["id", "payload"], columns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]).ToArray());

        _requests.Clear();
        command.CommandText = "SELECT id, payload FROM boundary_rows";
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("preserved", reader.GetString(1));
        Assert.False(await reader.ReadAsync(_deadline.Token));
        AssertSelectRequests(mode);
    }

    /// <summary>DataTypes 是 SDK 本地模型能力表；连接方式不改变它，也不触发 schema 网络请求。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public void GetSchema_DataTypes_SeparatesRelationalColumnsFromMeasurementFields(string mode)
    {
        using var connection = Open(mode);
        _requests.Clear();
        var types = connection.GetSchema("DataTypes");
        Assert.Empty(_requests);
        using var localMetadata = new SndbConnection();
        var expected = localMetadata.GetSchema("DataTypes");
        Assert.Equal(11, expected.Rows.Count);
        Assert.Equal(expected.Columns.Cast<DataColumn>().Select(column => column.ColumnName),
            types.Columns.Cast<DataColumn>().Select(column => column.ColumnName));
        Assert.Equal(expected.Rows.Count, types.Rows.Count);
        for (int i = 0; i < expected.Rows.Count; i++)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            Assert.Equal(expected.Rows[i].ItemArray, types.Rows[i].ItemArray);
        }
        Assert.Equal(["INT", "FLOAT", "DECIMAL", "BOOL", "STRING", "DATETIME", "TIME", "BLOB", "JSON"],
            types.Rows.Cast<DataRow>().Where(row => (bool)row["SupportsRelationalColumn"]).Select(row => (string)row["TypeName"]).ToArray());
        Assert.Equal(["INT", "FLOAT", "BOOL", "STRING", "VECTOR", "GEOPOINT"],
            types.Rows.Cast<DataRow>().Where(row => (bool)row["SupportsMeasurementField"]).Select(row => (string)row["TypeName"]).ToArray());
        var vector = Assert.Single(types.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "VECTOR");
        var point = Assert.Single(types.Rows.Cast<DataRow>(), row => (string)row["TypeName"] == "GEOPOINT");
        Assert.Equal(typeof(float[]).FullName, vector["DataType"]);
        Assert.Equal("VECTOR({0})", vector["CreateFormat"]);
        Assert.Equal("dimension", vector["CreateParameters"]);
        Assert.Equal(typeof(GeoPoint).FullName, point["DataType"]);
        Assert.False((bool)vector["IsBestMatch"]);
        Assert.False((bool)point["IsBestMatch"]);
        Assert.Equal((int)DbType.Object, vector["ProviderDbType"]);
        Assert.Equal((int)DbType.Object, point["ProviderDbType"]);
    }

    /// <summary>同一专用类型在 measurement FIELD 入口可写读，且不伪装成关系表元数据。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Measurement_SpecializedFields_PreservesTypedValuesWithoutRelationalColumns(string mode)
    {
        using var connection = Open(mode);
        Assert.Equal(0, await ExecuteAsync(connection,
            "CREATE MEASUREMENT boundary_samples (entity TAG, embedding FIELD VECTOR(3), location FIELD GEOPOINT)"));
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "INSERT INTO boundary_samples (time, entity, embedding, location) VALUES (1000, '1', @embedding, @location)";
        command.Parameters.AddWithValue("@embedding", new float[] { 1.25f, -0.5f, 3f });
        command.Parameters.AddWithValue("@location", new GeoPoint(39.9042, 116.4074));
        Assert.Equal(1, await command.ExecuteNonQueryAsync(_deadline.Token));
        Assert.Empty(connection.GetSchema("Tables", [null, null, "boundary_samples", null]).Rows.Cast<DataRow>());
        Assert.Empty(connection.GetSchema("Columns", [null, null, "boundary_samples", null]).Rows.Cast<DataRow>());
        AssertControlRequests(mode, expectSchema: true);

        _requests.Clear();
        command.Parameters.Clear();
        command.CommandText = "SELECT embedding, location FROM boundary_samples";
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal(typeof(float[]), reader.GetFieldType(0));
        Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, Assert.IsType<float[]>(reader.GetValue(0)));
        Assert.Equal(typeof(GeoPoint), reader.GetFieldType(1));
        Assert.Equal(new GeoPoint(39.9042, 116.4074), Assert.IsType<GeoPoint>(reader.GetValue(1)));
        Assert.False(await reader.ReadAsync(_deadline.Token));
        AssertSelectRequests(mode);
    }

    /// <summary>官方显式替代模型使用普通 FLOAT 与 JSON，坐标约束和查询不冒充专用类型。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Relation_ExplicitScalarAndJsonAlternative_RoundTripsAsOrdinaryColumns(string mode)
    {
        using var connection = Open(mode);
        Assert.Equal(0, await ExecuteAsync(connection, """
            CREATE TABLE places (
                id INT, name STRING NOT NULL, latitude FLOAT NULL, longitude FLOAT NULL, embedding JSON NULL,
                PRIMARY KEY (id),
                CONSTRAINT ck_places_latitude CHECK (latitude >= -90 AND latitude <= 90),
                CONSTRAINT ck_places_longitude CHECK (longitude >= -180 AND longitude <= 180)
            )
            """));
        Assert.Equal(1, await ExecuteAsync(connection, """
            INSERT INTO places (id, name, latitude, longitude, embedding)
            VALUES (1, 'Beijing', 39.9042, 116.4074, '[1.25,-0.5,3.0]')
            """));
        var columns = connection.GetSchema("Columns", [null, null, "places", null]);
        Assert.Equal(["INT", "STRING", "FLOAT", "FLOAT", "JSON"],
            columns.Rows.Cast<DataRow>().Select(row => (string)row["DATA_TYPE"]).ToArray());
        AssertControlRequests(mode, expectSchema: true);

        _requests.Clear();
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT latitude, longitude, embedding FROM places WHERE id = 1";
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.True(await reader.ReadAsync(_deadline.Token));
        Assert.Equal(39.9042, reader.GetDouble(0));
        Assert.Equal(116.4074, reader.GetDouble(1));
        Assert.Equal(typeof(string), reader.GetFieldType(2));
        Assert.Equal("[1.25,-0.5,3.0]", reader.GetString(2));
        Assert.False(await reader.ReadAsync(_deadline.Token));
        AssertSelectRequests(mode);
    }

    private SndbConnection Open(string mode)
    {
        _deadline.Token.ThrowIfCancellationRequested();
        string url = mode == "frame-http2" ? _frameUrl : _restUrl;
        var connection = new SndbConnection(mode == "embedded"
            ? $"Data Source={Path.Combine(_root, "embedded")};Timeout=5"
            : $"Data Source=sonnetdb+http://{new Uri(url).Authority}/{DatabaseName};Token={AdminToken};Protocol={mode};Timeout=5");
        connection.Open();
        return connection;
    }

    private async Task<int> ExecuteAsync(SndbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(_deadline.Token);
    }

    private async Task<string> AssertRejectedAsync(SndbCommand command, string mode, string typeName)
    {
        Exception error;
        if (mode == "embedded")
        {
            var parse = await Assert.ThrowsAsync<SqlParseException>(() => command.ExecuteNonQueryAsync(_deadline.Token));
            Assert.Equal(SqlParseException.DefaultErrorCode, parse.Code);
            error = parse;
        }
        else
        {
            var remote = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync(_deadline.Token));
            Assert.Equal("sql_error", remote.Error);
            error = remote;
        }
        Assert.Contains($"关系表 MVP 暂不支持 {typeName} 类型", error.Message, StringComparison.Ordinal);
        return error.Message;
    }

    private void AssertControlRequests(string mode, bool expectSchema)
    {
        if (mode == "embedded") { Assert.Empty(_requests); return; }
        Assert.Contains(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        if (expectSchema) Assert.Contains(_requests, request => request.Path == $"/v1/db/{DatabaseName}/schema");
        Assert.DoesNotContain(_requests, request => request.Path == "/v1/frame");
        Assert.All(_requests, request => Assert.Equal(mode == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol));
    }

    private void AssertSelectRequests(string mode)
    {
        if (mode == "embedded") { Assert.Empty(_requests); return; }
        var request = Assert.Single(_requests);
        Assert.Equal(mode == "frame-http2" ? "/v1/frame" : $"/v1/db/{DatabaseName}/sql", request.Path);
        Assert.Equal(mode == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol);
    }

    private sealed record ObservedRequest(string Path, string Protocol);
}
