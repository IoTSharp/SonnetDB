using System.Buffers;
using System.Net;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Remote;

/// <summary>
/// M28 P5b #241 <see cref="FrameChannel"/> 探测状态机单元测试：用 stub <see cref="HttpMessageHandler"/>
/// 驱动，验证 auto 探测回落、rest 强制、frame-http2 强制不回落、带内错误帧转异常。
/// </summary>
public sealed class FrameChannelTests
{
    private const string FrameUrl = "/v1/frame";

    [Fact]
    public async Task Auto_TransportFailure_CachesRest_And_SkipsSubsequentFramePosts()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType));
        var channel = new FrameChannel(CreateClient(handler), SndbTransportProtocol.Auto);

        Assert.True(channel.ShouldTryFrames());
        var first = await channel.TrySendAsync(SamplePublishFrame(), CancellationToken.None);
        Assert.Null(first); // 传输级失败 → 回落信号

        // 探测缓存为 Rest：后续不应再尝试帧
        Assert.False(channel.ShouldTryFrames());
        var second = await channel.TrySendAsync(SamplePublishFrame(), CancellationToken.None);
        Assert.Null(second);
        Assert.Equal(1, handler.FrameCallCount); // 只尝试过一次
    }

    [Fact]
    public async Task Auto_ConnectionException_FallsBackToRest()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var channel = new FrameChannel(CreateClient(handler), SndbTransportProtocol.Auto);

        var result = await channel.TrySendAsync(SamplePublishFrame(), CancellationToken.None);
        Assert.Null(result);
        Assert.False(channel.ShouldTryFrames());
    }

    [Fact]
    public async Task Rest_NeverPostsToFrameEndpoint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var channel = new FrameChannel(CreateClient(handler), SndbTransportProtocol.Rest);

        Assert.False(channel.ShouldTryFrames());
        var result = await channel.TrySendAsync(SamplePublishFrame(), CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(0, handler.FrameCallCount);
    }

    [Fact]
    public async Task FrameHttp2_TransportFailure_Throws_NoSilentFallback()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.UnsupportedMediaType));
        var channel = new FrameChannel(CreateClient(handler), SndbTransportProtocol.FrameHttp2);

        var ex = await Assert.ThrowsAsync<SndbServerException>(
            () => channel.TrySendAsync(SamplePublishFrame(), CancellationToken.None));
        Assert.Equal("frame_transport_error", ex.Error);
        Assert.True(channel.ShouldTryFrames()); // 强制模式不缓存回落
    }

    [Fact]
    public async Task InbandErrorFrame_ThrowIfError_MapsToServerException()
    {
        // 服务端"懂帧"但操作失败：HTTP 200 + 一个错误帧
        var errorBody = BuildErrorFrame(streamId: 1, code: "db_not_found", message: "库不存在");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(errorBody),
        });
        var channel = new FrameChannel(CreateClient(handler), SndbTransportProtocol.Auto);

        var ex = await Assert.ThrowsAsync<SndbServerException>(
            () => channel.SendUnaryAsync(SamplePublishFrame(), CancellationToken.None));
        Assert.Equal("db_not_found", ex.Error);
        Assert.True(channel.ShouldTryFrames()); // 成功成帧 → 缓存走帧
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("http://frame-test.local/") };

    private static byte[] SamplePublishFrame()
    {
        var w = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodePublishRequest(w, 1, "db", "topic", null, [1, 2, 3]);
        return w.WrittenMemory.ToArray();
    }

    private static byte[] BuildErrorFrame(uint streamId, string code, string message)
    {
        var w = new ArrayBufferWriter<byte>();
        FrameCodec.WriteErrorFrame(w, (byte)FrameService.Mq, (byte)MqFrameOp.Publish, streamId, code, message);
        return w.WrittenMemory.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int FrameCallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == FrameUrl)
                FrameCallCount++;
            return Task.FromResult(responder(request));
        }
    }
}
