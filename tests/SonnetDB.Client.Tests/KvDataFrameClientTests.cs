using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.IO;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

public sealed class KvDataFrameClientTests
{
    [Fact]
    public async Task PutKvAsync_BinaryAndExpiry_UsesExistingWireContract()
    {
        var expiry = new DateTimeOffset(2030, 11, 6, 1, 2, 3, TimeSpan.FromHours(8)).AddTicks(7);
        var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
            var sequence = new ReadOnlySequence<byte>(await request.Content!.ReadAsByteArrayAsync(cancellation));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal((byte)FrameService.Kv, header.Service);
            Assert.Equal((byte)KvFrameOp.Put, header.Op);
            Assert.Equal(72u, header.StreamId);
            var reader = new SpanReader(payload.ToArray());
            Assert.Equal("数据库", reader.ReadVarString());
            Assert.Equal("Cache", reader.ReadVarString());
            Assert.Equal(2u, reader.ReadVarUInt32());
            Assert.Equal(new byte[] { 0, 255 }, reader.ReadBytes(2).ToArray());
            Assert.Equal(1, reader.ReadByte());
            Assert.Equal(expiry.UtcTicks, reader.ReadInt64());
            Assert.Equal(3u, reader.ReadVarUInt32());
            Assert.Equal(new byte[] { 0, 128, 255 }, reader.ReadBytes(3).ToArray());
            Assert.Equal(0, reader.Remaining);
            Assert.True(sequence.IsEmpty);
            return Reply(header, [255, 255, 255, 255, 255, 255, 255, 255, 127]);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"), "token");
        Assert.Equal(long.MaxValue, await client.PutKvAsync("数据库", "Cache", new byte[] { 0, 255 },
            new byte[] { 0, 128, 255 }, expiry, streamId: 72));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetKvAsync_MissingAndEmpty_AreDistinct(bool found)
    {
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            var sequence = new ReadOnlySequence<byte>(await request.Content!.ReadAsByteArrayAsync(cancellation));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out _));
            return Reply(header, found ? [1, 1, 0, 0] : [0]);
        }));
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));
        var result = await client.GetKvAsync("d", "k", new byte[] { 1 });
        if (found)
        {
            Assert.NotNull(result);
            Assert.Empty(result.Value);
            Assert.Equal(1, result.Version);
            Assert.Null(result.ExpiresAtUtc);
        }
        else Assert.Null(result);
    }

    [Theory]
    [InlineData("get-found")]
    [InlineData("get-trailing")]
    [InlineData("get-truncated")]
    [InlineData("get-expiry")]
    [InlineData("get-version")]
    [InlineData("put-trailing")]
    [InlineData("put-truncated")]
    [InlineData("put-version")]
    [InlineData("scan-count")]
    [InlineData("scan-trailing")]
    public async Task TypedKv_MalformedPayload_IsUnknownAndNeverRetried(string shape)
    {
        byte[] payload = shape switch
        {
            "get-found" => [2],
            "get-trailing" => [0, 0],
            "get-truncated" => [1, 1, 0, 2, 42],
            "get-expiry" => [1, 1, 2, 0],
            "get-version" => [1, 255, 255, 255, 255, 255, 255, 255, 255, 255, 1, 0, 0],
            "put-trailing" => [1, 0],
            "put-truncated" => [128],
            "put-version" => [255, 255, 255, 255, 255, 255, 255, 255, 255, 1],
            "scan-count" => [2, 0, 1, 0, 0, 0, 1, 0, 0],
            "scan-trailing" => [0, 0],
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        var handler = new Handler(async (request, cancellation) =>
        {
            var sequence = new ReadOnlySequence<byte>(await request.Content!.ReadAsByteArrayAsync(cancellation));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out _));
            return Reply(header, payload);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));
        var error = await Assert.ThrowsAsync<SndbFrameException>(async () =>
        {
            if (shape.StartsWith("get", StringComparison.Ordinal)) await client.GetKvAsync("d", "k", new byte[] { 1 });
            else if (shape.StartsWith("put", StringComparison.Ordinal)) await client.PutKvAsync("d", "k", new byte[] { 1 }, ReadOnlyMemory<byte>.Empty);
            else await client.ScanKvPageAsync("d", "k", limit: 1);
        });
        Assert.Equal("frame_kv_terminal", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PutKvAsync_ExceedsConfiguredBudget_RejectsBeforeSending()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"), maxPayloadBytes: 16);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.PutKvAsync("d", "k", new byte[1], new byte[16]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetKvAsync("d", "k", new byte[KvDataFrameCodec.MaxKeyBytes + 1]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetKvAsync(new string('中', 200), "k", new byte[1]));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ScanKvPageAsync("d", "k", limit: 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ScanKvPageAsync("d", "k", limit: 10_001));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TypedKv_PreCanceled_RejectsBeforeEncodingOrSending()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PutKvAsync("d", "k", new byte[1], new byte[1], cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetKvAsync("d", "k", new byte[1], cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ScanKvPageAsync("d", "k", cancellationToken: cancellation.Token));
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

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
