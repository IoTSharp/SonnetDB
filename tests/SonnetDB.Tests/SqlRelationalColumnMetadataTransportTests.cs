using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>通过真实 REST 与 h2c ADO 验证表列注释、字符串长度约束和声明元数据。</summary>
public sealed class SqlRelationalColumnMetadataTransportTests : IAsyncLifetime
{
    private const string Token = "metadata-admin";
    private const string Database = "metadata";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-metadata-transport-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<string> _protocols = new();
    private WebApplication? _app;
    private bool _initialized;
    private string _rest = string.Empty;
    private string _http2 = string.Empty;

    /// <summary>为每个用例启动动态端口的独立真实 Kestrel 数据库。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin }
        }, extraArgs:
            ["--Kestrel:Endpoints:UpdateH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:UpdateH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value?.Contains("/sql", StringComparison.Ordinal) == true
                || context.Request.Path == "/v1/frame")
                _protocols.Enqueue(context.Request.Protocol);
            await next(context);
        });
        await _app.StartAsync(deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses)
        {
            using var probe = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(3) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz")
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                using var response = await probe.SendAsync(request, deadline.Token);
                if (response.IsSuccessStatusCode) _rest = address;
                else _http2 = address;
            }
            catch (HttpRequestException) { _http2 = address; }
        }
        Assert.NotEmpty(_rest);
        Assert.NotEmpty(_http2);
        if (_initialized) return;
        _initialized = true;
        using var client = new HttpClient { BaseAddress = new Uri(_rest) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var created = await client.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{Database}\"}}", Encoding.UTF8, "application/json"), deadline.Token);
        created.EnsureSuccessStatusCode();
    }

    /// <summary>真实 REST/h2c 的写约束、目录/读者元数据、注释和索引在 Server 重启后保持。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Metadata_DdlAndWrites_PersistAcrossServerRestart(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        const string comment = "中文 '引号' \"双引号\"\n第二行";
        await using (var connection = Open(protocol))
        {
            await Execute(connection, "CREATE TABLE Items(Id INT,Name STRING(4),Notes STRING,PRIMARY KEY(Id))", deadline.Token);
            await Execute(connection, "CREATE UNIQUE INDEX ix_name ON Items(Name)", deadline.Token);
            await Execute(connection, "COMMENT ON TABLE Items IS " + Quote(comment), deadline.Token);
            await Execute(connection, "COMMENT ON COLUMN items.name IS " + Quote(comment), deadline.Token);
            await AssertMetadata(connection, comment, 4, deadline.Token);
            await Execute(connection, "INSERT INTO Items(Id,Name) VALUES(1,''),(2,'甲乙'),(3,'😀'),(4,NULL)", deadline.Token);
            await AssertLengthFailure(() => Execute(connection,
                "INSERT INTO Items(Id,Name) VALUES(5,'abc'),(6,'abcde')", deadline.Token));
            await AssertLengthFailure(() => Execute(connection,
                "UPDATE Items SET Name='abcde' WHERE Id=2", deadline.Token));
            await AssertLengthFailure(() => Execute(connection,
                "ALTER TABLE Items ALTER Name TYPE STRING(1)", deadline.Token));
            await AssertMetadata(connection, comment, 4, deadline.Token);
            await Execute(connection, "ALTER TABLE Items ALTER Name TYPE VARCHAR(8)", deadline.Token);
            await Execute(connection, "ALTER TABLE Items ADD Extra STRING(2) DEFAULT '中文'", deadline.Token);
            await AssertLengthFailure(() => Execute(connection,
                "ALTER TABLE Items ALTER Extra SET DEFAULT 'abc'", deadline.Token));
            await Execute(connection, "ALTER TABLE Items ALTER Name TYPE STRING(2)", deadline.Token);
            Assert.Equal(4L, await Scalar(connection, "SELECT COUNT(*) FROM Items", deadline.Token));
            Assert.Equal(2L, await Scalar(connection, "SELECT Id FROM Items WHERE Name='甲乙'", deadline.Token));
        }
        Assert.NotNull(_app);
        await _app.StopAsync(deadline.Token);
        await _app.DisposeAsync();
        _app = null;
        await InitializeAsync();
        await using (var reopened = Open(protocol))
        {
            await AssertMetadata(reopened, comment, 2, deadline.Token);
            Assert.Equal(4L, await Scalar(reopened, "SELECT COUNT(*) FROM Items", deadline.Token));
            Assert.Equal(3L, await Scalar(reopened, "SELECT Id FROM Items WHERE Name='😀'", deadline.Token));
            await AssertLengthFailure(() => Execute(reopened,
                "INSERT INTO Items(Id,Name) VALUES(5,'😀a')", deadline.Token));
            await Execute(reopened, "COMMENT ON TABLE Items IS ''", deadline.Token);
            await Execute(reopened, "COMMENT ON COLUMN Items.Name IS ''", deadline.Token);
            await AssertMetadata(reopened, string.Empty, 2, deadline.Token);
            await Execute(reopened, "COMMENT ON TABLE Items IS NULL", deadline.Token);
            await Execute(reopened, "COMMENT ON COLUMN Items.Name IS NULL", deadline.Token);
            await AssertMetadata(reopened, null, 2, deadline.Token);
        }
        Assert.NotEmpty(_protocols);
        Assert.All(_protocols, observed => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", observed));
    }

    private static async Task AssertMetadata(SndbConnection connection, string? comment, int length, CancellationToken cancellation)
    {
        var columns = connection.GetSchema("Columns", [null, null, "Items", "Name"]);
        Assert.Equal(length, columns.Rows[0]["CHARACTER_MAXIMUM_LENGTH"]);
        Assert.Equal((object?)comment ?? DBNull.Value, columns.Rows[0]["REMARKS"]);
        Assert.Equal((object?)comment ?? DBNull.Value, connection.GetSchema("Tables", [null, null, "Items"]).Rows[0]["REMARKS"]);
        Assert.Equal(int.MaxValue, connection.GetSchema("Columns", [null, null, "Items", "Notes"]).Rows[0]["CHARACTER_MAXIMUM_LENGTH"]);
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT Name AS DisplayName FROM Items WHERE Id=0";
        await using (var reader = await query.ExecuteReaderAsync(cancellation))
        {
            Assert.Equal(length, reader.GetColumnSchema()[0].ColumnSize);
            Assert.Equal(length, reader.GetSchemaTable()!.Rows[0]["ColumnSize"]);
            Assert.False(await reader.ReadAsync(cancellation));
        }
        Assert.Equal((long)length, await Scalar(connection,
            "SELECT character_maximum_length FROM information_schema.columns WHERE table_name='Items' AND column_name='Name'", cancellation));
        Assert.Equal(comment, await Scalar(connection,
            "SELECT column_comment FROM information_schema.columns WHERE table_name='Items' AND column_name='Name'", cancellation));
        Assert.Equal(comment, await Scalar(connection,
            "SELECT table_comment FROM information_schema.tables WHERE table_name='Items'", cancellation));
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<object?> Scalar(SndbConnection connection, string sql, CancellationToken cancellation)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellation);
    }

    private SndbConnection Open(string protocol)
    {
        string endpoint = protocol == "rest" ? _rest : _http2;
        var connection = new SndbConnection(
            $"Data Source=sonnetdb+http://{new Uri(endpoint).Authority}/{Database};Token={Token};Protocol={protocol};Timeout=10");
        connection.Open();
        return connection;
    }

    private static async Task AssertLengthFailure(Func<Task> execute)
    {
        var exception = await Assert.ThrowsAsync<SndbServerException>(execute);
        Assert.Equal("table_string_length_violation", exception.Error);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
    }

    private static async Task Execute(SndbConnection connection, string sql, CancellationToken cancellation)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellation);
    }

    /// <summary>停止本夹具服务并只回收本次创建的唯一目录。</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }
        string absolute = Path.GetFullPath(_root);
        if (Path.GetDirectoryName(absolute) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())))
            throw new InvalidOperationException("元数据传输夹具目录超出临时目录。");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }
}
