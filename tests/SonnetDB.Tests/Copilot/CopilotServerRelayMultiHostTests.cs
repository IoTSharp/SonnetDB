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

namespace SonnetDB.Tests.Copilot;

/// <summary>通过两个独立 Kestrel/DI 宿主验证共享日志的生产配置与 HTTP 续流入口。</summary>
[Collection(CopilotTestCollection.Name)]
public sealed class CopilotServerRelayMultiHostTests : IAsyncLifetime
{
    private const string Token = "relay-multi-host-admin";
    private const string Database = "relay_data";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-relay-hosts-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(45));
    private readonly GatedProvider _cloud = new();
    private readonly List<WebApplication> _apps = [];
    private readonly List<HttpClient> _clients = [];

    public async Task InitializeAsync()
    {
        try
        {
            for (int index = 0; index < 2; index++)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                var options = new ServerOptions
                {
                    DataRoot = Path.Combine(_root, "host-" + index),
                    Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
                    AllowAnonymousProbes = true,
                };
                options.Copilot.ServerRelayJournalPath = Path.Combine(_root, "shared", "relay.json");
                options.Copilot.Chat.Provider = "openai";
                options.Copilot.Chat.Endpoint = "http://127.0.0.1:19090/v1/";
                options.Copilot.Chat.ApiKey = "relay-test-key";
                options.Copilot.Chat.Model = "relay-test-model";
                options.Copilot.Docs.AutoIngestOnStartup = false;
                options.Copilot.Skills.AutoIngestOnStartup = false;
                options.Mqtt.Enabled = false;
                var app = TestServerHost.Build(options, services =>
                    services.AddSingleton<IChatProvider>(_cloud));
                _apps.Add(app);
                await app.StartAsync(_deadline.Token);
                var address = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
                _clients.Add(client);
                using var created = await client.PostAsync("/v1/db",
                    JsonContent.Create(new CreateDatabaseRequest(Database), ServerJsonContext.Default.CreateDatabaseRequest),
                    _deadline.Token);
                Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(_deadline.Token));
            }
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_SecondHostAttachesToActiveRun_StreamsIdenticalEventsWithoutRepeatingProvider(bool sse)
    {
        using var first = await SendAsync(_clients[0], sse, _deadline.Token);
        await _cloud.Started.Task.WaitAsync(_deadline.Token);
        using var second = await SendAsync(_clients[1], sse, _deadline.Token);
        using var firstReader = new StreamReader(await first.Content.ReadAsStreamAsync(_deadline.Token));
        using var secondReader = new StreamReader(await second.Content.ReadAsStreamAsync(_deadline.Token));
        var firstStart = await ReadEventAsync(firstReader, sse, _deadline.Token);
        var secondStart = await ReadEventAsync(secondReader, sse, _deadline.Token);
        Assert.Equal("start", secondStart.Type);
        Assert.Equal(SerializeEvent(firstStart), SerializeEvent(secondStart));
        Assert.Equal(2, _cloud.Calls);

        _cloud.Release.TrySetResult();
        var firstTail = await ReadTailAsync(firstReader, sse, _deadline.Token);
        var secondTail = await ReadTailAsync(secondReader, sse, _deadline.Token);
        Assert.Equal(firstTail.Select(SerializeEvent), secondTail.Select(SerializeEvent));
        Assert.Single(secondTail, evt => evt.Type == "final" && evt.Answer == "shared live answer");
        Assert.Single(secondTail, evt => evt.Type == "done");
        Assert.DoesNotContain(secondTail, evt => evt.Type == "error");
        Assert.Equal(2, _cloud.Calls);
        Assert.True(File.Exists(Path.Combine(_root, "shared", "relay.json")));
    }

    [Fact]
    public async Task Chat_CancelledFollower_LeavesOwnerAndOtherSubscribersRunning()
    {
        using var first = await SendAsync(_clients[0], false, _deadline.Token);
        await _cloud.Started.Task.WaitAsync(_deadline.Token);
        using var followerCancellation = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
        using (var follower = await SendAsync(_clients[1], false, followerCancellation.Token))
        using (var followerReader = new StreamReader(await follower.Content.ReadAsStreamAsync(_deadline.Token)))
        {
            Assert.Equal("start", (await ReadEventAsync(followerReader, false, _deadline.Token)).Type);
            followerCancellation.Cancel();
        }
        using var replacement = await SendAsync(_clients[1], false, _deadline.Token);
        using var replacementReader = new StreamReader(await replacement.Content.ReadAsStreamAsync(_deadline.Token));
        Assert.Equal("start", (await ReadEventAsync(replacementReader, false, _deadline.Token)).Type);
        Assert.False(_cloud.OwnerCancelled);
        _cloud.Release.TrySetResult();
        var tail = await ReadTailAsync(replacementReader, false, _deadline.Token);
        Assert.Single(tail, evt => evt.Type == "final");
        Assert.DoesNotContain(tail, evt => evt.Type == "error");
        Assert.Equal(2, _cloud.Calls);
        Assert.False(_cloud.OwnerCancelled);
    }

    public async Task DisposeAsync()
    {
        _cloud.Release.TrySetResult();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (var client in _clients)
            client.Dispose();
        foreach (var app in _apps)
        {
            await app.StopAsync(cleanup.Token);
            await app.DisposeAsync();
        }
        _apps.Clear();
        _clients.Clear();
        _deadline.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, bool sse, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sse ? "/v1/copilot/chat/stream" : "/v1/copilot/chat")
        {
            Content = JsonContent.Create(new CopilotChatRequest(Database, "Describe this database")
            {
                RunId = "multi-host-live",
                ConversationId = "multi-host-session",
            }, ServerJsonContext.Default.CopilotChatRequest),
        };
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static async Task<CopilotChatEvent> ReadEventAsync(StreamReader reader, bool sse, CancellationToken cancellationToken)
    {
        for (int lineCount = 0; lineCount < 16; lineCount++)
        {
            string? line = await reader.ReadLineAsync(cancellationToken);
            Assert.NotNull(line);
            if (sse)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                    continue;
                line = line[6..];
            }
            if (line.Length == 0)
                continue;
            return JsonSerializer.Deserialize(line, ServerJsonContext.Default.CopilotChatEvent)!;
        }
        throw new InvalidOperationException("Relay event exceeded the bounded line budget.");
    }

    private static async Task<List<CopilotChatEvent>> ReadTailAsync(StreamReader reader, bool sse, CancellationToken cancellationToken)
    {
        var events = new List<CopilotChatEvent>();
        for (int count = 0; count < 256; count++)
        {
            var evt = await ReadEventAsync(reader, sse, cancellationToken);
            events.Add(evt);
            if (evt.Type == "done")
                return events;
        }
        throw new InvalidOperationException("Relay exceeded the bounded event budget.");
    }

    private static string SerializeEvent(CopilotChatEvent evt)
        => JsonSerializer.Serialize(evt, ServerJsonContext.Default.CopilotChatEvent);

    private sealed class GatedProvider : IChatProvider
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool OwnerCancelled { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<string> CompleteAsync(IReadOnlyList<AiMessage> messages,
            string? modelOverride = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                return "{\"tools\":[]}";
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                OwnerCancelled = true;
                throw;
            }
            return "shared live answer";
        }
    }
}
