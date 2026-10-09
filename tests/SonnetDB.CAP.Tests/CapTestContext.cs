using System.Net;
using System.Net.Http.Headers;
using DotNetCore.CAP;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SonnetDB.CAP.Storage.Document;
using SonnetDB.Configuration;
using SonnetDB.Data.Documents;

namespace SonnetDB.CAP.Tests;

internal sealed class CapTestContext : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-cap-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _server;
    private IHost? _capHost;
    public string ConnectionString { get; private set; } = string.Empty;
    public string MqConnectionString { get; private set; } = string.Empty;
    public string? BaseUrl { get; private set; }
    public ServiceProvider? Services { get; private set; }
    public ManualClock Clock { get; } = new();
    public IServiceProvider Provider => _capHost?.Services ?? Services!;
    public IDataStorage Storage => Provider.GetRequiredService<IDataStorage>();
    public CapDocumentStore Backend => Provider.GetRequiredService<CapDocumentStore>();

    public static async Task<CapTestContext> CreateAsync(bool remote, bool startCap = false, string version = "v1")
    {
        var context = new CapTestContext();
        try
        {
            Directory.CreateDirectory(context._root);
            if (remote)
            {
                await context.StartServerAsync(createDatabase: true);
            }
            else
            {
                context.ConnectionString = "Data Source=" + Path.Combine(context._root, "documents");
                context.MqConnectionString = "Data Source=" + Path.Combine(context._root, "mq");
            }
            if (startCap)
            {
                var builder = Host.CreateApplicationBuilder();
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole();
                context.Configure(builder.Services, version, useManualClock: false);
                builder.Services.AddSingleton<JourneySubscriber>();
                context._capHost = builder.Build();
                await context._capHost.StartAsync();
            }
            else
            {
                var services = new ServiceCollection();
                services.AddLogging(logging => logging.ClearProviders());
                context.Configure(services, version, useManualClock: true);
                context.Services = services.BuildServiceProvider();
                await context.Provider.GetRequiredService<IStorageInitializer>().InitializeAsync(CancellationToken.None);
            }
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private void Configure(IServiceCollection services, string version, bool useManualClock)
    {
        services.AddCap(cap =>
        {
            cap.Version = version;
            cap.FailedRetryCount = 3;
            cap.UseStorageLock = true;
            cap.UseSonnetDbDocumentStorage(options =>
            {
                options.ConnectionString = ConnectionString;
                if (useManualClock)
                    options.TimeProvider = Clock;
            });
            cap.UseSonnetDbTransport(options => options.ConnectionString = MqConnectionString);
        });
    }

    public async Task ReopenStoreAsync()
    {
        if (Services is null)
            throw new InvalidOperationException("只有未启动 CAP 的测试支持重开存储。");
        await Services.DisposeAsync();
        if (_server is not null)
        {
            await _server.StopAsync(TimeSpan.FromSeconds(10));
            await _server.DisposeAsync();
            _server = null;
            await StartServerAsync(createDatabase: false);
        }
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        Configure(services, "v1", useManualClock: true);
        Services = services.BuildServiceProvider();
        await Provider.GetRequiredService<IStorageInitializer>().InitializeAsync(CancellationToken.None);
    }

    private async Task StartServerAsync(bool createDatabase)
    {
        _server = SonnetDB.Tests.TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { ["cap-admin-test"] = ServerRoles.Admin, ["cap-reader-test"] = ServerRoles.ReadOnly }
        }, services => services.AddLogging(logging => logging.ClearProviders()));
        await _server.StartAsync();
        BaseUrl = _server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        if (createDatabase)
        {
            using var http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "cap-admin-test");
            using var request = new StringContent("""{"name":"captests"}""", System.Text.Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await http.PostAsync("/v1/db", request);
            if (response.StatusCode != HttpStatusCode.Created)
                throw new InvalidOperationException("CAP 远程测试数据库创建失败。");
        }
        ConnectionString = $"Data Source={BaseUrl}/captests;Token=cap-admin-test;Protocol=Rest";
        MqConnectionString = ConnectionString;
    }

    public static Message Message(string id, string name = "orders.created", string body = "payload")
        => new(new Dictionary<string, string?>
        {
            [Headers.MessageId] = id,
            [Headers.MessageName] = name,
            [Headers.SentTime] = DateTime.Now.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, body);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_capHost is not null)
            {
                await _capHost.StopAsync(TimeSpan.FromSeconds(10));
                _capHost.Dispose();
            }
            if (Services is not null)
                await Services.DisposeAsync();
            if (_server is not null)
            {
                await _server.StopAsync(TimeSpan.FromSeconds(10));
                await _server.DisposeAsync();
            }
        }
        finally
        {
            string path = Path.GetFullPath(_root);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "sonnetdb-cap-tests-");
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("CAP 测试目录归属校验失败。");
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

public sealed class JourneySubscriber : ICapSubscribe
{
    public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [CapSubscribe("cap.journey")]
    public void Handle(string value) => Received.TrySetResult(value);
}
