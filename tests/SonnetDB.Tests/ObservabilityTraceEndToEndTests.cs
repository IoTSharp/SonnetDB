using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M17 #98：验证真实 HTTP、Copilot、本地工具、Core 查询与 Segment 读取形成同一条追踪链路。
/// </summary>
public sealed class ObservabilityTraceEndToEndTests : IAsyncLifetime
{
    private const string AdminToken = "otel-e2e-admin";
    private const string DatabaseName = "otel_e2e";

    private readonly List<Activity> _exportedActivities = [];
    private readonly TraceCloudGatewayClient _cloud = new();
    private WebApplication? _app;
    private string? _dataRoot;
    private string? _baseUrl;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-trace-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = false,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        options.Copilot.Enabled = true;
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;

        _app = TestServerHost.Build(options, services =>
        {
            services.AddSingleton<ICopilotCloudGatewayClient>(_cloud);
            services.ConfigureOpenTelemetryTracerProvider(builder =>
                builder.AddInMemoryExporter(_exportedActivities));
        });
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        _app.Services.GetRequiredService<AiConfigStore>().Save(new AiOptions
        {
            Enabled = true,
            CloudAccessToken = "otel-cloud-token",
            CloudAccessTokenExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
            CloudScope = "ai.invoke",
            CloudBoundAtUtc = DateTimeOffset.UtcNow,
        });

        using var client = CreateClient();
        await PostAsync(
            client,
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(DatabaseName), ServerJsonContext.Default.CreateDatabaseRequest));
        await ExecuteSqlAsync(client, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        await ExecuteSqlAsync(
            client,
            "INSERT INTO cpu (time, host, usage) VALUES (1000, 'edge-1', 0.5), (2000, 'edge-1', 0.7)");

        var registry = _app.Services.GetRequiredService<Hosting.TsdbRegistry>();
        Assert.True(registry.TryGet(DatabaseName, out var database));
        Assert.NotNull(database.FlushNow());

        lock (_exportedActivities)
        {
            _exportedActivities.Clear();
        }
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CopilotQuery_FromHttpToSegment_ExportsSingleParentedTrace()
    {
        _cloud.Enqueue(
            ToolRequiredEvent(
                "query_sql",
                """{"sql":"SELECT usage FROM cpu WHERE time >= 1000 AND time <= 1500"}"""),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.Enqueue(
            CloudEvent("final", answer: "查询完成。"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient();
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "查询 cpu 在 1000 到 1500 的 usage"),
                ServerJsonContext.Default.CopilotChatRequest));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await response.Content.ReadAsStringAsync();

        var tracerProvider = _app!.Services.GetRequiredService<TracerProvider>();
        Assert.True(tracerProvider.ForceFlush(10_000));

        Activity[] trace;
        lock (_exportedActivities)
        {
            var chat = Assert.Single(_exportedActivities, static item => item.DisplayName == "copilot.chat");
            trace = _exportedActivities.Where(item => item.TraceId == chat.TraceId).ToArray();
        }

        var copilotChat = Assert.Single(trace, static item => item.DisplayName == "copilot.chat");
        var http = Assert.Single(trace, item => item.SpanId == copilotChat.ParentSpanId);
        var tool = Assert.Single(trace, static item => item.DisplayName == "copilot.agent.run_tool");
        var query = Assert.Single(trace, static item => item.DisplayName == "sonnetdb.query.points");
        var segmentRead = Assert.Single(trace, static item => item.DisplayName == "sonnetdb.segment.read");

        Assert.Equal(ActivityKind.Server, http.Kind);
        Assert.Equal(http.SpanId, copilotChat.ParentSpanId);
        Assert.Equal(copilotChat.SpanId, tool.ParentSpanId);
        Assert.Equal(tool.SpanId, query.ParentSpanId);
        Assert.Equal(query.SpanId, segmentRead.ParentSpanId);
        Assert.Equal("query_sql", tool.GetTagItem("tool.name"));
        Assert.Equal(false, segmentRead.GetTagItem("sonnetdb.segment.cache.hit"));
        Assert.NotNull(segmentRead.GetTagItem("sonnetdb.segment.id"));
    }

    private HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    private static async Task ExecuteSqlAsync(HttpClient client, string sql)
        => await PostAsync(
            client,
            $"/v1/db/{DatabaseName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));

    private static async Task PostAsync(HttpClient client, string path, HttpContent content)
    {
        using var response = await client.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"请求失败：{(int)response.StatusCode} {body}");
    }

    private static CopilotCloudRuntimeEvent CloudEvent(string type, string? message = null, string? answer = null)
        => new(
            Type: type,
            RequestId: "req-" + type,
            ConversationId: "trace-session",
            Message: message,
            Answer: answer);

    private static CopilotCloudRuntimeEvent ToolRequiredEvent(string name, string argumentsJson)
        => new(
            Type: "tool_result_required",
            RequestId: "req-tool",
            ConversationId: "trace-session",
            Tool: new CopilotCloudToolCallEvent(
                "trace-tool-call",
                name,
                ParseJson(argumentsJson),
                RequiresConfirmation: false,
                TimeoutSeconds: 30,
                MaxRows: 100,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1)));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TraceCloudGatewayClient : ICopilotCloudGatewayClient
    {
        private readonly Queue<IReadOnlyList<CopilotCloudRuntimeEvent>> _responses = new();

        public void Enqueue(params CopilotCloudRuntimeEvent[] events) => _responses.Enqueue(events);

        public Task<CopilotCloudChatResponse> ChatAsync(
            AiOptions options,
            CopilotCloudChatRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new CopilotCloudChatResponse(
                StatusCodes.Status200OK,
                "trace-request",
                _responses.Dequeue()));

        public Task<CopilotCloudToolResultResponse> SubmitToolResultAsync(
            AiOptions options,
            CopilotCloudToolResultRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(new CopilotCloudToolResultResponse(
                "tool_result",
                request.RequestId ?? "req-tool",
                request.ConversationId,
                request.ToolCallId ?? "trace-tool-call",
                "query_sql",
                request.Result?.Ok == true ? "accepted" : "rejected",
                new CopilotCloudToolResultEvent(
                    request.ToolCallId ?? "trace-tool-call",
                    "query_sql",
                    request.Result?.Ok == true,
                    request.Result?.Content ?? ParseJson("{}"))));
    }
}
