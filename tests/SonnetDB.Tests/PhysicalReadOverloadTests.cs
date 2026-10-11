using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Exceptions;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证尚未开始响应的 REST 过载请求保留稳定错误及重试提示。</summary>
public sealed class PhysicalReadOverloadTests
{
    /// <summary>实际通过请求管线验证物理读取过载不会变成未分类存储错误。</summary>
    [Fact]
    public async Task Request_WithPhysicalReadOverload_Returns503AndRetryAfter()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-overload-" + Guid.NewGuid().ToString("N"));
        var app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = root,
            AllowAnonymousProbes = true,
        });
        app.MapGet("/healthz/read-overload", () => ThrowOverload());
        try
        {
            await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            string address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("/healthz/read-overload");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(KvReadOverloadedException.Code, error.RootElement.GetProperty("error").GetString());
            Assert.Equal(KvReadOverloadedException.Code, error.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await app.DisposeAsync();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>模拟引擎在尚未写出响应时报告过载。</summary>
    private static string ThrowOverload() => throw new KvReadOverloadedException("物理读取繁忙。");
}
