using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using InfluxDB.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using Xunit;

namespace SonnetDB.Accuracy.Tests;

public sealed class AccuracyFixture : IAsyncLifetime
{
    private const ushort InfluxPort = 8086;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private IContainer? _influxContainer;
    private WebApplication? _app;
    private HttpClient? _sonnetClient;
    private HttpClient? _influxWriteClient;
    private InfluxDBClient? _influxClient;
    private string? _dataRoot;
    private string? _skipReason;

    public HttpClient SonnetClient
        => _sonnetClient ?? throw new InvalidOperationException("SonnetDB 客户端尚未初始化。");

    public InfluxDBClient InfluxClient
        => _influxClient ?? throw new InvalidOperationException("InfluxDB 客户端尚未初始化。");

    public bool TryEnsureReady()
    {
        if (_skipReason is null)
            return true;

        Console.Error.WriteLine(_skipReason);
        return false;
    }

    public void EnsureReady()
    {
        if (_skipReason is not null)
            throw new InvalidOperationException(_skipReason);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var influxContainer = BuildInfluxContainer();
            _influxContainer = influxContainer;

            await influxContainer.StartAsync().ConfigureAwait(false);

            var influxBaseUrl = $"http://{influxContainer.Hostname}:{influxContainer.GetMappedPublicPort(InfluxPort)}";
            _influxWriteClient = new HttpClient { BaseAddress = new Uri(influxBaseUrl) };
            _influxWriteClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Token", AccuracyDataSet.InfluxAdminToken);
            _influxClient = new InfluxDBClient(influxBaseUrl, AccuracyDataSet.InfluxAdminToken);

            await WaitForInfluxReadyAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _skipReason = BuildSkipReason(ex);
            return;
        }

        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-accuracy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AccuracyDataSet.SonnetAdminToken] = ServerRoles.Admin,
            },
        };
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        _app = TestServerHost.Build(options);
        await _app.StartAsync().ConfigureAwait(false);

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        var baseUrl = addresses.Addresses.First();

        _sonnetClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _sonnetClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AccuracyDataSet.SonnetAdminToken);

        await CreateSonnetDatabaseAsync().ConfigureAwait(false);
        await SeedSonnetAsync().ConfigureAwait(false);

        try
        {
            await SeedInfluxAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _skipReason = BuildSkipReason(ex);
        }
    }

    public async Task DisposeAsync()
    {
        _sonnetClient?.Dispose();
        _influxWriteClient?.Dispose();
        _influxClient?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        if (_influxContainer is not null)
            await _influxContainer.DisposeAsync().ConfigureAwait(false);

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup
            }
            catch (UnauthorizedAccessException)
            {
                // best-effort cleanup
            }
        }
    }

    public async Task<SqlQueryResult> QuerySonnetAsync(string sql)
    {
        EnsureReady();

        using var response = await SonnetClient.PostAsync(
            $"/v1/db/{AccuracyDataSet.DatabaseName}/sql",
            JsonContent.Create(new SqlRequest(sql), options: JsonOptions)).ConfigureAwait(false);
        return await SqlQueryResult.ParseAsync(response).ConfigureAwait(false);
    }

    private async Task WaitForInfluxReadyAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            try
            {
                if (await InfluxClient.PingAsync().ConfigureAwait(false))
                    return;
            }
            catch
            {
                // Wait for the container bootstrap to finish.
            }

            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        throw new TimeoutException("InfluxDB 2.x 未能在预期时间内完成初始化。");
    }

    private async Task CreateSonnetDatabaseAsync()
    {
        using var createResponse = await SonnetClient.PostAsync(
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(AccuracyDataSet.DatabaseName), options: JsonOptions)).ConfigureAwait(false);
        if (createResponse.StatusCode is not HttpStatusCode.Created and not HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"创建 SonnetDB Accuracy 数据库失败：{createResponse.StatusCode} / {await createResponse.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }

        foreach (var measurement in AccuracyDataSet.Measurements)
            await ExecuteSonnetSqlAsync(measurement.CreateMeasurementSql).ConfigureAwait(false);
    }

    private async Task SeedSonnetAsync()
    {
        foreach (var measurement in AccuracyDataSet.Measurements)
        {
            using var response = await SonnetClient.PostAsync(
                $"/v1/db/{AccuracyDataSet.DatabaseName}/measurements/{measurement.Name}/lp?flush=true",
                new StringContent(measurement.LineProtocol, Encoding.UTF8, "text/plain")).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"SonnetDB LP 写入失败（{measurement.Name}）：{response.StatusCode} / {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            }
        }
    }

    private async Task SeedInfluxAsync()
    {
        foreach (var measurement in AccuracyDataSet.Measurements)
        {
            using var response = await _influxWriteClient!.PostAsync(
                $"/api/v2/write?org={AccuracyDataSet.InfluxOrg}&bucket={AccuracyDataSet.InfluxBucket}&precision=ms",
                new StringContent(measurement.LineProtocol, Encoding.UTF8, "text/plain")).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NoContent)
            {
                throw new InvalidOperationException(
                    $"InfluxDB LP 写入失败（{measurement.Name}）：{response.StatusCode} / {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
            }
        }
    }

    private async Task ExecuteSonnetSqlAsync(string sql)
    {
        using var response = await SonnetClient.PostAsync(
            $"/v1/db/{AccuracyDataSet.DatabaseName}/sql",
            JsonContent.Create(new SqlRequest(sql), options: JsonOptions)).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"执行 SonnetDB SQL 失败：{response.StatusCode} / {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }
    }

    private static string BuildSkipReason(Exception ex)
        => $"Docker 或 InfluxDB 2.x 测试环境不可用，已跳过 Accuracy Tests。{ex.GetType().Name}: {ex.Message}";

    private static IContainer BuildInfluxContainer()
        => new ContainerBuilder("influxdb:2.7")
            .WithImagePullPolicy(PullPolicy.Missing)
            .WithPortBinding(InfluxPort, true)
            .WithEnvironment("DOCKER_INFLUXDB_INIT_MODE", "setup")
            .WithEnvironment("DOCKER_INFLUXDB_INIT_USERNAME", "admin")
            .WithEnvironment("DOCKER_INFLUXDB_INIT_PASSWORD", "password123")
            .WithEnvironment("DOCKER_INFLUXDB_INIT_ORG", AccuracyDataSet.InfluxOrg)
            .WithEnvironment("DOCKER_INFLUXDB_INIT_BUCKET", AccuracyDataSet.InfluxBucket)
            .WithEnvironment("DOCKER_INFLUXDB_INIT_ADMIN_TOKEN", AccuracyDataSet.InfluxAdminToken)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                .ForPort(InfluxPort)
                .ForPath("/health")))
            .Build();
}

public sealed record SqlQueryResult(IReadOnlyList<string> Columns, IReadOnlyList<JsonElement> Rows)
{
    public static async Task<SqlQueryResult> ParseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SQL 请求失败：{response.StatusCode} / {body}");

        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            throw new InvalidOperationException($"SQL NDJSON 响应格式无效：{body}");

        using var meta = JsonDocument.Parse(lines[0]);
        var columns = meta.RootElement.GetProperty("columns")
            .EnumerateArray()
            .Select(element => element.GetString() ?? string.Empty)
            .ToArray();

        var rows = new List<JsonElement>();
        for (var i = 1; i < lines.Length - 1; i++)
        {
            using var row = JsonDocument.Parse(lines[i]);
            rows.Add(row.RootElement.Clone());
        }

        return new SqlQueryResult(columns, rows);
    }
}
