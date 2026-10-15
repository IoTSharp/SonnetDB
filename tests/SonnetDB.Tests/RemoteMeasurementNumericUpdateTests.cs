using System.Collections.Concurrent;
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

/// <summary>GH-Issue #313：嵌入式与真实 REST/h2c ADO 的同步、异步数值更新合同。</summary>
public sealed class RemoteMeasurementNumericUpdateTests : IAsyncLifetime
{
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-update313-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _h2Url = string.Empty;
    private const string Token = "numeric-update-admin";
    private const string Database = "numeric_update";

    public async Task InitializeAsync()
    {
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
        }, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        foreach (string address in addresses.Addresses)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                using var response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode) _restUrl = address;
                else _h2Url = address;
            }
            catch (HttpRequestException) { _h2Url = address; }
        }
        Assert.NotEmpty(_restUrl);
        Assert.NotEmpty(_h2Url);
        using var http = new HttpClient { BaseAddress = new Uri(_restUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var created = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{Database}\"}}", Encoding.UTF8, "application/json"));
        created.EnsureSuccessStatusCode();
        _requests.Clear();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task NumericUpdate_LiteralParameterAndFailure_KeepSameResults(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string connectionString = protocol == "embedded"
            ? $"Data Source={Path.Combine(_root, "embedded")}" :
            $"Data Source=sonnetdb+http://{new Uri(protocol == "rest" ? _restUrl : _h2Url).Authority}/{Database};Token={Token};Protocol={protocol};Timeout=10";
        await using var connection = new SndbConnection(connectionString);
        if (useAsync) await connection.OpenAsync(deadline.Token);
        else connection.Open();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        await NonQuery("CREATE MEASUREMENT metrics (Host TAG, Value FIELD FLOAT, Other FIELD INT)");
        Assert.Equal(2, await NonQuery("INSERT INTO metrics(time,Host,Value,Other) VALUES(1,'a',1.0,10),(2,'a',2.0,20)"));
        Assert.Equal(1, await NonQuery("UPDATE metrics SET value=3.0 WHERE time=1 AND host='a'"));
        command.Parameters.AddWithValue("@value", -3.25m);
        command.Parameters.AddWithValue("@time", 1L);
        command.Parameters.AddWithValue("@host", "a");
        Assert.Equal(1, await NonQuery("UPDATE metrics SET Value=@value WHERE time=@time AND Host=@host"));
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@value", 9007199254740993L);
        Assert.Equal(1, await NonQuery("UPDATE metrics SET Other=@value WHERE time=1"));
        command.Parameters.Clear();

        foreach (string invalid in new[]
        {
            "UPDATE metrics SET Value=NULL WHERE time=1",
            "UPDATE metrics SET Value='bad' WHERE time=1",
            "UPDATE metrics SET Other=1.5 WHERE time=1",
            "UPDATE metrics SET Value=8 WHERE Value>0",
            "UPDATE metrics SET Host='b' WHERE time=1",
            "INSERT INTO metrics(time,Host,Value) VALUES(1,'a',9.0)",
        })
        {
            Exception? failure = await Record.ExceptionAsync(async () => { await NonQuery(invalid); });
            Assert.NotNull(failure);
        }
        Assert.Equal(0, await NonQuery("UPDATE metrics SET Value=9 WHERE host='missing'"));
        command.CommandText = "SELECT time,Value,Other FROM metrics ORDER BY time";
        await using (var reader = useAsync ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader())
        {
            Assert.True(useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(-3.25d, reader.GetDouble(1));
            Assert.Equal(9007199254740993L, reader.GetInt64(2));
            Assert.True(useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.Equal(2d, reader.GetDouble(1));
            Assert.Equal(20L, reader.GetInt64(2));
            Assert.False(useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read());
        }
        command.CommandText = "SELECT sum(Value) FROM metrics";
        Assert.Equal(-1.25d, useAsync ? await command.ExecuteScalarAsync(deadline.Token) : command.ExecuteScalar());

        if (protocol != "embedded")
        {
            Assert.NotEmpty(_requests);
            Assert.All(_requests, request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
            Assert.Contains(_requests, request => request.Path == $"/v1/db/{Database}/sql");
            if (protocol == "frame-http2") Assert.Contains(_requests, request => request.Path == "/v1/frame");
        }

        async Task<int> NonQuery(string sql)
        {
            command.CommandText = sql;
            return useAsync ? await command.ExecuteNonQueryAsync(deadline.Token) : command.ExecuteNonQuery();
        }
    }
}
