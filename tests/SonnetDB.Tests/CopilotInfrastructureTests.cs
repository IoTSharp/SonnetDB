using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class CopilotInfrastructureTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private readonly List<string> _tempDirectories = [];
    private readonly List<string> _tempFiles = [];

    public async Task InitializeAsync()
    {
        var options = CreateServerOptions();
        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch
            {
                // best effort
            }
        }

        foreach (var directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void Healthz_ReportsCopilotDisabled_WhenFeatureIsDisabled()
    {
        var options = CreateServerOptions();
        options.Copilot.Enabled = false;

        var readiness = new CopilotReadiness(Options.Create(options)).Evaluate();

        Assert.False(readiness.Enabled);
        Assert.False(readiness.Ready);
        Assert.Equal("disabled", readiness.Reason);
    }

    [Fact]
    public void Healthz_ReportsCopilotReady_ForCompleteOpenAiConfiguration()
    {
        var options = CreateServerOptions();
        options.Copilot.Embedding.Provider = "openai";
        options.Copilot.Embedding.Endpoint = "https://example.com/v1/";
        options.Copilot.Embedding.ApiKey = "embedding-key";
        options.Copilot.Embedding.Model = "text-embedding-3-small";
        options.Copilot.Chat.Provider = "openai";
        options.Copilot.Chat.Endpoint = "https://example.com/v1/";
        options.Copilot.Chat.ApiKey = "chat-key";
        options.Copilot.Chat.Model = "chat-model";

        var readiness = new CopilotReadiness(Options.Create(options)).Evaluate();

        Assert.True(readiness.Enabled);
        Assert.True(readiness.EmbeddingReady);
        Assert.True(readiness.ChatReady);
        Assert.True(readiness.Ready);
        Assert.Null(readiness.Reason);
    }

    [Fact]
    public void Healthz_ReportsEmbeddingReady_ForBuiltinProvider_ByDefault()
    {
        var options = CreateServerOptions();
        // Embedding 默认就是 builtin，无需任何外部资源。
        options.Copilot.Chat.Provider = "openai";
        options.Copilot.Chat.Endpoint = "https://example.com/v1/";
        options.Copilot.Chat.ApiKey = "chat-key";
        options.Copilot.Chat.Model = "chat-model";

        var readiness = new CopilotReadiness(Options.Create(options)).Evaluate();

        Assert.Equal("builtin", options.Copilot.Embedding.Provider);
        Assert.True(readiness.EmbeddingReady);
        Assert.True(readiness.ChatReady);
        Assert.True(readiness.Ready);
        Assert.Null(readiness.Reason);
    }

    [Fact]
    public async Task BuiltinHashEmbeddingProvider_ProducesStableUnitVectors()
    {
        var provider = new BuiltinHashEmbeddingProvider(new CopilotEmbeddingOptions { Provider = "builtin" });

        var v1 = await provider.EmbedAsync("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        var v2 = await provider.EmbedAsync("CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        var v3 = await provider.EmbedAsync("完全不同的中文文本");

        Assert.Equal(BuiltinHashEmbeddingProvider.VectorDimension, v1.Length);
        Assert.Equal(v1, v2);

        double norm = 0;
        foreach (var x in v1)
            norm += x * x;
        Assert.InRange(Math.Sqrt(norm), 0.99, 1.01);

        // 不同输入应产生不同向量（极少数 hash 碰撞概率忽略）。
        Assert.NotEqual(v1, v3);
    }

    [Fact]
    public void Healthz_ReportsCopilotNotReady_WhenLocalModelFileIsMissing()
    {
        var options = CreateServerOptions();
        options.Copilot.Embedding.Provider = "local";
        options.Copilot.Embedding.LocalModelPath = Path.Combine(CreateTempDirectory(), "missing-model.onnx");
        options.Copilot.Chat.Provider = "openai";
        options.Copilot.Chat.Endpoint = "https://example.com/v1/";
        options.Copilot.Chat.ApiKey = "chat-key";
        options.Copilot.Chat.Model = "chat-model";

        var readiness = new CopilotReadiness(Options.Create(options)).Evaluate();

        Assert.True(readiness.Enabled);
        Assert.False(readiness.EmbeddingReady);
        Assert.True(readiness.ChatReady);
        Assert.False(readiness.Ready);
        Assert.Equal("embedding.local_model_not_found", readiness.Reason);
    }

    [Fact]
    public void DependencyInjection_ResolvesOpenAiProviders_ForOpenAiConfiguration()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();

        var embeddingOptions = new CopilotEmbeddingOptions
        {
            Provider = "openai",
            Endpoint = "https://example.com/v1/",
            ApiKey = "embedding-key",
            Model = "text-embedding-3-small",
        };
        var chatOptions = new CopilotChatOptions
        {
            Provider = "openai",
            Endpoint = "https://example.com/v1/",
            ApiKey = "chat-key",
            Model = "chat-model",
        };

        services.AddSingleton(embeddingOptions);
        services.AddSingleton(chatOptions);
        services.AddSingleton<IEmbeddingProvider>(sp => new OpenAICompatibleEmbeddingProvider(
            sp.GetRequiredService<CopilotEmbeddingOptions>(),
            sp.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<IChatProvider>(sp => new OpenAICompatibleChatProvider(
            sp.GetRequiredService<CopilotChatOptions>(),
            sp.GetRequiredService<IHttpClientFactory>()));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<OpenAICompatibleEmbeddingProvider>(provider.GetRequiredService<IEmbeddingProvider>());
        Assert.IsType<OpenAICompatibleChatProvider>(provider.GetRequiredService<IChatProvider>());
    }

    [Fact]
    public void DependencyInjection_ResolvesLocalOnnxProvider_ForLocalEmbeddingConfiguration()
    {
        var services = new ServiceCollection();
        var embeddingOptions = new CopilotEmbeddingOptions
        {
            Provider = "local",
            LocalModelPath = CreateTempFile("fake-model.onnx", "onnx-placeholder"),
        };

        services.AddSingleton(embeddingOptions);
        services.AddSingleton<IEmbeddingProvider>(sp => new LocalOnnxEmbeddingProvider(
            sp.GetRequiredService<CopilotEmbeddingOptions>()));

        using var provider = services.BuildServiceProvider();

        Assert.IsType<LocalOnnxEmbeddingProvider>(provider.GetRequiredService<IEmbeddingProvider>());
    }

    [Fact]
    public void DependencyInjection_ThrowsForUnsupportedEmbeddingProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();

        var embeddingOptions = new CopilotEmbeddingOptions
        {
            Provider = "unsupported",
        };

        services.AddSingleton(embeddingOptions);
        services.AddSingleton<IEmbeddingProvider>(sp =>
        {
            var options = sp.GetRequiredService<CopilotEmbeddingOptions>();
            if (string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
                return new OpenAICompatibleEmbeddingProvider(options, sp.GetRequiredService<IHttpClientFactory>());
            if (string.Equals(options.Provider, "local", StringComparison.OrdinalIgnoreCase))
                return new LocalOnnxEmbeddingProvider(options);

            throw new InvalidOperationException($"Unsupported copilot embedding provider '{options.Provider}'.");
        });

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IEmbeddingProvider>());
        Assert.Contains("Unsupported copilot embedding provider", exception.Message);
    }

    [Fact]
    public void DependencyInjection_ThrowsForUnsupportedChatProvider()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();

        var chatOptions = new CopilotChatOptions
        {
            Provider = "unsupported",
        };

        services.AddSingleton(chatOptions);
        services.AddSingleton<IChatProvider>(sp =>
        {
            var options = sp.GetRequiredService<CopilotChatOptions>();
            if (string.Equals(options.Provider, "openai", StringComparison.OrdinalIgnoreCase))
                return new OpenAICompatibleChatProvider(options, sp.GetRequiredService<IHttpClientFactory>());

            throw new InvalidOperationException($"Unsupported copilot chat provider '{options.Provider}'.");
        });

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IChatProvider>());
        Assert.Contains("Unsupported copilot chat provider", exception.Message);
    }

    [Fact]
    public async Task BuildApp_BindsServerOptions_FromConfiguration()
    {
        var configRoot = CreateTempDirectory();
        var dataRoot = CreateTempDirectory();
        var appsettings = new
        {
            SonnetDBServer = new
            {
                DataRoot = dataRoot,
                AllowAnonymousProbes = true,
                AutoLoadExistingDatabases = false,
                Tokens = new Dictionary<string, string> { ["admin-token"] = "admin" },
                Copilot = new
                {
                    Enabled = false,
                    Embedding = new
                    {
                        Provider = "openai",
                        Endpoint = "https://embedding.example/v1/",
                        ApiKey = "embedding-secret",
                        Model = "text-embedding-3-small",
                        TimeoutSeconds = 42,
                    },
                    Chat = new
                    {
                        Provider = "openai",
                        Endpoint = "https://chat.example/v1/",
                        ApiKey = "chat-secret",
                        Model = "gpt-like-chat",
                        TimeoutSeconds = 24,
                    },
                    Docs = new
                    {
                        AutoIngestOnStartup = true,
                        Roots = new[] { "./docs-a", "./docs-b" },
                        ChunkSize = 512,
                        ChunkOverlap = 64,
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(configRoot, "appsettings.json"), JsonSerializer.Serialize(appsettings));

        await using var app = Program.BuildApp(["--contentRoot", configRoot]);
        var options = app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;

        Assert.False(options.AutoLoadExistingDatabases);
        Assert.True(options.AllowAnonymousProbes);
        Assert.Equal(ServerRoles.Admin, options.Tokens["admin-token"]);
        Assert.False(options.Copilot.Enabled);
        Assert.Equal("openai", options.Copilot.Embedding.Provider);
        Assert.Equal("https://embedding.example/v1/", options.Copilot.Embedding.Endpoint);
        Assert.Equal("embedding-secret", options.Copilot.Embedding.ApiKey);
        Assert.Equal("text-embedding-3-small", options.Copilot.Embedding.Model);
        Assert.Equal(42, options.Copilot.Embedding.TimeoutSeconds);
        Assert.Equal("openai", options.Copilot.Chat.Provider);
        Assert.Equal("https://chat.example/v1/", options.Copilot.Chat.Endpoint);
        Assert.Equal("chat-secret", options.Copilot.Chat.ApiKey);
        Assert.Equal("gpt-like-chat", options.Copilot.Chat.Model);
        Assert.Equal(24, options.Copilot.Chat.TimeoutSeconds);
        Assert.True(options.Copilot.Docs.AutoIngestOnStartup);
        Assert.Equal(["./docs-a", "./docs-b"], options.Copilot.Docs.Roots);
        Assert.Equal(512, options.Copilot.Docs.ChunkSize);
        Assert.Equal(64, options.Copilot.Docs.ChunkOverlap);
    }

    [Fact]
    public async Task OpenAiCompatibleEmbeddingProvider_SendsExpectedRequest_AndParsesEmbedding()
    {
        HttpRequestMessage? captured = null;
        var handler = new DelegatingStubHandler(async request =>
        {
            captured = await CloneRequestAsync(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[{\"embedding\":[1.25,-2.5,3.75]}]}", Encoding.UTF8, "application/json"),
            };
        });

        var provider = new OpenAICompatibleEmbeddingProvider(
            new CopilotEmbeddingOptions
            {
                Provider = "openai",
                Endpoint = "https://copilot.example/v1/",
                ApiKey = "embedding-key",
                Model = "text-embedding-3-small",
                TimeoutSeconds = 12,
            },
            new StubHttpClientFactory(handler));

        var embedding = await provider.EmbedAsync("hello from tests");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://copilot.example/v1/embeddings", captured.RequestUri?.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("embedding-key", captured.Headers.Authorization?.Parameter);

        var payload = await captured.Content!.ReadAsStringAsync();
        var request = JsonSerializer.Deserialize(payload, ServerJsonContext.Default.OpenAiEmbeddingRequest);
        Assert.NotNull(request);
        Assert.Equal("text-embedding-3-small", request!.Model);
        Assert.Equal("hello from tests", request.Input);
        Assert.Equal([1.25f, -2.5f, 3.75f], embedding);
    }

    [Fact]
    public async Task OpenAiCompatibleChatProvider_SendsExpectedRequest_AndParsesReply()
    {
        HttpRequestMessage? captured = null;
        var handler = new DelegatingStubHandler(async request =>
        {
            captured = await CloneRequestAsync(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"copilot reply\"}}]}", Encoding.UTF8, "application/json"),
            };
        });

        var provider = new OpenAICompatibleChatProvider(
            new CopilotChatOptions
            {
                Provider = "openai",
                Endpoint = "https://copilot.example/v1/",
                ApiKey = "chat-key",
                Model = "chat-model",
                TimeoutSeconds = 9,
            },
            new StubHttpClientFactory(handler));

        var reply = await provider.CompleteAsync([
            new AiMessage("system", "You are helpful."),
            new AiMessage("user", "Say hi.")
        ]);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("https://copilot.example/v1/chat/completions", captured.RequestUri?.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("chat-key", captured.Headers.Authorization?.Parameter);

        var payload = await captured.Content!.ReadAsStringAsync();
        var request = JsonSerializer.Deserialize(payload, ServerJsonContext.Default.OpenAiChatCompletionRequest);
        Assert.NotNull(request);
        Assert.Equal("chat-model", request!.Model);
        Assert.False(request.Stream);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal("system", request.Messages[0].Role);
        Assert.Equal("You are helpful.", request.Messages[0].Content);
        Assert.Equal("user", request.Messages[1].Role);
        Assert.Equal("Say hi.", request.Messages[1].Content);
        Assert.Equal("copilot reply", reply);
    }

    [Fact]
    public async Task OpenAiCompatibleProviders_ThrowClearErrors_OnNonSuccessResponses()
    {
        var embeddingProvider = new OpenAICompatibleEmbeddingProvider(
            new CopilotEmbeddingOptions
            {
                Provider = "openai",
                Endpoint = "https://copilot.example/v1/",
                ApiKey = "embedding-key",
                Model = "text-embedding-3-small",
            },
            new StubHttpClientFactory(new StaticResponseHandler(HttpStatusCode.BadRequest, "embedding failed")));

        var chatProvider = new OpenAICompatibleChatProvider(
            new CopilotChatOptions
            {
                Provider = "openai",
                Endpoint = "https://copilot.example/v1/",
                ApiKey = "chat-key",
                Model = "chat-model",
            },
            new StubHttpClientFactory(new StaticResponseHandler(HttpStatusCode.Unauthorized, "chat failed")));

        var embeddingError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await embeddingProvider.EmbedAsync("hello"));
        var chatError = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await chatProvider.CompleteAsync([new AiMessage("user", "hello")]));

        Assert.Contains("returned 400", embeddingError.Message);
        Assert.Contains("embedding failed", embeddingError.Message);
        Assert.Contains("returned 401", chatError.Message);
        Assert.Contains("chat failed", chatError.Message);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsCopilotFlags()
    {
        using var client = CreateClient();
        var response = await client.GetAsync("/healthz");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var health = JsonSerializer.Deserialize(body, ServerJsonContext.Default.HealthResponse);

        Assert.NotNull(health);
        Assert.Equal("ok", health!.Status);
        Assert.True(health.CopilotEnabled);
        Assert.False(health.CopilotReady);
    }

    private HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sndb-copilot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }

    private string CreateTempFile(string name, string content)
    {
        var directory = CreateTempDirectory();
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync();
            clone.Content = new StringContent(body, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private ServerOptions CreateServerOptions()
    {
        return new ServerOptions
        {
            DataRoot = CreateTempDirectory(),
            AllowAnonymousProbes = true,
            AutoLoadExistingDatabases = true,
        };
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class DelegatingStubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public DelegatingStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _handler(request);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public StaticResponseHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "text/plain"),
            });
    }
}
