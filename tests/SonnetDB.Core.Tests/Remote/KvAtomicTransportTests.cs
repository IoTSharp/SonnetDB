using System.Buffers;
using System.Net;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Remote;

/// <summary>原子 KV 请求的传输故障不得触发自动重发或返回 REST 回退信号。</summary>
public sealed class KvAtomicTransportTests
{
    private const uint StreamId = 73;

    /// <summary>连接在请求处理后中断时，只能报告未知结果。</summary>
    [Theory]
    [InlineData(SndbTransportProtocol.Auto)]
    [InlineData(SndbTransportProtocol.FrameHttp2)]
    public async Task SendUnaryAsync_WithConnectionFailureAndFallbackDisabled_SendsOnce(SndbTransportProtocol protocol)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new CountingHandler(_ => throw new HttpRequestException("response lost after request processing"));
        using var http = CreateClient(handler);
        var channel = new FrameChannel(http, protocol);

        var error = await Assert.ThrowsAsync<SndbServerException>(() =>
            channel.SendUnaryAsync(Request(), deadline.Token, allowFallback: false));

        Assert.Equal("frame_transport_error", error.Error);
        Assert.True(channel.ShouldTryFrames());
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>HTTP 错误不能成为原子写自动回退的依据。</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendUnaryAsync_WithHttpErrorAndFallbackDisabled_SendsOnce(HttpStatusCode status)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new CountingHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var http = CreateClient(handler);
        var channel = new FrameChannel(http, SndbTransportProtocol.Auto);

        var error = await Assert.ThrowsAsync<SndbServerException>(() =>
            channel.SendUnaryAsync(Request(), deadline.Token, allowFallback: false));

