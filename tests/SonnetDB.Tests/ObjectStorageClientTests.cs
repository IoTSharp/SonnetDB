using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

public sealed class ObjectStorageClientTests : IAsyncLifetime
{
    private const string AdminToken = "admin-object-client-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-s3-client-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var create = await http.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("objectsclient"),
            ServerJsonContext.Default.CreateDatabaseRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
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
    public async Task RemoteClient_PutReadDelete_RoundTrips()
    {
        var connectionString = $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/objectsclient;Token={AdminToken};Timeout=30";
        using var client = new SndbObjectStorageClient(connectionString);
        await client.CreateBucketAsync("iotsharp-blob-storage", "iotsharp-blob-storage");

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes("blob payload"));
        var written = await client.PutObjectAsync("iotsharp-blob-storage", "attachments/a.txt", input, "text/plain");
        Assert.Equal(12, written.SizeBytes);

        var listed = await client.ListObjectsAsync("iotsharp-blob-storage", "attachments/");
        Assert.Single(listed.Objects);
        Assert.Equal("attachments/a.txt", listed.Objects[0].Key);

        var read = await client.OpenReadAsync("iotsharp-blob-storage", "attachments/a.txt");
        Assert.NotNull(read);
        await using (read!.Content)
        using (var reader = new StreamReader(read.Content, Encoding.UTF8))
        {
            Assert.Equal("blob payload", await reader.ReadToEndAsync());
        }

        await client.DeleteObjectAsync("iotsharp-blob-storage", "attachments/a.txt");
        Assert.Null(await client.OpenReadAsync("iotsharp-blob-storage", "attachments/a.txt"));
    }
}
