using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class CopilotModelsEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "models-admin-token";
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-models-" + Guid.NewGuid().ToString("N"));
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        _app = TestServerHost.Build(
            options,
            services => services.AddSingleton<IHttpClientFactory>(new ModelCatalogHttpClientFactory()));
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
        _app.Services.GetRequiredService<AiConfigStore>().Save(new AiOptions
        {
            CloudAccessToken = "cloud-token",
            CloudAccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
        });
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
            try
            {
                Directory.Delete(_dataRoot, recursive: true);
            }
            catch
            {
                // 测试临时目录按 best effort 清理。
            }
        }
    }

    [Fact]
    public async Task GetModels_WithGroupedGatewayMetadata_ReturnsCompatibleAndGroupedCatalog()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var response = await client.GetAsync("/v1/copilot/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        var catalog = JsonSerializer.Deserialize(payload, ServerJsonContext.Default.CopilotModelsResponse);

        Assert.NotNull(catalog);
        Assert.Equal("balanced", catalog.Default);
        Assert.Equal(["balanced", "edge-qwen", "specialist"], catalog.Candidates);
        Assert.Equal("balanced", Assert.Single(catalog.Groups[0].Models).Id);
        Assert.Equal("specialist", Assert.Single(catalog.Groups[1].Models).Id);
        Assert.Equal("edge-qwen", Assert.Single(catalog.Groups[2].Models).Id);
    }

    private sealed class ModelCatalogHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new ModelCatalogHandler(), disposeHandler: true);
    }

    private sealed class ModelCatalogHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string json = """
                {"data":[
                  {"id":"balanced","display_name":"Balanced","is_default":true},
                  {"id":"edge-qwen","display_name":"Edge Qwen","group":"local"},
                  {"id":"specialist","display_name":"Specialist","group":"custom"}
                ]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