        Assert.Equal("frame_transport_error", error.Error);
        Assert.True(channel.ShouldTryFrames());
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>空体、截断和超大声明均保留失败，不转换成回退信号。</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("oversized")]
    public async Task SendUnaryAsync_WithMalformedResponseAndFallbackDisabled_SendsOnce(string kind)
    {
        byte[] body = kind switch
        {
            "empty" => [],
            "truncated" => Response()[..^1],
            "trailing" => [.. Response(), 0],
            "oversized" => OversizedHeader(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await AssertTransportFailureOnceAsync(body);
    }

    /// <summary>一元原子写要求响应版本、标记和关联字段全部匹配。</summary>
    [Theory]
    [InlineData("service")]
    [InlineData("op")]
    [InlineData("stream")]
    [InlineData("response-flag")]
    [InlineData("reserved-flag")]
    [InlineData("version")]
    [InlineData("multiple")]
    public async Task SendUnaryAsync_WithUncorrelatedResponseAndFallbackDisabled_SendsOnce(string kind)
    {
        byte[] body = kind switch
        {
            "service" => Response(service: FrameService.Mq),
            "op" => Response(op: KvFrameOp.Get),
            "stream" => Response(streamId: StreamId + 1),
            "response-flag" => Response(flags: FrameFlags.None),
            "reserved-flag" => Response(flags: (FrameFlags)0x81),
            "version" => Response(version: FrameHeader.CurrentVersion + 1),
            "multiple" => [.. Response(), .. Response()],
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await AssertTransportFailureOnceAsync(body);
    }

    /// <summary>等待响应超时也不能发第二次请求。</summary>
    [Fact]
    public async Task SendUnaryAsync_WithResponseTimeoutAndFallbackDisabled_SendsOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new CountingHandler(async cancellationToken =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return Ok(Response());
        });
        using var http = CreateClient(handler);
        http.Timeout = TimeSpan.FromMilliseconds(100);
        var channel = new FrameChannel(http, SndbTransportProtocol.Auto);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.SendUnaryAsync(Request(), deadline.Token, allowFallback: false));

        Assert.True(channel.ShouldTryFrames());
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>匹配的一元响应返回原始 payload，不重复请求。</summary>
    [Fact]
    public async Task SendUnaryAsync_WithMatchingResponseAndFallbackDisabled_ReturnsOnce()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new CountingHandler(_ => Task.FromResult(Ok(Response())));
        using var http = CreateClient(handler);
        var channel = new FrameChannel(http, SndbTransportProtocol.Auto);

        var response = await channel.SendUnaryAsync(Request(), deadline.Token, allowFallback: false);

        Assert.NotNull(response);
        Assert.Equal(42, KvFrameCodec.DecodePutResponse(response.Value.Payload));
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>匹配的业务错误仍保留稳定错误码，不触发重发。</summary>
    [Fact]
    public async Task SendUnaryAsync_WithInbandErrorAndFallbackDisabled_PreservesErrorWithoutRetry()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var writer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteErrorFrame(writer, (byte)FrameService.Kv, (byte)KvFrameOp.Put, StreamId, "forbidden", "write denied");
        var handler = new CountingHandler(_ => Task.FromResult(Ok(writer.WrittenMemory.ToArray())));
        using var http = CreateClient(handler);
        var channel = new FrameChannel(http, SndbTransportProtocol.Auto);

        var error = await Assert.ThrowsAsync<SndbServerException>(() =>
            channel.SendUnaryAsync(Request(), deadline.Token, allowFallback: false));

        Assert.Equal("forbidden", error.Error);
        Assert.True(channel.ShouldTryFrames());
        Assert.Equal(1, handler.CallCount);
    }

    /// <summary>预取消不能把请求发送到传输 handler。</summary>
    [Fact]
    public async Task SendUnaryAsync_WithPreCancellationAndFallbackDisabled_DoesNotSend()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var handler = new CountingHandler(_ => Task.FromResult(Ok(Response())));
        using var http = CreateClient(handler);
        var channel = new FrameChannel(http, SndbTransportProtocol.Auto);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            channel.SendUnaryAsync(Request(), canceled.Token, allowFallback: false));

        Assert.Equal(0, handler.CallCount);
    }

    private static async Task AssertTransportFailureOnceAsync(byte[] responseBody)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = new CountingHandler(_ => Task.FromResult(Ok(responseBody)));
        using var http = CreateClient(handler);
        var channel = new FrameChannel(http, SndbTransportProtocol.Auto);

        var error = await Assert.ThrowsAsync<SndbServerException>(() =>
            channel.SendUnaryAsync(Request(), deadline.Token, allowFallback: false));

        Assert.Equal("frame_transport_error", error.Error);
        Assert.True(channel.ShouldTryFrames());
        Assert.Equal(1, handler.CallCount);
    }

    private static HttpClient CreateClient(HttpMessageHandler handler) => new(handler)
    {
        BaseAddress = new Uri("http://kv-atomic-transport.test/"),
        Timeout = TimeSpan.FromSeconds(5),
    };

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(body),
    };

    private static byte[] Request()
    {
        var writer = new ArrayBufferWriter<byte>();
        KvFrameCodec.EncodePutRequest(writer, StreamId, "demo", "cache", "key"u8, [1, 2, 3]);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] Response(FrameService service = FrameService.Kv, KvFrameOp op = KvFrameOp.Put,
        uint streamId = StreamId, FrameFlags flags = FrameFlags.Response, byte version = FrameHeader.CurrentVersion)
    {
        var writer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(writer, new FrameHeader(1, version, (byte)service, (byte)op, (byte)flags, streamId), [42]);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] OversizedHeader()
    {
        byte[] header = new byte[FrameHeader.Size];
        new FrameHeader(FrameHeader.MaxFramePayloadBytes + 1, FrameHeader.CurrentVersion,
            (byte)FrameService.Kv, (byte)KvFrameOp.Put, (byte)FrameFlags.Response, StreamId).Write(header);
        return header;
    }

    private sealed class CountingHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/v1/frame", request.RequestUri!.AbsolutePath);
            CallCount++;
            return responder(cancellationToken);
        }
    }
}
