using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Channels;
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
/// M28 P5b #236 MQ 推送订阅双工流端点端到端测试：subscribe → REST publish → 收到推送帧、
/// 一连接多订阅交错、流上 ack + 重连续传、unsubscribe 停推、订阅错误隔离、非 HTTP/2 拒绝、
/// 流上控制帧与推送交错。用自写 duplex HttpContent 保持请求体打开、按需推帧。
/// </summary>
public sealed class FrameStreamEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _frameH2Url;
    private string? _dataRoot;
    private const string _adminToken = "admin-stream-token";
    private const string FrameContentType = "application/x-sonnetdb-frame";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-frame-stream-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [_adminToken] = ServerRoles.Admin },
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

    // ────────────────────────────── 1. subscribe → publish → 推送 ──────────────────────────────

    [Fact]
    public async Task Subscribe_ThenRestPublish_ReceivesPushFrame()
    {
        const string db = "streampush";
        await CreateDatabaseAsync(db);

        await using var stream = await OpenStreamAsync();
        // 从 latest 订阅：仅收订阅后到达的新消息。
        await stream.SendAsync(EncodeSubscribe(1, db, "t", MqSubscribeStartMode.Latest));

        (FrameHeader confirmHeader, byte[] confirmPayload) = await stream.ReadFrameAsync();
        Assert.Equal((byte)MqFrameOp.Subscribe, confirmHeader.Op);
        Assert.True(confirmHeader.IsResponse);
        Assert.Equal(1u, confirmHeader.StreamId);
        Assert.Equal(0, MqFrameCodec.DecodeSubscribeResponse(confirmPayload));

        byte[] payload = Encoding.UTF8.GetBytes("推送-1");
        await RestPublishAsync(db, "t", payload);

        (FrameHeader pushHeader, byte[] pushPayload) = await stream.ReadFrameAsync();
        Assert.Equal((byte)FrameFlags.Push, pushHeader.Flags);
        Assert.Equal(1u, pushHeader.StreamId);
        SonnetMqMessage[] messages = MqFrameCodec.DecodePullResponse(pushPayload, "t");
        Assert.Single(messages);
        Assert.Equal(payload, messages[0].Payload);
    }

    // ────────────────────────────── 2. 一连接多订阅交错 ──────────────────────────────

    [Fact]
    public async Task MultipleSubscriptions_OneConnection_InterleavePushes()
    {
        const string db = "streammulti";
        await CreateDatabaseAsync(db);

        await using var stream = await OpenStreamAsync();
        await stream.SendAsync(EncodeSubscribe(10, db, "ta", MqSubscribeStartMode.Latest));
        await stream.SendAsync(EncodeSubscribe(20, db, "tb", MqSubscribeStartMode.Latest));

        // 读两条确认帧（顺序不保证，按 streamId 归类）。
        var confirmed = new HashSet<uint>();
        for (int i = 0; i < 2; i++)
        {
            (FrameHeader h, _) = await stream.ReadFrameAsync();
            Assert.True(h.IsResponse);
            confirmed.Add(h.StreamId);
        }
        Assert.Equal([10u, 20u], confirmed.OrderBy(x => x).ToArray());

        await RestPublishAsync(db, "ta", Encoding.UTF8.GetBytes("a"));
        await RestPublishAsync(db, "tb", Encoding.UTF8.GetBytes("b"));

        var pushed = new Dictionary<uint, byte[]>();
        for (int i = 0; i < 2; i++)
        {
            (FrameHeader h, byte[] p) = await stream.ReadFrameAsync();
            Assert.Equal((byte)FrameFlags.Push, h.Flags);
            pushed[h.StreamId] = MqFrameCodec.DecodePullResponse(p, "t")[0].Payload;
        }

        Assert.Equal(Encoding.UTF8.GetBytes("a"), pushed[10]);
        Assert.Equal(Encoding.UTF8.GetBytes("b"), pushed[20]);
    }

    // ────────────────────────────── 3. 流上 ack + 重连续传 ──────────────────────────────

    [Fact]
    public async Task AckOverStream_ThenReconnect_ResumesFromCommittedOffset()
    {
        const string db = "streamack";
        await CreateDatabaseAsync(db);

        // 预置两条消息。
        await RestPublishAsync(db, "t", Encoding.UTF8.GetBytes("m0"));
        await RestPublishAsync(db, "t", Encoding.UTF8.GetBytes("m1"));

        await using (var stream = await OpenStreamAsync())
        {
            await stream.SendAsync(EncodeSubscribe(1, db, "t", MqSubscribeStartMode.ConsumerGroup, group: "g1"));
            Assert.Equal(0, MqFrameCodec.DecodeSubscribeResponse((await stream.ReadFrameAsync()).Payload));

            // 收到从 0 起的推送。
            (FrameHeader ph, byte[] pp) = await stream.ReadFrameAsync();
            SonnetMqMessage[] first = MqFrameCodec.DecodePullResponse(pp, "t");
            Assert.Equal(0, first[0].Offset);

            // 流上 ack offset 0（消费组 g1 提交到 1）。
            await stream.SendAsync(EncodeAck(2, db, "t", "g1", 0));
            (FrameHeader ah, byte[] ap) = await stream.ReadFrameAsync();
            Assert.Equal((byte)MqFrameOp.Ack, ah.Op);
            Assert.True(ah.IsResponse);
            Assert.Equal(1, MqFrameCodec.DecodeAckResponse(ap));
        }

        // 重连：consumerGroup 模式应从已提交位点 1 续传，不重发 offset 0。
        await using (var stream = await OpenStreamAsync())
        {
            await stream.SendAsync(EncodeSubscribe(1, db, "t", MqSubscribeStartMode.ConsumerGroup, group: "g1"));
            Assert.Equal(1, MqFrameCodec.DecodeSubscribeResponse((await stream.ReadFrameAsync()).Payload));

            (FrameHeader ph, byte[] pp) = await stream.ReadFrameAsync();
            SonnetMqMessage[] resumed = MqFrameCodec.DecodePullResponse(pp, "t");
            Assert.Equal(1, resumed[0].Offset);
        }
    }

    // ────────────────────────────── 4. unsubscribe 停推 ──────────────────────────────

    [Fact]
    public async Task Unsubscribe_StopsFurtherPushes()
    {
        const string db = "streamunsub";
        await CreateDatabaseAsync(db);

        await using var stream = await OpenStreamAsync();
        await stream.SendAsync(EncodeSubscribe(5, db, "t", MqSubscribeStartMode.Latest));
        await stream.ReadFrameAsync(); // confirm

        await stream.SendAsync(EncodeUnsubscribe(5));
        (FrameHeader uh, _) = await stream.ReadFrameAsync();
        Assert.Equal((byte)MqFrameOp.Unsubscribe, uh.Op);
        Assert.True(uh.IsResponse);

        // 退订后发布：不应再收到 streamId=5 的推送。用一条新 pull 控制帧作为“栅栏”验证无推送插入。
        await RestPublishAsync(db, "t", Encoding.UTF8.GetBytes("after-unsub"));
        await stream.SendAsync(EncodePull(6, db, "t", "probe", 10));
        (FrameHeader fh, _) = await stream.ReadFrameAsync();
        Assert.Equal(6u, fh.StreamId); // 直接是 pull 响应，没有 streamId=5 的推送抢先
        Assert.Equal((byte)MqFrameOp.Pull, fh.Op);
    }

    // ────────────────────────────── 5. 订阅错误隔离 ──────────────────────────────

    [Fact]
    public async Task BadSubscribeOnOneStream_DoesNotKillOther()
    {
        const string db = "streamiso";
        await CreateDatabaseAsync(db);

        await using var stream = await OpenStreamAsync();
        // stream 1：正常订阅。
        await stream.SendAsync(EncodeSubscribe(1, db, "t", MqSubscribeStartMode.Latest));
        Assert.True((await stream.ReadFrameAsync()).Header.IsResponse);

        // stream 2：不存在的 db → 错误帧，但连接与 stream 1 存活。
        await stream.SendAsync(EncodeSubscribe(2, "no-such-db", "t", MqSubscribeStartMode.Latest));
        (FrameHeader eh, byte[] ep) = await stream.ReadFrameAsync();
        Assert.Equal(2u, eh.StreamId);
        Assert.True(eh.IsError);
        (string code, _) = FrameCodec.ReadErrorPayload(ep);
        Assert.Equal("db_not_found", code);

        // stream 1 仍能收推送。
        await RestPublishAsync(db, "t", Encoding.UTF8.GetBytes("alive"));
        (FrameHeader ph, byte[] pp) = await stream.ReadFrameAsync();
        Assert.Equal(1u, ph.StreamId);
        Assert.Equal((byte)FrameFlags.Push, ph.Flags);
        Assert.Equal(Encoding.UTF8.GetBytes("alive"), MqFrameCodec.DecodePullResponse(pp, "t")[0].Payload);
    }

    // ────────────────────────────── 6. 非 HTTP/2 拒绝 ──────────────────────────────

    [Fact]
    public async Task NonHttp2_Returns400()
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        using var content = new ByteArrayContent([]);
        content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/frame/stream")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = content,
        };
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ────────────────────────────── 7. 流上控制帧与推送交错 ──────────────────────────────

    [Fact]
    public async Task PublishOverStream_ResponseInterleavesWithPushes()
    {
        const string db = "streamctrl";
        await CreateDatabaseAsync(db);

        await using var stream = await OpenStreamAsync();
        await stream.SendAsync(EncodeSubscribe(1, db, "t", MqSubscribeStartMode.Latest));
        await stream.ReadFrameAsync(); // confirm

        // 流上直接 publish（控制帧），应回 publish 响应帧 + 触发 streamId=1 的推送。
        await stream.SendAsync(EncodePublish(9, db, "t", Encoding.UTF8.GetBytes("via-stream")));

        var byStream = new Dictionary<uint, (FrameHeader Header, byte[] Payload)>();
        for (int i = 0; i < 2; i++)
        {
            (FrameHeader h, byte[] p) = await stream.ReadFrameAsync();
            byStream[h.StreamId] = (h, p);
        }

        Assert.True(byStream.ContainsKey(9)); // publish 响应
        Assert.Equal(0, MqFrameCodec.DecodePublishResponse(byStream[9].Payload));
        Assert.True(byStream.ContainsKey(1)); // 推送
        Assert.Equal((byte)FrameFlags.Push, byStream[1].Header.Flags);
        Assert.Equal(Encoding.UTF8.GetBytes("via-stream"), MqFrameCodec.DecodePullResponse(byStream[1].Payload, "t")[0].Payload);
    }

    // ────────────────────────────── 辅助 ──────────────────────────────

    private async Task CreateDatabaseAsync(string db)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await client.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(db), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task RestPublishAsync(string db, string topic, byte[] payload)
    {
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _adminToken);
        var resp = await client.PostAsync($"/v1/db/{db}/mq/{topic}/publish",
            JsonContent.Create(new MqPublishRequest(payload), ServerJsonContext.Default.MqPublishRequest));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<DuplexStream> OpenStreamAsync()
    {
        var duplex = new DuplexStream(_frameH2Url! + "/v1/frame/stream", _adminToken);
        await duplex.StartAsync();
        return duplex;
    }

    private static byte[] EncodeSubscribe(uint streamId, string db, string topic, MqSubscribeStartMode mode,
        string group = "", long startOffset = 0, int batchMax = 0)
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeSubscribeRequest(writer, streamId, db, topic, group, mode, startOffset, batchMax);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeUnsubscribe(uint streamId)
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeUnsubscribeRequest(writer, streamId);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodePublish(uint streamId, string db, string topic, byte[] payload)
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(writer, streamId, db, topic, null, payload);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodePull(uint streamId, string db, string topic, string group, int maxCount)
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePullRequest(writer, streamId, db, topic, group, maxCount);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeAck(uint streamId, string db, string topic, string group, long offset)
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeAckRequest(writer, streamId, db, topic, group, offset);
        return writer.WrittenMemory.ToArray();
    }

    /// <summary>
    /// 双工 HTTP/2 客户端：请求体是一条按需写入的 <see cref="PushStreamContent"/>，保持打开以持续发帧；
    /// 响应体增量读取推送帧。每帧一读，供测试断言。
    /// </summary>
    private sealed class DuplexStream : IAsyncDisposable
    {
        private readonly string _url;
        private readonly string _token;
        private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private Task<HttpResponseMessage>? _sendTask;
        private HttpResponseMessage? _response;
        private Stream? _responseStream;
        private readonly byte[] _readBuffer = new byte[64 * 1024];
        private readonly List<byte> _pending = [];

        public DuplexStream(string url, string token)
        {
            _url = url;
            _token = token;
        }

        public Task StartAsync()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _url)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new PushStreamContent(_outbound.Reader),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            // 关键：不 await 整个 SendAsync。full-duplex 下客户端请求头随首个 body 字节一起冲刷——若在此 await，
            // SerializeToStreamAsync 会阻塞在空 channel 上、请求头永不到达服务端、SendAsync 也就永不返回响应头。
            // 改为先起 send（其内部 SerializeToStreamAsync 开始 await outbound），响应在首次读取时惰性解析。
            _sendTask = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(byte[] frame) => _outbound.Writer.WriteAsync(frame);

        public async Task<(FrameHeader Header, byte[] Payload)> ReadFrameAsync()
        {
            await EnsureResponseAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                if (TryExtractFrame(out FrameHeader header, out byte[] payload))
                    return (header, payload);

                int read = await _responseStream!.ReadAsync(_readBuffer, timeout.Token);
                if (read == 0)
                    throw new InvalidOperationException("响应流在读到完整帧前结束。");
                _pending.AddRange(_readBuffer.AsSpan(0, read).ToArray());
            }
        }

        private async Task EnsureResponseAsync()
        {
            if (_responseStream is not null)
                return;
            _response = await _sendTask!;
            Assert.Equal(HttpVersion.Version20, _response.Version);
            Assert.Equal(HttpStatusCode.OK, _response.StatusCode);
            _responseStream = await _response.Content.ReadAsStreamAsync();
        }

        private bool TryExtractFrame(out FrameHeader header, out byte[] payload)
        {
            var buffer = new ReadOnlySequence<byte>(_pending.ToArray());
            if (FrameCodec.TryReadFrame(ref buffer, out header, out ReadOnlySequence<byte> payloadSeq))
            {
                payload = payloadSeq.ToArray();
                long consumed = _pending.Count - buffer.Length;
                _pending.RemoveRange(0, (int)consumed);
                return true;
            }

            payload = [];
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            _outbound.Writer.TryComplete();
            _responseStream?.Dispose();
            _response?.Dispose();
            _client.Dispose();
            await Task.CompletedTask;
        }
    }

    /// <summary>把一个 channel 里的字节块作为 HTTP 请求体持续写出，channel 完成时结束请求体。</summary>
    private sealed class PushStreamContent(ChannelReader<byte[]> reader) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await foreach (byte[] chunk in reader.ReadAllAsync())
            {
                await stream.WriteAsync(chunk);
                await stream.FlushAsync();
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
