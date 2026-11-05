using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class McpEndToEndTests
{
    /// <summary>实例 MQ 恢复到全新 Server 后仍按数据库和 grant 隔离，legacy 主题不泄漏，并再次重开。</summary>
    [Fact]
    public async Task MqSnapshot_RestoredServer_PreservesOffsetsAndMcpAuthorizationAcrossTwoReopens()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string original = _dataRoot!;
        string snapshot = original + "-instance-mq-snapshot";
        string offline = original + "-offline";
        string restored = original + "-restored";
        var sourceOwner = _app!.Services.GetRequiredService<SonnetMqStore>();
        sourceOwner.Publish(_dbName + ".events", [1]);
        sourceOwner.Publish(_dbName + ".events", [2]);
        sourceOwner.Ack(_dbName + ".events", "workers", 0);
        sourceOwner.Publish(_hiddenDbName + ".secret", [3]);
        sourceOwner.Publish("legacy_unscoped", [4]);
        sourceOwner.CreateSnapshot(snapshot);
        await _app.StopAsync(deadline.Token);
        await _app.DisposeAsync();
        _app = null;
        Directory.Move(original, offline);
        SonnetMqStore.RestoreSnapshot(snapshot, Path.Combine(restored, ".system", "mq"));
        _dataRoot = restored;
        try
        {
            for (int reopen = 0; reopen < 2; reopen++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                _app = TestServerHost.Build(new ServerOptions
                {
                    DataRoot = restored,
                    AutoLoadExistingDatabases = true,
                    AllowAnonymousProbes = true,
                    Tokens = new Dictionary<string, string>
                    {
                        [_adminToken] = ServerRoles.Admin,
                        [_readOnlyToken] = ServerRoles.ReadOnly,
                    },
                });
                await _app.StartAsync(deadline.Token);
                _baseUrl = Assert.Single(_app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses);
                if (reopen == 0)
                {
                    using var adminHttp = CreateHttpClient(_adminToken);
                    await CreateDatabaseAsync(adminHttp, _dbName);
                    await CreateDatabaseAsync(adminHttp, _hiddenDbName);
                }
                await using (var reader = await CreateMcpClientAsync())
                await using (var admin = await CreateMcpClientAsync(_adminToken))
                {
                    var topics = await ModelCallAsync(reader, "list_mq_topics");
                    Assert.Equal(new[] { "events" }, topics.GetProperty("names").EnumerateArray().Select(item => item.GetString()).ToArray());
                    var stats = await ModelCallAsync(reader, "get_mq_stats", ("topic", "events"));
                    Assert.Equal(1, stats.GetProperty("consumerOffsets").GetProperty("workers").GetInt64());
                    var messages = await ModelCallAsync(reader, "read_mq", ("topic", "events"), ("offset", 1));
                    Assert.Equal(reopen == 0 ? 1 : 2, messages.GetProperty("messages").GetArrayLength());
                    Assert.Equal("Ag==", messages.GetProperty("messages")[0].GetProperty("payloadBase64").GetString());
                    var hidden = await ModelCallAsync(reader, "read_mq", ("topic", _hiddenDbName + ".secret"));
                    Assert.Empty(hidden.GetProperty("messages").EnumerateArray());
                    var denied = await reader.CallToolAsync("publish_mq", new Dictionary<string, object?>
                    {
                        ["topic"] = "events",
                        ["payloadBase64"] = "CQ==",
                    }, cancellationToken: deadline.Token);
                    AssertModelError(denied, "forbidden");
                    if (reopen == 0)
                        await ModelCallAsync(admin, "publish_mq", ("topic", "events"), ("payloadBase64", "BQ=="));
                }
                var owner = _app.Services.GetRequiredService<SonnetMqStore>();
                Assert.Equal(new byte[] { 3 }, Assert.Single(owner.Pull(_hiddenDbName + ".secret", 0, 1)).Payload);
                Assert.Equal(new byte[] { 4 }, Assert.Single(owner.Pull("legacy_unscoped", 0, 1)).Payload);
                if (reopen == 0)
                {
                    await _app.StopAsync(deadline.Token);
                    await _app.DisposeAsync();
                    _app = null;
                }
            }
        }
        finally
        {
            if (Directory.Exists(snapshot)) Directory.Delete(snapshot, recursive: true);
            if (Directory.Exists(offline)) Directory.Delete(offline, recursive: true);
        }
    }
}
