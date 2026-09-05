using SonnetDB.Data.Kv;
using SonnetDB.Kv;

namespace SonnetDB.Core.Tests.Remote;

/// <summary>REST 原子响应的缺失或矛盾字段不能被解释为成功或条件冲突。</summary>
public sealed class KvAtomicRestResponseTests
{
    /// <summary>HTTP 200 的损坏合同产生明确异常，SDK 不重试请求。</summary>
    [Theory]
    [InlineData("set-conditional", "{}")]
    [InlineData("set-conditional", "{\"applied\":true}")]
    [InlineData("set-conditional", "{\"applied\":false,\"version\":1}")]
    [InlineData("set-conditional", "{\"applied\":true,\"version\":0}")]
    [InlineData("set-conditional", "{\"applied\":true,\"version\":-1}")]
    [InlineData("get-and-set", "{}")]
    [InlineData("get-and-set", "{\"previous\":{},\"mutationVersion\":2}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":true,\"value\":\"\"},\"mutationVersion\":2}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":true,\"value\":\"\",\"version\":1}}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":false,\"value\":\"\"},\"mutationVersion\":2}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":false,\"version\":1},\"mutationVersion\":2}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":false,\"expiresAtUtc\":\"2030-01-01T00:00:00Z\"},\"mutationVersion\":2}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":false}}")]
    [InlineData("get-and-delete", "{\"previous\":{\"found\":false},\"mutationVersion\":1}")]
    public async Task AtomicRest_WithInvalidSuccessBody_RejectsWithoutRetry(string operation, string json)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new KvLoopbackHttpServer(_ => new KvLoopbackResponse(200, json));
        using var client = CreateClient(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => ExecuteAsync(client, operation, deadline.Token));

        KvLoopbackRequest request = Assert.Single(server.Requests);
        Assert.Equal("/v1/db/demo/kv/cache/" + operation, request.Path);
        Assert.Equal("POST", request.Method);
    }

    /// <summary>严格检查仍接受明确成功、条件未满足、创建和删除缺失的有效返回值。</summary>
    [Theory]
    [InlineData("set-conditional", "{\"applied\":true,\"version\":1}")]
    [InlineData("set-conditional", "{\"applied\":false,\"version\":null}")]
    [InlineData("get-and-set", "{\"previous\":{\"found\":false},\"mutationVersion\":1}")]
    [InlineData("get-and-delete", "{\"previous\":{\"found\":false},\"mutationVersion\":null}")]
    public async Task AtomicRest_WithValidSuccessBody_AcceptsOnce(string operation, string json)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var server = new KvLoopbackHttpServer(_ => new KvLoopbackResponse(200, json));
        using var client = CreateClient(server);

        await ExecuteAsync(client, operation, deadline.Token);

        Assert.Single(server.Requests);
    }

    private static SndbKvClient CreateClient(KvLoopbackHttpServer server) => new(
        $"Data Source=sonnetdb+http://{server.Address.Authority}/demo;Timeout=5;Protocol=rest");

    private static Task ExecuteAsync(SndbKvClient client, string operation, CancellationToken cancellationToken) => operation switch
    {
        "set-conditional" => client.SetConditionalAsync("cache", "tenant", "key", [1], KvSetCondition.IfNotExists,
            cancellationToken: cancellationToken),
        "get-and-set" => client.GetAndSetAsync("cache", "tenant", "key", [1], cancellationToken: cancellationToken),
        "get-and-delete" => client.GetAndDeleteAsync("cache", "tenant", "key", cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(operation)),
    };
}
