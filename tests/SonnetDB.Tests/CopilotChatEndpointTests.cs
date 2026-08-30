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
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.Tests.Copilot;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// Cloud-only Copilot endpoint bridge tests.
/// </summary>
[Collection(CopilotTestCollection.Name)]
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
    public async Task CopilotChat_UsesConfiguredChatProvider_WhenCloudIsNotBound()
    {
        var dataRoot = CreateTempDirectory("sndb-copilot-local-data-");
        var options = new ServerOptions
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };
        options.Copilot.Chat.Provider = "openai";
        options.Copilot.Chat.Endpoint = "http://127.0.0.1:19090/v1/";
        options.Copilot.Chat.ApiKey = "local-test-key";
        options.Copilot.Chat.Model = "local-test-model";
        options.Copilot.Embedding.Provider = "builtin";
        options.Copilot.Docs.AutoIngestOnStartup = false;
        options.Copilot.Skills.AutoIngestOnStartup = false;
        var provider = new QueueChatProvider(
            "{\"tools\":[]}",
            "local provider answer");

        var app = TestServerHost.Build(options, services => services.AddSingleton<IChatProvider>(provider));
        try
        {
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            using var client = new HttpClient { BaseAddress = new Uri(addresses.Addresses.First()) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);

            using var systemOnlyResponse = await client.PostAsync(
                "/v1/copilot/chat",
                JsonContent.Create(
                    new CopilotChatRequest(
                        DatabaseName,
                        Message: null,
                        Messages: [new AiMessage("system", "仅系统消息")]),
                    ServerJsonContext.Default.CopilotChatRequest));
            Assert.Equal(HttpStatusCode.BadRequest, systemOnlyResponse.StatusCode);
            Assert.Contains("user message", await systemOnlyResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            await CreateDatabaseAsync(client, DatabaseName);

            using var response = await client.PostAsync(
                "/v1/copilot/chat",
                JsonContent.Create(
                    new CopilotChatRequest(DatabaseName, "请直接回答本地 provider 测试", DocsK: 0, SkillsK: 0),
                    ServerJsonContext.Default.CopilotChatRequest));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var events = await ReadNdjsonEventsAsync(response);
            Assert.Equal("local provider answer", Assert.Single(events, static evt => evt.Type == "final").Answer);
            Assert.Equal(2, provider.CallCount);

            using var writeResponse = await client.PostAsync(
                "/v1/copilot/chat",
                JsonContent.Create(
                    new CopilotChatRequest(DatabaseName, "请写入数据", Mode: "read-write"),
                    ServerJsonContext.Default.CopilotChatRequest));
            Assert.Equal(HttpStatusCode.Conflict, writeResponse.StatusCode);
            Assert.Contains("local_write_confirmation_required", await writeResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            DeleteDirectory(dataRoot);
        }
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
    public async Task CopilotChat_WithModelOverride_ForwardsModelToCloudRuntime()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "已使用指定模型。"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "使用本地模型分析 cpu")
                {
                    Model = "local-qwen",
                },
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        _ = await ReadNdjsonEventsAsync(response);
        Assert.Equal("local-qwen", Assert.Single(_cloud.ChatRequests).Model);
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
    public async Task CopilotChatStream_WithToolCall_EmitsStableRelayIdentifiers()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "describe_measurement",
                """{"measurement":"cpu"}""",
                requestId: "req-relay-identifiers",
                toolCallId: "tool-relay-identifiers"),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.EnqueueChat(
            CloudEvent("final", answer: "cpu schema ready"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat/stream",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "描述 cpu",
                    ConversationId: "relay-identifiers-conversation")
                {
                    RunId = "relay-identifiers-run",
                },
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadSseEventsAsync(response);
        Assert.Equal([1L, 2L, 3L, 4L], events.Select(static evt => evt.Sequence));
        Assert.All(events, static evt => Assert.Equal("relay-identifiers-run", evt.RunId));
        Assert.Equal(
            events.Select(static evt => $"relay-identifiers-run:{evt.Sequence}"),
            events.Select(static evt => evt.Cursor));

        var toolCall = Assert.Single(events, static evt => evt.Type == "tool_call");
        var toolResult = Assert.Single(events, static evt => evt.Type == "tool_result");
        Assert.Equal("tool-relay-identifiers", toolCall.ToolCallId);
        Assert.Equal(toolCall.ToolCallId, toolResult.ToolCallId);
    }

    [Fact]
    public async Task CopilotChatStream_WithKnownCursor_ReplaysTailWithoutCloudReexecution()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "stable relay answer"),
            CloudEvent("done", message: "completed"));
        var request = new CopilotChatRequest(
            DatabaseName,
            "重放同一运行",
            ConversationId: "relay-replay-conversation")
        {
            RunId = "relay-replay-run",
        };

        using var client = CreateClient(AdminToken);
        using var firstResponse = await client.PostAsync(
            "/v1/copilot/chat/stream",
            JsonContent.Create(request, ServerJsonContext.Default.CopilotChatRequest));
        var firstEvents = await ReadSseEventsAsync(firstResponse);
        Assert.Equal(["final", "done"], firstEvents.Select(static evt => evt.Type));

        using var replayResponse = await client.PostAsync(
            "/v1/copilot/chat/stream",
            JsonContent.Create(
                request with { Cursor = firstEvents[0].Cursor },
                ServerJsonContext.Default.CopilotChatRequest));
        var replayEvents = await ReadSseEventsAsync(replayResponse);

        var replayed = Assert.Single(replayEvents);
        Assert.Equal("done", replayed.Type);
        Assert.Equal(2, replayed.Sequence);
        Assert.Equal("relay-replay-run:2", replayed.Cursor);
        Assert.Single(_cloud.ChatRequests);
    }

    [Fact]
    public async Task CopilotChat_WithConflictingRunShape_RejectsBeforeCloudCall()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "first answer"),
            CloudEvent("done", message: "completed"));
        var request = new CopilotChatRequest(
            DatabaseName,
            "first question",
            ConversationId: "relay-conflict-conversation")
        {
            RunId = "relay-conflict-run",
        };

        using var client = CreateClient(AdminToken);
        using var firstResponse = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(request, ServerJsonContext.Default.CopilotChatRequest));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        _ = await ReadNdjsonEventsAsync(firstResponse);

        using var conflictResponse = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                request with { Message = "different question" },
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Contains("relay_run_conflict", await conflictResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(_cloud.ChatRequests);
    }

    [Fact]
    public async Task CopilotChat_WithUnknownCursor_RejectsBeforeCloudCall()
    {
        SaveCloudConfig();
        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "unknown relay run")
                {
                    RunId = "relay-unknown-run",
                    Cursor = "relay-unknown-run:1",
                },
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Contains("relay_run_unknown", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(_cloud!.ChatRequests);
    }

    [Fact]
    public async Task CopilotChat_WithFutureCursor_RejectsBeforeCloudReexecution()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "cursor answer"),
            CloudEvent("done", message: "completed"));
        var request = new CopilotChatRequest(
            DatabaseName,
            "cursor bounds")
        {
            RunId = "relay-cursor-run",
        };

        using var client = CreateClient(AdminToken);
        using var firstResponse = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(request, ServerJsonContext.Default.CopilotChatRequest));
        _ = await ReadNdjsonEventsAsync(firstResponse);

        using var invalidResponse = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                request with { Cursor = "relay-cursor-run:99" },
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.Conflict, invalidResponse.StatusCode);
        Assert.Contains("relay_cursor_invalid", await invalidResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Single(_cloud.ChatRequests);
    }

    [Fact]
    public void ServerRelayRun_WithLocalToolEvents_AssignsOneStableToolCallId()
    {
        var store = new CopilotServerRelayRunStore();
        var attached = store.Attach(
            "relay-local-tool-run",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"));
        var run = Assert.IsType<CopilotServerRelayRun>(attached.Run);

        var toolCall = run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 1\"}"));
        var retry = run.Publish(new CopilotChatEvent(
            "tool_retry",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 2\"}",
            Attempt: 1));
        var result = run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[]}"));
        _ = run.Publish(new CopilotChatEvent("final", Answer: "done"));
        _ = run.Publish(new CopilotChatEvent("done", Message: "completed"));
        run.Complete();

        Assert.Equal("relay-local-tool-run:tool:1", toolCall.ToolCallId);
        Assert.Equal(toolCall.ToolCallId, retry.ToolCallId);
        Assert.Equal(toolCall.ToolCallId, result.ToolCallId);
        Assert.Equal([1L, 2L, 3L], new[] { toolCall.Sequence, retry.Sequence, result.Sequence });
    }

    [Fact]
    public void ServerRelayRun_WithConcurrentOrDuplicateToolCall_RejectsLifecycleConflict()
    {
        var run = CreateRelayRun("relay-tool-lifecycle");
        _ = run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 1\"}")
        {
            ToolCallId = "tool-lifecycle-1",
        });

        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 2\"}")
        {
            ToolCallId = "tool-lifecycle-2",
        }));
        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 1\"}")
        {
            ToolCallId = "tool-lifecycle-1",
        }));

        _ = run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[]}")
        {
            ToolCallId = "tool-lifecycle-1",
        });
        _ = run.Publish(new CopilotChatEvent("final", Answer: "done"));
        _ = run.Publish(new CopilotChatEvent("done", Message: "completed"));
        run.Complete();
    }

    [Fact]
    public async Task ServerRelayRun_WithActiveToolCall_RejectsFinalButAllowsErrorSeal()
    {
        var run = CreateRelayRun("relay-active-tool-final");
        _ = run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 1\"}"));

        Assert.Throws<InvalidOperationException>(() =>
            run.Publish(new CopilotChatEvent("final", Answer: "incomplete success")));
        _ = run.Publish(new CopilotChatEvent("error", Message: "tool interrupted"));
        _ = run.Publish(new CopilotChatEvent("done", Message: "completed"));
        run.Complete();

        Assert.Equal(
            ["tool_call", "error", "done"],
            (await ReadRelayEventsAsync(run)).Select(static evt => evt.Type));
    }

    [Fact]
    public void ServerRelayRun_WithWrongNameOrCompletedToolCall_RejectsStaleEvents()
    {
        var run = CreateRelayRun("relay-tool-stale");
        _ = run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 1\"}")
        {
            ToolCallId = "tool-stale-1",
        });

        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_retry",
            ToolName: "execute_sql",
            Attempt: 1)
        {
            ToolCallId = "tool-stale-1",
        }));
        _ = run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[]}")
        {
            ToolCallId = "tool-stale-1",
        });
        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_retry",
            ToolName: "query_sql",
            Attempt: 2)
        {
            ToolCallId = "tool-stale-1",
        }));
        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[]}")
        {
            ToolCallId = "tool-stale-1",
        }));

        _ = run.Publish(new CopilotChatEvent("final", Answer: "done"));
        _ = run.Publish(new CopilotChatEvent("done", Message: "completed"));
        run.Complete();
    }

    [Fact]
    public void ServerRelayRun_WithCompletedExactReplay_AcceptsOnlyEquivalentIdentityAndResult()
    {
        var run = CreateRelayRun("relay-tool-replay");
        _ = run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 1\"}")
        {
            ToolCallId = "tool-replay-1",
        });
        _ = run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[]}")
        {
            ToolCallId = "tool-replay-1",
        });

        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{\"sql\":\"SELECT 2\"}")
        {
            ToolCallId = "tool-replay-1",
        }));
        _ = run.Publish(new CopilotChatEvent(
            "tool_call",
            ToolName: "query_sql",
            ToolArguments: "{ \"sql\" : \"SELECT 1\" }")
        {
            ToolCallId = "tool-replay-1",
        });
        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_retry",
            ToolName: "query_sql",
            Attempt: 1)
        {
            ToolCallId = "tool-replay-1",
        }));
        Assert.Throws<InvalidOperationException>(() => run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[1]}")
        {
            ToolCallId = "tool-replay-1",
        }));
        var replayResult = run.Publish(new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{ \"rows\" : [] }")
        {
            ToolCallId = "tool-replay-1",
        });

        Assert.Equal("tool-replay-1", replayResult.ToolCallId);
        Assert.Equal(4, replayResult.Sequence);
        _ = run.Publish(new CopilotChatEvent("final", Answer: "done"));
        _ = run.Publish(new CopilotChatEvent("done", Message: "completed"));
        run.Complete();
    }

    [Theory]
    [InlineData("final")]
    [InlineData("error")]
    public async Task ServerRelayRun_WithOversizedOutcome_RejectsBeforeConsumingDoneReserve(
        string outcomeType)
    {
        var run = CreateRelayRun("relay-outcome-capacity");
        var oversized = new string('x', 4_194_200);
        var outcome = string.Equals(outcomeType, "final", StringComparison.Ordinal)
            ? new CopilotChatEvent("final", Answer: oversized)
            : new CopilotChatEvent("error", Message: oversized);

        Assert.Throws<InvalidOperationException>(() => run.Publish(outcome));
        Assert.Null(Record.Exception(() => run.Fail("capacity closed")));
        Assert.Null(Record.Exception(run.Complete));

        var events = await ReadRelayEventsAsync(run);
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal([1L, 2L], events.Select(static evt => evt.Sequence));
    }

    [Fact]
    public async Task ServerRelayRun_WhenDeadlineCallbackThrows_StillClosesWithoutLeakingException()
    {
        var completed = new TaskCompletionSource<CopilotServerRelayRun>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new CopilotServerRelayRun(
            "relay-throwing-deadline-callback",
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            closed => completed.TrySetResult(closed));
        using var registration = run.DeadlineToken.Register(
            static () => throw new InvalidOperationException("deadline callback failed"));

        Assert.Null(Record.Exception(run.Expire));
        Assert.Same(run, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            ["error", "done"],
            (await ReadRelayEventsAsync(run)).Select(static evt => evt.Type));
    }

    [Fact]
    public void ServerRelayRun_WhenCompletionCallbackThrows_DoesNotLeakException()
    {
        var run = new CopilotServerRelayRun(
            "relay-throwing-completion-callback",
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            static _ => throw new InvalidOperationException("completion callback failed"));
        run.Fail("closed");

        Assert.Null(Record.Exception(run.Complete));
        Assert.Throws<InvalidOperationException>(() =>
            run.Publish(new CopilotChatEvent("start", Message: "late")));
    }

    [Fact]
    public void CopilotContracts_KeepLegacyPositionalConstructors()
    {
        Assert.NotNull(typeof(CopilotChatRequest).GetConstructor(
        [
            typeof(string),
            typeof(string),
            typeof(List<AiMessage>),
            typeof(int?),
            typeof(int?),
            typeof(string),
            typeof(string),
            typeof(string),
        ]));
        Assert.NotNull(typeof(CopilotChatEvent).GetConstructor(
        [
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<CopilotCitation>),
            typeof(int?),
        ]));
    }

    [Fact]
    public void ServerRelayRunStore_WithCompletedRuns_DoesNotConsumeActiveCapacity()
    {
        var store = new CopilotServerRelayRunStore();
        for (var index = 0; index < 80; index++)
        {
            var attached = store.Attach(
                $"relay-capacity-{index}",
                cursor: null,
                new CopilotServerRelayRunBinding("owner", DatabaseName, $"fingerprint-{index}"));
            Assert.Equal(CopilotServerRelayAttachStatus.Created, attached.Status);
            attached.Run!.Fail("closed");
            attached.Run.Complete();
        }

        var next = store.Attach(
            "relay-capacity-next",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint-next"));
        Assert.Equal(CopilotServerRelayAttachStatus.Created, next.Status);
        next.Run!.Fail("closed");
        next.Run.Complete();

        var retired = store.Attach(
            "relay-capacity-0",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint-0"));
        Assert.Equal(CopilotServerRelayAttachStatus.Expired, retired.Status);
    }

    [Fact]
    public void ServerRelayRunStore_WhenActiveCapacityIsFull_RejectsUntilRunCompletes()
    {
        var store = new CopilotServerRelayRunStore();
        var activeRuns = new List<CopilotServerRelayRun>();
        for (var index = 0; index < 64; index++)
        {
            var attached = store.Attach(
                $"relay-active-{index}",
                cursor: null,
                new CopilotServerRelayRunBinding("owner", DatabaseName, $"fingerprint-{index}"));
            Assert.Equal(CopilotServerRelayAttachStatus.Created, attached.Status);
            activeRuns.Add(attached.Run!);
        }

        var rejected = store.Attach(
            "relay-active-overflow",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint-overflow"));
        Assert.Equal(CopilotServerRelayAttachStatus.CapacityExceeded, rejected.Status);
        Assert.Null(rejected.Run);

        foreach (var run in activeRuns)
        {
            run.Fail("closed");
            run.Complete();
        }
    }

    [Fact]
    public void ServerRelayRunStore_ScopesRunIdByOwner()
    {
        var store = new CopilotServerRelayRunStore();
        var ownerA = store.Attach(
            "shared-run-id",
            cursor: null,
            new CopilotServerRelayRunBinding("owner-a", DatabaseName, "fingerprint-a"));
        var ownerB = store.Attach(
            "shared-run-id",
            cursor: null,
            new CopilotServerRelayRunBinding("owner-b", DatabaseName, "fingerprint-b"));

        Assert.Equal(CopilotServerRelayAttachStatus.Created, ownerA.Status);
        Assert.Equal(CopilotServerRelayAttachStatus.Created, ownerB.Status);
        Assert.NotSame(ownerA.Run, ownerB.Run);
        ownerA.Run!.Fail("closed");
        ownerA.Run.Complete();
        ownerB.Run!.Fail("closed");
        ownerB.Run.Complete();
    }

    [Fact]
    public void ServerRelayRun_WhenDonePrecedesOutcome_RejectsEvent()
    {
        var store = new CopilotServerRelayRunStore();
        var attached = store.Attach(
            "relay-done-before-outcome",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            attached.Run!.Publish(new CopilotChatEvent("done", Message: "completed")));
        Assert.Contains("final/error", exception.Message, StringComparison.Ordinal);
        attached.Run!.Fail("closed");
        attached.Run.Complete();
    }

    [Fact]
    public async Task ServerRelayRun_WhenActiveDeadlineExpires_ClosesAsErrorOutcome()
    {
        var completed = new TaskCompletionSource<CopilotServerRelayRun>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new CopilotServerRelayRun(
            "relay-deadline",
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"),
            DateTimeOffset.UtcNow.AddMilliseconds(25),
            closed => completed.TrySetResult(closed));

        var closedRun = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(run, closedRun);
        Assert.True(run.DeadlineToken.IsCancellationRequested);
        var events = new List<CopilotChatEvent>();
        await foreach (var evt in run.ReadAfterAsync(0, CancellationToken.None))
            events.Add(evt);

        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Throws<InvalidOperationException>(() =>
            run.Publish(new CopilotChatEvent("start", Message: "late")));
    }

    [Fact]
    public async Task ServerRelayRun_WithPastDeadline_InvokesCompletionAfterTimerFieldInitialization()
    {
        var completed = new TaskCompletionSource<CopilotServerRelayRun>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new CopilotServerRelayRun(
            "relay-immediate-deadline",
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"),
            DateTimeOffset.UtcNow.AddSeconds(-1),
            closed => completed.TrySetResult(closed));

        Assert.Same(run, await completed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(
            ["error", "done"],
            (await ReadRelayEventsAsync(run)).Select(static evt => evt.Type));
    }

    [Fact]
    public async Task CopilotChatStream_WhenRelayDeadlineExpires_ReturnsJournaledTerminalTailToCurrentClient()
    {
        string dataRoot = CreateTempDirectory("sndb-copilot-relay-deadline-");
        var options = new ServerOptions
        {
            DataRoot = dataRoot,
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
        var cloud = new FakeCloudGatewayClient
        {
            BlockChatUntilCancellation = true,
        };
        var relayRuns = new CopilotServerRelayRunStore(TimeSpan.FromMilliseconds(100));
        WebApplication app = TestServerHost.Build(
            options,
            services =>
            {
                services.AddSingleton<ICopilotCloudGatewayClient>(cloud);
                services.AddSingleton(relayRuns);
            });

        try
        {
            await app.StartAsync();
            app.Services.GetRequiredService<AiConfigStore>().Save(CreateBoundCloudOptions());
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
            using var client = new HttpClient
            {
                BaseAddress = new Uri(addresses.Addresses.First()),
                Timeout = TimeSpan.FromSeconds(10),
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", AdminToken);
            await CreateDatabaseAsync(client, DatabaseName);
            var request = new CopilotChatRequest(
                DatabaseName,
                "等待 relay deadline",
                ConversationId: "relay-deadline-current-client")
            {
                RunId = "relay-deadline-current-client-run",
            };

            using var response = await client.PostAsync(
                "/v1/copilot/chat/stream",
                JsonContent.Create(request, ServerJsonContext.Default.CopilotChatRequest));
            var events = await ReadSseEventsAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
            Assert.Equal([1L, 2L], events.Select(static evt => evt.Sequence));
            Assert.Contains("TTL", events[0].Message ?? string.Empty, StringComparison.Ordinal);
            Assert.True(cloud.ChatCancellationObserved);

            cloud.BlockChatUntilCancellation = false;
            using var replay = await client.PostAsync(
                "/v1/copilot/chat/stream",
                JsonContent.Create(
                    request with { Cursor = events[0].Cursor },
                    ServerJsonContext.Default.CopilotChatRequest));
            var replayEvents = await ReadSseEventsAsync(replay);
            Assert.Equal("done", Assert.Single(replayEvents).Type);
            Assert.Single(cloud.ChatRequests);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            DeleteDirectory(dataRoot);
        }
    }

    [Fact]
    public async Task CopilotLocalToolExecutor_QuerySql_CancelsControllablyBlockingSqlExecution()
    {
        using var admin = CreateClient(AdminToken);
        await ExecuteSqlAsync(
            admin,
            "CREATE TABLE relay_cancel_left (id INT, PRIMARY KEY (id))");
        await ExecuteSqlAsync(
            admin,
            "CREATE TABLE relay_cancel_right (id INT, PRIMARY KEY (id))");
        string values = string.Join(
            ',',
            Enumerable.Range(0, 10_000).Select(static value => $"({value})"));
        await ExecuteSqlAsync(
            admin,
            $"INSERT INTO relay_cancel_left (id) VALUES {values}");
        await ExecuteSqlAsync(
            admin,
            $"INSERT INTO relay_cancel_right (id) VALUES {values}");

        var registry = _app!.Services.GetRequiredService<TsdbRegistry>();
        Assert.True(registry.TryGet(DatabaseName, out var database));
        var httpContext = new DefaultHttpContext();
        httpContext.Items[BearerAuthMiddleware.RoleKey] = ServerRoles.Admin;
        using var cancellation = new CancellationTokenSource();
        var context = new CopilotLocalToolContext(
            httpContext,
            _app.Services.GetRequiredService<GrantsStore>(),
            DatabaseName,
            database,
            [DatabaseName],
            AllowWrite: false,
            CanUseControlPlane: false,
            cancellation.Token);
        var tool = new CopilotCloudToolCallEvent(
            "relay-cancel-tool",
            "query_sql",
            ParseJson(
                "{\"sql\":\"SELECT COUNT(*) AS total FROM relay_cancel_left l " +
                "JOIN relay_cancel_right r ON l.id >= 0\",\"maxRows\":10}"),
            RequiresConfirmation: false,
            TimeoutSeconds: 30,
            MaxRows: 10,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));
        var executor = _app.Services.GetRequiredService<CopilotLocalToolExecutor>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CopilotLocalToolResult> execution = Task.Run(() =>
        {
            started.TrySetResult();
            return executor.Execute(context, tool);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(25);
        Assert.False(execution.IsCompleted, "The SQL fixture completed before cancellation could be observed.");
        cancellation.Cancel();

        Exception exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }

    [Fact]
    public async Task RelayResponse_WhenProducerEndsWithoutOutcome_WritesSealedTailToCurrentClient()
    {
        var run = CreateRelayRun("relay-normal-truncation");
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var progress = new CopilotChatEndpointHandler.CopilotRelayResponseProgress();

        await CopilotChatEndpointHandler.CompleteRelayResponseAsync(
            context,
            run,
            progress,
            sse: false);

        body.Position = 0;
        using var reader = new StreamReader(body, leaveOpen: true);
        string payload = await reader.ReadToEndAsync();
        CopilotChatEvent[] events = payload
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize(
                line,
                ServerJsonContext.Default.CopilotChatEvent)!)
            .ToArray();
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal([1L, 2L], events.Select(static evt => evt.Sequence));
        Assert.Equal(2, progress.LastWrittenSequence);
    }

    [Fact]
    public void ServerRelayRun_ExplicitExpire_CancelsDeadlineToken()
    {
        var run = new CopilotServerRelayRun(
            "relay-explicit-expire",
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            static _ => { });

        run.Expire();

        Assert.True(run.DeadlineToken.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() =>
            run.Publish(new CopilotChatEvent("start", Message: "late")));
    }

    [Fact]
    public void ServerRelayRunStore_WhenReplayExpires_RetainsLightweightTombstone()
    {
        var store = new CopilotServerRelayRunStore();
        var binding = new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint");
        var attached = store.Attach("relay-replay-expired", cursor: null, binding);
        attached.Run!.Fail("closed");
        attached.Run.Complete();
        attached.Run.SetReplayExpiresAt(DateTimeOffset.UtcNow.AddMilliseconds(-1));

        var expired = store.Attach("relay-replay-expired", cursor: null, binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Expired, expired.Status);
        Assert.Null(expired.Run);
    }

    [Fact]
    public void ServerRelayRunStore_WhenIdentityCapacityIsFull_DoesNotEvictLiveTombstone()
    {
        var store = new CopilotServerRelayRunStore();
        for (var index = 0; index < 2048; index++)
        {
            var attached = store.Attach(
                $"relay-identity-{index}",
                cursor: null,
                new CopilotServerRelayRunBinding("owner", DatabaseName, $"fingerprint-{index}"));
            Assert.Equal(CopilotServerRelayAttachStatus.Created, attached.Status);
            attached.Run!.Fail("closed");
            attached.Run.Complete();
        }

        var overflow = store.Attach(
            "relay-identity-overflow",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint-overflow"));
        Assert.Equal(CopilotServerRelayAttachStatus.CapacityExceeded, overflow.Status);

        var firstIdentity = store.Attach(
            "relay-identity-0",
            cursor: null,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint-0"));
        Assert.Equal(CopilotServerRelayAttachStatus.Expired, firstIdentity.Status);
    }

    [Fact]
    public async Task CopilotChatStream_WhenCloudOmitsOutcome_ReturnsErrorBeforeDone()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("start", message: "cloud-start"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat/stream",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "分析 cpu"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadSseEventsAsync(response);
        Assert.Equal(["start", "error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal(
            "云端 Copilot 未返回 final/error 终态或本地工具调用，无法确认运行结果。",
            events[1].Message);
    }

    [Fact]
    public async Task CopilotChatStream_WhenCloudOmitsDone_RejectsTruncatedFinal()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(CloudEvent("final", answer: "不完整的答案"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat/stream",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "分析 cpu"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadSseEventsAsync(response);
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal("云端 Copilot 响应缺少 done 事件，无法确认响应完整性。", events[0].Message);
        Assert.DoesNotContain(events, static evt => evt.Type == "final");
    }

    [Fact]
    public async Task CopilotChat_WhenCloudSendsToolAfterFinal_RejectsWithoutSideEffect()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "已完成"),
            ToolRequiredEvent(
                "execute_sql",
                """{"sql":"CREATE MEASUREMENT late_side_effect (value FIELD FLOAT)"}""",
                requestId: "req-late-tool",
                toolCallId: "tool-late"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "创建 late_side_effect",
                    Mode: "read-write",
                    ConversationId: "session-late-tool"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal(
            "云端 Copilot 在 final/error 终态后返回了额外事件，已拒绝该响应。",
            events[0].Message);
        Assert.Empty(_cloud.ToolResults);
        var measurements = await ExecuteSqlBodyAsync(client, DatabaseName, "SHOW MEASUREMENTS");
        Assert.DoesNotContain("late_side_effect", measurements, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopilotChat_WhenCloudSendsMultipleOutcomes_RejectsBoth()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "first"),
            CloudEvent("error", message: "second"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "分析 cpu"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal("云端 Copilot 返回了多个 final/error 终态，已拒绝该响应。", events[0].Message);
        Assert.DoesNotContain(events, static evt => evt.Type == "final");
    }

    [Fact]
    public async Task CopilotChat_WhenCloudSendsEmptyFinal_RejectsOutcome()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            CloudEvent("final", answer: "   "),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(DatabaseName, "分析 cpu"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal("云端 Copilot 返回了空 final answer，已拒绝该响应。", events[0].Message);
    }

    [Fact]
    public async Task CopilotChat_PersistsConversationAndMetrics_ForCrossDeviceSync()
    {
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            new CopilotCloudRuntimeEvent(
                Type: "final",
                RequestId: "req-persist",
                ConversationId: "server-sync-1",
                Answer: "服务端会话已保存。",
                Model: "gpt-test",
                Usage: new CopilotCloudUsage(InputTokens: 21, OutputTokens: 9, TotalTokens: 30)),
            CloudEvent("done", message: "completed"));

        using (var firstDevice = CreateClient(AdminToken))
        {
            using var response = await firstDevice.PostAsync(
                "/v1/copilot/chat",
                JsonContent.Create(
                    new CopilotChatRequest(DatabaseName, "保存这次对话", ConversationId: "server-sync-1"),
                    ServerJsonContext.Default.CopilotChatRequest));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            _ = await ReadNdjsonEventsAsync(response);
        }

        using var secondDevice = CreateClient(AdminToken);
        var conversationsJson = await secondDevice.GetStringAsync("/v1/copilot/conversations");
        var conversations = JsonSerializer.Deserialize(
            conversationsJson,
            ServerJsonContext.Default.CopilotConversationListResponse);
        var conversation = Assert.Single(conversations!.Conversations);
        Assert.Equal("server-sync-1", conversation.Id);
        Assert.Equal(2, conversation.MessageCount);

        var messagesJson = await secondDevice.GetStringAsync("/v1/copilot/conversations/server-sync-1/messages");
        var messages = JsonSerializer.Deserialize(messagesJson, ServerJsonContext.Default.CopilotMessageListResponse);
        Assert.Equal(["user", "assistant"], messages!.Messages.Select(static message => message.Role));
        Assert.Equal("服务端会话已保存。", messages.Messages[1].Content);

        var metricsJson = await secondDevice.GetStringAsync("/v1/copilot/metrics?windowMinutes=60");
        var metrics = JsonSerializer.Deserialize(metricsJson, ServerJsonContext.Default.CopilotMetricsResponse);
        Assert.Equal(1, metrics!.RequestCount);
        Assert.Equal(21, metrics.InputTokens);
        Assert.Equal(9, metrics.OutputTokens);
        Assert.Equal(30, metrics.TotalTokens);
        Assert.False(metrics.IncludesEstimatedTokens);
        Assert.Equal("gpt-test", Assert.Single(metrics.Models).Model);
    }

    [Fact]
    public async Task CopilotConversations_AreIsolatedByAuthenticatedOwner()
    {
        using var admin = CreateClient(AdminToken);
        var created = await admin.PostAsync(
            "/v1/copilot/conversations",
            JsonContent.Create(
                new CopilotConversationUpsertRequest("admin-session", "管理员会话", DatabaseName),
                ServerJsonContext.Default.CopilotConversationUpsertRequest));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        await ExecuteSqlAsync(admin, "CREATE USER session_reader WITH PASSWORD 'p'");
        var readerToken = await LoginAsync("session_reader", "p");
        using var otherDevice = CreateClient(readerToken);
        var json = await otherDevice.GetStringAsync("/v1/copilot/conversations");
        var response = JsonSerializer.Deserialize(json, ServerJsonContext.Default.CopilotConversationListResponse);
        Assert.Empty(response!.Conversations);
    }

    [Fact]
    public async Task CopilotChat_WhenCloudReplaysToolCallInSameRound_ReusesResultInEventOrder()
    {
        const string arguments =
            """{"sql":"CREATE MEASUREMENT replay_same_round (value FIELD FLOAT)"}""";
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "execute_sql",
                arguments,
                requestId: "req-replay-same-round-1",
                toolCallId: "tool-replay-same-round"),
            ToolRequiredEvent(
                "execute_sql",
                arguments,
                requestId: "req-replay-same-round-2",
                toolCallId: "tool-replay-same-round"),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.EnqueueChat(
            CloudEvent("final", answer: "已创建 replay_same_round。"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "创建 replay_same_round",
                    Mode: "read-write",
                    ConversationId: "session-replay-same-round"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(
            ["tool_call", "tool_result", "tool_call", "tool_result", "final", "done"],
            events.Select(static evt => evt.Type));
        var emittedResults = events.Where(static evt => evt.Type == "tool_result").ToArray();
        Assert.Equal(2, emittedResults.Length);
        Assert.Equal(emittedResults[0].ToolResult, emittedResults[1].ToolResult);

        Assert.Equal(2, _cloud.ToolResults.Count);
        Assert.Equal(
            ["req-replay-same-round-1", "req-replay-same-round-2"],
            _cloud.ToolResults.Select(static result => result.RequestId));
        Assert.All(_cloud.ToolResults, static result => Assert.True(result.Result?.Ok));
        var measurements = await ExecuteSqlBodyAsync(client, DatabaseName, "SHOW MEASUREMENTS");
        Assert.Contains("replay_same_round", measurements, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        "query_sql",
        """{"sql":"CREATE MEASUREMENT replay_conflict_initial (value FIELD FLOAT)"}""")]
    [InlineData(
        "execute_sql",
        """{"sql":"CREATE MEASUREMENT replay_conflict_alternate (value FIELD FLOAT)"}""")]
    public async Task CopilotChat_WhenToolCallIdConflictsInSameRound_RejectsBeforeSideEffect(
        string conflictingToolName,
        string conflictingArguments)
    {
        const string initialArguments =
            """{"sql":"CREATE MEASUREMENT replay_conflict_initial (value FIELD FLOAT)"}""";
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "execute_sql",
                initialArguments,
                requestId: "req-conflict-1",
                toolCallId: "tool-conflict"),
            ToolRequiredEvent(
                conflictingToolName,
                conflictingArguments,
                requestId: "req-conflict-2",
                toolCallId: "tool-conflict"),
            CloudEvent("done", message: "waiting for tool result"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "执行冲突的云端工具调用",
                    Mode: "read-write",
                    ConversationId: "session-tool-conflict"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(["error", "done"], events.Select(static evt => evt.Type));
        Assert.Equal(
            "云端 Copilot 对同一 toolCallId 返回了冲突的工具名称或参数，已拒绝该响应。",
            events[0].Message);
        Assert.Empty(_cloud.ToolResults);

        var measurements = await ExecuteSqlBodyAsync(client, DatabaseName, "SHOW MEASUREMENTS");
        Assert.DoesNotContain("replay_conflict_initial", measurements, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("replay_conflict_alternate", measurements, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CopilotChat_WhenCloudReplaysToolCallAcrossRounds_ReusesCachedResult()
    {
        const string arguments =
            """{"sql":"CREATE MEASUREMENT replay_across_rounds (value FIELD FLOAT)"}""";
        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "execute_sql",
                arguments,
                requestId: "req-replay-round-1",
                toolCallId: "tool-replay-across-rounds"),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.EnqueueChat(
            ToolRequiredEvent(
                "execute_sql",
                """{ "sql" : "CREATE MEASUREMENT replay_across_rounds (value FIELD FLOAT)" }""",
                requestId: "req-replay-round-2",
                toolCallId: "tool-replay-across-rounds"),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.EnqueueChat(
            CloudEvent("final", answer: "已创建 replay_across_rounds。"),
            CloudEvent("done", message: "completed"));

        using var client = CreateClient(AdminToken);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "创建 replay_across_rounds",
                    Mode: "read-write",
                    ConversationId: "session-replay-across-rounds"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        Assert.Equal(
            ["tool_call", "tool_result", "tool_call", "tool_result", "final", "done"],
            events.Select(static evt => evt.Type));
        Assert.Equal(3, _cloud.ChatRequests.Count);
        Assert.Equal(2, _cloud.ToolResults.Count);
        Assert.All(_cloud.ToolResults, static result => Assert.True(result.Result?.Ok));
        Assert.Equal(
            _cloud.ToolResults[0].Result?.Content?.GetRawText(),
            _cloud.ToolResults[1].Result?.Content?.GetRawText());
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

    /// <summary>
    /// 验证 Copilot 创建 Modbus 映射表时拒绝仅有 WRITE 的用户，并允许数据库或服务端管理员执行。
    /// </summary>
    [Theory]
    [InlineData("WRITE", false)]
    [InlineData("ADMIN", true)]
    [InlineData("SERVER_ADMIN", true)]
    public async Task CopilotChat_CreateModbusMappedTable_RequiresDatabaseAdmin(
        string permission,
        bool expectedAllowed)
    {
        const string createSourceSql = """
            CREATE MODBUS SOURCE copilot_source
            WITH (
                TRANSPORT TCP,
                ENDPOINT '127.0.0.1:1502',
                UNIT_ID 1,
                POLL_INTERVAL '1s',
                TIMEOUT '500ms',
                RETRY 1,
                ADDRESSING MODICON,
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER BIG_ENDIAN,
                ENABLED FALSE
            )
            """;
        var suffix = permission.ToLowerInvariant();
        var tableName = $"copilot_mapped_{suffix}";

        using (var admin = CreateClient(AdminToken))
        {
            await ExecuteSqlAsync(admin, createSourceSql);
        }

        SaveCloudConfig();
        _cloud!.EnqueueChat(
            ToolRequiredEvent(
                "execute_sql",
                $$"""{"sql":"CREATE TABLE {{tableName}} (id INT NOT NULL, value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16, PRIMARY KEY (id)) USING MODBUS SOURCE copilot_source WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)"}""",
                requestId: $"req-{suffix}",
                toolCallId: $"tool-{suffix}"),
            CloudEvent("done", message: "waiting for tool result"));
        _cloud.EnqueueChat(
            CloudEvent("final", answer: "Modbus 映射表处理完成。"),
            CloudEvent("done", message: "completed"));

        using var client = string.Equals(permission, "SERVER_ADMIN", StringComparison.Ordinal)
            ? CreateClient(AdminToken)
            : await CreateDatabaseUserClientAsync($"copilot_{suffix}", permission);
        using var response = await client.PostAsync(
            "/v1/copilot/chat",
            JsonContent.Create(
                new CopilotChatRequest(
                    DatabaseName,
                    "创建 Modbus 映射表",
                    Mode: "read-write",
                    ConversationId: $"session-{suffix}"),
                ServerJsonContext.Default.CopilotChatRequest));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await ReadNdjsonEventsAsync(response);
        _ = Assert.Single(events, static evt => evt.Type == "tool_result");
        var submitted = Assert.Single(_cloud.ToolResults);
        Assert.Equal(expectedAllowed, submitted.Result?.Ok ?? false);

        using var verificationClient = CreateClient(AdminToken);
        var showTablesBody = await ExecuteSqlBodyAsync(verificationClient, DatabaseName, "SHOW TABLES");
        if (expectedAllowed)
        {
            Assert.Contains(tableName, showTablesBody, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains(
                "当前数据库的 Admin 权限",
                submitted.Result?.ErrorMessage ?? string.Empty,
                StringComparison.Ordinal);
            Assert.DoesNotContain(tableName, showTablesBody, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void SaveCloudConfig(
        string accessToken = "cloud-access-token",
        DateTimeOffset? expiresAtUtc = null)
    {
        var store = _app!.Services.GetRequiredService<AiConfigStore>();
        store.Save(CreateBoundCloudOptions(accessToken, expiresAtUtc));
        _cloud!.Reset();
    }

    private static AiOptions CreateBoundCloudOptions(
        string accessToken = "cloud-access-token",
        DateTimeOffset? expiresAtUtc = null)
        => new()
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
        };

    /// <summary>
    /// 创建仅有数据库 READ 权限的测试客户端。
    /// </summary>
    private Task<HttpClient> CreateReaderClientAsync(string userName)
        => CreateDatabaseUserClientAsync(userName, "READ");

    /// <summary>
    /// 创建具有指定数据库权限的测试用户并返回认证客户端。
    /// </summary>
    private async Task<HttpClient> CreateDatabaseUserClientAsync(string userName, string permission)
    {
        using var admin = CreateClient(AdminToken);
        await ExecuteSqlAsync(admin, $"CREATE USER {userName} WITH PASSWORD 'p'");
        await ExecuteSqlAsync(admin, $"GRANT {permission} ON DATABASE {DatabaseName} TO {userName}");
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

    private static CopilotServerRelayRun CreateRelayRun(string runId)
        => new(
            runId,
            new CopilotServerRelayRunBinding("owner", DatabaseName, "fingerprint"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            static _ => { });

    private static async Task<List<CopilotChatEvent>> ReadRelayEventsAsync(
        CopilotServerRelayRun run)
    {
        var events = new List<CopilotChatEvent>();
        await foreach (var evt in run.ReadAfterAsync(0, CancellationToken.None))
            events.Add(evt);
        return events;
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

    private sealed class QueueChatProvider(params string[] responses) : IChatProvider
    {
        private readonly Queue<string> _responses = new(responses);

        public int CallCount { get; private set; }

        public ValueTask<string> CompleteAsync(
            IReadOnlyList<AiMessage> messages,
            string? modelOverride = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(_responses.Count > 0 ? _responses.Dequeue() : string.Empty);
        }
    }

    private sealed class FakeCloudGatewayClient : ICopilotCloudGatewayClient
    {
        private readonly Queue<IReadOnlyList<CopilotCloudRuntimeEvent>> _responses = new();

        public List<CopilotCloudChatRequest> ChatRequests { get; } = [];

        public List<CopilotCloudToolResultRequest> ToolResults { get; } = [];

        public bool BlockChatUntilCancellation { get; set; }

        public bool ChatCancellationObserved { get; private set; }

        public void Reset()
        {
            _responses.Clear();
            ChatRequests.Clear();
            ToolResults.Clear();
            ChatCancellationObserved = false;
        }

        public void EnqueueChat(params CopilotCloudRuntimeEvent[] events)
            => _responses.Enqueue(events);

        public async Task<CopilotCloudChatResponse> ChatAsync(
            AiOptions options,
            CopilotCloudChatRequest request,
            CancellationToken cancellationToken)
        {
            ChatRequests.Add(request);
            if (BlockChatUntilCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ChatCancellationObserved = true;
                    throw;
                }
            }

            var events = _responses.Count > 0
                ? _responses.Dequeue()
                : [CloudEvent("final", answer: "默认云端回答。"), CloudEvent("done", message: "completed")];
            return new CopilotCloudChatResponse(StatusCodes.Status200OK, "req-chat", events);
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
