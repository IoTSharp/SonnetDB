using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.IO;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

public sealed class KvGetAndDeleteClientTests
{
    private const uint StreamId = 72;
    private const long PreviousVersion = 0x0102030405060708;
    private static readonly Uri Server = new("http://localhost/");

    [Fact]
    public async Task GetAndDeleteKvAsync_BinaryKeyAndExchangeReply_PreservesExistingWireContract()
    {
        var expiry = new DateTimeOffset(2030, 11, 7, 1, 2, 3, TimeSpan.FromHours(8)).AddTicks(7);
        var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("http://localhost/v1/frame", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
            Assert.Equal("application/x-sonnetdb-frame", request.Content!.Headers.ContentType!.MediaType);
            var sequence = new ReadOnlySequence<byte>(await request.Content.ReadAsByteArrayAsync(cancellation));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal((byte)FrameService.Kv, header.Service);
            Assert.Equal((byte)KvFrameOp.GetAndDelete, header.Op);
            Assert.Equal(StreamId, header.StreamId);
            Assert.Equal((byte)FrameFlags.None, header.Flags);
            var reader = new SpanReader(payload.ToArray());
            Assert.Equal("数据库", reader.ReadVarString());
            Assert.Equal("Cache", reader.ReadVarString());
            Assert.Equal(2u, reader.ReadVarUInt32());
            Assert.Equal(new byte[] { 0, 255 }, reader.ReadBytes(2).ToArray());
            Assert.Equal(0, reader.Remaining);
            Assert.True(sequence.IsEmpty);
            return Reply(ExchangeWithExpiry(expiry.UtcTicks));
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server, "token");
        var result = await client.GetAndDeleteKvAsync("数据库", "Cache", new byte[] { 0, 255 }, StreamId);
        Assert.Equal(long.MaxValue, result.MutationVersion);
        var previous = Assert.IsType<KvDataFrameValue>(result.PreviousValue);
        Assert.Equal(PreviousVersion, previous.Version);
        Assert.Equal(new byte[] { 0, 128, 255 }, previous.Value);
        Assert.Equal(expiry.UtcTicks, previous.ExpiresAtUtc!.Value.UtcTicks);
        Assert.Equal(TimeSpan.Zero, previous.ExpiresAtUtc.Value.Offset);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAndDeleteKvAsync_MissingAndEmptyOldValue_AreDistinct(bool found)
    {
        byte[] payload = found ? ExchangePayload()[..19] : new byte[9];
        if (found) payload[18] = 0;
        var handler = new Handler((_, _) => Task.FromResult(Reply(payload)));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var result = await client.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }, StreamId);
        if (found)
        {
            var previous = Assert.IsType<KvDataFrameValue>(result.PreviousValue);
            Assert.Empty(previous.Value);
            Assert.Equal(PreviousVersion, previous.Version);
            Assert.Null(previous.ExpiresAtUtc);
            Assert.Equal(long.MaxValue, result.MutationVersion);
        }
        else
        {
            Assert.Null(result.PreviousValue);
            Assert.Null(result.MutationVersion);
        }
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(KvDataFrameCodec.MaxKeyBytes)]
    public async Task GetAndDeleteKvAsync_EmptyAndMaximumKey_SendExactlyOnce(int keyLength)
    {
        var key = new byte[keyLength];
        Array.Fill(key, (byte)255);
        var handler = new Handler(async (request, cancellation) =>
        {
            var sequence = new ReadOnlySequence<byte>(await request.Content!.ReadAsByteArrayAsync(cancellation));
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal((byte)KvFrameOp.GetAndDelete, header.Op);
            var reader = new SpanReader(payload.ToArray());
            Assert.Equal("d", reader.ReadVarString());
            Assert.Equal("k", reader.ReadVarString());
            Assert.Equal((uint)keyLength, reader.ReadVarUInt32());
            Assert.Equal(key, reader.ReadBytes(keyLength).ToArray());
            Assert.Equal(0, reader.Remaining);
            return Reply(new byte[9]);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var result = await client.GetAndDeleteKvAsync("d", "k", key, StreamId);
        Assert.Null(result.PreviousValue);
        Assert.Null(result.MutationVersion);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(null, "k")]
    [InlineData("d", null)]
    [InlineData("", "k")]
    [InlineData("d", "")]
    [InlineData(" ", "k")]
    [InlineData("d", "\t")]
    public async Task GetAndDeleteKvAsync_InvalidNames_RejectsWithoutSending(string? db, string? keyspace)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetAndDeleteKvAsync(db!, keyspace!, new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAndDeleteKvAsync_InvalidEncodingAndBudgets_RejectsWithoutSending()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetAndDeleteKvAsync(new string('中', 172), "k", new byte[] { 1 }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.GetAndDeleteKvAsync("d", "\uD800", new byte[] { 1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetAndDeleteKvAsync("d", "k", new byte[KvDataFrameCodec.MaxKeyBytes + 1]));
        using var small = new SndbFrameClient(http, Server, maxPayloadBytes: 5);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => small.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task GetAndDeleteKvAsync_PreCanceled_RejectsWithoutUnknownSend()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }, cancellationToken: cancellation.Token));
        Assert.IsNotType<SndbFrameCanceledException>(failure);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("get-payload")]
    [InlineData("truncated-mutation")]
    [InlineData("truncated-previous")]
    [InlineData("truncated-value")]
    [InlineData("trailing-payload")]
    [InlineData("found-marker")]
    [InlineData("expiry-marker")]
    [InlineData("negative-mutation")]
    [InlineData("missing-mutation")]
    [InlineData("zero-previous")]
    [InlineData("negative-previous")]
    [InlineData("expiry-range")]
    public async Task GetAndDeleteKvAsync_InvalidExchangePayload_IsUnknownAndNeverRetried(string mode)
    {
        byte[] payload = ExchangePayload();
        switch (mode)
        {
            case "get-payload": payload = [0]; break;
            case "truncated-mutation": payload = payload[..7]; break;
            case "truncated-previous": payload = payload[..16]; break;
            case "truncated-value": payload = payload[..^1]; break;
            case "trailing-payload": payload = [.. payload, 0]; break;
            case "found-marker": payload[8] = 2; break;
            case "expiry-marker": payload[17] = 2; break;
            case "negative-mutation": payload.AsSpan(0, 8).Fill(255); break;
            case "missing-mutation": payload.AsSpan(0, 8).Clear(); break;
            case "zero-previous": payload.AsSpan(9, 8).Clear(); break;
            case "negative-previous": payload.AsSpan(9, 8).Fill(255); break;
            case "expiry-range": payload = ExchangeWithExpiry(long.MaxValue); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        var handler = new Handler((_, _) => Task.FromResult(Reply(payload)));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => client.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }, StreamId));
        Assert.Equal("frame_kv_terminal", failure.Code);
        Assert.True(failure.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("service", "frame_identity")]
    [InlineData("operation", "frame_identity")]
    [InlineData("stream", "frame_identity")]
    [InlineData("version", "frame_identity")]
    [InlineData("flags", "frame_identity")]
    [InlineData("header-truncated", "frame_transport")]
    [InlineData("body-truncated", "frame_transport")]
    [InlineData("trailing-byte", "frame_trailing_data")]
    [InlineData("extra-frame", "frame_trailing_data")]
    [InlineData("response-budget", "frame_budget")]
    public async Task GetAndDeleteKvAsync_InvalidEnvelope_PreservesUnknownWithoutReplay(string mode, string code)
    {
        var handler = new Handler((_, _) =>
        {
            byte[] payload = ExchangePayload();
            var header = new FrameHeader(mode == "response-budget" ? 129u : (uint)payload.Length,
                mode == "version" ? (byte)99 : FrameHeader.CurrentVersion,
                mode == "service" ? (byte)FrameService.Sql : (byte)FrameService.Kv,
                mode == "operation" ? (byte)KvFrameOp.Get : (byte)KvFrameOp.GetAndDelete,
                mode == "flags" ? (byte)0 : (byte)FrameFlags.Response,
                mode == "stream" ? 73u : StreamId);
            byte[] bytes = EncodeReply(header, payload);
            if (mode == "header-truncated") bytes = bytes[..(FrameHeader.Size - 1)];
            if (mode == "body-truncated") bytes = bytes[..^1];
            if (mode == "trailing-byte") bytes = [.. bytes, 0];
            if (mode == "extra-frame") bytes = [.. bytes, .. bytes];
            return Task.FromResult(Response(bytes));
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server, maxPayloadBytes: 128);
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => client.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }, StreamId));
        Assert.Equal(code, failure.Code);
        Assert.True(failure.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetAndDeleteKvAsync_ServerError_PreservesCodeAndDoesNotRetry()
    {
        var handler = new Handler((_, _) =>
        {
            var encoded = new ArrayBufferWriter<byte>();
            FrameCodec.WriteErrorFrame(encoded, (byte)FrameService.Kv, (byte)KvFrameOp.GetAndDelete, StreamId, "forbidden", "禁止删除");
            return Task.FromResult(Response(encoded.WrittenMemory.ToArray()));
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => client.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }, StreamId));
        Assert.Equal("forbidden", failure.Code);
        Assert.Equal("禁止删除", failure.Message);
        Assert.True(failure.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetAndDeleteKvAsync_CancelOrTimeoutWaitingForEof_IsUnknownAndReleasesBody(bool cancel)
    {
        var bytes = EncodeReply(new FrameHeader((uint)ExchangePayload().Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Kv, (byte)KvFrameOp.GetAndDelete, (byte)FrameFlags.Response, StreamId), ExchangePayload());
        using var body = new PendingEofStream(bytes);
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new StreamContent(body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server, timeout: cancel ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(500));
        using var cancellation = new CancellationTokenSource();
        var pending = client.GetAndDeleteKvAsync("d", "k", new byte[] { 1 }, StreamId, cancellation.Token);
        await body.EofRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(pending.IsCompleted);
        if (cancel)
        {
            cancellation.Cancel();
            var failure = await Assert.ThrowsAsync<SndbFrameCanceledException>(() => pending);
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.True(failure.OutcomeUnknown);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<SndbFrameException>(() => pending);
            Assert.Equal("request_timeout", failure.Code);
            Assert.True(failure.OutcomeUnknown);
        }
        Assert.True(body.Disposed);
        Assert.Equal(1, handler.Calls);
    }

    // Golden exchange bytes: fixed Int64 mutation, found, fixed Int64 previous version,
    // expiry marker, value length and binary value. Get uses a different wire layout.
    private static byte[] ExchangePayload() => Convert.FromHexString("FFFFFFFFFFFFFF7F01080706050403020100030080FF");

    private static byte[] ExchangeWithExpiry(long ticks)
    {
        byte[] original = ExchangePayload();
        var result = new byte[original.Length + sizeof(long)];
        original.AsSpan(0, 18).CopyTo(result);
        result[17] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(18, sizeof(long)), ticks);
        original.AsSpan(18).CopyTo(result.AsSpan(26));
        return result;
    }

    private static HttpResponseMessage Reply(byte[] payload)
        => Response(EncodeReply(new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Kv, (byte)KvFrameOp.GetAndDelete, (byte)FrameFlags.Response, StreamId), payload));

    private static byte[] EncodeReply(FrameHeader header, byte[] payload)
    {
        var bytes = new byte[FrameHeader.Size + payload.Length];
        header.Write(bytes);
        payload.CopyTo(bytes.AsSpan(FrameHeader.Size));
        return bytes;
    }

    private static HttpResponseMessage Response(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Version = HttpVersion.Version20,
            Content = new ByteArrayContent(bytes),
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

    private sealed class PendingEofStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public TaskCompletionSource EofRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return base.ReadAsync(buffer, cancellationToken);
            EofRequested.TrySetResult();
            return WaitForEofAsync(cancellationToken);
        }

        private static async ValueTask<int> WaitForEofAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
