using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 验证 Copilot 知识库状态端点回显实际 embedding profile 合同。
/// </summary>
public sealed class CopilotKnowledgeStatusEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "copilot-status-admin-token";
    private WebApplication? _app;
    private string? _dataRoot;
    private string? _baseUrl;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-copilot-status-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var modelPath = Path.Combine(_dataRoot, "tiny-model.onnx");
        var tokenizerPath = Path.Combine(_dataRoot, "vocab.txt");
        await File.WriteAllBytesAsync(modelPath, [0]);
        await File.WriteAllTextAsync(tokenizerPath, "[PAD]\n[UNK]\n[CLS]\n[SEP]\nhello\n");

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AllowAnonymousProbes = true,
            AutoLoadExistingDatabases = false,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        options.Copilot.Enabled = true;
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;
        options.Copilot.Embedding.Provider = "local";
        options.Copilot.Embedding.LocalModelPath = modelPath;
        options.Copilot.Embedding.ModelProfile = new CopilotEmbeddingModelProfile
        {
            TokenizerType = "bert-wordpiece",
            TokenizerModelPath = tokenizerPath,
            MaxTokens = 8,
            Dimensions = 2,
            Pooling = "mean",
        };

        _app = TestServerHost.Build(options);
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
    public async Task GetKnowledgeStatus_WithLocalProfile_ReportsProfileDimensionWithoutFallback()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

        using var response = await client.GetAsync("/v1/copilot/knowledge/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var status = JsonSerializer.Deserialize(body, ServerJsonContext.Default.CopilotKnowledgeStatusResponse);

        Assert.NotNull(status);
        Assert.True(status!.Enabled);
        Assert.Equal("local", status.EmbeddingProvider);
        Assert.False(status.EmbeddingFallback);
        Assert.Equal(2, status.VectorDimension);
    }
}
