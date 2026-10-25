using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.Client;
using SonnetDB.IO;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

public sealed class FrameStreamingClientTests
{
    private const byte Service = (byte)FrameService.Sql;
    private const byte Operation = (byte)SqlFrameOp.Query;
    private const uint StreamId = 17;

    [Fact]
    public async Task SendStreaming_ValidFrames_StopsAtTerminalAndPreservesPayloads()
    {
        byte[] response = Frames(
            Frame(Service, Operation, StreamId, [1, 2]),
            Frame(Service, Operation, StreamId, [3, 4]));
        using var http = new HttpClient(new ResponseHandler(response));
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));
        var frames = new List<SndbFrameResponse>();

        await foreach (SndbFrameResponse frame in client.SendStreamingAsync(
            Service, Operation, StreamId, new byte[] { 9 }, payload => payload.Span[0] == 3))
        {
            frames.Add(frame);
        }

        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 1, 2 }, frames[0].Payload.ToArray());
        Assert.Equal(new byte[] { 3, 4 }, frames[1].Payload.ToArray());
        Assert.All(frames, frame => Assert.True(frame.Header.IsResponse));
    }

    [Fact]
    public async Task SendStreaming_MissingTerminal_ReportsUnknownOutcome()
    {
        using var http = new HttpClient(new ResponseHandler(Frames(Frame(Service, Operation, StreamId, [1]))));
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));
        var frames = new List<SndbFrameResponse>();

        SndbFrameException error = await Assert.ThrowsAsync<SndbFrameException>(async () =>
        {
            await foreach (SndbFrameResponse frame in client.SendStreamingAsync(
                Service, Operation, StreamId, new byte[] { 9 }, payload => payload.Span[0] == 3))
            {
                frames.Add(frame);
            }
        });

        Assert.Equal("frame_terminal", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.Single(frames);
    }

    [Fact]
    public async Task SendStreaming_IdentityMismatch_FailsBeforeYield()
    {
        using var http = new HttpClient(new ResponseHandler(
            Frames(Frame(Service, Operation, StreamId + 1, [3]))));
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));

        SndbFrameException error = await Assert.ThrowsAsync<SndbFrameException>(async () =>
        {
            await foreach (SndbFrameResponse _ in client.SendStreamingAsync(
                Service, Operation, StreamId, new byte[] { 9 }, static _ => true))
            {
            }
        });

        Assert.Equal("frame_terminal", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task SendStreaming_TrailingBytesAfterTerminal_AreRejected()
    {
        byte[] terminal = Frame(Service, Operation, StreamId, [3]);
        byte[] response = [.. terminal, 0xA5];
        using var http = new HttpClient(new ResponseHandler(response));
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));

        SndbFrameException error = await Assert.ThrowsAsync<SndbFrameException>(async () =>
        {
            await foreach (SndbFrameResponse _ in client.SendStreamingAsync(
                Service, Operation, StreamId, new byte[] { 9 }, static _ => true))
            {
            }
        });

        Assert.Equal("frame_trailing_data", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task SendStreaming_CallerCancellationPreservesUnknownOutcome()
    {
        using var http = new HttpClient(new BlockingResponseHandler());
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"), timeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        SndbFrameCanceledException error = await Assert.ThrowsAsync<SndbFrameCanceledException>(async () =>
        {
            await foreach (SndbFrameResponse _ in client.SendStreamingAsync(
                Service, Operation, StreamId, new byte[] { 9 }, static _ => true, cancellationToken: cancellation.Token))
            {
            }
        });

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task SendStreaming_FrameBudgetRejectsUnboundedResponse()
    {
        using var http = new HttpClient(new ResponseHandler(Frames(
            Frame(Service, Operation, StreamId, [1]),
            Frame(Service, Operation, StreamId, [1]),
            Frame(Service, Operation, StreamId, [3]))));
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));

        SndbFrameException error = await Assert.ThrowsAsync<SndbFrameException>(async () =>
        {
            await foreach (SndbFrameResponse _ in client.SendStreamingAsync(
                Service, Operation, StreamId, new byte[] { 9 }, static _ => false, maxFrames: 2))
            {
            }
        });

        Assert.Equal("frame_budget", error.Code);
        Assert.True(error.OutcomeUnknown);
    }

    [Fact]
    public async Task SendStreaming_BreakingEnumerationDisposesResponseStream()
    {
        var handler = new TrackingResponseHandler(Frames(
            Frame(Service, Operation, StreamId, [1]),
            Frame(Service, Operation, StreamId, [3])));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, new Uri("http://localhost/"));

        await foreach (SndbFrameResponse _ in client.SendStreamingAsync(
            Service, Operation, StreamId, new byte[] { 9 }, static _ => false))
        {
            break;
        }

        Assert.True(handler.StreamDisposed);
    }

    [Fact]
    public void ValidateResponse_RejectsUnexpectedFlagsAndTrailingErrorBytes()
    {
        var header = new FrameHeader(0, FrameHeader.CurrentVersion, Service, Operation,
            (byte)FrameFlags.Push, StreamId);
        FrameFormatException flags = Assert.Throws<FrameFormatException>(
            () => FrameCodec.ValidateResponse(header, Service, Operation, StreamId));
        Assert.Contains("Response", flags.Message, StringComparison.Ordinal);

        var writer = new ArrayBufferWriter<byte>();
        var payload = new SpanWriter(writer.GetSpan(64));
        payload.WriteVarString("bad");
        payload.WriteVarString("message");
        writer.Advance(payload.Position);
        Assert.Throws<FrameFormatException>(() => FrameCodec.ReadErrorPayload(
            [.. writer.WrittenSpan, 0x00]));
        Assert.Throws<FrameFormatException>(() => FrameCodec.ReadErrorPayload([1]));
    }

    private static byte[] Frame(byte service, byte operation, uint streamId, byte[] payload)
    {
        var writer = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(writer,
            new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion, service, operation,
                (byte)FrameFlags.Response, streamId), payload);
        return writer.WrittenSpan.ToArray();
    }

    private static byte[] Frames(params byte[][] frames)
    {
        int length = frames.Sum(static frame => frame.Length);
        var result = new byte[length];
        int offset = 0;
        foreach (byte[] frame in frames)
        {
            frame.CopyTo(result, offset);
            offset += frame.Length;
        }
        return result;
    }

    private sealed class ResponseHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new ByteArrayContent(body),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new StreamContent(new BlockingStream()),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingResponseHandler(byte[] body) : HttpMessageHandler
    {
        public bool StreamDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Version = HttpVersion.Version20,
                Content = new StreamContent(new TrackingStream(body, () => StreamDisposed = true)),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sonnetdb-frame");
            return Task.FromResult(response);
        }
    }

    private sealed class TrackingStream(byte[] bytes, Action onDispose) : MemoryStream(bytes, writable: false)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                onDispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return 0;
        }
    }
}
