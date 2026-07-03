using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M29 A #245 多模型只读管理契约端到端测试：KV keyspaces/scan、向量 indexes/search-preview、
/// 全文 indexes/search-preview/analyze、MQ topics/offsets/browse。数据经既有写端点铺设，
/// 断言只读契约的 JSON 结构与鉴权（readonly 可读、无 token 401）。
/// </summary>
public sealed class ManagementContractEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminToken = "admin-mgmt-token";
    private const string _readOnlyToken = "ro-mgmt-token";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-mgmt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminToken] = ServerRoles.Admin,
                [_readOnlyToken] = ServerRoles.ReadOnly,
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
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
            try { Directory.Delete(_dataRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    private HttpClient CreateClient(string? token = _adminToken)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task CreateDatabaseAsync(HttpClient client, string db)
    {
        var resp = await client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(db), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task ExecuteSqlAsync(HttpClient client, string db, string sql)
    {
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.IsSuccessStatusCode, $"SQL 失败：{(int)resp.StatusCode} {text}");
    }

    [Fact]
    public async Task Kv_Keyspaces_And_Scan_Cursor_Paginates()
    {
        using var admin = CreateClient(_adminToken);
        using var ro = CreateClient(_readOnlyToken);
        var db = "kvmgmt";
        await CreateDatabaseAsync(admin, db);

        // 铺数据：3 个 key（同前缀），走既有 KV set 端点。
        for (int i = 0; i < 3; i++)
        {
            var set = await admin.PostAsync($"/v1/db/{db}/kv/main/set",
                JsonContent.Create(new KvSetRequest($"user:{i}", Encoding.UTF8.GetBytes("v" + i), null),
                    ServerJsonContext.Default.KvSetRequest));
            Assert.True(set.IsSuccessStatusCode);
        }

        // keyspaces 列表
        var ksResp = await ro.PostAsync($"/v1/db/{db}/kv/keyspaces", null);
        Assert.Equal(HttpStatusCode.OK, ksResp.StatusCode);
        var ks = await ksResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvKeyspaceListResponse);
        Assert.Contains("main", ks!.Keyspaces);

        // 游标 scan：limit=2 → 第一页 2 条 + hasMore，第二页 1 条。
        var page1Resp = await ro.PostAsync($"/v1/db/{db}/kv/main/scan",
            JsonContent.Create(new KvScanCursorRequest("user:", null, 2), ServerJsonContext.Default.KvScanCursorRequest));
        Assert.Equal(HttpStatusCode.OK, page1Resp.StatusCode);
        var page1 = await page1Resp.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvScanCursorResponse);
        Assert.Equal(2, page1!.Entries.Count);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);

        var page2Resp = await ro.PostAsync($"/v1/db/{db}/kv/main/scan",
            JsonContent.Create(new KvScanCursorRequest("user:", page1.NextCursor, 2), ServerJsonContext.Default.KvScanCursorRequest));
        var page2 = await page2Resp.Content.ReadFromJsonAsync(ServerJsonContext.Default.KvScanCursorResponse);
        Assert.Single(page2!.Entries);
        Assert.False(page2.HasMore);
        Assert.Null(page2.NextCursor);
        Assert.Equal("user:2", page2.Entries[0].Key);
    }

    [Fact]
    public async Task Vector_Indexes_And_SearchPreview_ReturnHits()
    {
        using var admin = CreateClient(_adminToken);
        using var ro = CreateClient(_readOnlyToken);
        var db = "vecmgmt";
        await CreateDatabaseAsync(admin, db);

        await ExecuteSqlAsync(admin, db,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3) WITH INDEX hnsw(m=4, ef=8))");
        await ExecuteSqlAsync(admin, db,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1, 0, 0]), (2000, 'a', [0, 1, 0]), (3000, 'b', [0, 0, 1])");

        // 索引统计
        var idxResp = await ro.PostAsync($"/v1/db/{db}/vector/indexes", null);
        Assert.Equal(HttpStatusCode.OK, idxResp.StatusCode);
        var idx = await idxResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.VectorIndexStatResponse);
        var stat = Assert.Single(idx!.Indexes);
        Assert.Equal("docs", stat.Measurement);
        Assert.Equal("embedding", stat.Column);
        Assert.Equal("Hnsw", stat.Kind);
        Assert.Equal(3, stat.Dimension);
        Assert.Equal("cosine", stat.Metric);
        Assert.Contains(stat.Params, p => p.Key == "m" && p.Value == "4");
        Assert.Contains(stat.Params, p => p.Key == "ef" && p.Value == "8");

        // search-preview
        var searchResp = await ro.PostAsync($"/v1/db/{db}/vector/search-preview",
            JsonContent.Create(new VectorSearchPreviewRequest("docs", "embedding", new[] { 1f, 0f, 0f }, 2),
                ServerJsonContext.Default.VectorSearchPreviewRequest));
        Assert.Equal(HttpStatusCode.OK, searchResp.StatusCode);
        var search = await searchResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.VectorSearchPreviewResponse);
        Assert.NotEmpty(search!.Hits);
        Assert.True(search.Hits.Count <= 2);
    }

    [Fact]
    public async Task Vector_SearchPreview_RejectsInvalidIdentifier()
    {
        using var admin = CreateClient(_adminToken);
        var db = "vecmgmt2";
        await CreateDatabaseAsync(admin, db);

        var resp = await admin.PostAsync($"/v1/db/{db}/vector/search-preview",
            JsonContent.Create(new VectorSearchPreviewRequest("docs; DROP", "embedding", new[] { 1f, 0f }, 2),
                ServerJsonContext.Default.VectorSearchPreviewRequest));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task FullText_Indexes_Search_And_Analyze()
    {
        using var admin = CreateClient(_adminToken);
        using var ro = CreateClient(_readOnlyToken);
        var db = "ftmgmt";
        await CreateDatabaseAsync(admin, db);

        await ExecuteSqlAsync(admin, db, "CREATE DOCUMENT COLLECTION logs");
        await ExecuteSqlAsync(admin, db, """
            INSERT INTO logs (id, document)
            VALUES ('log-1', '{"message":"Pump alarm in north station"}'),
                   ('log-2', '{"message":"Fan alarm cleared"}'),
                   ('log-3', '{"message":"Pump pressure normal"}')
            """);
        await ExecuteSqlAsync(admin, db, "CREATE FULLTEXT INDEX ft_logs_message ON logs ('$.message') USING unicode");

        // 索引统计
        var idxResp = await ro.PostAsync($"/v1/db/{db}/fulltext/indexes", null);
        Assert.Equal(HttpStatusCode.OK, idxResp.StatusCode);
        var idx = await idxResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.FullTextIndexStatResponse);
        var stat = Assert.Single(idx!.Indexes);
        Assert.Equal("logs", stat.Collection);
        Assert.Equal("ft_logs_message", stat.Name);
        Assert.Equal("unicode", stat.Tokenizer);
        Assert.Equal(3, stat.DocumentCount);
        Assert.Contains("$.message", stat.Fields);

        // BM25 search-preview
        var searchResp = await ro.PostAsync($"/v1/db/{db}/fulltext/search-preview",
            JsonContent.Create(new FullTextSearchPreviewRequest("logs", "ft_logs_message", "$.message", "pump alarm", 5, null),
                ServerJsonContext.Default.FullTextSearchPreviewRequest));
        Assert.Equal(HttpStatusCode.OK, searchResp.StatusCode);
        var search = await searchResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.FullTextSearchPreviewResponse);
        Assert.NotEmpty(search!.Hits);
        Assert.Equal("log-1", search.Hits[0].DocumentId);
        Assert.True(search.Hits[0].Score > 0);

        // analyze
        var analyzeResp = await ro.PostAsync($"/v1/db/{db}/fulltext/analyze",
            JsonContent.Create(new FullTextAnalyzeRequest("unicode", "Pump alarm"), ServerJsonContext.Default.FullTextAnalyzeRequest));
        Assert.Equal(HttpStatusCode.OK, analyzeResp.StatusCode);
        var analyze = await analyzeResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.FullTextAnalyzeResponse);
        Assert.Equal(2, analyze!.Tokens.Count);
        Assert.Equal("pump", analyze.Tokens[0].Text.ToLowerInvariant());
    }

    [Fact]
    public async Task Mq_Topics_Offsets_And_Browse()
    {
        using var admin = CreateClient(_adminToken);
        using var ro = CreateClient(_readOnlyToken);
        var db = "mqmgmt";
        await CreateDatabaseAsync(admin, db);

        // 铺数据：publish 3 条到 telemetry，走既有 MQ publish 端点。
        for (int i = 0; i < 3; i++)
        {
            var pub = await admin.PostAsync($"/v1/db/{db}/mq/telemetry/publish",
                JsonContent.Create(new MqPublishRequest(Encoding.UTF8.GetBytes("m" + i), null),
                    ServerJsonContext.Default.MqPublishRequest));
            Assert.True(pub.IsSuccessStatusCode);
        }
        // 消费一条并 ack，制造 lag。
        var ack = await admin.PostAsync($"/v1/db/{db}/mq/telemetry/ack",
            JsonContent.Create(new MqAckRequest("rules", 0), ServerJsonContext.Default.MqAckRequest));
        Assert.True(ack.IsSuccessStatusCode);

        // topics 列表（前缀已剥离）
        var topicsResp = await ro.PostAsync($"/v1/db/{db}/mq/topics", null);
        Assert.Equal(HttpStatusCode.OK, topicsResp.StatusCode);
        var topics = await topicsResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.MqTopicListResponse);
        var topic = Assert.Single(topics!.Topics);
        Assert.Equal("telemetry", topic.Topic);
        Assert.Equal(3, topic.NextOffset);

        // offsets + lag
        var offResp = await ro.PostAsync($"/v1/db/{db}/mq/telemetry/offsets", null);
        Assert.Equal(HttpStatusCode.OK, offResp.StatusCode);
        var offsets = await offResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.MqOffsetsResponse);
        Assert.Equal(3, offsets!.NextOffset);
        var consumer = Assert.Single(offsets.Consumers);
        Assert.Equal("rules", consumer.ConsumerGroup);
        Assert.Equal(1, consumer.CommittedOffset);
        Assert.Equal(2, consumer.Lag);

        // browse by offset（只读，不改消费者组状态）
        var browseResp = await ro.PostAsync($"/v1/db/{db}/mq/telemetry/browse",
            JsonContent.Create(new MqBrowseRequest(0, 10), ServerJsonContext.Default.MqBrowseRequest));
        Assert.Equal(HttpStatusCode.OK, browseResp.StatusCode);
        var browse = await browseResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.MqBrowseResponse);
        Assert.Equal(3, browse!.Messages.Count);
        Assert.Equal(0, browse.Messages[0].Offset);
    }

    [Fact]
    public async Task ManagementEndpoints_RequireAuth()
    {
        using var admin = CreateClient(_adminToken);
        var db = "authmgmt";
        await CreateDatabaseAsync(admin, db);

        using var anon = CreateClient(token: null);
        var resp = await anon.PostAsync($"/v1/db/{db}/kv/keyspaces", null);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
