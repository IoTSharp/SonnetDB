using System.Buffers;
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
using SonnetDB.Json;
using SonnetDB.Protocol;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M28 P5b #235 通用二进制帧端点端到端测试：帧内 MQ publish/publish-batch/pull/ack 往返、
/// 跨协议等价（帧 ↔ REST）、多帧 streamId 回显、批内混合成败、鉴权、协议错误与真 h2c。
/// </summary>
public sealed class FrameEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _frameH2Url;
    private string? _dataRoot;
    private const string _adminToken = "admin-frame-token";
    private const string _readOnlyToken = "ro-frame-token";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-frame-tests-" + Guid.NewGuid().ToString("N"));
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

        _app = TestServerHost.Build(options, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        Assert.Equal(2, addresses.Addresses.Count);

        // 探测法区分两个端口：HTTP/1.1 精确版本探测 /healthz，h2c-only 端点会拒绝
        foreach (string address in addresses.Addresses)
        {
            if (await ProbeIsHttp11Async(address))
                _baseUrl = address;
            else
                _frameH2Url = address;
        }

        Assert.NotNull(_baseUrl);
        Assert.NotNull(_frameH2Url);
    }

    private static async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request);
            // h2c-only 端点对 HTTP/1.1 请求可能回一个 HTTP/1.1 错误响应而非断连，故要求 2xx
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
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

    private static async Task<List<(FrameHeader Header, byte[] Payload)>> PostFramesAsync(
        HttpClient client, byte[] body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        using var response = await client.PostAsync("/v1/frame", content);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.OK)
            return [];

        Assert.Equal(FrameContentType, response.Content.Headers.ContentType?.MediaType);
        byte[] responseBody = await response.Content.ReadAsByteArrayAsync();
        return ParseFrames(responseBody);
    }

    private static List<(FrameHeader Header, byte[] Payload)> ParseFrames(byte[] body)
    {
        var frames = new List<(FrameHeader, byte[])>();
        var buffer = new ReadOnlySequence<byte>(body);
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
            frames.Add((header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        return frames;
    }

    // ────────────────────────────── 1. 帧内全往返 ──────────────────────────────

    [Fact]
    public async Task Frame_Publish_Pull_Ack_RoundTrip()
    {
        using var admin = CreateClient();
        const string db = "framerd";
        await CreateDatabaseAsync(admin, db);

        byte[] payload = [0x00, 0x01, 0xFE, 0xFF, 0x7F, 0x80];
        var headers = new Dictionary<string, string> { ["source"] = "设备-01" };

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, db, "events", headers, payload);
        var publishFrames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Single(publishFrames);
        Assert.True(publishFrames[0].Header.IsResponse);
        Assert.False(publishFrames[0].Header.IsError);
        Assert.Equal(1u, publishFrames[0].Header.StreamId);
        long offset = MqFrameCodec.DecodePublishResponse(publishFrames[0].Payload);
        Assert.Equal(0, offset);

        writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullRequest(writer, 2, db, "events", "g1", 10);
        var pullFrames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Single(pullFrames);
        SonnetMqMessage[] messages = MqFrameCodec.DecodePullResponse(pullFrames[0].Payload, "events");
        Assert.Single(messages);
        Assert.Equal(payload, messages[0].Payload);
        Assert.Equal("设备-01", messages[0].Headers["source"]);
        Assert.Equal(0, messages[0].Offset);

        writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeAckRequest(writer, 3, db, "events", "g1", 0);
        var ackFrames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Single(ackFrames);
        Assert.Equal(1, MqFrameCodec.DecodeAckResponse(ackFrames[0].Payload));
    }

    // ────────────────────────────── 2. 跨协议等价 ──────────────────────────────

    [Fact]
    public async Task Frame_Publish_Then_Rest_Pull_PayloadIdentical()
    {
        using var admin = CreateClient();
        const string db = "framerest";
        await CreateDatabaseAsync(admin, db);

        byte[] payload = Encoding.UTF8.GetBytes("frame→rest 等价 ✓");
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, db, "xproto", null, payload);
        await PostFramesAsync(admin, writer.WrittenMemory.ToArray());

        // REST pull：byte[] JSON 反序列化时自动 Base64 解码
        var pullResp = await admin.PostAsync($"/v1/db/{db}/mq/xproto/pull",
            JsonContent.Create(new MqPullRequest("g1", 10), ServerJsonContext.Default.MqPullRequest));
        Assert.Equal(HttpStatusCode.OK, pullResp.StatusCode);
        var pull = await pullResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.MqPullResponse);
        Assert.Single(pull!.Messages);
        Assert.Equal(payload, pull.Messages[0].Payload);

        // REST stats 反映帧发布
        var statsResp = await admin.PostAsync($"/v1/db/{db}/mq/xproto/stats", null);
        var stats = await statsResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.MqStatsResponse);
        Assert.Equal(1, stats!.NextOffset);
    }

    [Fact]
    public async Task Rest_Publish_Then_Frame_Pull_PayloadIdentical()
    {
        using var admin = CreateClient();
        const string db = "restframe";
        await CreateDatabaseAsync(admin, db);

        byte[] payload = [1, 2, 3, 4, 5];
        var publishResp = await admin.PostAsync($"/v1/db/{db}/mq/xproto2/publish",
            JsonContent.Create(new MqPublishRequest(payload), ServerJsonContext.Default.MqPublishRequest));
        Assert.Equal(HttpStatusCode.Created, publishResp.StatusCode);

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullRequest(writer, 1, db, "xproto2", "g1", 10);
        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        SonnetMqMessage[] messages = MqFrameCodec.DecodePullResponse(frames[0].Payload, "xproto2");
        Assert.Single(messages);
        Assert.Equal(payload, messages[0].Payload);
    }

    // ────────────────────────────── 3. 多帧 + streamId 回显 ──────────────────────────────

    [Fact]
    public async Task MultiFrame_Body_RespondsInOrder_EchoesStreamIds()
    {
        using var admin = CreateClient();
        const string db = "framemulti";
        await CreateDatabaseAsync(admin, db);

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 11, db, "t", null, [1]);
        MqFrameCodec.EncodePublishRequest(writer, 22, db, "t", null, [2]);
        MqFrameCodec.EncodePullRequest(writer, 33, db, "t", "g1", 10);
        MqFrameCodec.EncodeAckRequest(writer, 44, db, "t", "g1", 1);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(4, frames.Count);
        Assert.Equal(11u, frames[0].Header.StreamId);
        Assert.Equal(22u, frames[1].Header.StreamId);
        Assert.Equal(33u, frames[2].Header.StreamId);
        Assert.Equal(44u, frames[3].Header.StreamId);
        Assert.All(frames, f => Assert.False(f.Header.IsError));

        Assert.Equal(0, MqFrameCodec.DecodePublishResponse(frames[0].Payload));
        Assert.Equal(1, MqFrameCodec.DecodePublishResponse(frames[1].Payload));
        Assert.Equal(2, MqFrameCodec.DecodePullResponse(frames[2].Payload, "t").Length);
        Assert.Equal(2, MqFrameCodec.DecodeAckResponse(frames[3].Payload));
    }

    // ────────────────────────────── 4. 批内混合成败 ──────────────────────────────

    [Fact]
    public async Task MixedBatch_OneBadDb_OthersSucceed()
    {
        using var admin = CreateClient();
        const string db = "framemixed";
        await CreateDatabaseAsync(admin, db);

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, db, "t", null, [1]);
        MqFrameCodec.EncodePublishRequest(writer, 2, "no-such-db", "t", null, [2]);
        MqFrameCodec.EncodePublishRequest(writer, 3, db, "t", null, [3]);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(3, frames.Count);

        Assert.False(frames[0].Header.IsError);
        Assert.Equal(0, MqFrameCodec.DecodePublishResponse(frames[0].Payload));

        Assert.True(frames[1].Header.IsError);
        Assert.Equal(2u, frames[1].Header.StreamId);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[1].Payload);
        Assert.Equal("db_not_found", code);

        Assert.False(frames[2].Header.IsError);
        Assert.Equal(1, MqFrameCodec.DecodePublishResponse(frames[2].Payload));
    }

    // ────────────────────────────── 5. 鉴权 ──────────────────────────────

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var anonymous = CreateClient(token: null);
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, "any", "t", null, [1]);
        using var content = new ByteArrayContent(writer.WrittenMemory.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        using var response = await anonymous.PostAsync("/v1/frame", content);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyToken_PullOk_PublishForbiddenErrorFrame()
    {
        using var admin = CreateClient();
        using var ro = CreateClient(_readOnlyToken);
        const string db = "framero";
        await CreateDatabaseAsync(admin, db);

        var adminWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(adminWriter, 1, db, "t", null, [9]);
        await PostFramesAsync(admin, adminWriter.WrittenMemory.ToArray());

        // readonly pull 成功
        var pullWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullRequest(pullWriter, 2, db, "t", "g1", 10);
        var pullFrames = await PostFramesAsync(ro, pullWriter.WrittenMemory.ToArray());
        Assert.False(pullFrames[0].Header.IsError);
        Assert.Single(MqFrameCodec.DecodePullResponse(pullFrames[0].Payload, "t"));

        // readonly publish → forbidden 错误帧
        var publishWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(publishWriter, 3, db, "t", null, [1]);
        var publishFrames = await PostFramesAsync(ro, publishWriter.WrittenMemory.ToArray());
        Assert.True(publishFrames[0].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(publishFrames[0].Payload);
        Assert.Equal("forbidden", code);
    }

    // ────────────────────────────── 6. 协议错误 ──────────────────────────────

    [Fact]
    public async Task WrongContentType_Returns415()
    {
        using var admin = CreateClient();
        using var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await admin.PostAsync("/v1/frame", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task GarbageFirstFrame_Returns400()
    {
        using var admin = CreateClient();
        // 前 4 字节声明超限 payload 长度 → 首帧即无法成帧
        byte[] garbage = [0xFF, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00];
        await PostFramesAsync(admin, garbage, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EmptyBody_Returns400()
    {
        using var admin = CreateClient();
        await PostFramesAsync(admin, [], HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TruncatedTrailingFrame_ValidFrameProcessed_TrailingErrorFrame()
    {
        using var admin = CreateClient();
        const string db = "frametrunc";
        await CreateDatabaseAsync(admin, db);

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, db, "t", null, [1]);
        // 追加一个只有帧头、payload 缺失的残帧
        var truncated = new FrameHeader(100, FrameHeader.CurrentVersion,
            (byte)FrameService.Mq, (byte)MqFrameOp.Publish, 0, 999);
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        truncated.Write(headerBytes);
        writer.Write(headerBytes);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(2, frames.Count);
        Assert.False(frames[0].Header.IsError);
        Assert.True(frames[1].Header.IsError);
        Assert.Equal(999u, frames[1].Header.StreamId);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[1].Payload);
        Assert.Equal("bad_frame", code);
    }

    [Fact]
    public async Task UnsupportedService_AfterValidFrame_ErrorFrame()
    {
        using var admin = CreateClient();
        const string db = "framesvc";
        await CreateDatabaseAsync(admin, db);

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, db, "t", null, [1]);
        // service 8 未定义（mq..doc = 1..7 已全部挂载）
        var kvFrame = new FrameHeader(0, FrameHeader.CurrentVersion, 8, 1, 0, 2);
        Span<byte> headerBytes = stackalloc byte[FrameHeader.Size];
        kvFrame.Write(headerBytes);
        writer.Write(headerBytes);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Equal(2, frames.Count);
        Assert.True(frames[1].Header.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(frames[1].Payload);
        Assert.Equal("unsupported_service", code);
    }

    // ────────────────────────────── 7. 真 h2c ──────────────────────────────

    [Fact]
    public async Task H2c_PriorKnowledge_Publish_RespondsHttp2()
    {
        using var admin = CreateClient();
        const string db = "frameh2c";
        await CreateDatabaseAsync(admin, db);

        byte[] payload = Encoding.UTF8.GetBytes("h2c 帧发布");
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, 1, db, "h2topic", null, payload);

        using var h2Client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, _frameH2Url + "/v1/frame")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(writer.WrittenMemory.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);

        using var response = await h2Client.SendAsync(request);
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var frames = ParseFrames(await response.Content.ReadAsByteArrayAsync());
        Assert.Single(frames);
        Assert.False(frames[0].Header.IsError);
        Assert.Equal(0, MqFrameCodec.DecodePublishResponse(frames[0].Payload));

        // h2c 口发布的消息可经 REST（HTTP/1.1 口）拉到
        var pullResp = await admin.PostAsync($"/v1/db/{db}/mq/h2topic/pull",
            JsonContent.Create(new MqPullRequest("g1", 10), ServerJsonContext.Default.MqPullRequest));
        var pull = await pullResp.Content.ReadFromJsonAsync(ServerJsonContext.Default.MqPullResponse);
        Assert.Single(pull!.Messages);
        Assert.Equal(payload, pull.Messages[0].Payload);
    }

    // ────────────────────────────── 8. 大体（> 默认 30MB 限制） ──────────────────────────────

    [Fact]
    public async Task LargeBatch_Over30MB_Succeeds()
    {
        using var admin = CreateClient();
        const string db = "framelarge";
        await CreateDatabaseAsync(admin, db);

        // 5 条 × 8 MiB ≈ 40 MiB，超过 Kestrel 默认 30MB 请求体限制
        const int entrySize = 8 * 1024 * 1024;
        var entries = new List<SonnetMqPublishEntry>();
        for (int i = 0; i < 5; i++)
        {
            byte[] data = new byte[entrySize];
            Array.Fill(data, (byte)(i + 1));
            entries.Add(new SonnetMqPublishEntry(data));
        }

        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishBatchRequest(writer, 1, db, "big", entries);

        var frames = await PostFramesAsync(admin, writer.WrittenMemory.ToArray());
        Assert.Single(frames);
        Assert.False(frames[0].Header.IsError);
        long[] offsets = MqFrameCodec.DecodePublishBatchResponse(frames[0].Payload);
        Assert.Equal(new long[] { 0, 1, 2, 3, 4 }, offsets);

        // 抽查第 3 条内容完整
        var pullWriter = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullRequest(pullWriter, 2, db, "big", "g1", 5);
        var pullFrames = await PostFramesAsync(admin, pullWriter.WrittenMemory.ToArray());
        SonnetMqMessage[] messages = MqFrameCodec.DecodePullResponse(pullFrames[0].Payload, "big");
        Assert.Equal(5, messages.Length);
        Assert.Equal(entrySize, messages[2].Payload.Length);
        Assert.All(messages[2].Payload, b => Assert.Equal(3, b));
    }
}
