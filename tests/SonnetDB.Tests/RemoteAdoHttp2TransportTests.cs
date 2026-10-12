using System.Buffers;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Exceptions;
using SonnetDB.Hosting;
using SonnetDB.IO;
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 远程 ADO 的 <c>Protocol=frame-http2</c> 必须使用真正的 h2c，而不只是把 SQL 编码成帧。
/// 写语句仍走 REST endpoint，但与只读帧查询共享同一个 HTTP/2 exact 客户端。
/// </summary>
public sealed class RemoteAdoHttp2TransportTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();
    private WebApplication? _app;
    private string _http11Url = string.Empty;
    private string _frameH2Url = string.Empty;
    private string? _dataRoot;
    private bool _suppressSqlResultVersion;
    private bool _rejectSqlV3;
    private bool _rejectAllSqlVersions;
    private bool _corruptSqlResponse;
    private const string AdminToken = "ado-h2-admin";
    private const string DatabaseName = "ado_h2_transport";

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task ViewDeclarations_EmptyAllNullAndMaterializedReopen_UseRealTransport(string protocol)
    {
        string unknownSchemaJson = System.Text.Json.JsonSerializer.Serialize(
            new ResultColumnSchema("object", true, false, false, false), ServerJsonContext.Default.ResultColumnSchema);
        using var unknownSchema = System.Text.Json.JsonDocument.Parse(unknownSchemaJson);
        Assert.True(unknownSchema.RootElement.TryGetProperty("declaredNullable", out var nullable));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, nullable.ValueKind);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var connection = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol)))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            foreach (string sql in new[]
            {
                "CREATE TABLE ViewItems (DeviceID INT, Name STRING, Amount DECIMAL(20,6), Payload BLOB, PRIMARY KEY(DeviceID))",
                "INSERT INTO ViewItems (DeviceID, Name, Amount, Payload) VALUES (1, NULL, NULL, NULL)",
                "CREATE VIEW ItemView AS SELECT DeviceID AS ItemID, Name, Amount, Payload FROM ViewItems",
                "CREATE VIEW NestedView AS SELECT * FROM ItemView",
                "CREATE VIEW UnknownView AS SELECT concat('id=', CAST(DeviceID AS STRING)) AS Rendered FROM ViewItems",
                "CREATE TABLE BranchKeys (id INT, PRIMARY KEY(id))",
                "INSERT INTO BranchKeys (id) VALUES (1), (2)",
                "CREATE VIEW UnknownSource AS SELECT id, concat('id=', CAST(id AS STRING)) AS Rendered FROM BranchKeys WHERE id = 1",
                "CREATE VIEW UnknownBranches AS SELECT l.id, CASE WHEN l.id = 1 THEN r.Rendered ELSE 0 END AS chosen, "
                    + "COALESCE(r.Rendered, 0) AS filled, CASE WHEN l.id = 1 THEN l.id ELSE NULL END AS genuine_null, "
                    + "CASE WHEN l.id = 1 THEN l.id END AS implicit_null, COALESCE(NULL, l.id) AS null_first, "
                    + "CASE WHEN l.id = 1 THEN l.id ELSE CAST(NULL AS INT) END AS typed_null "
                    + "FROM BranchKeys l LEFT JOIN UnknownSource r ON l.id = r.id",
                "CREATE MATERIALIZED VIEW SnapshotView AS SELECT * FROM NestedView",
                "REFRESH MATERIALIZED VIEW SnapshotView",
            })
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
            foreach (string source in new[] { "ItemView", "NestedView", "SnapshotView" })
            {
                foreach (string suffix in new[] { " LIMIT 0", " WHERE ItemID < 0", string.Empty })
                {
                    command.CommandText = "SELECT * FROM " + source + suffix;
                    await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                    AssertRealViewDeclarations(reader);
                    if (suffix.Length == 0)
                    {
                        Assert.True(await reader.ReadAsync(deadline.Token));
                        Assert.Equal(1L, reader.GetInt64(0));
                        Assert.True(reader.IsDBNull(1));
                        Assert.True(reader.IsDBNull(2));
                        Assert.True(reader.IsDBNull(3));
                    }
                    Assert.False(await reader.ReadAsync(deadline.Token));
                    AssertRealViewDeclarations(reader);
                }
            }
            foreach (string suffix in new[] { " LIMIT 0", " ORDER BY id" })
            {
                command.CommandText = "SELECT * FROM UnknownBranches" + suffix;
                await using var reader = await command.ExecuteReaderAsync(deadline.Token);
                AssertRealUnknownBranchDeclarations(reader);
                if (suffix == " ORDER BY id")
                {
                    Assert.True(await reader.ReadAsync(deadline.Token));
                    Assert.Equal(1L, reader.GetInt64(0));
                    Assert.Equal("id=1", reader.GetString(1));
                    Assert.Equal("id=1", reader.GetString(2));
                    Assert.Equal(1L, reader.GetInt64(3));
                    Assert.Equal(1L, reader.GetInt64(4));
                    Assert.Equal(1L, reader.GetInt64(5));
                    Assert.Equal(1L, reader.GetInt64(6));
                    AssertRealUnknownBranchDeclarations(reader);
                    Assert.True(await reader.ReadAsync(deadline.Token));
                    Assert.Equal(2L, reader.GetInt64(0));
                    Assert.Equal(0L, reader.GetInt64(1));
                    Assert.Equal(0L, reader.GetInt64(2));
                    Assert.True(reader.IsDBNull(3));
                    Assert.True(reader.IsDBNull(4));
                    Assert.Equal(2L, reader.GetInt64(5));
                    Assert.True(reader.IsDBNull(6));
                    AssertRealUnknownBranchDeclarations(reader);
                }
                Assert.False(await reader.ReadAsync(deadline.Token));
                AssertRealUnknownBranchDeclarations(reader);
            }
            command.CommandText = "SELECT * FROM UnknownView";
            await using var unknown = await command.ExecuteReaderAsync(deadline.Token);
            Assert.Equal(typeof(object), unknown.GetFieldType(0));
            Assert.Null(unknown.GetColumnSchema()[0].NumericPrecision);
            Assert.Null(unknown.GetColumnSchema()[0].AllowDBNull);
            Assert.True(await unknown.ReadAsync(deadline.Token));
            Assert.Equal("id=1", unknown.GetString(0));
            Assert.Equal(typeof(object), unknown.GetFieldType(0));
            Assert.False(await unknown.ReadAsync(deadline.Token));
        }
        await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol)))
        {
            await reopened.OpenAsync(deadline.Token);
            await using var command = reopened.CreateCommand();
            command.CommandText = "SELECT * FROM SnapshotView LIMIT 0";
            await using var reader = await command.ExecuteReaderAsync(deadline.Token);
            AssertRealViewDeclarations(reader);
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        string expectedHttp = protocol == "rest" ? "HTTP/1.1" : "HTTP/2";
        Assert.All(_requests.Where(request => request.Path.StartsWith("/v1/db/", StringComparison.Ordinal)
            || request.Path == "/v1/frame"), request => Assert.Equal(expectedHttp, request.Protocol));
        if (protocol == "frame-http2")
            Assert.Contains(_requests, request => request.Path == "/v1/frame"
                && request.ResultVersion == SqlFrameCodec.DeclaredResultVersion);
    }

    private static void AssertRealUnknownBranchDeclarations(DbDataReader reader)
    {
        Assert.Equal(new[] { typeof(long), typeof(object), typeof(object), typeof(long), typeof(long), typeof(long), typeof(long) },
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray());
        var schema = reader.GetColumnSchema();
        Assert.Equal(new bool?[] { false, true, false, true, true, false, true },
            schema.Select(column => column.AllowDBNull).ToArray());
        Assert.Equal(new int?[] { 19, null, null, 19, 19, 19, 19 },
            schema.Select(column => column.NumericPrecision).ToArray());
        Assert.Equal(new int?[] { 0, null, null, 0, 0, 0, 0 },
            schema.Select(column => column.NumericScale).ToArray());
        using var table = reader.GetSchemaTable()!;
        Assert.Equal(new object[] { (short)19, DBNull.Value, DBNull.Value, (short)19, (short)19, (short)19, (short)19 },
            table.Rows.Cast<DataRow>().Select(row => row[SchemaTableColumn.NumericPrecision]).ToArray());
        Assert.Equal(new object[] { (short)0, DBNull.Value, DBNull.Value, (short)0, (short)0, (short)0, (short)0 },
            table.Rows.Cast<DataRow>().Select(row => row[SchemaTableColumn.NumericScale]).ToArray());
    }

    private static void AssertRealViewDeclarations(DbDataReader reader)
    {
        Assert.Equal(["ItemID", "Name", "Amount", "Payload"],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
        Assert.Equal([typeof(long), typeof(string), typeof(decimal), typeof(byte[])],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray());
        Assert.Equal(["Int64", "String", "Decimal", "Blob"],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetDataTypeName).ToArray());
        var schema = reader.GetColumnSchema();
        Assert.Equal(new bool?[] { false, true, true, true }, schema.Select(column => column.AllowDBNull).ToArray());
        Assert.Equal(20, schema[2].NumericPrecision);
        Assert.Equal(6, schema[2].NumericScale);
        Assert.Equal("Decimal", schema[2].DataTypeName);
        Assert.Null(schema[1].ColumnSize);
        Assert.Null(schema[3].ColumnSize);
        Assert.Equal(DBNull.Value, reader.GetSchemaTable()!.Rows[1][SchemaTableColumn.ColumnSize]);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task LegacyViewGeneration_ReadsRowsRequiresRefreshAndMigrates_UseRealTransport(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using (var connection = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol)))
        {
            await connection.OpenAsync(deadline.Token);
            await using var command = connection.CreateCommand();
            foreach (string sql in new[]
            {
                "CREATE TABLE legacy_items (value INT, PRIMARY KEY(value))",
                "INSERT INTO legacy_items (value) VALUES (42)",
                "CREATE MATERIALIZED VIEW LegacyView AS SELECT value FROM legacy_items",
                "REFRESH MATERIALIZED VIEW LegacyView",
            })
            {
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(deadline.Token);
            }
        }
        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet(DatabaseName, out var database));
        string snapshot = Assert.Single(Directory.GetFiles(database.MaterializedViews.RootDirectory,
            "*.sdbmvsnap", SearchOption.AllDirectories));
        await ReopenHostAsync(deadline.Token, () => File.WriteAllBytes(snapshot, Convert.FromHexString(
            "5344424D56533031010000002000000001000000010000000000000000000000"
            + "0500000076616C7565012A00000000000000532F07A85344424D5653303100000000")));
        await using var reopened = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        await reopened.OpenAsync(deadline.Token);
        await using var query = reopened.CreateCommand();
        query.CommandText = "SELECT * FROM LegacyView";
        await using (var reader = await query.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Contains("REFRESH", Assert.Throws<InvalidOperationException>(() => reader.GetFieldType(0)).Message);
            Assert.Throws<InvalidOperationException>(() => reader.GetDataTypeName(0));
            Assert.Throws<InvalidOperationException>(() => reader.GetSchemaTable());
            Assert.Throws<InvalidOperationException>(() => reader.GetColumnSchema());
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(42L, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        query.CommandText = "REFRESH MATERIALIZED VIEW LegacyView";
        await query.ExecuteNonQueryAsync(deadline.Token);
        query.CommandText = "SELECT * FROM LegacyView LIMIT 0";
        await using var refreshed = await query.ExecuteReaderAsync(deadline.Token);
        Assert.Equal(typeof(long), refreshed.GetFieldType(0));
        Assert.False(await refreshed.ReadAsync(deadline.Token));
        Assert.Contains(_requests, request => request.Protocol == (protocol == "rest" ? "HTTP/1.1" : "HTTP/2")
            && request.Path == (protocol == "rest" ? $"/v1/db/{DatabaseName}/sql" : "/v1/frame"));
    }

    private async Task ReopenHostAsync(CancellationToken cancellationToken, Action? afterStop = null)
    {
        await _app!.StopAsync(cancellationToken);
        await _app.DisposeAsync();
        _app = null;
        afterStop?.Invoke();
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _dataRoot!,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        }, extraArgs:
        ["--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:FrameH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue(new ObservedRequest(context.Connection.LocalPort,
                context.Request.Path.Value ?? string.Empty, context.Request.Protocol,
                context.Request.Headers[SqlFrameCodec.ResultVersionHeader].ToString()));
            await next(context);
        });
        await _app.StartAsync(cancellationToken);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address)) _http11Url = address;
            else _frameH2Url = address;
        }
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("frame-http2")]
    public async Task OldV2Server_StablePreExecutionRejection_RetriesOnceAndKeepsOldContract(string protocol)
    {
        _rejectSqlV3 = true;
        await using var connection = new SndbConnection(ConnectionString(
            protocol == "auto" ? _http11Url : _frameH2Url, protocol));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE old_v2_items (id INT, amount DECIMAL(20,6), PRIMARY KEY(id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE VIEW OldV2View AS SELECT id, amount FROM old_v2_items";
        await command.ExecuteNonQueryAsync();
        _requests.Clear();
        command.CommandText = "SELECT * FROM OldV2View LIMIT 0";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(typeof(decimal), reader.GetFieldType(1));
        Assert.Null(reader.GetColumnSchema()[1].NumericPrecision);
        Assert.Null(reader.GetColumnSchema()[1].NumericScale);
        Assert.False(await reader.ReadAsync());
        var requests = _requests.ToArray();
        Assert.Equal(new[] { "3", "2" }, requests.Select(request => request.ResultVersion).ToArray());
        Assert.All(requests, request => Assert.Equal("/v1/frame", request.Path));
        Assert.All(requests, request => Assert.Equal(protocol == "auto" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqlFrame_RealErrorAndUncertainTransport_DoNotReplay(bool corruptTransport)
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        _corruptSqlResponse = corruptTransport;
        _requests.Clear();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM missing_view_declaration_source";
        await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync());
        Assert.Equal("3", Assert.Single(_requests).ResultVersion);
        Assert.Equal("/v1/frame", Assert.Single(_requests).Path);
    }

    [Fact]
    public async Task SqlFrame_UnsupportedV3AndV2_StopsAfterOneFallback()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        _rejectAllSqlVersions = true;
        _requests.Clear();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";
        var error = await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync());
        Assert.Equal("unsupported_result_version", error.Error);
        Assert.Equal(new[] { "3", "2" }, _requests.Select(request => request.ResultVersion).ToArray());
    }

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-ado-h2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        _app = TestServerHost.Build(options, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue(new ObservedRequest(
                context.Connection.LocalPort,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Protocol,
                context.Request.Headers[SqlFrameCodec.ResultVersionHeader].ToString()));
            if (context.Request.Path == "/v1/frame"
                && context.Request.Headers.ContainsKey(SqlFrameCodec.ResultVersionHeader))
            {
                if (_corruptSqlResponse)
                {
                    context.Response.ContentType = "application/x-sonnetdb-frame";
                    await context.Response.WriteAsync("uncertain frame response");
                    return;
                }
                if (_rejectAllSqlVersions || _rejectSqlV3
                    && context.Request.Headers[SqlFrameCodec.ResultVersionHeader] == SqlFrameCodec.DeclaredResultVersion)
                {
                    var rejected = new ArrayBufferWriter<byte>();
                    FrameCodec.WriteErrorFrame(rejected, (byte)FrameService.Sql, (byte)SqlFrameOp.Query, 1,
                        "unsupported_result_version", "版本检查在 SQL 执行前拒绝请求。");
                    context.Response.ContentType = "application/x-sonnetdb-frame";
                    await context.Response.Body.WriteAsync(rejected.WrittenMemory);
                    return;
                }
            }
            if (_suppressSqlResultVersion)
                context.Request.Headers.Remove(SqlFrameCodec.ResultVersionHeader);
            await next(context);
        });

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose listening addresses.");
        Assert.Equal(2, addresses.Addresses.Count);

        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address))
                _http11Url = address;
            else
                _frameH2Url = address;
        }

        Assert.NotEmpty(_http11Url);
        Assert.NotEmpty(_frameH2Url);

        using var http = new HttpClient { BaseAddress = new Uri(_http11Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{DatabaseName}\"}}", Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        _requests.Clear();
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task HasRows_RemoteResults_PreservesRowsAndEmptyMetadata(string protocol, bool useAsync)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var connection = new SndbConnection(ConnectionString(
            protocol == "rest" ? _http11Url : _frameH2Url, protocol));
        await connection.OpenAsync(cancellation.Token);
        using var setup = connection.CreateCommand();
        setup.CommandTimeout = 5;
        setup.CommandText = "CREATE TABLE hasrows (id INT, value STRING, PRIMARY KEY (id))";
        await setup.ExecuteNonQueryAsync(cancellation.Token);
        setup.CommandText = "CREATE TABLE empty_hasrows (id INT, PRIMARY KEY (id))";
        await setup.ExecuteNonQueryAsync(cancellation.Token);
        setup.CommandText = "INSERT INTO hasrows (id, value) VALUES (1, 'first'), (2, 'second')";
        await setup.ExecuteNonQueryAsync(cancellation.Token);

        await VerifyAsync("SELECT id FROM empty_hasrows", hasRows: false);
        await VerifyAsync("SELECT id FROM hasrows WHERE id = -1", hasRows: false);
        await VerifyAsync("UPDATE hasrows SET value = 'unused' WHERE id = -1 RETURNING id", hasRows: false);
        await VerifyAsync("SELECT id FROM hasrows ORDER BY id", hasRows: true);

        async Task VerifyAsync(string sql, bool hasRows)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = sql;
            using var reader = useAsync
                ? await command.ExecuteReaderAsync(cancellation.Token)
                : command.ExecuteReader();
            Assert.Equal(hasRows, reader.HasRows);
            Assert.Equal(hasRows, reader.HasRows);
            Assert.Equal(1, reader.FieldCount);
            Assert.Equal("id", reader.GetName(0));
            Assert.Equal(typeof(long), reader.GetFieldType(0));
            Assert.Equal(typeof(long), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
            Assert.Throws<InvalidOperationException>(() => reader.GetValue(0));
            Assert.Equal(hasRows, useAsync ? await reader.ReadAsync(cancellation.Token) : reader.Read());
            if (hasRows)
            {
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.True(useAsync ? await reader.ReadAsync(cancellation.Token) : reader.Read());
                Assert.Equal(2L, reader.GetInt64(0));
                Assert.False(useAsync ? await reader.ReadAsync(cancellation.Token) : reader.Read());
            }
            Assert.Equal(hasRows, reader.HasRows);
        }
    }

    [Fact]
    public void FrameHttp2_RestWritesAndFrameSelect_UseExactHttp2()
    {
        using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        connection.Open();
        Assert.Equal(ConnectionState.Open, connection.State);

        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE h2_rows (id INT, value STRING, PRIMARY KEY (id))";
            create.ExecuteNonQuery();
        }
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO h2_rows (id, value) VALUES (1, 'ok')";
            Assert.Equal(1, insert.ExecuteNonQuery());
        }
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT id, value FROM h2_rows WHERE id = 1";
            using var reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("ok", reader.GetString(1));
            Assert.False(reader.Read());
        }

        int h2Port = new Uri(_frameH2Url).Port;
        ObservedRequest[] h2Requests = _requests.Where(r => r.LocalPort == h2Port).ToArray();
        Assert.Contains(h2Requests, r => r.Path == $"/v1/db/{DatabaseName}/sql");
        Assert.Contains(h2Requests, r => r.Path == "/v1/frame");
        Assert.All(h2Requests, r => Assert.Equal("HTTP/2", r.Protocol));
    }

    [Fact]
    public async Task FrameHttp2_VectorParameterAndResults_UseNativeFrameForReads()
    {
        using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE MEASUREMENT vector_contract (source TAG, embedding FIELD VECTOR(3), score FIELD FLOAT)";
            setup.ExecuteNonQuery();
            setup.CommandText = "INSERT INTO vector_contract (time, source, embedding) VALUES (1000, 'a', @embedding)";
            setup.Parameters.AddWithValue("@embedding", new float[] { 1f, -0.5f, 0.25f });
            Assert.Equal(1, setup.ExecuteNonQuery());
            setup.Parameters.Clear();
            setup.CommandText = "INSERT INTO vector_contract (time, source, score) VALUES (2000, 'b', 1.0)";
            Assert.Equal(1, setup.ExecuteNonQuery());
        }

        using (var invalid = connection.CreateCommand())
        {
            invalid.CommandText = "INSERT INTO vector_contract (time, source, embedding) VALUES (3000, 'bad', @embedding)";
            invalid.Parameters.AddWithValue("@embedding", new float[] { 1f, 2f });
            var mismatch = Assert.Throws<SndbServerException>(() => invalid.ExecuteNonQuery());
            Assert.Equal("sql_error", mismatch.Error);
            Assert.Contains("维度不匹配", mismatch.ServerMessage, StringComparison.Ordinal);
            invalid.Parameters.Clear();
            invalid.Parameters.AddWithValue("@embedding", new float[] { float.NaN });
            Assert.Throws<ArgumentException>(() => invalid.ExecuteNonQuery());
            invalid.Parameters.Clear();
            invalid.Parameters.AddWithValue("@embedding", Array.Empty<float>());
            Assert.Throws<ArgumentException>(() => invalid.ExecuteNonQuery());
        }

        foreach (string protocol in new[] { "rest", "frame-http2" })
        {
            using var readConnection = new SndbConnection(ConnectionString(
                protocol == "rest" ? _http11Url : _frameH2Url, protocol));
            readConnection.Open();
            using var query = readConnection.CreateCommand();
            query.CommandText = "SELECT embedding, time, score FROM vector_contract ORDER BY time";
            using var reader = await query.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(typeof(float[]), reader.GetFieldType(0));
            Assert.Equal(typeof(float[]), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
            Assert.Equal(new float[] { 1f, -0.5f, 0.25f }, Assert.IsType<float[]>(reader.GetValue(0)));
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.Equal(DBNull.Value, reader.GetValue(0));
            Assert.False(await reader.ReadAsync());

            using var aggregate = readConnection.CreateCommand();
            aggregate.CommandText = "SELECT centroid(embedding) FROM vector_contract";
            using var aggregateReader = await aggregate.ExecuteReaderAsync();
            Assert.True(await aggregateReader.ReadAsync());
            Assert.Equal(typeof(float[]), aggregateReader.GetFieldType(0));
            Assert.Equal(new float[] { 1f, -0.5f, 0.25f }, Assert.IsType<float[]>(aggregateReader.GetValue(0)));
        }

        var frames = await SendSqlFrameAsync(
            "SELECT * FROM knn(vector_contract, embedding, @query, 1)", SqlFrameCodec.TypedResultVersion,
            new Dictionary<string, object?> { ["query"] = new float[] { 1f, -0.5f, 0.25f } });
        var columns = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(frames[0].Payload).Columns;
        int vectorOrdinal = Array.IndexOf(columns, "embedding");
        Assert.True(vectorOrdinal >= 0);
        var rows = frames.Where(frame => SqlFrameCodec.PeekChunkKind(frame.Payload) == SqlQueryChunkKind.Rows)
            .SelectMany(frame => SqlFrameCodec.DecodeQueryRowsFrame(frame.Payload)).ToArray();
        Assert.Equal(new float[] { 1f, -0.5f, 0.25f }, Assert.IsType<float[]>(Assert.Single(rows)[vectorOrdinal]));

        using var knn = connection.CreateCommand();
        knn.CommandText = "SELECT embedding FROM knn(vector_contract, embedding, @query, 1)";
        knn.Parameters.AddWithValue("@query", new float[] { 1f, -0.5f, 0.25f });
        using var knnReader = await knn.ExecuteReaderAsync();
        Assert.Equal(typeof(float[]), knnReader.GetFieldType(0));
        Assert.True(await knnReader.ReadAsync());
        Assert.Equal(new float[] { 1f, -0.5f, 0.25f }, Assert.IsType<float[]>(knnReader.GetValue(0)));

        int h2Port = new Uri(_frameH2Url).Port;
        ObservedRequest[] requests = _requests.Where(r => r.LocalPort == h2Port).ToArray();
        Assert.Contains(requests, r => r.Path == $"/v1/db/{DatabaseName}/sql");
        Assert.Contains(requests, r => r.Path == "/v1/frame");
        Assert.Equal(SqlFrameCodec.DeclaredResultVersion, requests.Last(r => r.Path == "/v1/frame").ResultVersion);
        Assert.All(requests, r => Assert.Equal("HTTP/2", r.Protocol));

        using (var update = connection.CreateCommand())
        {
            update.CommandText = "UPDATE vector_contract SET embedding = @embedding WHERE source = 'a'";
            update.Parameters.AddWithValue("@embedding", new float[] { 0f, 1f, 0f });
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        using var afterUpdate = connection.CreateCommand();
        afterUpdate.CommandText = "SELECT embedding FROM knn(vector_contract, embedding, @query, 1)";
        afterUpdate.Parameters.AddWithValue("@query", new float[] { 1f, -0.5f, 0.25f });
        using var updatedReader = await afterUpdate.ExecuteReaderAsync();
        Assert.True(await updatedReader.ReadAsync());
        Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(updatedReader.GetValue(0)));
    }

    [Fact]
    public async Task RecursiveCte_RestNdjsonAndFrameHttp2_ReturnSameParameterizedRows()
    {
        using var rest = new SndbConnection(ConnectionString(_http11Url, "rest"));
        rest.Open();
        using (var setup = rest.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE recursive_devices (id INT, parent_id INT, PRIMARY KEY (id))";
            setup.ExecuteNonQuery();
            setup.CommandText = "INSERT INTO recursive_devices (id, parent_id) VALUES (1, NULL), (2, 1), (3, 1), (4, 2)";
            Assert.Equal(4, setup.ExecuteNonQuery());
        }

        const string query = """
            WITH RECURSIVE device_tree (id, depth) AS (
                SELECT id, 0 AS depth FROM recursive_devices WHERE id = @root
                UNION ALL
                SELECT child.id, device_tree.depth + 1 AS depth
                FROM recursive_devices AS child JOIN device_tree ON child.parent_id = device_tree.id
            )
            SELECT id, depth FROM device_tree ORDER BY id DESC LIMIT 2
            """;
        foreach (string protocol in new[] { "rest", "frame-http2" })
        {
            string url = protocol == "rest" ? _http11Url : _frameH2Url;
            using var connection = new SndbConnection(ConnectionString(url, protocol));
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = query;
            command.Parameters.AddWithValue("@root", 1L);
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(4L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.True(reader.Read());
            Assert.Equal(3L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
            Assert.False(reader.Read());
        }

        using var http = new HttpClient { BaseAddress = new Uri(_http11Url) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync(
            $"/v1/db/{DatabaseName}/sql",
            JsonContent.Create(new SqlRequest(query.Replace("@root", "1", StringComparison.Ordinal)),
                ServerJsonContext.Default.SqlRequest));
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        string[] lines = (await response.Content.ReadAsStringAsync())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length); // meta, two rows, end
        Assert.Contains("[4,2]", lines[1], StringComparison.Ordinal);
        Assert.Contains("[3,1]", lines[2], StringComparison.Ordinal);

        int h2Port = new Uri(_frameH2Url).Port;
        Assert.Contains(_requests, request => request.LocalPort == h2Port
            && request.Path == "/v1/frame" && request.Protocol == "HTTP/2");
    }

    /// <summary>关系排名通过真实 REST 与 h2c 保留参数、NULL 排序及空结果整数元数据。</summary>
    /// <param name="protocol">远程 ADO 协议。</param>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task RankingWindows_ParameterizedPeersAndEmptySchema_PreserveTransportInt64(string protocol)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string url = protocol == "rest" ? _http11Url : _frameH2Url;
        await using var connection = new SndbConnection(ConnectionString(url, protocol));
        await connection.OpenAsync(cancellation.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ranking_transport "
            + "(id INT, tenant INT, category INT, score INT, tie INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync(cancellation.Token);
        command.CommandText = """
            INSERT INTO ranking_transport (id, tenant, category, score, tie) VALUES
            (1, 1, NULL, NULL, 2), (2, 1, NULL, NULL, 2),
            (3, 1, NULL, 10, 2), (4, 1, NULL, 10, 1),
            (5, 1, 2, 10, 1), (6, 2, NULL, 5, 1)
            """;
        Assert.Equal(6, await command.ExecuteNonQueryAsync(cancellation.Token));
        command.CommandText = """
            SELECT id,
                   rank() OVER (PARTITION BY tenant, category ORDER BY score ASC, tie DESC) AS ar,
                   dense_rank() OVER (PARTITION BY tenant, category ORDER BY score ASC, tie DESC) AS ad,
                   rank() OVER (PARTITION BY tenant, category ORDER BY score DESC, tie ASC) AS dr,
                   dense_rank() OVER (PARTITION BY tenant, category ORDER BY score DESC, tie ASC) AS dd,
                   ntile(@buckets) OVER (PARTITION BY tenant, category ORDER BY score ASC, tie DESC, id) AS tile
            FROM ranking_transport WHERE id >= @minimum ORDER BY id
            """;
        command.Parameters.AddWithValue("@buckets", 3L);
        command.Parameters.AddWithValue("@minimum", 1L);
        await using (var reader = await command.ExecuteReaderAsync(cancellation.Token))
        {
            AssertRankingTransportSchema(reader);
            long[][] expectedRows =
            [
                [1L, 1L, 1L, 3L, 3L, 1L],
                [2L, 1L, 1L, 3L, 3L, 1L],
                [3L, 3L, 2L, 2L, 2L, 2L],
                [4L, 4L, 3L, 1L, 1L, 3L],
                [5L, 1L, 1L, 1L, 1L, 1L],
                [6L, 1L, 1L, 1L, 1L, 1L],
            ];
            foreach (long[] expected in expectedRows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Assert.True(await reader.ReadAsync(cancellation.Token));
                for (int column = 0; column < expected.Length; column++)
                {
                    Assert.Equal(expected[column], reader.GetInt64(column));
                    Assert.Equal((int)expected[column], reader.GetInt32(column));
                }
            }
            Assert.False(await reader.ReadAsync(cancellation.Token));
        }

        command.Parameters["@minimum"].Value = long.MaxValue;
        await using (var empty = await command.ExecuteReaderAsync(cancellation.Token))
        {
            AssertRankingTransportSchema(empty);
            Assert.False(await empty.ReadAsync(cancellation.Token));
        }

        command.Parameters["@minimum"].Value = 1L;
        command.Parameters["@buckets"].Value = 0L;
        var invalid = await Assert.ThrowsAsync<SndbServerException>(() =>
            command.ExecuteReaderAsync(cancellation.Token));
        Assert.Equal("sql_error", invalid.Error);
        Assert.Contains("ntile()", invalid.ServerMessage, StringComparison.Ordinal);

        int port = new Uri(url).Port;
        ObservedRequest[] observed = _requests.Where(request => request.LocalPort == port).ToArray();
        Assert.Contains(observed, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        Assert.All(observed, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        if (protocol == "frame-http2")
            Assert.Contains(observed, request => request.Path == "/v1/frame");
    }

    private static void AssertRankingTransportSchema(DbDataReader reader)
    {
        Assert.Equal(["id", "ar", "ad", "dr", "dd", "tile"],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
        var schema = Assert.IsType<DataTable>(reader.GetSchemaTable());
        for (int column = 1; column < 6; column++)
        {
            Assert.Equal(typeof(long), reader.GetFieldType(column));
            Assert.Equal(typeof(long), schema.Rows[column][SchemaTableColumn.DataType]);
            Assert.Equal((int)DbType.Int64, schema.Rows[column][SchemaTableColumn.ProviderType]);
            Assert.False((bool)schema.Rows[column][SchemaTableColumn.AllowDBNull]);
        }
    }

    [Fact]
    public async Task FrameHttp2_Int64Boundary_AsyncReadAndBigIntegerPreflight()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_int64_boundary (id INT, exact_text STRING, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO h2_int64_boundary (id, exact_text) VALUES "
            + "(-9223372036854775808, '9223372036854775808'), "
            + "(9223372036854775807, '9223372036854775807')";
        Assert.Equal(2, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT id, exact_text FROM h2_int64_boundary ORDER BY id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(long.MinValue, reader.GetInt64(0));
            Assert.Equal(typeof(long), reader.GetFieldType(0));
            Assert.Equal("9223372036854775808", reader.GetString(1));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(long.MaxValue, reader.GetInt64(0));
            Assert.False(await reader.ReadAsync());
        }

        command.CommandText = "SELECT @value";
        var requestsBeforeBinding = _requests.Count;
        var error = Assert.Throws<SndbParameterTypeException>(() =>
            command.Parameters.AddWithValue("@value", BigInteger.Parse("9223372036854775808")));
        Assert.Equal(SndbParameterTypeException.BigIntegerUnsupportedCode, error.Code);
        Assert.Equal(requestsBeforeBinding, _requests.Count);
        Assert.Contains(_requests, request => request.Path == "/v1/frame" && request.Protocol == "HTTP/2");
    }

    [Fact]
    public async Task FrameHttp2_InsertReturning_AsyncTransactionRollbackKeepsGeneratedSchema()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_returning_contract (id INT AUTO_INCREMENT, name STRING NOT NULL DEFAULT 'generated', version INT ROWVERSION, note STRING NULL, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO h2_returning_contract (name) VALUES (@name) RETURNING id, name, version, note";
            command.Parameters.AddWithValue("@name", "pump");
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.Equal(["id", "name", "version", "note"],
                    Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
                Assert.Equal(typeof(long), reader.GetFieldType(0));
                Assert.Equal(typeof(string), reader.GetFieldType(3));
                var schema = Assert.IsType<DataTable>(reader.GetSchemaTable());
                Assert.False((bool)schema.Rows[0][System.Data.Common.SchemaTableColumn.AllowDBNull]);
                Assert.True((bool)schema.Rows[0][System.Data.Common.SchemaTableOptionalColumn.IsAutoIncrement]);
                Assert.True((bool)schema.Rows[2]["IsRowVersion"]);
                Assert.True((bool)schema.Rows[3][System.Data.Common.SchemaTableColumn.AllowDBNull]);
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal("pump", reader.GetString(1));
                Assert.Equal(1L, reader.GetInt64(2));
                Assert.Equal(DBNull.Value, reader.GetValue(3));
                Assert.False(await reader.ReadAsync());
                Assert.Equal(1, reader.RecordsAffected);
            }
            await transaction.RollbackAsync();
        }

        command.Transaction = null;
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO h2_returning_contract (name) SELECT name FROM h2_returning_contract WHERE id = 404 RETURNING id, name, version, note";
        await using (var empty = await command.ExecuteReaderAsync())
        {
            Assert.Equal(["id", "name", "version", "note"],
                Enumerable.Range(0, empty.FieldCount).Select(empty.GetName).ToArray());
            Assert.Equal([typeof(long), typeof(string), typeof(long), typeof(string)],
                Enumerable.Range(0, empty.FieldCount).Select(empty.GetFieldType).ToArray());
            var schema = Assert.IsType<DataTable>(empty.GetSchemaTable());
            Assert.True((bool)schema.Rows[0][System.Data.Common.SchemaTableOptionalColumn.IsAutoIncrement]);
            Assert.True((bool)schema.Rows[2]["IsRowVersion"]);
            Assert.True((bool)schema.Rows[3][System.Data.Common.SchemaTableColumn.AllowDBNull]);
            Assert.False(await empty.ReadAsync());
            Assert.Equal(0, empty.RecordsAffected);
        }
        command.CommandText = "SELECT COUNT(*) FROM h2_returning_contract";
        Assert.Equal(0L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
        Assert.Contains(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql" && request.Protocol == "HTTP/2");
    }

    [Fact]
    public async Task FrameHttp2_UpdateDeleteReturning_EmptyResultsKeepDeclaredSchema()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_update_delete_schema (id INT, note STRING NULL, rv INT ROWVERSION, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO h2_update_delete_schema (id, note) VALUES (1, 'before')";
        await command.ExecuteNonQueryAsync();

        command.CommandText = "UPDATE h2_update_delete_schema SET note = 'after' WHERE id = 1 RETURNING id, note, rv";
        await using (var updated = await command.ExecuteReaderAsync())
        {
            Assert.Equal([typeof(long), typeof(string), typeof(long)],
                Enumerable.Range(0, updated.FieldCount).Select(updated.GetFieldType));
            Assert.True((bool)Assert.IsType<DataTable>(updated.GetSchemaTable()).Rows[2]["IsRowVersion"]);
            Assert.True(await updated.ReadAsync());
            Assert.Equal("after", updated.GetString(1));
            Assert.Equal(2L, updated.GetInt64(2));
            Assert.False(await updated.ReadAsync());
            Assert.Equal(1, updated.RecordsAffected);
        }

        command.CommandText = "DELETE FROM h2_update_delete_schema WHERE id = 404 RETURNING id, note, rv";
        await using var empty = await command.ExecuteReaderAsync();
        Assert.Equal([typeof(long), typeof(string), typeof(long)],
            Enumerable.Range(0, empty.FieldCount).Select(empty.GetFieldType));
        var schema = Assert.IsType<DataTable>(empty.GetSchemaTable());
        Assert.False((bool)schema.Rows[0][System.Data.Common.SchemaTableColumn.AllowDBNull]);
        Assert.True((bool)schema.Rows[1][System.Data.Common.SchemaTableColumn.AllowDBNull]);
        Assert.True((bool)schema.Rows[2]["IsRowVersion"]);
        Assert.False(await empty.ReadAsync());
        Assert.Equal(0, empty.RecordsAffected);
        Assert.Contains(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql" && request.Protocol == "HTTP/2");
    }

    [Fact]
    public async Task FrameHttp2_InsertReturning_AsyncMethodsAndCompositeKeyErrorStayConsistent()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_returning_composite (tenant INT, id INT, name STRING, PRIMARY KEY (tenant, id))";
        await command.ExecuteNonQueryAsync();

        command.CommandText = "INSERT INTO h2_returning_composite (tenant, id, name) VALUES (1, 1, 'first') RETURNING id";
        Assert.Equal(1L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
        command.CommandText = "INSERT INTO h2_returning_composite (tenant, id, name) VALUES (1, 2, 'second') RETURNING id";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        command.CommandText = "INSERT INTO h2_returning_composite (tenant, id, name) VALUES (2, 1, 'partial'), (1, 1, 'duplicate') RETURNING id";
        var error = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(TableConstraintException.UniqueViolation, error.Error);

        command.CommandText = "SELECT COUNT(*) FROM h2_returning_composite";
        Assert.Equal(2L, Assert.IsType<long>(await command.ExecuteScalarAsync()));
        Assert.Contains(_requests, request => request.Path == $"/v1/db/{DatabaseName}/sql" && request.Protocol == "HTTP/2");
    }

    /// <summary>Frame HTTP/2 远程连接在事务内外均返回 INSERT、UPDATE、DELETE 的真实影响行数。</summary>
    [Fact]
    public async Task FrameHttp2_DmlRecordsAffected_IsConsistentInsideAndOutsideTransaction()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_dml_counts (id INT, enabled BOOL, PRIMARY KEY (id))";
        Assert.Equal(0, await command.ExecuteNonQueryAsync());
        command.CommandText = "INSERT INTO h2_dml_counts (id, enabled) VALUES (1, false), (2, false), (3, true)";
        Assert.Equal(3, command.ExecuteNonQuery());
        command.CommandText = "UPDATE h2_dml_counts SET enabled = true WHERE id <= 2";
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
        command.CommandText = "DELETE FROM h2_dml_counts WHERE id = 404";
        Assert.Equal(0, command.ExecuteNonQuery());

        await using var transaction = await connection.BeginTransactionAsync();
        command.Transaction = transaction;
        command.CommandText = "UPDATE h2_dml_counts SET enabled = false WHERE enabled = true";
        Assert.Equal(3, await command.ExecuteNonQueryAsync());
        command.CommandText = "UPDATE h2_dml_counts SET enabled = true WHERE id = 404";
        Assert.Equal(0, command.ExecuteNonQuery());
        command.CommandText = "DELETE FROM h2_dml_counts WHERE id >= 2";
        Assert.Equal(2, await command.ExecuteNonQueryAsync());
        await transaction.CommitAsync();

        int h2Port = new Uri(_frameH2Url).Port;
        ObservedRequest[] h2Requests = _requests.Where(request => request.LocalPort == h2Port).ToArray();
        Assert.Contains(h2Requests, request => request.Path == $"/v1/db/{DatabaseName}/sql/transactions");
        Assert.Contains(h2Requests, request => request.Path.StartsWith(
            $"/v1/db/{DatabaseName}/sql/transactions/", StringComparison.Ordinal)
            && request.Path.EndsWith("/sql", StringComparison.Ordinal));
        Assert.Contains(h2Requests, request => request.Path.EndsWith("/commit", StringComparison.Ordinal));
        Assert.All(h2Requests, request => Assert.Equal("HTTP/2", request.Protocol));
    }

    [Fact]
    public async Task FrameHttp2_InsertSelectAsync_BindsParametersAndReturnsGeneratedRows()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE copy_source (id INT, value STRING, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "CREATE TABLE copy_target (id INT AUTO_INCREMENT, value STRING, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO copy_source (id, value) VALUES (1, 'first'), (2, 'second')";
        Assert.Equal(2, await command.ExecuteNonQueryAsync());

        command.CommandText = "INSERT INTO copy_target (value) "
            + "SELECT value FROM copy_source WHERE id >= @min ORDER BY id RETURNING id, value";
        command.Parameters.AddWithValue("@min", 2L);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal("second", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
            Assert.Equal(1, reader.RecordsAffected);
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT id, value FROM copy_target";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("second", reader.GetString(1));
            Assert.False(await reader.ReadAsync());
        }

        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 51, DatabaseName,
            "INSERT INTO copy_target (value) SELECT value FROM copy_source RETURNING id");
        using var http = new HttpClient();
        using var frameRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_frameH2Url), "/v1/frame"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(writer.WrittenMemory.ToArray()),
        };
        frameRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        frameRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var frameResponse = await http.SendAsync(frameRequest);
        Assert.Equal(HttpStatusCode.OK, frameResponse.StatusCode);
        Assert.Equal(HttpVersion.Version20, frameResponse.Version);
        ReadOnlySequence<byte> buffer = new(await frameResponse.Content.ReadAsByteArrayAsync());
        Assert.True(FrameCodec.TryReadFrame(ref buffer, out var header, out var payload));
        Assert.True(header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(payload.ToArray());
        Assert.Equal("bad_request", code);
        Assert.False(FrameCodec.TryReadFrame(ref buffer, out _, out _));

        command.CommandText = "SELECT COUNT(*) FROM copy_target";
        Assert.Equal(1L, Assert.IsType<long>(await command.ExecuteScalarAsync()));

        int h2Port = new Uri(_frameH2Url).Port;
        ObservedRequest[] requests = _requests.Where(request => request.LocalPort == h2Port).ToArray();
        Assert.Contains(requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        Assert.Contains(requests, request => request.Path == "/v1/frame");
        Assert.All(requests, request => Assert.Equal("HTTP/2", request.Protocol));
    }

    [Fact]
    public async Task FrameHttp2_EmptyAndAllNullSelect_UsesDeclaredMetaBeforeRows()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_typed_select (id INT, value INT NULL, amount DECIMAL(20,6) NULL, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO h2_typed_select (id, value, amount) VALUES (1, NULL, NULL)";
        await command.ExecuteNonQueryAsync();

        command.CommandText = "SELECT id AS key_alias, value AS nullable_alias FROM h2_typed_select WHERE id = 404";
        await using (var empty = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(long), empty.GetFieldType(0));
            Assert.Equal(typeof(long), empty.GetFieldType(1));
            var schema = Assert.IsType<DataTable>(empty.GetSchemaTable());
            Assert.True((bool)schema.Rows[0][System.Data.Common.SchemaTableColumn.IsKey]);
            Assert.False((bool)schema.Rows[0][System.Data.Common.SchemaTableColumn.AllowDBNull]);
            Assert.True((bool)schema.Rows[1][System.Data.Common.SchemaTableColumn.AllowDBNull]);
            Assert.False(await empty.ReadAsync());
        }

        command.CommandText = "SELECT value AS nullable_alias FROM h2_typed_select";
        await using (var allNull = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(long), allNull.GetFieldType(0));
            Assert.True(await allNull.ReadAsync());
            Assert.Equal(DBNull.Value, allNull.GetValue(0));
            Assert.False(await allNull.ReadAsync());
        }

        command.CommandText = "SELECT COUNT(*) AS total, MIN(value) AS minimum, "
            + "AVG(amount) AS average, SUM(value) AS summed FROM h2_typed_select WHERE id = 404";
        await using var aggregate = await command.ExecuteReaderAsync();
        Assert.Equal([typeof(long), typeof(long), typeof(decimal), typeof(long)],
            Enumerable.Range(0, aggregate.FieldCount).Select(aggregate.GetFieldType).ToArray());
        Assert.True(await aggregate.ReadAsync());
        Assert.Equal(0L, aggregate.GetInt64(0));
        Assert.Equal(DBNull.Value, aggregate.GetValue(1));
        Assert.False(await aggregate.ReadAsync());
        Assert.Contains(_requests, request => request.Path == "/v1/frame" && request.Protocol == "HTTP/2");
    }

    [Fact]
    public async Task FrameHttp2_DateTimeTimeAndBlob_SelectValuesMatchDeclaredTypes()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_typed_values (id INT, occurred_at DATETIME NULL, starts TIME NULL, payload BLOB NULL, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO h2_typed_values (id, occurred_at, starts, payload) VALUES "
            + "(1, '2026-09-25T12:34:56Z', '23:59:59.1234567', 'AP9h'), (2, NULL, NULL, NULL)";
        Assert.Equal(2, await command.ExecuteNonQueryAsync());

        command.CommandText = "SELECT occurred_at, starts, payload FROM h2_typed_values ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal([typeof(DateTime), typeof(TimeOnly), typeof(byte[])],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray());
        Assert.True(await reader.ReadAsync());
        Assert.Equal(new DateTime(2026, 9, 25, 12, 34, 56, DateTimeKind.Utc),
            Assert.IsType<DateTime>(reader.GetValue(0)));
        Assert.Equal(new TimeOnly(23, 59, 59, 123).Add(TimeSpan.FromTicks(4567)),
            Assert.IsType<TimeOnly>(reader.GetValue(1)));
        Assert.Equal(new byte[] { 0, 255, 97 }, Assert.IsType<byte[]>(reader.GetValue(2)));
        Assert.Equal(3, reader.GetBytes(2, 0, null, 0, 0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal([typeof(DateTime), typeof(TimeOnly), typeof(byte[])],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToArray());
        Assert.All(Enumerable.Range(0, reader.FieldCount), ordinal => Assert.Equal(DBNull.Value, reader.GetValue(ordinal)));
        Assert.False(await reader.ReadAsync());
        Assert.Contains(_requests, request => request.Path == "/v1/frame" && request.Protocol == "HTTP/2");
    }

    [Fact]
    public async Task FrameHttp2_LegacyAndTypedSqlResults_NegotiateMetaAndDecimalTag()
    {
        await using var connection = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE h2_result_versions (id INT, amount DECIMAL(24,6), PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO h2_result_versions (id, amount) VALUES "
            + "(1, '9007199254740993.125000')";
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        const string sql = "SELECT amount FROM h2_result_versions WHERE id = 1";
        var legacy = await SendSqlFrameAsync(sql, resultVersion: null);
        Assert.Equal("amount", Assert.Single(DecodeLegacySqlMeta(legacy[0].Payload)));
        Assert.Equal(Convert.ToDouble(9007199254740993.125000m),
            DecodeLegacySingleFloatRow(legacy[1].Payload));
        Assert.Equal(1L, SqlFrameCodec.DecodeQueryEndFrame(legacy[2].Payload).RowCount);
        var explicitLegacy = await SendSqlFrameAsync(sql, "1");
        Assert.Equal("amount", Assert.Single(DecodeLegacySqlMeta(explicitLegacy[0].Payload)));
        Assert.Equal(Convert.ToDouble(9007199254740993.125000m),
            DecodeLegacySingleFloatRow(explicitLegacy[1].Payload));

        var typed = await SendSqlFrameAsync(sql, SqlFrameCodec.TypedResultVersion);
        Assert.Throws<InvalidDataException>(() => DecodeLegacySqlMeta(typed[0].Payload));
        Assert.Throws<InvalidDataException>(() => DecodeLegacySingleFloatRow(typed[1].Payload));
        var (columns, info) = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(typed[0].Payload);
        Assert.Equal("amount", Assert.Single(columns));
        Assert.Equal(TableColumnType.Decimal, Assert.Single(Assert.IsType<SelectColumnInfo[]>(info)).DataType);
        Assert.Equal(9007199254740993.125000m,
            Assert.IsType<decimal>(SqlFrameCodec.DecodeQueryRowsFrame(typed[1].Payload)[0][0]));

        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.Equal(typeof(decimal), reader.GetFieldType(0));
        Assert.True(await reader.ReadAsync());
        Assert.Equal(9007199254740993.125000m, Assert.IsType<decimal>(reader.GetValue(0)));
        Assert.False(await reader.ReadAsync());

        var declared = await SendSqlFrameAsync(sql, SqlFrameCodec.DeclaredResultVersion);
        var declaration = Assert.Single(SqlFrameCodec.DecodeQueryMetaFrameWithInfo(declared[0].Payload).ColumnInfo!);
        Assert.Equal((byte)24, declaration.NumericPrecision);
        Assert.Equal((byte)6, declaration.NumericScale);
        var unsupported = await SendSqlFrameAsync(sql, "4");
        Assert.True(Assert.Single(unsupported).Header.IsError);
        Assert.Equal("unsupported_result_version", FrameCodec.ReadErrorPayload(unsupported[0].Payload).Code);
        Assert.Contains(_requests, request => request.Path == "/v1/frame" && request.Protocol == "HTTP/2");
    }

    [Fact]
    public async Task OldSqlFrameServer_IgnoredVersion_AutoAndForcedReportWithoutSqlReplay()
    {
        _suppressSqlResultVersion = true;
        using var auto = new SndbConnection(ConnectionString(_http11Url, "auto"));
        auto.Open();
        using (var setup = auto.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE h2_old_result_version (id INT, amount DECIMAL(24,6), PRIMARY KEY (id))";
            setup.ExecuteNonQuery();
            setup.CommandText = "INSERT INTO h2_old_result_version (id, amount) VALUES "
                + "(1, '9007199254740993.125000')";
            Assert.Equal(1, setup.ExecuteNonQuery());
        }

        int before = _requests.Count;
        using (var query = auto.CreateCommand())
        {
            query.CommandText = "SELECT amount FROM h2_old_result_version WHERE id = 1";
            var autoError = Assert.Throws<SndbServerException>(() => query.ExecuteReader());
            Assert.Equal("frame_sql_result_version_unsupported", autoError.Error);
        }
        ObservedRequest[] fallbackRequests = _requests.ToArray().Skip(before).ToArray();
        Assert.Equal("/v1/frame", Assert.Single(fallbackRequests).Path);

        await using var forced = new SndbConnection(ConnectionString(_frameH2Url, "frame-http2"));
        await forced.OpenAsync();
        await using var forcedQuery = forced.CreateCommand();
        forcedQuery.CommandText = "SELECT amount FROM h2_old_result_version WHERE id = 1";
        var error = await Assert.ThrowsAsync<SndbServerException>(
            async () => await forcedQuery.ExecuteReaderAsync());
        Assert.Equal("frame_sql_result_version_unsupported", error.Error);
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("auto")]
    public void RestAndAuto_KeepDefaultHttp11Behavior(string protocol)
    {
        string table = "http11_" + protocol;
        using var connection = new SndbConnection(ConnectionString(_http11Url, protocol));
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE {table} (id INT, PRIMARY KEY (id))";
            create.ExecuteNonQuery();
        }
        using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT id FROM {table}";
            using var reader = select.ExecuteReader();
            Assert.False(reader.Read());
        }

        int http11Port = new Uri(_http11Url).Port;
        ObservedRequest[] requests = _requests
            .Where(r => r.LocalPort == http11Port)
            .ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, r => Assert.Equal("HTTP/1.1", r.Protocol));
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task UuidFunction_PerRowReturningAndTransactions_UseRealTransport(string protocol)
    {
        string baseUrl = protocol == "rest" ? _http11Url : _frameH2Url;
        await using var connection = new SndbConnection(ConnectionString(baseUrl, protocol));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE uuid_contract (id INT, guid_value STRING NULL, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT uuid() FROM uuid_contract";
        await using (var empty = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(string), empty.GetFieldType(0));
            Assert.False(empty.GetColumnSchema().Single().AllowDBNull);
            Assert.False(await empty.ReadAsync());
        }
        command.CommandText = "INSERT INTO uuid_contract (id, guid_value) VALUES (1, NULL), (2, NULL), (3, NULL), (4, NULL)";
        Assert.Equal(4, await command.ExecuteNonQueryAsync());
        command.CommandText = "SELECT uuid(), UUID() FROM uuid_contract ORDER BY id";
        var projected = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            for (int row = 0; row < 4; row++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.True(projected.Add(ValidateGuid(reader.GetString(0))));
                Assert.True(projected.Add(ValidateGuid(reader.GetString(1))));
            }
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(8, projected.Count);

        var discarded = new HashSet<string>(StringComparer.Ordinal);
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE uuid_contract SET guid_value = uuid() WHERE guid_value IS NULL RETURNING id, guid_value";
            await using (var reader = await command.ExecuteReaderAsync())
            {
                for (int row = 0; row < 4; row++)
                {
                    Assert.True(await reader.ReadAsync());
                    Assert.True(discarded.Add(ValidateGuid(reader.GetString(1))));
                }
                Assert.False(await reader.ReadAsync());
                Assert.Equal(4, reader.RecordsAffected);
            }
            await transaction.RollbackAsync();
        }
        command.Transaction = null;
        command.CommandText = "SELECT COUNT(*) FROM uuid_contract WHERE guid_value IS NULL";
        Assert.Equal(4L, Assert.IsType<long>(await command.ExecuteScalarAsync()));

        var committed = new Dictionary<long, string>();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE uuid_contract SET guid_value = uuid() WHERE id >= @minimum RETURNING id, guid_value";
            command.Parameters.AddWithValue("@minimum", 1L);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                for (int row = 0; row < 4; row++)
                {
                    Assert.True(await reader.ReadAsync());
                    committed.Add(reader.GetInt64(0), ValidateGuid(reader.GetString(1)));
                }
                Assert.False(await reader.ReadAsync());
                Assert.Equal(4, reader.RecordsAffected);
            }
            Assert.Equal(4, committed.Values.Distinct(StringComparer.Ordinal).Count());
            Assert.Empty(committed.Values.Intersect(discarded, StringComparer.Ordinal));
            await transaction.CommitAsync();
        }
        command.Transaction = null;
        command.Parameters.Clear();
        command.CommandText = "SELECT id, guid_value FROM uuid_contract ORDER BY id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            for (int row = 0; row < 4; row++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(committed[reader.GetInt64(0)], reader.GetString(1));
            }
            Assert.False(await reader.ReadAsync());
        }
        command.CommandText = "SELECT uuid(NULL) FROM uuid_contract WHERE id < 0";
        var invalidArity = await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync());
        Assert.Contains("0 个参数", invalidArity.ServerMessage, StringComparison.Ordinal);
        command.CommandText = "ALTER TABLE uuid_contract ADD COLUMN generated STRING DEFAULT lower(uuid())";
        var invalidDefault = await Assert.ThrowsAsync<SndbServerException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("不支持列 DEFAULT", invalidDefault.ServerMessage, StringComparison.Ordinal);

        int port = new Uri(baseUrl).Port;
        var observed = _requests.Where(request => request.LocalPort == port).ToArray();
        Assert.NotEmpty(observed);
        Assert.All(observed, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        Assert.Contains(observed, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        if (protocol == "frame-http2")
            Assert.Contains(observed, request => request.Path == "/v1/frame");

        static string ValidateGuid(string text)
        {
            Assert.True(Guid.TryParseExact(text, "D", out var guid));
            Assert.NotEqual(Guid.Empty, guid);
            Assert.Equal(guid.ToString("D"), text);
            return text;
        }
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task AggregateCast_ValuesNullMetadataAndOverflow_UseRealTransport(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string baseUrl = protocol == "rest" ? _http11Url : _frameH2Url;
        await using var connection = new SndbConnection(ConnectionString(baseUrl, protocol));
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE cast_rows (id INT, tenant INT, value INT NULL, amount DECIMAL(28,6), PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO cast_rows (id, tenant, value, amount) VALUES "
            + "(1, 1, 3, '9007199254740993.125'), (2, 1, 5, '0.875'), (3, 2, 7, '0.1'), (4, 3, NULL, NULL)";
        Assert.Equal(4, await command.ExecuteNonQueryAsync(deadline.Token));
        const string projection = "SELECT CAST(SUM(value) AS INT), CAST(AVG(value) AS FLOAT), "
            + "CAST(MIN(value) AS DECIMAL), CAST(MAX(value) AS STRING), CAST(COUNT(value) AS INT), "
            + "CAST(COUNT(*) AS FLOAT), CAST(SUM(amount) AS DECIMAL) FROM cast_rows ";
        Type[] types = [typeof(long), typeof(double), typeof(decimal), typeof(string), typeof(long), typeof(double), typeof(decimal)];
        command.CommandText = projection + "WHERE tenant = 1";
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal(types, Enumerable.Range(0, 7).Select(reader.GetFieldType).ToArray());
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(4d, reader.GetFieldValue<double>(1));
            Assert.Equal(4d, reader.GetDouble(1));
            Assert.Equal(2d, reader.GetFieldValue<double>(5));
            Assert.Equal(new object?[] { 8L, 4d, 3m, "5", 2L, 2d, 9007199254740994m },
                Enumerable.Range(0, 7).Select(reader.GetValue).ToArray());
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        command.CommandText = projection + "WHERE id < 0";
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal(types, Enumerable.Range(0, 7).Select(reader.GetFieldType).ToArray());
            Assert.Equal(new bool?[] { true, true, true, true, false, false, true },
                reader.GetColumnSchema().Select(column => column.AllowDBNull).ToArray());
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(0d, reader.GetFieldValue<double>(5));
            Assert.Equal(0d, reader.GetDouble(5));
            Assert.Equal(new object?[] { DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, 0L, 0d, DBNull.Value },
                Enumerable.Range(0, 7).Select(reader.GetValue).ToArray());
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        command.CommandText = "SELECT tenant, CAST(SUM(value) AS INT), CAST(AVG(value) AS FLOAT), "
            + "CAST(SUM(amount) AS DECIMAL), concat('n=', CAST(COUNT(*) AS STRING)) FROM cast_rows GROUP BY tenant "
            + "HAVING CAST(SUM(value) AS INT) > 7 ORDER BY tenant";
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
        {
            Assert.True(await reader.ReadAsync(deadline.Token));
            Assert.Equal(new object?[] { 1L, 8L, 4d, 9007199254740994m, "n=2" },
                Enumerable.Range(0, 5).Select(reader.GetValue).ToArray());
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        command.CommandText = "SELECT CAST(SUM(amount) + CAST('0.1' AS DECIMAL) AS DECIMAL) FROM cast_rows WHERE tenant = 1 "
            + "HAVING CAST(SUM(amount) AS DECIMAL) = CAST('9007199254740994' AS DECIMAL)";
        Assert.Equal(9007199254740994.1m, Assert.IsType<decimal>(await command.ExecuteScalarAsync(deadline.Token)));
        command.CommandText = "SELECT CAST(AVG(value) AS FLOAT) FROM cast_rows WHERE tenant = 1";
        Assert.Equal(4d, Assert.IsType<double>(await command.ExecuteScalarAsync(deadline.Token)));
        command.CommandText = "SELECT CAST(SUM(value) AS INT) FROM cast_rows WHERE tenant = 3";
        Assert.Null(await command.ExecuteScalarAsync(deadline.Token));
        command.CommandText = "SELECT tenant, CAST(SUM(amount) AS DECIMAL) FROM cast_rows WHERE id < 0 GROUP BY tenant";
        await using (var reader = await command.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal(typeof(decimal), reader.GetFieldType(1));
            Assert.True(reader.GetColumnSchema()[1].AllowDBNull);
            Assert.False(await reader.ReadAsync(deadline.Token));
        }
        command.CommandText = "CREATE TABLE cast_bigints (id INT, value INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO cast_bigints (id, value) VALUES (1, 9223372036854775807), (2, 1)";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "SELECT CAST(SUM(value) AS DECIMAL) FROM cast_bigints";
        var aggregateOverflow = await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync(deadline.Token));
        Assert.Contains("SUM(INT) 的 Int64 累加发生溢出", aggregateOverflow.ServerMessage, StringComparison.Ordinal);
        command.CommandText = "SELECT CAST(SUM(CAST(value AS DECIMAL)) AS DECIMAL) FROM cast_bigints";
        Assert.Equal(9223372036854775808m, Assert.IsType<decimal>(await command.ExecuteScalarAsync(deadline.Token)));
        command.CommandText = "SELECT CAST(SUM(CAST(value AS DECIMAL)) AS INT) FROM cast_bigints";
        var castOverflow = await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync(deadline.Token));
        Assert.Contains("Int64", castOverflow.ServerMessage, StringComparison.Ordinal);
        command.CommandText = "CREATE TABLE cast_decimal_bounds (id INT, amount DECIMAL(29,0), PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO cast_decimal_bounds (id, amount) VALUES (1, '79228162514264337593543950335'), (2, '1')";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "SELECT CAST(MAX(amount) AS DECIMAL) FROM cast_decimal_bounds";
        Assert.Equal(decimal.MaxValue, Assert.IsType<decimal>(await command.ExecuteScalarAsync(deadline.Token)));
        command.CommandText = "SELECT CAST(SUM(amount) AS DECIMAL) FROM cast_decimal_bounds";
        var decimalOverflow = await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync(deadline.Token));
        Assert.Contains("DECIMAL 聚合发生溢出", decimalOverflow.ServerMessage, StringComparison.Ordinal);

        int port = new Uri(baseUrl).Port;
        var observed = _requests.Where(request => request.LocalPort == port).ToArray();
        Assert.NotEmpty(observed);
        Assert.All(observed, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        Assert.Contains(observed, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        if (protocol == "frame-http2") Assert.Contains(observed, request => request.Path == "/v1/frame");
    }

    /// <summary>真实 REST 与 h2c ADO 保持移位参数、UPDATE、NULL 和空结果 Int64 元数据一致。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public void ShiftFunctions_RealTransport_PreservesValuesAndDeclaredMetadata(string protocol)
    {
        string baseUrl = protocol == "frame-http2" ? _frameH2Url : _http11Url;
        using var connection = new SndbConnection(ConnectionString(baseUrl, protocol));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE shift_bits (id INT, flags INT, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT shift_left(flags, 1) AS shifted FROM shift_bits";
        using (var empty = command.ExecuteReader())
        {
            Assert.Equal(typeof(long), empty.GetFieldType(0));
            Assert.False(empty.Read());
        }
        command.CommandText = "INSERT INTO shift_bits(id,flags) VALUES(1,6),(2,NULL)";
        Assert.Equal(2, command.ExecuteNonQuery());
        var count = command.CreateParameter();
        count.ParameterName = "@count";
        count.Value = 1L;
        command.Parameters.Add(count);
        command.CommandText = "UPDATE shift_bits SET flags=shift_left(flags,@count) WHERE shift_right(flags,@count)=3";
        Assert.Equal(1, command.ExecuteNonQuery());
        command.CommandText = "SELECT id,shift_right(flags,@count) AS shifted FROM shift_bits ORDER BY id";
        using (var rows = command.ExecuteReader())
        {
            Assert.Equal(typeof(long), rows.GetFieldType(1));
            Assert.True(rows.Read());
            Assert.Equal(6L, rows.GetInt64(1));
            Assert.True(rows.Read());
            Assert.True(rows.IsDBNull(1));
            Assert.False(rows.Read());
        }
        command.CommandText = "SELECT shift_right(flags,@count) FROM shift_bits WHERE flags IS NULL";
        using var missing = command.ExecuteReader();
        Assert.Equal(typeof(long), missing.GetFieldType(0));
        Assert.True(missing.Read());
        Assert.True(missing.IsDBNull(0));
    }

    /// <summary>真实 REST 与 h2c ADO 保持填充参数、UTF-16 宽度、NULL 和空结果 STRING 元数据一致。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public void PaddingFunctions_RealTransport_PreservesTextAndDeclaredMetadata(string protocol)
    {
        string baseUrl = protocol == "frame-http2" ? _frameH2Url : _http11Url;
        using var connection = new SndbConnection(ConnectionString(baseUrl, protocol));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE padded_codes (id INT, code STRING, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT lpad(code,4), rpad(code,4) FROM padded_codes";
        using (var empty = command.ExecuteReader())
        {
            Assert.Equal(typeof(string), empty.GetFieldType(0));
            Assert.Equal(typeof(string), empty.GetFieldType(1));
            Assert.False(empty.Read());
        }
        command.CommandText = "INSERT INTO padded_codes(id,code) VALUES(1,'7'),(2,NULL),(3,'😀'),(4,'abcdef')";
        Assert.Equal(4, command.ExecuteNonQuery());
        var width = command.CreateParameter();
        width.ParameterName = "@width";
        width.Value = 4L;
        command.Parameters.Add(width);
        var padding = command.CreateParameter();
        padding.ParameterName = "@padding";
        padding.Value = "ab";
        command.Parameters.Add(padding);
        command.CommandText = "SELECT lpad(code,@width,@padding),rpad(code,@width,@padding) FROM padded_codes WHERE rpad(code,@width,@padding)='7aba'";
        using (var row = command.ExecuteReader())
        {
            Assert.True(row.Read());
            Assert.Equal("aba7", row.GetString(0));
            Assert.Equal("7aba", row.GetString(1));
            Assert.False(row.Read());
        }
        command.CommandText = "SELECT lpad(code,@width,@padding),rpad(code,@width,@padding) FROM padded_codes WHERE code IS NULL";
        using (var missing = command.ExecuteReader())
        {
            Assert.Equal(typeof(string), missing.GetFieldType(0));
            Assert.Equal(typeof(string), missing.GetFieldType(1));
            Assert.True(missing.Read());
            Assert.True(missing.IsDBNull(0));
            Assert.True(missing.IsDBNull(1));
        }
        command.CommandText = "SELECT lpad(code,@width,@padding),rpad(code,@width,@padding) FROM padded_codes WHERE id=3";
        using (var unicode = command.ExecuteReader())
        {
            Assert.True(unicode.Read());
            Assert.Equal("ab😀", unicode.GetString(0));
            Assert.Equal("😀ab", unicode.GetString(1));
        }
        command.CommandText = "SELECT lpad(code,@width,@padding) FROM padded_codes WHERE id=4";
        Assert.Equal("abcdef", command.ExecuteScalar());
        width.Value = 3L;
        padding.Value = "😀";
        command.CommandText = "SELECT lpad(code,@width,@padding),rpad(code,@width,@padding) FROM padded_codes WHERE id=1";
        using (var completePair = command.ExecuteReader())
        {
            Assert.True(completePair.Read());
            Assert.Equal("😀7", completePair.GetString(0));
            Assert.Equal("7😀", completePair.GetString(1));
        }
        width.Value = 2L;
        command.CommandText = "SELECT lpad(code,@width,@padding) FROM padded_codes WHERE id=1";
        var leftError = Assert.Throws<SndbServerException>(() => command.ExecuteScalar());
        Assert.Contains("截断填充串会切开代理对", leftError.Message, StringComparison.Ordinal);
        width.Value = 4L;
        command.CommandText = "SELECT rpad(code,@width,@padding) FROM padded_codes WHERE id=1";
        var rightError = Assert.Throws<SndbServerException>(() => command.ExecuteScalar());
        Assert.Contains("截断填充串会切开代理对", rightError.Message, StringComparison.Ordinal);
        width.Value = 1_048_577L;
        command.CommandText = "SELECT rpad(code,@width,@padding) FROM padded_codes WHERE id=1";
        Assert.Throws<SndbServerException>(() => command.ExecuteScalar());
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public void TrigonometricFunctions_ParametersErrorsAndMetadata_UseRealTransport(string protocol)
    {
        string baseUrl = protocol == "rest" ? _http11Url : _frameH2Url;
        using var connection = new SndbConnection(ConnectionString(baseUrl, protocol));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE trig_contract (id INT, angle FLOAT, y FLOAT, x FLOAT, PRIMARY KEY (id))";
        command.ExecuteNonQuery();
        const string projection = "SELECT sin(a.angle), cos(a.angle), tan(a.angle), asin(a.angle), "
            + "acos(a.angle), atan(a.angle), atan2(a.y, a.x) FROM trig_contract a";
        Assert.All(new[] { projection, projection + " JOIN trig_contract b ON a.id = b.id" }, sql =>
        {
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            Assert.Equal(7, reader.FieldCount);
            Assert.All(reader.GetColumnSchema(), column =>
            {
                Assert.Equal(typeof(double), column.DataType);
                Assert.True(column.AllowDBNull);
            });
            Assert.False(reader.Read());
        });

        command.CommandText = "INSERT INTO trig_contract (id, angle, y, x) VALUES "
            + "(1, 0, 1, 1), (2, NULL, NULL, NULL)";
        Assert.Equal(2, command.ExecuteNonQuery());
        command.CommandText = projection + " WHERE sin(a.angle) >= @minimum";
        command.Parameters.AddWithValue("@minimum", 0d);
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            double[] expected = [0d, 1d, 0d, 0d, Math.PI / 2d, 0d, Math.PI / 4d];
            Assert.All(Enumerable.Range(0, expected.Length), index =>
            {
                Assert.Equal(typeof(double), reader.GetFieldType(index));
                Assert.Equal(expected[index], reader.GetDouble(index), 12);
            });
            Assert.False(reader.Read());
        }
        command.Parameters.Clear();
        command.CommandText = projection + " WHERE a.id = 2";
        using (var reader = command.ExecuteReader())
        {
            Assert.All(Enumerable.Range(0, 7), index => Assert.Equal(typeof(double), reader.GetFieldType(index)));
            Assert.True(reader.Read());
            Assert.All(Enumerable.Range(0, 7), index => Assert.True(reader.IsDBNull(index)));
            Assert.False(reader.Read());
        }

        command.CommandText = "SELECT sin(@angle), atan2(@y, @x)";
        command.Parameters.AddWithValue("@angle", Math.PI / 2d);
        command.Parameters.AddWithValue("@y", 1d);
        command.Parameters.AddWithValue("@x", -1d);
        using (var reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal(1d, reader.GetDouble(0), 12);
            Assert.Equal(3d * Math.PI / 4d, reader.GetDouble(1), 12);
        }
        command.Parameters.Clear();
        Assert.All(new[]
        {
            (Sql: "SELECT asin(2)", Message: "[-1, 1]"),
            (Sql: "SELECT sin(exp(1000))", Message: "有限数值参数"),
            (Sql: "SELECT atan2(1)", Message: "2 个参数"),
            (Sql: "SELECT cos('0')", Message: "数值参数"),
        }, invalid =>
        {
            command.CommandText = invalid.Sql;
            var error = Assert.Throws<SndbServerException>(() => command.ExecuteReader());
            Assert.Contains(invalid.Message, error.ServerMessage, StringComparison.Ordinal);
        });

        int port = new Uri(baseUrl).Port;
        ObservedRequest[] requests = _requests.Where(request => request.LocalPort == port).ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.Equal(
            protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        Assert.Contains(requests, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        if (protocol == "frame-http2")
            Assert.Contains(requests, request => request.Path == "/v1/frame");
    }

    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task StringAggregate_GroupByJoinAndCorrelatedQuery_UseRealTransport(string protocol)
    {
        string baseUrl = protocol == "rest" ? _http11Url : _frameH2Url;
        await using var connection = new SndbConnection(ConnectionString(baseUrl, protocol));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE agg_labels (id INT, tenant INT, name STRING, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT string_agg(name, ',') FROM agg_labels";
        await using (var empty = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(string), empty.GetFieldType(0));
            Assert.True(empty.GetColumnSchema().Single().AllowDBNull);
            Assert.True(await empty.ReadAsync());
            Assert.True(empty.IsDBNull(0));
            Assert.False(await empty.ReadAsync());
        }
        command.CommandText = "INSERT INTO agg_labels (id, tenant, name) VALUES "
            + "(1, 1, 'red'), (2, 1, 'blue'), (3, 1, 'red'), (4, 1, NULL), (5, 2, NULL), (6, 3, ''), (7, 3, '😀汉字')";
        Assert.Equal(7, await command.ExecuteNonQueryAsync());
        command.CommandText = "SELECT a.tenant, string_agg(a.name, @separator), group_concat(a.name, @separator) "
            + "FROM agg_labels a JOIN agg_labels b ON a.id = b.id GROUP BY a.tenant ORDER BY a.tenant";
        command.Parameters.AddWithValue("@separator", "::");
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(string), reader.GetFieldType(1));
            for (int group = 0; group < 3; group++)
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(group + 1L, reader.GetInt64(0));
                if (group == 1)
                {
                    Assert.True(reader.IsDBNull(1));
                    Assert.True(reader.IsDBNull(2));
                }
                else
                {
                    string[] expected = group == 0 ? ["blue", "red", "red"] : ["", "😀汉字"];
                    Assert.Equal(expected, SortedPieces(reader.GetString(1), "::"));
                    Assert.Equal(expected, SortedPieces(reader.GetString(2), "::"));
                }
            }
            Assert.False(await reader.ReadAsync());
        }
        command.Parameters.Clear();
        command.CommandText = "CREATE TABLE agg_tenants (id INT, PRIMARY KEY (id))";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO agg_tenants (id) VALUES (1), (2), (3), (4)";
        await command.ExecuteNonQueryAsync();
        string correlated = "SELECT t.id, (SELECT group_concat(l.name, ',') FROM agg_labels l WHERE l.tenant = t.id) "
            + "AS names FROM agg_tenants t";
        command.CommandText = correlated + " ORDER BY t.id";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(string), reader.GetFieldType(1));
            for (int tenant = 0; tenant < 4; tenant++)
            {
                Assert.True(await reader.ReadAsync());
                if (tenant is 1 or 3) Assert.True(reader.IsDBNull(1));
                else Assert.Equal(tenant == 0 ? new[] { "blue", "red", "red" } : new[] { "", "😀汉字" }, SortedPieces(reader.GetString(1), ","));
            }
            Assert.False(await reader.ReadAsync());
        }
        command.CommandText = correlated + " WHERE t.id < 0";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.Equal(typeof(string), reader.GetFieldType(1));
            Assert.False(await reader.ReadAsync());
        }
        command.CommandText = "SELECT string_agg(name, NULL) FROM agg_labels WHERE tenant = 2";
        Assert.Null(await command.ExecuteScalarAsync());
        command.CommandText = "SELECT string_agg(DISTINCT name, ',') FROM agg_labels WHERE id < 0";
        var unsupported = await Assert.ThrowsAsync<SndbServerException>(async () => await command.ExecuteReaderAsync());
        Assert.Contains("DISTINCT", unsupported.ServerMessage, StringComparison.Ordinal);

        int port = new Uri(baseUrl).Port;
        var observed = _requests.Where(request => request.LocalPort == port).ToArray();
        Assert.NotEmpty(observed);
        Assert.All(observed, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
        Assert.Contains(observed, request => request.Path == $"/v1/db/{DatabaseName}/sql");
        if (protocol == "frame-http2") Assert.Contains(observed, request => request.Path == "/v1/frame");

        static string[] SortedPieces(string text, string delimiter)
            => text.Split(delimiter, StringSplitOptions.None).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task<List<(FrameHeader Header, byte[] Payload)>> SendSqlFrameAsync(
        string sql, string? resultVersion, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var writer = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(writer, 91, DatabaseName, sql, parameters);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_frameH2Url), "/v1/frame"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(writer.WrittenMemory.ToArray()),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        if (resultVersion is not null)
            request.Headers.Add(SqlFrameCodec.ResultVersionHeader, resultVersion);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpVersion.Version20, response.Version);
        ReadOnlySequence<byte> buffer = new(await response.Content.ReadAsByteArrayAsync());
        var frames = new List<(FrameHeader, byte[])>();
        while (FrameCodec.TryReadFrame(ref buffer, out var header, out var payload))
            frames.Add((header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        return frames;
    }

    private static string[] DecodeLegacySqlMeta(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        if (reader.ReadByte() != (byte)SqlQueryChunkKind.Meta)
            throw new InvalidDataException("旧客户端预期 meta 帧。");
        int count = checked((int)reader.ReadVarUInt32());
        var columns = new string[count];
        for (int i = 0; i < count; i++)
            columns[i] = reader.ReadVarString();
        if (reader.Remaining != 0)
            throw new InvalidDataException("旧客户端拒绝 meta 尾部字节。");
        return columns;
    }

    private static double DecodeLegacySingleFloatRow(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        if (reader.ReadByte() != (byte)SqlQueryChunkKind.Rows
            || reader.ReadVarUInt32() != 1
            || reader.ReadVarUInt32() != 1
            || reader.ReadByte() != (byte)SqlValueKind.Float64
            || reader.ReadByte() != 0)
            throw new InvalidDataException("旧客户端不支持此行的值标记。");
        double value = reader.ReadDouble();
        if (reader.Remaining != 0)
            throw new InvalidDataException("旧客户端拒绝 rows 尾部字节。");
        return value;
    }

    private string ConnectionString(string baseUrl, string protocol)
        => $"Data Source=sonnetdb+http://{new Uri(baseUrl).Authority}/{DatabaseName};" +
           $"Token={AdminToken};Timeout=30;Protocol={protocol}";

    private static async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private sealed record ObservedRequest(int LocalPort, string Path, string Protocol, string ResultVersion);
}
