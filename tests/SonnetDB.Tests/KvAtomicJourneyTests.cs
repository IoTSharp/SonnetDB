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
using SonnetDB.Data;
using SonnetDB.Data.Kv;
using SonnetDB.Json;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 同一 KV 原子操作旅程经嵌入式、REST、真实 HTTP/2 Frame 和自动协议执行的验收测试。
/// </summary>
public sealed class KvAtomicJourneyTests(KvAtomicJourneyFixture fixture)
    : IClassFixture<KvAtomicJourneyFixture>
{
    private const string Tenant = "tenant";

    /// <summary>条件写、交换、删除、版本与 namespace 使用相同的可见记录合同。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithGoldenJourney_PreserveValuesVersionsAndNamespace(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        const string key = "device";
        DateTimeOffset expiry = UtcMilliseconds().AddHours(1);

        Assert.Null(await client.GetAsync(keyspace, Tenant, key, cancellation));
        var missingXx = await client.SetConditionalAsync(
            keyspace, Tenant, key, [99], KvSetCondition.IfExists, cancellationToken: cancellation);
        Assert.False(missingXx.Applied);
        Assert.Null(missingXx.Version);

        var created = await client.SetConditionalAsync(
            keyspace, Tenant, key, [], KvSetCondition.IfNotExists, expiry, cancellation);
        Assert.True(created.Applied);
        Assert.NotNull(created.Version);
        var empty = await client.GetAsync(keyspace, Tenant, key, cancellation);
        Assert.NotNull(empty);
        Assert.Empty(empty.Value);
        Assert.Equal(key, empty.Key);
        Assert.Equal(created.Version, empty.Version);
        Assert.Equal(expiry, empty.ExpiresAtUtc);

        var duplicate = await client.SetConditionalAsync(
            keyspace, Tenant, key, [99], KvSetCondition.IfNotExists, cancellationToken: cancellation);
        Assert.False(duplicate.Applied);
        Assert.Null(duplicate.Version);
        Assert.Empty((await client.GetAsync(keyspace, Tenant, key, cancellation))!.Value);

        var replaced = await client.SetConditionalAsync(
            keyspace, Tenant, key, [0, 127, 128, 255], KvSetCondition.IfExists, expiry, cancellation);
        Assert.True(replaced.Applied);
        Assert.Equal(created.Version!.Value + 1, replaced.Version);

        var exchanged = await client.GetAndSetAsync(
            keyspace, Tenant, key, [3, 4], cancellationToken: cancellation);
        Assert.NotNull(exchanged.PreviousEntry);
        Assert.Equal(key, exchanged.PreviousEntry.Key);
        Assert.Equal(new byte[] { 0, 127, 128, 255 }, exchanged.PreviousEntry.Value);
        Assert.Equal(replaced.Version, exchanged.PreviousEntry.Version);
        Assert.Equal(expiry, exchanged.PreviousEntry.ExpiresAtUtc);
        Assert.Equal(replaced.Version!.Value + 1, exchanged.MutationVersion);
        Assert.Equal(-1, (await client.GetTimeToLiveAsync(keyspace, Tenant, key, cancellation)).Milliseconds);

        var conflict = await client.CompareAndSetAsync(
            keyspace, Tenant, key, replaced.Version.Value, [99], cancellationToken: cancellation);
        Assert.False(conflict.Succeeded);
        Assert.Equal(exchanged.MutationVersion, conflict.CurrentVersion);
        Assert.Null(conflict.NewVersion);
        var cas = await client.CompareAndSetAsync(
            keyspace, Tenant, key, exchanged.MutationVersion!.Value, [5], cancellationToken: cancellation);
        Assert.True(cas.Succeeded);
        Assert.Equal(exchanged.MutationVersion.Value + 1, cas.NewVersion);

        var deleted = await client.GetAndDeleteAsync(keyspace, Tenant, key, cancellation);
        Assert.NotNull(deleted.PreviousEntry);
        Assert.Equal(key, deleted.PreviousEntry.Key);
        Assert.Equal(new byte[] { 5 }, deleted.PreviousEntry.Value);
        Assert.Equal(cas.NewVersion, deleted.PreviousEntry.Version);
        Assert.Equal(cas.NewVersion!.Value + 1, deleted.MutationVersion);
        Assert.Null(await client.GetAsync(keyspace, Tenant, key, cancellation));

        var repeatedDelete = await client.GetAndDeleteAsync(keyspace, Tenant, key, cancellation);
        Assert.Null(repeatedDelete.PreviousEntry);
        Assert.Null(repeatedDelete.MutationVersion);
        var recreated = await client.GetAndSetAsync(keyspace, Tenant, key, [], expiry, cancellation);
        Assert.Null(recreated.PreviousEntry);
        Assert.Equal(deleted.MutationVersion!.Value + 1, recreated.MutationVersion);
        var deletedEmpty = await client.GetAndDeleteAsync(keyspace, Tenant, key, cancellation);
        Assert.NotNull(deletedEmpty.PreviousEntry);
        Assert.Empty(deletedEmpty.PreviousEntry.Value);
        Assert.Equal(expiry, deletedEmpty.PreviousEntry.ExpiresAtUtc);

        await client.SetAsync(keyspace, "other", key, [9], cancellationToken: cancellation);
        Assert.Null(await client.GetAsync(keyspace, Tenant, key, cancellation));
        Assert.Equal(new byte[] { 9 }, (await client.GetAsync(keyspace, "other", key, cancellation))!.Value);
        var qualified = await client.GetAsync(keyspace, string.Empty, "other:" + key, cancellation);
        Assert.NotNull(qualified);
        Assert.Equal("other:" + key, qualified.Key);
    }

    /// <summary>真实到期后 NX/XX 和交换把过期记录视为不存在，Persist 清除 TTL。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_AfterTtlExpires_TreatExpiredEntriesAsMissing(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        DateTimeOffset expiry = UtcMilliseconds().AddMilliseconds(300);

        await client.SetAsync(keyspace, Tenant, "item", [1], expiry, cancellation);
        await Task.Delay(TimeSpan.FromMilliseconds(450), cancellation);
        Assert.Null(await client.GetAsync(keyspace, Tenant, "item", cancellation));
        Assert.Equal(-2, (await client.GetTimeToLiveAsync(keyspace, Tenant, "item", cancellation)).Milliseconds);
        var xx = await client.SetConditionalAsync(
            keyspace, Tenant, "item", [99], KvSetCondition.IfExists, cancellationToken: cancellation);
        Assert.False(xx.Applied);
        var deleted = await client.GetAndDeleteAsync(keyspace, Tenant, "item", cancellation);
        Assert.Null(deleted.PreviousEntry);
        Assert.Null(deleted.MutationVersion);

        var nx = await client.SetConditionalAsync(
            keyspace, Tenant, "item", [2], KvSetCondition.IfNotExists,
            UtcMilliseconds().AddHours(1), cancellation);
        Assert.True(nx.Applied);
        Assert.True((await client.GetTimeToLiveAsync(keyspace, Tenant, "item", cancellation)).Milliseconds > 0);
        Assert.True(await client.PersistAsync(keyspace, Tenant, "item", cancellation));
        Assert.Equal(-1, (await client.GetTimeToLiveAsync(keyspace, Tenant, "item", cancellation)).Milliseconds);
        Assert.True(await client.ExpireAsync(
            keyspace, Tenant, "item", UtcMilliseconds().AddSeconds(-1), cancellation));
        var exchanged = await client.GetAndSetAsync(keyspace, Tenant, "item", [3], cancellationToken: cancellation);
        Assert.Null(exchanged.PreviousEntry);
        Assert.NotNull(exchanged.MutationVersion);
        Assert.Equal(new byte[] { 3 }, (await client.GetAsync(keyspace, Tenant, "item", cancellation))!.Value);
        Assert.False(await client.ExpireAsync(
            keyspace, Tenant, "missing", UtcMilliseconds().AddHours(1), cancellation));
        Assert.False(await client.PersistAsync(keyspace, Tenant, "missing", cancellation));
    }

    /// <summary>绝对 TTL 保留亚毫秒 ticks，读取、交换和 CAS 均返回精确时间。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithSubMillisecondExpiry_PreserveEveryTick(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        DateTimeOffset firstExpiry = UtcMilliseconds().AddHours(1).AddTicks(1234);
        DateTimeOffset secondExpiry = firstExpiry.AddTicks(4321);
        DateTimeOffset thirdExpiry = secondExpiry.AddTicks(1234);

        var created = await client.SetConditionalAsync(
            keyspace, Tenant, "item", [1], KvSetCondition.IfNotExists, firstExpiry, cancellation);
        Assert.True(created.Applied);
        Assert.Equal(firstExpiry, (await client.GetAsync(keyspace, Tenant, "item", cancellation))!.ExpiresAtUtc);
        Assert.Equal(firstExpiry, (await client.GetTimeToLiveAsync(keyspace, Tenant, "item", cancellation)).ExpiresAtUtc);
        var exchanged = await client.GetAndSetAsync(keyspace, Tenant, "item", [2], secondExpiry, cancellation);
        Assert.Equal(firstExpiry, exchanged.PreviousEntry!.ExpiresAtUtc);
        Assert.Equal(secondExpiry, (await client.GetAsync(keyspace, Tenant, "item", cancellation))!.ExpiresAtUtc);
        var cas = await client.CompareAndSetAsync(
            keyspace, Tenant, "item", exchanged.MutationVersion!.Value, [3], thirdExpiry, cancellation);
        Assert.True(cas.Succeeded);
        Assert.Equal(thirdExpiry, (await client.GetTimeToLiveAsync(keyspace, Tenant, "item", cancellation)).ExpiresAtUtc);
        Assert.True(await client.ExpireAsync(keyspace, Tenant, "item", firstExpiry, cancellation));
        var deleted = await client.GetAndDeleteAsync(keyspace, Tenant, "item", cancellation);
        Assert.Equal(firstExpiry, deleted.PreviousEntry!.ExpiresAtUtc);
    }

    /// <summary>非 UTC 时间在所有入口均被拒绝，不能通过 Frame 编码自动改写后提交。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithNonUtcExpiry_RejectWithoutMutation(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        long version = await client.SetAsync(keyspace, Tenant, "item", [1], cancellationToken: cancellation);
        DateTimeOffset nonUtc = UtcMilliseconds().AddHours(1).ToOffset(TimeSpan.FromHours(8));
        Func<Task>[] mutations =
        [
            () => client.SetAsync(keyspace, Tenant, "item", [9], nonUtc, cancellation),
            () => client.SetConditionalAsync(keyspace, Tenant, "item", [9], KvSetCondition.IfExists, nonUtc, cancellation),
            () => client.GetAndSetAsync(keyspace, Tenant, "item", [9], nonUtc, cancellation),
            () => client.CompareAndSetAsync(keyspace, Tenant, "item", version, [9], nonUtc, cancellation),
            () => client.ExpireAsync(keyspace, Tenant, "item", nonUtc, cancellation),
        ];
        foreach (Func<Task> mutate in mutations)
        {
            cancellation.ThrowIfCancellationRequested();
            await Assert.ThrowsAsync<ArgumentException>(mutate);
        }

        var unchanged = await client.GetAsync(keyspace, Tenant, "item", cancellation);
        Assert.NotNull(unchanged);
        Assert.Equal(new byte[] { 1 }, unchanged.Value);
        Assert.Equal(version, unchanged.Version);
        Assert.Null(unchanged.ExpiresAtUtc);
        Assert.Equal(version + 1, await client.SetAsync(keyspace, Tenant, "after", [2], cancellationToken: cancellation));
    }

    /// <summary>并发 NX 只有一个成功，交换返回完整线性历史，原子删除只消费一次。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithSixteenContenders_HaveOneWinnerAndCompleteExchangeHistory(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        const int contenders = 16;
        var startNx = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SndbKvSetResult>[] attempts = Enumerable.Range(0, contenders).Select(async i =>
        {
            await startNx.Task.WaitAsync(cancellation);
            return await client.SetConditionalAsync(
                keyspace, Tenant, "claim", [(byte)i], KvSetCondition.IfNotExists, cancellationToken: cancellation);
        }).ToArray();
        startNx.SetResult();
        SndbKvSetResult[] nx = await Task.WhenAll(attempts).WaitAsync(cancellation);
        Assert.Single(nx, result => result.Applied);
        Assert.All(nx.Where(result => !result.Applied), result => Assert.Null(result.Version));

        await client.SetAsync(keyspace, Tenant, "exchange", [255], cancellationToken: cancellation);
        var startExchange = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SndbKvExchangeResult>[] exchangeTasks = Enumerable.Range(0, contenders).Select(async i =>
        {
            await startExchange.Task.WaitAsync(cancellation);
            return await client.GetAndSetAsync(
                keyspace, Tenant, "exchange", [(byte)i], cancellationToken: cancellation);
        }).ToArray();
        startExchange.SetResult();
        SndbKvExchangeResult[] exchanges = await Task.WhenAll(exchangeTasks).WaitAsync(cancellation);
        Assert.All(exchanges, result => Assert.NotNull(result.PreviousEntry));
        Assert.Equal(contenders, exchanges.Select(result => result.MutationVersion).Distinct().Count());
        SndbKvEntry finalEntry = (await client.GetAsync(keyspace, Tenant, "exchange", cancellation))!;
        byte[] history = exchanges.Select(result => Assert.Single(result.PreviousEntry!.Value))
            .Append(Assert.Single(finalEntry.Value)).Order().ToArray();
        byte[] expected = Enumerable.Range(0, contenders).Select(i => (byte)i).Append((byte)255).Order().ToArray();
        Assert.Equal(expected, history);
        Assert.Equal(exchanges.Max(result => result.MutationVersion), finalEntry.Version);

        var startDelete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SndbKvExchangeResult>[] deleteTasks = Enumerable.Range(0, contenders).Select(async _ =>
        {
            await startDelete.Task.WaitAsync(cancellation);
            return await client.GetAndDeleteAsync(keyspace, Tenant, "exchange", cancellation);
        }).ToArray();
        startDelete.SetResult();
        SndbKvExchangeResult[] deletes = await Task.WhenAll(deleteTasks).WaitAsync(cancellation);
        var winner = Assert.Single(deletes, result => result.PreviousEntry is not null);
        Assert.Equal(finalEntry.Value, winner.PreviousEntry!.Value);
        Assert.All(deletes.Where(result => result.PreviousEntry is null), result => Assert.Null(result.MutationVersion));
    }

    /// <summary>预取消的每一种原子写均不修改已有记录或创建新记录。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithPreCanceledToken_DoNotMutate(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        long version = await client.SetAsync(keyspace, Tenant, "item", [1], cancellationToken: deadline.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetAsync(
            keyspace, Tenant, "item", [9], cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SetConditionalAsync(
            keyspace, Tenant, "missing", [9], KvSetCondition.IfNotExists, cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAndSetAsync(
            keyspace, Tenant, "item", [9], cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAndDeleteAsync(
            keyspace, Tenant, "item", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompareAndSetAsync(
            keyspace, Tenant, "item", version, [9], cancellationToken: canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExpireAsync(
            keyspace, Tenant, "item", UtcMilliseconds().AddSeconds(-1), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PersistAsync(
            keyspace, Tenant, "item", canceled.Token));

        var unchanged = await client.GetAsync(keyspace, Tenant, "item", deadline.Token);
        Assert.NotNull(unchanged);
        Assert.Equal(version, unchanged.Version);
        Assert.Equal(new byte[] { 1 }, unchanged.Value);
        Assert.Null(await client.GetAsync(keyspace, Tenant, "missing", deadline.Token));
        Assert.Equal(version + 1, await client.SetAsync(
            keyspace, Tenant, "after", [2], cancellationToken: deadline.Token));
    }

    /// <summary>远程只读身份读取成功，所有原子写均返回稳定 forbidden 且无副作用。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithReadOnlyToken_ReturnForbiddenWithoutMutation(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        using var admin = fixture.CreateClient(protocol);
        using var readOnly = fixture.CreateClient(protocol, readOnly: true);
        string keyspace = NewKeyspace();
        long version = await admin.SetAsync(keyspace, Tenant, "item", [1], cancellationToken: cancellation);
        Assert.NotNull(await readOnly.GetAsync(keyspace, Tenant, "item", cancellation));

        Func<Task>[] mutations =
        [
            () => readOnly.SetConditionalAsync(keyspace, Tenant, "item", [9], KvSetCondition.IfExists, cancellationToken: cancellation),
            () => readOnly.GetAndSetAsync(keyspace, Tenant, "item", [9], cancellationToken: cancellation),
            () => readOnly.GetAndDeleteAsync(keyspace, Tenant, "item", cancellation),
            () => readOnly.CompareAndSetAsync(keyspace, Tenant, "item", version, [9], cancellationToken: cancellation),
            () => readOnly.ExpireAsync(keyspace, Tenant, "item", UtcMilliseconds().AddSeconds(-1), cancellation),
            () => readOnly.PersistAsync(keyspace, Tenant, "item", cancellation),
        ];
        foreach (Func<Task> mutate in mutations)
        {
            cancellation.ThrowIfCancellationRequested();
            var error = await Assert.ThrowsAsync<SndbServerException>(mutate);
            Assert.Equal("forbidden", error.Error);
        }

        var entry = await admin.GetAsync(keyspace, Tenant, "item", cancellation);
        Assert.NotNull(entry);
        Assert.Equal(version, entry.Version);
        Assert.Equal(new byte[] { 1 }, entry.Value);
        Assert.Equal(version + 1, await admin.SetAsync(keyspace, Tenant, "after", [2], cancellationToken: cancellation));
    }

    /// <summary>字符串 key 使用严格 UTF-8，非法代理项不能变成替代字符记录。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_WithInvalidUtf16_RejectBeforeMutation(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = fixture.CreateClient(protocol);
        string keyspace = NewKeyspace();
        const string invalidKey = "\uD800";

        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.SetConditionalAsync(
            keyspace, Tenant, invalidKey, [9], KvSetCondition.IfNotExists, cancellationToken: deadline.Token));
        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.GetAndSetAsync(
            keyspace, Tenant, invalidKey, [9], cancellationToken: deadline.Token));
        await Assert.ThrowsAsync<EncoderFallbackException>(() => client.GetAndDeleteAsync(
            keyspace, Tenant, invalidKey, deadline.Token));
        Assert.Null(await client.GetAsync(keyspace, Tenant, "\uFFFD", deadline.Token));
    }

    /// <summary>正常关闭并重新打开真实宿主后，原子提交、删除、TTL 和版本继续成立。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    [InlineData("auto")]
    public async Task AtomicOperations_AfterOrderlyRestart_RecoverCommittedStateAndContinueVersions(string protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken cancellation = deadline.Token;
        string keyspace = NewKeyspace();
        DateTimeOffset expiry = UtcMilliseconds().AddHours(1);
        long finalVersion;
        long persistentVersion;

        using (var client = fixture.CreateClient(protocol))
        {
            Assert.True((await client.SetConditionalAsync(
                keyspace, Tenant, "persistent", [1], KvSetCondition.IfNotExists, cancellationToken: cancellation)).Applied);
            var exchanged = await client.GetAndSetAsync(keyspace, Tenant, "persistent", [2], expiry, cancellation);
            persistentVersion = exchanged.MutationVersion!.Value;
            await client.SetAsync(keyspace, Tenant, "deleted", [3], cancellationToken: cancellation);
            finalVersion = (await client.GetAndDeleteAsync(keyspace, Tenant, "deleted", cancellation)).MutationVersion!.Value;
        }

        if (protocol != "embedded")
            await fixture.RestartAsync(cancellation);

        using var reopened = fixture.CreateClient(protocol);
        var recovered = await reopened.GetAsync(keyspace, Tenant, "persistent", cancellation);
        Assert.NotNull(recovered);
        Assert.Equal(new byte[] { 2 }, recovered.Value);
        Assert.Equal(persistentVersion, recovered.Version);
        Assert.Equal(expiry, recovered.ExpiresAtUtc);
        Assert.Null(await reopened.GetAsync(keyspace, Tenant, "deleted", cancellation));
        Assert.False((await reopened.SetConditionalAsync(
            keyspace, Tenant, "persistent", [99], KvSetCondition.IfNotExists, cancellationToken: cancellation)).Applied);
        var next = await reopened.SetConditionalAsync(
            keyspace, Tenant, "after", [4], KvSetCondition.IfNotExists, cancellationToken: cancellation);
        Assert.True(next.Applied);
        Assert.Equal(finalVersion + 1, next.Version);
    }

    private static string NewKeyspace() => "journey_" + Guid.NewGuid().ToString("N");

    private static DateTimeOffset UtcMilliseconds() =>
        DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
}

