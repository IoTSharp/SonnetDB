using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Backup;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>覆盖真实备份包的在线恢复入口和配置边界。</summary>
public sealed class BackupRestoreEndpointTests : IDisposable
{
    private const string AdminToken = "restore-admin-test";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sndb-restore-tests-");

    /// <summary>ZIP、TAR 和 gzip 压缩的 TAR 均可通过无原始后缀的上传暂存恢复。</summary>
    [Theory]
    [InlineData("zip")]
    [InlineData("tar")]
    [InlineData("tar.gz")]
    public async Task Upload_ValidArchives_RegisterNewDatabase(string format)
    {
        string backup = CreateBackup();
        string package = Path.Combine(_root.FullName, "backup-" + format + ".package");
        if (format == "zip")
        {
            ZipFile.CreateFromDirectory(backup, package, CompressionLevel.Fastest, includeBaseDirectory: true);
        }
        else if (format == "tar.gz")
        {
            await using var file = File.Create(package);
            await using var gzip = new GZipStream(file, CompressionLevel.Fastest);
            TarFile.CreateFromDirectory(backup, gzip, includeBaseDirectory: true);
        }
        else
        {
            TarFile.CreateFromDirectory(backup, package, includeBaseDirectory: true);
        }

        await using WebApplication app = TestServerHost.Build(CreateOptions(enableRestore: true));
        await app.StartAsync();
        using HttpClient client = CreateClient(app);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StreamContent(File.OpenRead(package)), "file", "archive");
        using HttpResponseMessage response = await client.PostAsync("/v1/system/backup-restore/upload", multipart);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        BackupRestoreResponse result = Assert.IsType<BackupRestoreResponse>(
            await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.BackupRestoreResponse));
        Assert.True(result.Verified);
        Assert.Equal("upload", result.Source);
        Assert.True(result.CheckedFiles > 0);
        Assert.True(app.Services.GetRequiredService<TsdbRegistry>().TryGet(result.DatabaseName, out Tsdb? restored));
        byte[]? value = restored.Keyspaces.Open("restore-check").Get("key");
        Assert.Equal("value", Encoding.UTF8.GetString(Assert.IsType<byte[]>(value)));
    }

    /// <summary>默认禁止在线恢复，管理员请求不会创建数据库。</summary>
    [Fact]
    public async Task Restore_DefaultOptions_DisableRoutes()
    {
        string backup = CreateBackup();
        await using WebApplication app = TestServerHost.Build(CreateOptions(enableRestore: false));
        await app.StartAsync();
        using HttpClient client = CreateClient(app);
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        string[] databasesBefore = registry.ListDatabases().ToArray();
        using HttpResponseMessage restoreResponse = await client.PostAsJsonAsync(
            "/v1/system/backup-restore", new BackupRestoreRequest(Path: backup), ServerJsonContext.Default.BackupRestoreRequest);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent([1, 2]), "file", "archive");
        using HttpResponseMessage uploadResponse = await client.PostAsync("/v1/system/backup-restore/upload", multipart);
        Assert.Equal(HttpStatusCode.Forbidden, restoreResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, uploadResponse.StatusCode);
        Assert.Equal(databasesBefore, registry.ListDatabases());
    }

    /// <summary>启用目录恢复后仍要求路径处于服务器备份根范围，URL 默认未开放。</summary>
    [Fact]
    public async Task Restore_RequiresTrustedLocation()
    {
        string backup = CreateBackup();
        await using WebApplication app = TestServerHost.Build(CreateOptions(enableRestore: true));
        await app.StartAsync();
        using HttpClient client = CreateClient(app);
        using HttpResponseMessage outsideRoot = await client.PostAsJsonAsync(
            "/v1/system/backup-restore", new BackupRestoreRequest(Path: _root.FullName), ServerJsonContext.Default.BackupRestoreRequest);
        using HttpResponseMessage unconfiguredOrigin = await client.PostAsJsonAsync(
            "/v1/system/backup-restore", new BackupRestoreRequest(Url: "https://backup.example.invalid/archive.zip"),
            ServerJsonContext.Default.BackupRestoreRequest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, outsideRoot.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unconfiguredOrigin.StatusCode);

        using HttpResponseMessage accepted = await client.PostAsJsonAsync(
            "/v1/system/backup-restore", new BackupRestoreRequest(Path: backup), ServerJsonContext.Default.BackupRestoreRequest);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    /// <summary>生成由真实 Core 写出的备份目录。</summary>
    private string CreateBackup()
    {
        string backup = Path.Combine(_root.FullName, "backups", "snapshot");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root.FullName, "source") });
        database.Keyspaces.Open("restore-check").Put("key", "value"u8.ToArray());
        new BackupService().Create(database, new BackupCreateOptions { DestinationDirectory = backup });
        return backup;
    }

    /// <summary>限制测试宿主使用自有目录和一次性管理员令牌。</summary>
    private ServerOptions CreateOptions(bool enableRestore)
        => new()
        {
            DataRoot = Path.Combine(_root.FullName, "data"),
            AutoLoadExistingDatabases = false,
            Tokens = new() { [AdminToken] = ServerRoles.Admin },
            Backup = new BackupRuntimeOptions
            {
                Enabled = false,
                EnableRestore = enableRestore,
                DestinationRoot = Path.Combine(_root.FullName, "backups"),
            },
        };

    /// <summary>连接当前测试宿主的回环监听地址。</summary>
    private static HttpClient CreateClient(WebApplication app)
    {
        string address = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        return client;
    }

    /// <summary>清除本测试取得所有权的临时目录。</summary>
    public void Dispose() => _root.Delete(recursive: true);
}
