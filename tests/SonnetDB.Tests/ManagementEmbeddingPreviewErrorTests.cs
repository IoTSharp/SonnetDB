using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 验证向量 embedding preview 对 provider 合同错误返回稳定的服务不可用响应。
/// </summary>
public sealed class ManagementEmbeddingPreviewErrorTests : IAsyncLifetime
{
    private const string AdminToken = "embedding-preview-admin-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-embedding-preview-errors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = false,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        _app = TestServerHost.Build(options, services =>
            services.AddSingleton<IEmbeddingProvider>(new ContractErrorEmbeddingProvider()));
        await _app.StartAsync();
        _baseUrl = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .First();
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
    public async Task EmbedPreview_WhenProviderReportsContractError_ReturnsStable503()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var createResponse = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(
                new CreateDatabaseRequest("embeddingerrors"),
                ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var response = await client.PostAsync(
            "/v1/db/embeddingerrors/vector/embed-preview",
            JsonContent.Create(
                new VectorEmbedPreviewRequest("pump alarm"),
                ServerJsonContext.Default.VectorEmbedPreviewRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.ErrorResponse);
        Assert.NotNull(error);
        Assert.Equal("embedding_failed", error!.Error);
        Assert.Contains("profile output contract", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedPreview_WhenTokenizerReportsArgumentError_ReturnsStable503()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var createResponse = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(
                new CreateDatabaseRequest("embeddingargumenterrors"),
                ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var response = await client.PostAsync(
            "/v1/db/embeddingargumenterrors/vector/embed-preview",
            JsonContent.Create(
                new VectorEmbedPreviewRequest("tokenizer argument error"),
                ServerJsonContext.Default.VectorEmbedPreviewRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.ErrorResponse);
        Assert.NotNull(error);
        Assert.Equal("embedding_failed", error!.Error);
        Assert.Contains("tokenizer profile argument contract", error.Message, StringComparison.Ordinal);
    }

    private sealed class ContractErrorEmbeddingProvider : IEmbeddingProvider
    {
        public ValueTask<float[]> EmbedAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            if (text.Contains("argument", StringComparison.Ordinal))
                throw new ArgumentException("tokenizer profile argument contract is invalid", nameof(text));

            throw new InvalidDataException("profile output contract is invalid");
        }
    }
}