/// <summary>复用真实 TestServerHost 的双协议宿主，并仅回收本 fixture 拥有的目录。</summary>
public sealed class KvAtomicJourneyFixture : IAsyncLifetime
{
    private const string DatabaseName = "kv_atomic_journey";
    private const string AdminToken = "kv-journey-admin";
    private const string ReadOnlyToken = "kv-journey-readonly";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-kv-journey-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _frameUrl = string.Empty;

    /// <summary>启动独立 HTTP/1.1 与 HTTP/2 监听器，并创建旅程数据库。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(_restUrl), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            using var body = JsonContent.Create(new CreateDatabaseRequest(DatabaseName), ServerJsonContext.Default.CreateDatabaseRequest);
            using var response = await http.PostAsync("/v1/db", body, deadline.Token);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>创建指定传输的真实 SDK；嵌入式使用 fixture 内独立目录。</summary>
    public SndbKvClient CreateClient(string protocol, bool readOnly = false)
    {
        if (protocol == "embedded")
            return new SndbKvClient($"Data Source={Path.Combine(_root, "embedded")};Timeout=10");

        string address = protocol == "frame-http2" ? _frameUrl : _restUrl;
        string token = readOnly ? ReadOnlyToken : AdminToken;
        return new SndbKvClient(
            $"Data Source=sonnetdb+http://{new Uri(address).Authority}/{DatabaseName};Token={token};Timeout=10;Protocol={protocol}");
    }

