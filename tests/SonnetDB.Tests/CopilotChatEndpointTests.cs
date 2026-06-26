using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// Cloud-only Copilot endpoint bridge tests.
/// </summary>
public sealed class CopilotChatEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "copilot-admin-token";
    private const string DatabaseName = "alpha";

    private WebApplication? _app;
    private string _baseUrl = string.Empty;
    private string? _dataRoot;
    private FakeCloudGatewayClient? _cloud;

    public async Task InitializeAsync()
    {
        _dataRoot = CreateTempDirectory("sndb-copilot-cloud-data-");
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
            },
        };
        options.Copilot.Enabled = true;
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;
        _cloud = new FakeCloudGatewayClient();

        _app = TestServerHost.Build(
            options,
            services => services.AddSingleton<ICopilotCloudGatewayClient>(_cloud));
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var admin = CreateClient(AdminToken);
        await CreateDatabaseAsync(admin, DatabaseName);
        await ExecuteSqlAsync(admin, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, temp FIELD INT)");
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        DeleteDirectory(_dataRoot);
    }

    [Fact]
    public async Task CopilotChat_WhenCloudNotBound_ReturnsCloudNotBound()
    {
        SaveCloudConfig(accessToken: string.Empty);
        using var client = CreateClient(AdminToken);

        var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(new CopilotChatRequest(DatabaseName, "cpu 表有哪些字段？"), ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cloud_not_bound", body, StringComparison.Ordinal);
        Assert.Empty(_cloud!.ChatRequests);
    }

    [Fact]
    public async Task CopilotChat_WhenCloudTokenExpired_ReturnsCloudTokenExpired()
    {
        SaveCloudConfig(expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(-1));
        using var client = CreateClient(AdminToken);

        var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(new CopilotChatRequest(DatabaseName, "cpu 表有哪些字段？"), ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cloud_token_expired", body, StringComparison.Ordinal);
        Assert.Empty(_cloud!.ChatRequests);
    }

    [Fact]
    public async Task CopilotChat_WithoutDatabaseGrant_ReturnsForbiddenBeforeCloudCall()
    {
        SaveCloudConfig();
        using var admin = CreateClient(AdminToken);
        await ExecuteSqlAsync(admin, "CREATE USER nogrant WITH PASSWORD 'p'");
        var token = await LoginAsync("nogrant", "p");

        using var client = CreateClient(token);
        var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(new CopilotChatRequest(DatabaseName, "cpu 表有哪些字段？"), ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("forbidden", body, StringComparison.Ordinal);
        Assert.Empty(_cloud!.ChatRequests);
    }

    [Fact]
    public async Task CopilotChat_WithCloudFinal_ReturnsNdjsonAndForwardsContext()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("start", message: "cloud-start"),
            CloudEvent("retrieval", message: "cloud-retrieval", skills: ["schema-design"]),
            CloudEvent("final", answer: "cpu measurement 包含 host、usage 和 temp。"),
            CloudEvent("done", message: "completed"));

        using var client = await CreateReaderClientAsync("reader_ndjson");
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    Messages: [new AiMessage("user", "cpu 表有哪些字段？")],
                    Mode: "read-only",
                    CloudMode: "sql_assist",
                    ConversationId: "session-1"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(["start", "retrieval", "final", "done"], events.Select(static evt => evt.Type));
        Assert.Equal(["schema-design"], events.Single(static evt => evt.Type == "retrieval").SkillNames);
        Assert.Contains("usage", events.Single(static evt => evt.Type == "final").Answer ?? string.Empty, StringComparison.Ordinal);

        var cloudRequest = Assert.Single(_cloud.ChatRequests);
        Assert.Equal("session-1", cloudRequest.ConversationId);
        Assert.Equal("sql_assist", cloudRequest.Mode);
        Assert.Equal(DatabaseName, cloudRequest.Database?.Name);
        Assert.Contains("tool:query_sql", cloudRequest.Client.Capabilities);
        var measurement = Assert.Single(cloudRequest.Context.Measurements ?? []);
        Assert.Equal("cpu", measurement.Name);
        Assert.Contains(measurement.Fields ?? [], field => field.Name == "usage");
    }

    [Fact]
    public async Task CopilotChatStream_WhenCloudNeedsToolResult_ExecutesLocalToolAndContinues()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "describe_measurement",
                """{"measurement":"cpu"}""",
                requestId: "req-1",
                toolCallId: "tool-1"),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.EnqueueChat(
            CloudEvent("final", answer: "本地 schema 显示 cpu 有 host、usage、temp。"),
            CloudEvent("done", message: "completed"));

        using var client = await CreateReaderClientAsync("reader_tool");
        using var response = await client.PostAsync(
            "/v1/copilot/chat/stream",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "描述 cpu", ConversationId: "session-tool"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = await ReadSseEventsAsync(response);
        Assert.Contains(events, static evt => evt.Type == "tool_call" && evt.ToolName == "describe_measurement");
        var toolResult = Assert.Single(events, static evt => evt.Type == "tool_result");
        Assert.Contains("usage", toolResult.ToolResult ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("temp", events.Single(static evt => evt.Type == "final").Answer ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("done", events[^1].Type);

        var submitted = Assert.Single(_cloud.ToolResults);
        Assert.Equal("session-tool", submitted.ConversationId);
        Assert.Equal("req-1", submitted.RequestId);
        Assert.Equal("tool-1", submitted.ToolCallId);
        Assert.True(submitted.Result?.Ok);
        Assert.Equal(2, _cloud.ChatRequests.Count);
    }

    [Fact]
    public async Task CopilotChat_WhenCloudToolRequiresConfirmation_RejectsWithoutExecuting()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "execute_sql",
                """{"sql":"CREATE MEASUREMENT danger (value FIELD FLOAT)"}""",
                requiresConfirmation: true,
                requestId: "req-danger",
                toolCallId: "tool-danger"),
            CloudEvent("done", message: "waiting for confirmation"));
        _cloud.EnqueueChat(
            CloudEvent("final", answer: "该写入需要本地确认，已阻止自动执行。"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "创建 danger 表", Mode: "read-write", ConversationId: "session-danger"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        var toolResult = Assert.Single(events, static evt => evt.Type == "tool_result");
        Assert.Contains("client_confirmation_required", toolResult.ToolResult ?? string.Empty, StringComparison.Ordinal);

        var submitted = Assert.Single(_cloud!.ToolResults);
        Assert.False(submitted.Result?.Ok);
        Assert.True(submitted.Result?.Rejected);
        Assert.Equal("client_confirmation_required", submitted.Result?.ErrorCode);

        var showMeasurementsBody = await ExecuteSqlBodyAsync(client, DatabaseName, "SHOW MEASUREMENTS");
        Assert.DoesNotContain("danger", showMeasurementsBody, StringComparison.OrdinalIgnoreCase);
    }

    private void SaveCloudConfig(
        string accessToken = "cloud-access-token",
        DateTimeOffset? expiresAtUtc = null)
    {
        var store = _app!.Services.GetRequiredService<AiConfigStore>();
        store.Save(new AiOptions
        {
            Enabled = true,
            GatewayBaseUrl = "https://ai.sonnetdb.com",
            PlatformApiBaseUrl = "https://api.sonnetdb.com",
            CloudAccessToken = accessToken,
            CloudRefreshToken = "cloud-refresh-token",
            CloudTokenType = "Bearer",
            CloudAccessTokenExpiresAtUtc = expiresAtUtc ?? DateTimeOffset.UtcNow.AddHours(1),
            CloudScope = "ai.invoke",
            CloudBoundAtUtc = DateTimeOffset.UtcNow,
            TimeoutSeconds = 60,
        });
        _cloud!.Reset();
    }

    private async Task<HttpClient> CreateReaderClientAsync(string userName)
    {
        using var admin = CreateClient(AdminToken);
        await ExecuteSqlAsync(admin, $"CREATE USER {userName} WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, $"GRANT READ ON DATABASE {DatabaseName} TO {userName}");
        var token = await LoginAsync(userName, "p");
        return CreateClient(token);
    }

    private HttpClient CreateClient(string? token = null)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        using var client = CreateClient();
        var response = await client.PostAsync(
            "/v1/auth/login",
            JsonContent.Create(new LoginRequest(username, password), ServerJsonContext.Default.LoginRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"登录失败：{(int)response.StatusCode} {body}");

        var login = JsonSerializer.Deserialize(body, ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);
        return login!.Token;
    }

    private async Task ExecuteSqlAsync(HttpClient client, string sql)
        => await ExecuteSqlAsync(client, DatabaseName, sql);

    private static async Task ExecuteSqlAsync(HttpClient client, string databaseName, string sql)
        => await ExecuteSqlBodyAsync(client, databaseName, sql);

    private static async Task<string> ExecuteSqlBodyAsync(HttpClient client, string databaseName, string sql)
    {
        var response = await client.PostAsync(
            $"/v1/db/{databaseName}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"执行 SQL 失败：{(int)response.StatusCode} {body}");
        return body;
    }

    private static async Task CreateDatabaseAsync(HttpClient client, string databaseName)
    {
        var response = await client.PostAsync(
            "/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(databaseName), ServerJsonContext.Default.CreateDatabaseRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"创建数据库失败：{(int)response.StatusCode} {body}");
    }

    private static CopilotCloudRuntimeEvent CloudEvent(
        string type,
        string? message = null,
        string? answer = null,
        IReadOnlyCollection<string>? skills = null)
        => new(
            Type: type,
            RequestId: "req-" + type,
            ConversationId: "session-1",
            Message: message,
            Answer: answer,
            Skills: skills);

    private static CopilotCloudRuntimeEvent ToolRequiredEvent(
        string name,
        string argumentsJson,
        bool requiresConfirmation = false,
        string requestId = "req-tool",
        string toolCallId = "tool-call")
        => new(
            Type: "tool_result_required",
            RequestId: requestId,
            ConversationId: "session-tool",
            Tool: new CopilotCloudToolCallEvent(
                toolCallId,
                name,
                ParseJson(argumentsJson),
                requiresConfirmation,
                TimeoutSeconds: 30,
                MaxRows: 100,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5)));

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static async Task<List<CopilotChatEvent>> ReadNdjsonEventsAsync(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var events = new List<CopilotChatEvent>();
        while (await reader.ReadLineAsync() is { Length: > 0 } line)
        {
            var evt = JsonSerializer.Deserialize(line, ServerJsonContext.Default.CopilotChatEvent);
            Assert.NotNull(evt);
            events.Add(evt!);
            if (evt!.Type == "done")
                break;
        }

        return events;
    }

    private static async Task<List<CopilotChatEvent>> ReadSseEventsAsync(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var events = new List<CopilotChatEvent>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]")
                break;
            if (data.Length == 0)
                continue;

            var evt = JsonSerializer.Deserialize(data, ServerJsonContext.Default.CopilotChatEvent);
            Assert.NotNull(evt);
            events.Add(evt!);
        }

        return events;
    }

    private sealed class FakeCloudGatewayClient : ICopilotCloudGatewayClient
    {
        private readonly Queue<IReadOnlyList<CopilotCloudRuntimeEvent>> _responses = new();

        public List<CopilotCloudChatRequest> ChatRequests { get; } = [];

        public List<CopilotCloudToolResultRequest> ToolResults { get; } = [];

        public void Reset()
        {
            _responses.Clear();
            ChatRequests.Clear();
            ToolResults.Clear();
        }

        public void EnqueueChat(params CopilotCloudRuntimeEvent[] events)
            => _responses.Enqueue(events);

        public Task<CopilotCloudChatResponse> ChatAsync(
            AiOptions options,
            CopilotCloudChatRequest request,
            CancellationToken cancellationToken)
        {
            ChatRequests.Add(request);
            var events = _responses.Count > 0
                ? _responses.Dequeue()
                : [CloudEvent("final", answer: "默认云端回答。"), CloudEvent("done", message: "completed")];
            return Task.FromResult(new CopilotCloudChatResponse(StatusCodes.Status200OK, "req-chat", events));
        }

        public Task<CopilotCloudToolResultResponse> SubmitToolResultAsync(
            AiOptions options,
            CopilotCloudToolResultRequest request,
            CancellationToken cancellationToken)
        {
            ToolResults.Add(request);
            return Task.FromResult(new CopilotCloudToolResultResponse(
                "tool_result",
                request.RequestId ?? "req-tool",
                request.ConversationId,
                request.ToolCallId ?? "tool-call",
                "local_tool",
                request.Result?.Ok == true ? "accepted" : "rejected",
                new CopilotCloudToolResultEvent(
                    request.ToolCallId ?? "tool-call",
                    "local_tool",
                    request.Result?.Ok == true,
                    request.Result?.Content ?? ParseJson("{}"))));
        }
    }
}
