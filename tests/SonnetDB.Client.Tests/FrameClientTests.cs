using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.Client;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Client.Tests;

public sealed class FrameClientTests
{
    private static readonly Uri Server = new("http://localhost:5080/");

    [Fact]
    public void Dependencies_ClientAndProtocol_DoNotReferenceEngineOrData()
    {
        var references = typeof(SndbFrameClient).Assembly.GetReferencedAssemblies()
            .Concat(typeof(FrameHeader).Assembly.GetReferencedAssemblies()).ToArray();
        Assert.DoesNotContain(references, reference => reference.Name is "SonnetDB.Core" or "SonnetDB.Data" or "SonnetDB.Native");
        Assert.Equal("SonnetDB.Protocol", typeof(FrameHeader).Assembly.GetName().Name);
        Assert.Equal("SonnetDB.Protocol", typeof(SonnetDB.IO.SpanReader).Assembly.GetName().Name);
    }

    [Fact]
    public async Task SendUnary_ValidReply_UsesExactHttp2AndBinaryEnvelope()
    {
        byte[] expected = [0, 255, 1, 2];
        var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Equal("http://localhost:5080/v1/frame", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer token", request.Headers.Authorization!.ToString());
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellation);
            var sequence = new ReadOnlySequence<byte>(bytes);
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal(5, header.Service);
            Assert.Equal(1, header.Op);
            Assert.Equal(42u, header.StreamId);
            Assert.Equal(expected, payload.ToArray());
            Assert.True(sequence.IsEmpty);
            return Reply(expected);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server, "token");
        Assert.Equal(expected, (await client.SendUnaryAsync(5, 1, 42, expected)).ToArray());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ExecuteSqlAsync_UsesExecuteOpcodeAndExactParameters()
    {
        var handler = new Handler(async (request, cancellation) =>
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            byte[] bytes = await request.Content!.ReadAsByteArrayAsync(cancellation);
            var sequence = new ReadOnlySequence<byte>(bytes);
            Assert.True(FrameCodec.TryReadFrame(ref sequence, out var header, out var payload));
            Assert.Equal((byte)FrameService.Sql, header.Service);
            Assert.Equal((byte)SqlFrameOp.Execute, header.Op);
            Assert.Equal(7u, header.StreamId);
            var decoded = SqlExecuteFrameCodec.DecodeRequest(payload.ToArray());
            Assert.Equal("demo", decoded.Db);
            Assert.Equal("INSERT INTO t (id, note) VALUES (@id, @note)", decoded.Sql);
            Assert.Equal(2, decoded.Parameters.Count);
            Assert.Equal(long.MaxValue, decoded.Parameters[0].Value);
            Assert.Equal("remote", decoded.Parameters[1].Value);

            var output = new ArrayBufferWriter<byte>();
            SqlExecuteFrameCodec.EncodeResponse(output, 7, 1);
            var responseSequence = new ReadOnlySequence<byte>(output.WrittenMemory);
            Assert.True(FrameCodec.TryReadFrame(ref responseSequence, out _, out var responsePayload));
            return Response(responsePayload.ToArray(), (byte)FrameService.Sql, (byte)SqlFrameOp.Execute, 7);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var result = await client.ExecuteSqlAsync(
            "demo",
            "INSERT INTO t (id, note) VALUES (@id, @note)",
            [
                SqlExecuteFrameCodec.FromClr("id", long.MaxValue),
                SqlExecuteFrameCodec.FromClr("note", "remote"),
            ],
            streamId: 7);
        Assert.Equal(1, result.RecordsAffected);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("version")]
    public async Task ExecuteSqlAsync_InvalidTerminal_PreservesUnknownWithoutRetry(string mode)
    {
        var handler = new Handler((_, _) =>
        {
            byte[] payload = mode == "missing" ? [] : new byte[10];
            if (payload.Length > 0) payload[0] = mode == "version" ? (byte)2 : SqlExecuteFrameCodec.CurrentVersion;
            return Task.FromResult(Response(payload, (byte)FrameService.Sql, (byte)SqlFrameOp.Execute, 1));
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.ExecuteSqlAsync("db", "DELETE FROM t WHERE id=1"));
        Assert.Equal("frame_sql_terminal", error.Code);
        Assert.True(error.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("version", "frame_identity")]
    [InlineData("service", "frame_identity")]
    [InlineData("operation", "frame_identity")]
    [InlineData("stream", "frame_identity")]
    [InlineData("flags", "frame_identity")]
    [InlineData("truncated", "frame_transport")]
    [InlineData("extra", "frame_trailing_data")]
    [InlineData("oversize", "frame_budget")]
    [InlineData("http1", "http_version")]
    [InlineData("content", "frame_content_type")]
    [InlineData("redirect", "http_error")]
    public async Task SendUnary_InvalidReply_FailsOnceWithoutFallback(string mode, string code)
    {
        var handler = new Handler((_, _) =>
        {
            var header = new FrameHeader(mode == "oversize" ? 17u : 1u,
                mode == "version" ? (byte)99 : FrameHeader.CurrentVersion,
                mode == "service" ? (byte)6 : (byte)5,
                mode == "operation" ? (byte)2 : (byte)1,
                mode == "flags" ? (byte)0 : (byte)FrameFlags.Response,
                mode == "stream" ? 99u : 42u);
            byte[] bytes = new byte[mode == "truncated" ? FrameHeader.Size : FrameHeader.Size + (mode == "extra" ? 2 : 1)];
            header.Write(bytes);
            var response = Response(bytes);
            if (mode == "http1") response.Version = HttpVersion.Version11;
            if (mode == "content") response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (mode == "redirect") response.StatusCode = HttpStatusCode.TemporaryRedirect;
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server, maxPayloadBytes: 16);
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => client.SendUnaryAsync(5, 1, 42, new byte[1]));
        Assert.Equal(code, failure.Code);
        Assert.True(failure.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SendUnary_ServerError_PreservesErrorAndDoesNotRetry()
    {
        var handler = new Handler((_, _) =>
        {
            var output = new ArrayBufferWriter<byte>();
            FrameCodec.WriteErrorFrame(output, 5, 1, 42, "forbidden", "禁止访问");
            return Task.FromResult(Response(output.WrittenMemory.ToArray()));
        });
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var error = await Assert.ThrowsAsync<SndbFrameException>(() => client.SendUnaryAsync(5, 1, 42, new byte[1]));
        Assert.Equal("forbidden", error.Code);
        Assert.Equal("禁止访问", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SendUnary_PreCancelledOversizeAndMultiFrameOperation_DoNotSend()
    {
        var handler = new Handler((_, _) => Task.FromResult(Reply([])));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server, maxPayloadBytes: 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendUnaryAsync(5, 1, 42, new byte[1], cancelled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.SendUnaryAsync(5, 1, 42, new byte[2]));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.SendUnaryAsync((byte)FrameService.Sql, 1, 42, new byte[1]));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendUnary_DisposedClient_DoesNotSend()
    {
        var handler = new Handler((_, _) => Task.FromResult(Reply([])));
        using var http = new HttpClient(handler);
        var client = new SndbFrameClient(http, Server);
        client.Dispose();
        client.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendUnaryAsync(5, 1, 42, new byte[1]));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task SendUnary_TransportCancellation_PreservesUnknownOutcome()
    {
        var handler = new Handler((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("transport stopped")));
        using var http = new HttpClient(handler);
        using var client = new SndbFrameClient(http, Server);
        var failure = await Assert.ThrowsAsync<SndbFrameException>(() => client.SendUnaryAsync(5, 1, 42, new byte[1]));
        Assert.Equal("frame_transport", failure.Code);
        Assert.True(failure.OutcomeUnknown);
        Assert.Equal(1, handler.Calls);
    }

    private static HttpResponseMessage Reply(byte[] payload)
        => Response(payload, 5, 1, 42);

    private static HttpResponseMessage Response(byte[] payload, byte service, byte operation, uint streamId)
    {
        var output = new ArrayBufferWriter<byte>();
        FrameCodec.WriteFrame(output, new FrameHeader((uint)payload.Length,
            FrameHeader.CurrentVersion, service, operation, (byte)FrameFlags.Response, streamId), payload);
        return Response(output.WrittenMemory.ToArray());
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
}