    /// <summary>按正常关闭合同重启宿主，复用原数据库目录并自动加载。</summary>
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    /// <summary>释放真实宿主后，检查目录归属并删除本 fixture 创建的临时数据。</summary>
    public async Task DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await StopAsync(deadline.Token);
        string ownedRoot = Path.GetFullPath(_root);
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!ownedRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(ownedRoot).StartsWith("sonnetdb-kv-journey-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("KV fixture 临时目录不属于本测试。");
        }
        if (Directory.Exists(ownedRoot))
            Directory.Delete(ownedRoot, recursive: true);
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                [ReadOnlyToken] = ServerRoles.ReadOnly,
            },
        };
        _app = TestServerHost.Build(options, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        await _app.StartAsync(cancellationToken);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        Assert.Equal(2, addresses.Addresses.Count);
        _restUrl = string.Empty;
        _frameUrl = string.Empty;
        foreach (string address in addresses.Addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await ProbeHttp11Async(address, cancellationToken))
                _restUrl = address;
            else
                _frameUrl = address;
        }
        Assert.NotEmpty(_restUrl);
        Assert.NotEmpty(_frameUrl);
    }

    private async Task StopAsync(CancellationToken cancellationToken)
    {
        var app = _app;
        if (app is null)
            return;
        _app = null;
        try
        {
            await app.StopAsync(cancellationToken);
        }
        finally
        {
            await app.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task<bool> ProbeHttp11Async(string address, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
