using System.Net;
using System.Text;
using SonnetDB.Data;
using SonnetDB.Data.Kv;
using SonnetDB.Data.Remote;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests.Remote;

/// <summary>真实 loopback HTTP 重定向不能让 KV 的原子请求再发送一次。</summary>
public sealed class KvRedirectTests
{
    /// <summary>REST 和自动 Frame 遇到 307/308 时均停在第一次响应。</summary>
    [Theory]
    [InlineData(307, "rest")]
    [InlineData(308, "rest")]
    [InlineData(307, "auto")]
    [InlineData(308, "auto")]
    public async Task SetConditionalAsync_WithRedirect_DoesNotPostToRedirectTarget(int status, string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new KvLoopbackHttpServer(request => request.Path == "/target"
            ? new KvLoopbackResponse(200, "{\"applied\":true,\"version\":1}")
            : new KvLoopbackResponse(status, "{\"error\":\"redirect_refused\",\"message\":\"not replayed\"}", "/target"));
        using var client = new SndbKvClient(
            $"Data Source=sonnetdb+http://{server.Address.Authority}/demo;Token=kv-token;Timeout=5;Protocol={protocol}");

        var error = await Assert.ThrowsAsync<SndbServerException>(() => client.SetConditionalAsync(
            "cache", "tenant", "key", [1], KvSetCondition.IfNotExists, cancellationToken: deadline.Token));

        Assert.Equal("redirect_refused", error.Error);
        KvLoopbackRequest original = Assert.Single(server.Requests);
        Assert.Equal("POST", original.Method);
        Assert.Equal(protocol == "rest" ? "/v1/db/demo/kv/cache/set-conditional" : "/v1/frame", original.Path);
        Assert.Equal("Bearer kv-token", original.Authorization);
    }

    /// <summary>同一地址的默认和禁重定向客户端不会复用错误配置的连接池。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_WithMixedRedirectPolicies_IsolatesCachedHandlers(bool strictFirst)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new KvLoopbackHttpServer(request => request.Path == "/target"
            ? new KvLoopbackResponse(200, "{}")
            : new KvLoopbackResponse(307, "{}", "/target"));
        using var first = RemoteHttpClientFactory.Create(server.Address, null, null, "first", TimeSpan.FromSeconds(5),
            allowAutoRedirect: !strictFirst);
        using var second = RemoteHttpClientFactory.Create(server.Address, null, null, "second", TimeSpan.FromSeconds(5),
            allowAutoRedirect: strictFirst);
        using var firstBody = new StringContent("{}", Encoding.UTF8, "application/json");
        using var secondBody = new StringContent("{}", Encoding.UTF8, "application/json");

        using var firstResponse = await first.PostAsync("redirect", firstBody, deadline.Token);
        using var secondResponse = await second.PostAsync("redirect", secondBody, deadline.Token);

        Assert.Equal(strictFirst ? HttpStatusCode.TemporaryRedirect : HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(strictFirst ? HttpStatusCode.OK : HttpStatusCode.TemporaryRedirect, secondResponse.StatusCode);
        Assert.Equal(3, server.Requests.Length);
        Assert.Single(server.Requests, request => request.Path == "/target");
        Assert.Equal(new[] { "Bearer first", "Bearer second" },
            server.Requests.Where(request => request.Path == "/redirect").Select(request => request.Authorization));
    }

    /// <summary>禁重定向连接池可复用，认证头仍属于各自客户端。</summary>
    [Fact]
    public async Task Create_WithDifferentCredentials_KeepsAuthenticationOnEachClient()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new KvLoopbackHttpServer(_ => new KvLoopbackResponse(200, "{}"));
        using var bearer = RemoteHttpClientFactory.Create(server.Address, null, null, "bearer-token", TimeSpan.FromSeconds(5),
            allowAutoRedirect: false);
        using var basic = RemoteHttpClientFactory.Create(server.Address, "user", "password", "ignored-token", TimeSpan.FromSeconds(5),
            allowAutoRedirect: false);

        using var first = await bearer.GetAsync("first", deadline.Token);
        using var second = await basic.GetAsync("second", deadline.Token);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(new[] { "Bearer bearer-token", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:password")) },
            server.Requests.Select(request => request.Authorization));
    }
}
