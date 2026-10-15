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

/// <summary>以真实 Kestrel 验证 measurement 分组管线的 REST 与 HTTP/2 ADO 合同。</summary>
public sealed class MeasurementGroupedPipelineTransportTests : IAsyncLifetime
{
    private const string Token = "measurement224-admin";
    private const string Database = "measurement224";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-measurement224-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, string Protocol)> _requests = new();
    private WebApplication? _app;
    private string _rest = string.Empty;
    private string _h2 = string.Empty;

    /// <summary>创建独占数据库和两个实际协议端点。</summary>
    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = _root,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
        }, extraArgs:
            ["--Kestrel:Endpoints:GroupedH2:Url=http://127.0.0.1:0", "--Kestrel:Endpoints:GroupedH2:Protocols=Http2"]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue((context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
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
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                };
                using var response = await probe.SendAsync(request, deadline.Token);
                if (response.IsSuccessStatusCode) _rest = address;
                else _h2 = address;
            }
            catch (HttpRequestException) { _h2 = address; }
        }
        Assert.NotEmpty(_rest);
        Assert.NotEmpty(_h2);
        using var http = new HttpClient { BaseAddress = new Uri(_rest), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var created = await http.PostAsync("/v1/db", new StringContent("{\"name\":\"" + Database + "\"}",
            Encoding.UTF8, "application/json"), deadline.Token);
        created.EnsureSuccessStatusCode();
        _requests.Clear();
    }

    /// <summary>停止本测试宿主并回收独占根目录。</summary>
    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _app.StopAsync(deadline.Token);
            await _app.DisposeAsync();
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>参数化 HAVING、复合投影、多键排序和分页在两种实际远程协议中保持一致。</summary>
    [Theory]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Select_GroupedPipeline_ReturnsSameRowsAcrossRealTransports(string protocol, bool useAsync)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpoint = protocol == "rest" ? _rest : _h2;
        await using var connection = new SndbConnection($"Data Source=sonnetdb+http://{new Uri(endpoint).Authority}/{Database};"
            + $"Token={Token};Protocol={protocol}");
        await connection.OpenAsync(deadline.Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = "CREATE MEASUREMENT Metrics (Host TAG, Region TAG, Value FIELD INT)";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "INSERT INTO Metrics(time,Host,Region,Value) VALUES "
            + "(1000,'a','east',2),(2000,'a','west',3),(3000,'a','east',5),"
            + "(1000,'b','east',5),(2000,'b','east',5),(1000,'c','east',30)";
        await command.ExecuteNonQueryAsync(deadline.Token);
        command.CommandText = "SELECT Host, CAST(SUM(Value) AS STRING) AS TotalText, SUM(Value)+1 AS \"Adjusted.Total\" "
            + "FROM Metrics WHERE time>=@start GROUP BY Host HAVING COUNT(*)>@minimum "
            + "ORDER BY \"Adjusted.Total\" DESC, Host ASC LIMIT @take OFFSET @skip";
        command.Parameters.Add(new SndbParameter("@start", 1000L));
        command.Parameters.Add(new SndbParameter("@minimum", 1L));
        command.Parameters.Add(new SndbParameter("@take", 1L));
        command.Parameters.Add(new SndbParameter("@skip", 1L));
        await using (var reader = useAsync ? await command.ExecuteReaderAsync(deadline.Token) : command.ExecuteReader())
        {
            Assert.Equal(["Host", "TotalText", "Adjusted.Total"], Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
            Assert.True(useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read());
            Assert.Equal("b", reader.GetString(0));
            Assert.Equal("10", reader.GetString(1));
            Assert.Equal(11L, reader.GetInt64(2));
            Assert.False(useAsync ? await reader.ReadAsync(deadline.Token) : reader.Read());
        }

        command.Parameters.Clear();
        command.CommandText = "SELECT Host AS Name, SUM(Value)+1 AS Adjusted FROM Metrics GROUP BY Host HAVING COUNT(*)>99";
        await using (var empty = await command.ExecuteReaderAsync(deadline.Token))
        {
            Assert.Equal(2, empty.FieldCount);
            Assert.Equal("Name", empty.GetName(0));
            Assert.Equal("Adjusted", empty.GetName(1));
            Assert.False(await empty.ReadAsync(deadline.Token));
        }
        Assert.Contains(_requests, request => request.Path == (protocol == "rest" ? $"/v1/db/{Database}/sql" : "/v1/frame"));
        Assert.All(_requests.Where(static request => request.Path.StartsWith("/v1/", StringComparison.Ordinal)),
            request => Assert.Equal(protocol == "rest" ? "HTTP/1.1" : "HTTP/2", request.Protocol));
    }
}
