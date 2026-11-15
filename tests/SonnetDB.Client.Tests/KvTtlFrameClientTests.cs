using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.IO;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

/// <summary>KV GetTimeToLive 的独立 Protocol/Client wire 合同。</summary>
public sealed class KvTtlFrameClientTests
{
    private static readonly DateTimeOffset Expiry =
        new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234);

    [Fact]
    public void TtlCodec_RequestAndResponse_RoundTripsThreeStates()
    {
        var requestWriter = new ArrayBufferWriter<byte>();
        KvDataFrameCodec.EncodeGetTimeToLiveRequest(
            requestWriter, 41, "demo", "cache", new byte[] { 0, 255 });
        Assert.True(FrameHeader.TryRead(requestWriter.WrittenSpan, out var requestHeader));
        Assert.Equal((byte)KvFrameOp.GetTimeToLive, requestHeader.Op);
        Assert.Equal(41u, requestHeader.StreamId);

        var requestReader = new SpanReader(requestWriter.WrittenMemory[FrameHeader.Size..].Span);
        Assert.Equal("demo", requestReader.ReadVarString());
        Assert.Equal("cache", requestReader.ReadVarString());
        Assert.Equal(2u, requestReader.ReadVarUInt32());
        Assert.Equal(new byte[] { 0, 255 }, requestReader.ReadBytes(2).ToArray());
        Assert.Equal(0, requestReader.Remaining);

        Assert.Equal(new KvDataFrameTtl(-2, null),
            KvDataFrameCodec.DecodeGetTimeToLiveResponse(Join(Int64(-2), [0])));
        Assert.Equal(new KvDataFrameTtl(-1, null),
            KvDataFrameCodec.DecodeGetTimeToLiveResponse(Join(Int64(-1), [0])));
        Assert.Equal(new KvDataFrameTtl(1234, Expiry),
            KvDataFrameCodec.DecodeGetTimeToLiveResponse(Join(Int64(1234), [1], Int64(Expiry.UtcTicks))));
    }

    [Fact]
    public async Task GetTimeToLiveKvAsync_UsesExactHttp2AndDecodesExpiry()
    {
        var handler = new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            var sequence = new ReadOnlySequence<byte>(
                await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal((byte)FrameService.Kv, header.Service);
            Assert.Equal((byte)KvFrameOp.GetTimeToLive, header.Op);
            Assert.Equal(42u, header.StreamId);
            var reader = new SpanReader(payload.ToArray());
            Assert.Equal("demo", reader.ReadVarString());
            Assert.Equal("cache", reader.ReadVarString());
            Assert.Equal(2u, reader.ReadVarUInt32());
            Assert.Equal(new byte[] { 0, 255 }, reader.ReadBytes(2).ToArray());
            Assert.Equal(0, reader.Remaining);
            return Reply(header, Join(Int64(1234), [1], Int64(Expiry.UtcTicks)));
        });

        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"), "token");
        KvDataFrameTtl result = await client.GetTimeToLiveKvAsync(
            "demo", "cache", new byte[] { 0, 255 }, streamId: 42);

        Assert.Equal(1234, result.Milliseconds);
        Assert.Equal(Expiry, result.ExpiresAtUtc);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("negative")]
    [InlineData("missing-expiry")]
    [InlineData("positive-no-expiry")]
    [InlineData("trailing")]
    public async Task GetTimeToLiveKvAsync_MalformedPayloadIsUnknownAndNeverRetried(string shape)
    {
        byte[] payload = shape switch
        {
            "negative" => Join(Int64(-3), [0]),
            "missing-expiry" => Join(Int64(-2), [1], Int64(Expiry.UtcTicks)),
            "positive-no-expiry" => Join(Int64(1), [0]),
            "trailing" => [.. Join(Int64(-1), [0]), 0],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var handler = new Handler(async (request, cancellationToken) =>
        {
            var sequence = new ReadOnlySequence<byte>(
                await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out _));
            return Reply(header, payload);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));

        var error = await Assert.ThrowsAsync<SndbFrameException>(() =>
            client.GetTimeToLiveKvAsync("demo", "cache", new byte[] { 1 }));

        Assert.Equal("frame_kv_terminal", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetTimeToLiveKvAsync_PreCanceledDoesNotSend()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetTimeToLiveKvAsync("demo", "cache", new byte[] { 1 }, cancellationToken: cancellation.Token));
        Assert.Equal(0, handler.Calls);
    }

    private static HttpResponseMessage Reply(FrameHeader request, byte[] payload)
    {
        var encoded = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(encoded, new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            request.Service, request.Op, (byte)FrameFlags.Response, request.StreamId), payload);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(encoded.WrittenMemory.ToArray())
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
        return response;
    }

    private static byte[] Int64(long value)
    {
        byte[] bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(static part => part).ToArray();

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
